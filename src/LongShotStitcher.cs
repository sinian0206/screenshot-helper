using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace XiaoJie
{
    /// <summary>
    /// 滚动内容拼接器（长截图核心算法，可独立单测）。
    /// 用法：new LongShotStitcher(regionWidth) → 逐帧 AddFrame(frame) → TakeResult()。
    ///
    /// 算法概要：
    ///   1. 每帧只做一次 LockBits，转成 32 路灰度采样签名（每行/每列一个 byte[32] 向量），
    ///      用行（纵向滚动）/ 列（横向滚动）签名序列在“已拼接画布”的尾/首部搜索重叠区；
    ///   2. 用逐行 SAD（截断离群值）打分，粗筛 + 精修，取最优 offset；
    ///      相似度低于阈值/存在歧义时丢弃该帧（滚动过快、无重叠、重复帧都不会误拼）；
    ///   3. 用相邻帧的公共首/尾行自动探测粘性页眉页脚（固定工具栏、固定底栏），
    ///      匹配时避开这些边缘区域，前插时跳过粘连页眉，尽量容忍；
    ///   4. 画布使用“容量缓冲 + 主轴偏移”增量扩展（向下追加/向上前插、向右/向左同理），
    ///      避免每帧全图拷贝；带 40000px 单边、200MB 内容上限保护。
    /// 负责队友：longshot。约定 SelfTest() 返回一行摘要（失败请返回以 "FAIL" 开头的说明或抛异常）。
    /// </summary>
    public sealed class LongShotStitcher : IDisposable
    {
        // ---- 公开上限（长截图结果保护）----
        /// <summary>结果图单边最大像素（超过即停止拼接）。</summary>
        public const int MaxDimension = 40000;
        /// <summary>结果图最大字节数（约 200MB，超过即停止拼接）。</summary>
        public const long MaxBytes = 200L * 1024 * 1024;

        // ---- 算法参数 ----
        private const int SigSamples = 32;        // 每行/列签名采样点数
        private const int RowDiffCap = 40;        // 单行 SAD 截断上限，容忍局部动画/固定的页眉页脚
        private const double AcceptScore = 12.0;  // 接受匹配的平均截断 SAD 上限
        private const double VerifyAccept = 6.0;  // 2D 像素校验平均绝对差上限（0=逐像素一致）
        private const double AmbiguityGap = 3.0;  // 次优匹配与最优匹配的最小差距（防误拼）
        private const int CoarseStep = 4;         // 粗筛时每 N 行采样一次
        private const int MaxTopCandidates = 8;   // 粗筛保留的候选数
        private const int RefineRadius = 4;       // 候选附近精修半径

        // ---- 诊断（环境变量 XJ_LONGSHOT_DEBUG=1 时输出到日志；不影响正常运行）----
        private static readonly bool DebugEnabled = Environment.GetEnvironmentVariable("XJ_LONGSHOT_DEBUG") == "1";
        private static readonly object DebugSync = new object();

        private static void Dbg(string message)
        {
            if (!DebugEnabled) return;
            lock (DebugSync)
            {
                try { Log.Write("[stitch] " + message); } catch { }
            }
        }

        private enum AxisMode { Unknown, Vertical, Horizontal }

        private readonly int _width;              // 期望帧宽（ctor 传入，物理像素）
        private int _fixedHeight;                 // 横向模式的固定高度（=首帧高）

        // ---- 单帧阶段（尚未发生滚动，还无法判断滚动方向）----
        private Bitmap _singleFrame;
        private FrameSig _singleSig;

        // ---- 画布阶段 ----
        private Bitmap _buf;                      // 容量缓冲
        private int _cap;                         // 主轴容量
        private int _start;                       // 主轴有效内容起点（纵向=行偏移，横向=列偏移）
        private int _len;                         // 主轴有效内容长度
        private AxisMode _mode = AxisMode.Unknown;
        private List<byte[]> _axisSigs;           // 画布主轴签名（纵向=行签名，横向=列签名）
        private FrameSig _prevSig;                // 上一已接受帧的签名（用于粘性边缘探测）
        private int _prevSigStart;                // 上一帧有效范围起点
        private int _prevSigLen;                  // 上一帧有效范围长度
        private int _lastStep;                    // 上一次实际新增的像素数（用于歧义时优先延续滚动步长）

        // ---- 状态 ----
        private int _appended;
        private bool _full;
        private string _status = "";
        private bool _disposed;

        public LongShotStitcher(int width)
        {
            if (width <= 0) throw new ArgumentException("区域宽度必须大于 0", "width");
            _width = width;
            _status = "等待首帧";
        }

        /// <summary>已稳定拼接的帧数（第一帧成功后从 1 开始计）。</summary>
        public int AppendedFrames
        {
            get { return _appended; }
        }

        /// <summary>当前累积画面（可能为 null）。每次都返回独立副本，调用方用完请释放。</summary>
        public Bitmap Canvas
        {
            get { return Snapshot(); }
        }

        /// <summary>当前结果尺寸（未拼接时为单帧尺寸，尚未收到帧时为 Empty）。</summary>
        public Size ResultSize
        {
            get
            {
                if (_len <= 0)
                {
                    if (_singleFrame != null) return _singleFrame.Size;
                    return Size.Empty;
                }
                if (_mode == AxisMode.Horizontal) return new Size(_len, _fixedHeight);
                return new Size(_width, _len);
            }
        }

        /// <summary>是否已达到尺寸/内存上限。</summary>
        public bool IsFull
        {
            get { return _full; }
        }

        /// <summary>最近一次处理结果的说明（供 UI 显示“滚动过快已忽略”等提示）。</summary>
        public string Status
        {
            get { return _status; }
        }

        /// <summary>是否还没有任何有效帧。</summary>
        public bool IsEmpty
        {
            get { return _len <= 0 && _singleFrame == null; }
        }

        /// <summary>加入一帧；内容有延伸时返回 true。</summary>
        public bool AddFrame(Bitmap frame)
        {
            if (_disposed) throw new ObjectDisposedException("LongShotStitcher");
            if (frame == null)
            {
                _status = "无效帧";
                return false;
            }
            if (_full)
            {
                return false;
            }

            Bitmap prepared;
            bool own;
            if (!PrepareFrame(frame, out prepared, out own))
            {
                _status = "帧尺寸无效";
                return false;
            }
            try
            {
                FrameSig sig = ComputeSignatures(prepared);
                if (sig == null)
                {
                    _status = "帧数据无效";
                    return false;
                }

                // 第一帧：初始化
                if (_singleFrame == null && _len <= 0)
                {
                    if (sig.Width > MaxDimension || sig.Height > MaxDimension)
                    {
                        _full = true;
                        _status = "选区过大，超过长截图单边上限";
                        return false;
                    }
                    _singleFrame = CloneBitmap32(prepared);
                    _singleSig = sig;
                    _fixedHeight = sig.Height;
                    _appended = 1;
                    _prevSig = sig;
                    _prevSigStart = 0;
                    _prevSigLen = sig.Height;
                    _status = "已获取首帧";
                    return true;
                }

                // 提取画布端部灰度条带，供候选匹配做 2D 像素校验
                byte[] stripTailV = null, stripHeadV = null, stripTailH = null, stripHeadH = null;
                int stripLenV = 0, stripCrossV = 0, stripLenH = 0, stripCrossH = 0;
                if (_mode == AxisMode.Unknown || _mode == AxisMode.Vertical)
                {
                    int cv = _len > 0 ? _len : _singleFrame.Height;
                    stripLenV = Math.Min(cv, sig.Height);
                    if (stripLenV > 8)
                    {
                        stripTailV = ExtractCanvasGray(false, cv - stripLenV, stripLenV, out stripCrossV);
                        stripHeadV = ExtractCanvasGray(false, 0, stripLenV, out stripCrossV);
                    }
                }
                if ((_mode == AxisMode.Unknown && sig.Height == _singleFrame.Height) || _mode == AxisMode.Horizontal)
                {
                    int ch = _len > 0 ? _len : _singleFrame.Width;
                    stripLenH = Math.Min(ch, sig.Width);
                    if (stripLenH > 8)
                    {
                        stripTailH = ExtractCanvasGray(true, ch - stripLenH, stripLenH, out stripCrossH);
                        stripHeadH = ExtractCanvasGray(true, 0, stripLenH, out stripCrossH);
                    }
                }

                MatchResult best = null;
                if (_mode == AxisMode.Unknown)
                {
                    // 首次延伸：纵向/横向、向下/向上（向右/向左）都试，按相似度择优，纵向优先
                    MatchResult v = TryVertical(sig, _singleSig.RowSigs, _singleFrame.Height, _prevSig,
                        _prevSigStart, _prevSigLen, stripTailV, stripHeadV, stripLenV, stripCrossV);
                    MatchResult h = null;
                    if (sig.Height == _singleFrame.Height)
                        h = TryHorizontal(sig, _singleSig.ColSigs, _singleFrame.Width, _prevSig,
                            _prevSigStart, _prevSigLen, stripTailH, stripHeadH, stripLenH, stripCrossH);
                    Dbg("首次延伸: 纵向=" + Describe(v) + " 横向=" + Describe(h));
                    best = PickMatch(v, h);
                }
                else if (_mode == AxisMode.Horizontal)
                {
                    if (sig.Height != _fixedHeight)
                    {
                        _status = "画面高度变化，已忽略该帧";
                        Dbg("忽略: 高度变化 " + sig.Height + " != " + _fixedHeight);
                        return false;
                    }
                    best = TryHorizontal(sig, _axisSigs, _len, _prevSig, _prevSigStart, _prevSigLen,
                        stripTailH, stripHeadH, stripLenH, stripCrossH);
                }
                else
                {
                    best = TryVertical(sig, _axisSigs, _len, _prevSig, _prevSigStart, _prevSigLen,
                        stripTailV, stripHeadV, stripLenV, stripCrossV);
                }

                if (best == null || !best.Ok)
                {
                    if (best != null && !string.IsNullOrEmpty(best.Reason)) _status = best.Reason;
                    Dbg("忽略: 帧尺寸=" + sig.Width + "x" + sig.Height + " 画布=" + ResultSize + " 原因=" + (best == null ? "无结果" : Describe(best)));
                    return false;
                }

                Dbg("接受: 帧尺寸=" + sig.Width + "x" + sig.Height + " 方向=" + (best.Horizontal ? "横向" : "纵向") +
                    (best.Prepend ? "前插" : "追加") + " k=" + best.K + " score=" + best.Score.ToString("F2") +
                    " 新行=" + best.NewRows + " leadRaw=" + best.LeadingRaw + " 画布=" + ResultSize);

                bool ok;
                if (_mode == AxisMode.Unknown)
                    ok = InitCanvasAndApply(best, sig, prepared);
                else
                    ok = ApplyMatch(best, sig, prepared);
                if (!ok) return false;
                return true;
            }
            finally
            {
                if (own && prepared != null) prepared.Dispose();
            }
        }

        /// <summary>结束并返回最终图像（由调用方负责释放）。</summary>
        public Bitmap TakeResult()
        {
            return Snapshot();
        }

        /// <summary>生成缩放预览图（供长截图控制条显示缩略图，调用方负责释放）。</summary>
        public Bitmap CreatePreview(int maxSide)
        {
            if (maxSide < 16) maxSide = 16;
            if (_len <= 0)
            {
                if (_singleFrame == null) return null;
                return ScaleBitmap(_singleFrame, maxSide);
            }
            int srcW = _mode == AxisMode.Horizontal ? _len : _width;
            int srcH = _mode == AxisMode.Horizontal ? _fixedHeight : _len;
            float scale = Math.Min(1f, (float)maxSide / Math.Max(srcW, srcH));
            int dw = Math.Max(1, (int)Math.Round(srcW * scale));
            int dh = Math.Max(1, (int)Math.Round(srcH * scale));
            Bitmap dst = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    Rectangle srcRect = _mode == AxisMode.Horizontal
                        ? new Rectangle(_start, 0, _len, _fixedHeight)
                        : new Rectangle(0, _start, _width, _len);
                    g.DrawImage(_buf, new Rectangle(0, 0, dw, dh), srcRect, GraphicsUnit.Pixel);
                }
                return dst;
            }
            catch
            {
                dst.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_singleFrame != null) { _singleFrame.Dispose(); _singleFrame = null; }
            if (_buf != null) { _buf.Dispose(); _buf = null; }
            _singleSig = null;
            _axisSigs = null;
            _prevSig = null;
        }

        // ==================================================================
        // 匹配：在画布尾/首搜索当前帧的重叠区
        // ==================================================================

        /// <summary>提取画布沿主轴 [canvasStart, canvasStart+len) 的灰度条带（纵向=行×宽，横向=列×高）。</summary>
        private byte[] ExtractCanvasGray(bool horizontal, int canvasStart, int len, out int cross)
        {
            cross = horizontal ? _fixedHeight : _width;
            if (len <= 0 || cross <= 0) return null;
            Bitmap src = _len > 0 ? _buf : _singleFrame;
            if (src == null) return null;
            int bufStart = _len > 0 ? _start : 0;
            if (canvasStart < 0) canvasStart = 0;
            int avail = (horizontal ? src.Width : src.Height) - bufStart - canvasStart;
            if (avail <= 0) return null;
            if (len > avail) len = avail;
            byte[] gray = new byte[len * cross];
            BitmapData bd = null;
            try
            {
                bd = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int stride = bd.Stride;
                if (horizontal)
                {
                    int startCol = bufStart + canvasStart;
                    int rowBytes = len * 4;
                    byte[] row = new byte[rowBytes];
                    for (int y = 0; y < cross; y++)
                    {
                        Marshal.Copy(IntPtr.Add(bd.Scan0, y * stride + startCol * 4), row, 0, rowBytes);
                        for (int i = 0; i < len; i++)
                        {
                            int o = i * 4;
                            gray[i * cross + y] = (byte)((row[o] * 29 + row[o + 1] * 150 + row[o + 2] * 77) >> 8);
                        }
                    }
                }
                else
                {
                    int bytes = src.Width * 4;
                    byte[] row = new byte[bytes];
                    int startRow = bufStart + canvasStart;
                    for (int i = 0; i < len; i++)
                    {
                        Marshal.Copy(IntPtr.Add(bd.Scan0, (startRow + i) * stride), row, 0, bytes);
                        int gOff = i * cross;
                        for (int x = 0; x < src.Width; x++)
                        {
                            int o = x * 4;
                            gray[gOff + x] = (byte)((row[o] * 29 + row[o + 1] * 150 + row[o + 2] * 77) >> 8);
                        }
                    }
                }
                return gray;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (bd != null) src.UnlockBits(bd);
            }
        }

        private MatchResult TryVertical(FrameSig cur, IList<byte[]> canvasRowSigs, int canvasLen, FrameSig prevSig,
            int prevStart, int prevLen, byte[] stripTail, byte[] stripHead, int stripLen, int stripCross)
        {
            int trimTop = TrimLeadingUniform(cur, false);
            int trimBottom = TrimTrailingUniform(cur, false);
            MatchResult down = null;
            MatchResult up = null;
            int lead, trail;

            int downLen = cur.Height - trimBottom;
            if (downLen >= 24)
            {
                DetectStickyBands(prevSig, prevStart, prevLen, cur, 0, downLen, false, out lead, out trail);
                down = MatchAxis(cur, 0, downLen, canvasRowSigs, canvasLen, lead, trail, false, false,
                    stripTail, stripHead, stripLen, stripCross);
                if (down != null && down.Ok) down.LeadingRaw = lead;
            }
            int upLen = cur.Height - trimTop;
            if (upLen >= 24)
            {
                DetectStickyBands(prevSig, prevStart, prevLen, cur, trimTop, upLen, false, out lead, out trail);
                up = MatchAxis(cur, trimTop, upLen, canvasRowSigs, canvasLen, lead, trail, true, false,
                    stripTail, stripHead, stripLen, stripCross);
                if (up != null && up.Ok) up.LeadingRaw = lead;
            }
            if (trimTop > 0 || trimBottom > 0)
                Dbg("纵向匹配: 帧边缘未绘制/空白带已裁剪 top=" + trimTop + " bottom=" + trimBottom);
            Dbg("纵向匹配: 向下[" + Describe(down) + "] 向上[" + Describe(up) + "]");
            return Better(down, up);
        }

        private MatchResult TryHorizontal(FrameSig cur, IList<byte[]> canvasColSigs, int canvasLen, FrameSig prevSig,
            int prevStart, int prevLen, byte[] stripTail, byte[] stripHead, int stripLen, int stripCross)
        {
            int trimLeft = TrimLeadingUniform(cur, true);
            int trimRight = TrimTrailingUniform(cur, true);
            MatchResult right = null;
            MatchResult left = null;
            int lead, trail;

            int rightLen = cur.Width - trimRight;
            if (rightLen >= 24)
            {
                DetectStickyBands(prevSig, prevStart, prevLen, cur, 0, rightLen, true, out lead, out trail);
                right = MatchAxis(cur, 0, rightLen, canvasColSigs, canvasLen, lead, trail, false, true,
                    stripTail, stripHead, stripLen, stripCross);
                if (right != null && right.Ok) right.LeadingRaw = lead;
            }
            int leftLen = cur.Width - trimLeft;
            if (leftLen >= 24)
            {
                DetectStickyBands(prevSig, prevStart, prevLen, cur, trimLeft, leftLen, true, out lead, out trail);
                left = MatchAxis(cur, trimLeft, leftLen, canvasColSigs, canvasLen, lead, trail, true, true,
                    stripTail, stripHead, stripLen, stripCross);
                if (left != null && left.Ok) left.LeadingRaw = lead;
            }
            if (trimLeft > 0 || trimRight > 0)
                Dbg("横向匹配: 帧边缘未绘制/空白带已裁剪 left=" + trimLeft + " right=" + trimRight);
            Dbg("横向匹配: 向右[" + Describe(right) + "] 向左[" + Describe(left) + "]");
            return Better(right, left);
        }

        private static MatchResult Better(MatchResult a, MatchResult b)
        {
            if (a == null || !a.Ok) return b;
            if (b == null || !b.Ok) return a;
            return a.Score <= b.Score ? a : b;
        }

        private static string Describe(MatchResult r)
        {
            if (r == null) return "null";
            if (!r.Ok)
            {
                return "拒绝(" + r.Reason + " k=" + r.K + " score=" + r.Score.ToString("F2") + ")";
            }
            return (r.Horizontal ? "横" : "纵") + (r.Prepend ? "前插" : "追加") +
                " k=" + r.K + " score=" + r.Score.ToString("F2") + " new=" + r.NewRows;
        }

        private static string DescribeRefined(List<int> ks, List<double> ss)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ks.Count && i < 8; i++)
            {
                sb.Append(' ');
                sb.Append(ks[i]);
                sb.Append(':');
                sb.Append(ss[i].ToString("F1"));
            }
            return sb.ToString();
        }

        private static MatchResult PickMatch(MatchResult vertical, MatchResult horizontal)
        {
            bool vOk = vertical != null && vertical.Ok;
            bool hOk = horizontal != null && horizontal.Ok;
            if (vOk && !hOk) return vertical;
            if (hOk && !vOk) return horizontal;
            if (!vOk && !hOk) return vertical ?? horizontal;
            // 纵向为主场景，同等相似度时优先纵向
            return vertical.Score <= horizontal.Score + 2.0 ? vertical : horizontal;
        }

        /// <summary>
        /// 在主轴签名序列上搜索重叠。fStart/fLen 为“有效帧”范围（已裁掉未绘制/空白边缘带）。
        /// prepend=false：找当前帧首部 与 画布尾部 的重叠（向下/向右滚动）；
        /// prepend=true：找当前帧尾部 与 画布头部 的重叠（向上/向左滚动）。
        /// </summary>
        private MatchResult MatchAxis(FrameSig frame, int fStart, int fLen, IList<byte[]> cSigs, int C,
            int leadRaw, int trailRaw, bool prepend, bool horizontal,
            byte[] stripTail, byte[] stripHead, int stripLen, int stripCross)
        {
            byte[][] fSigs = frame != null ? (horizontal ? frame.ColSigs : frame.RowSigs) : null;
            int full = frame != null ? (horizontal ? frame.Width : frame.Height) : 0;
            if (frame == null || fSigs == null || cSigs == null) return MatchResult.Fail("数据不足");
            if (fStart < 0) fStart = 0;
            if (fLen <= 8 || fStart + fLen > full || fStart + fLen > fSigs.Length)
                return MatchResult.Fail("数据不足");
            if (C <= 8) return MatchResult.Fail("数据不足");
            int F = fLen;

            int minNew = Math.Max(4, F / 200);
            // 最小重叠行数：大选区更严格（防误拼），小选区放宽（保证 60px 左右的窄条也能拼接）
            int minOverlap = Math.Max(20, Math.Min(64, F / 16));
            int leadSkip = Math.Max(4, leadRaw);
            int trailSkip = Math.Max(4, trailRaw);
            if (leadSkip > F / 2) leadSkip = Math.Max(4, F / 2);
            if (trailSkip > F / 2) trailSkip = Math.Max(4, F / 2);

            int kMax = Math.Min(C, F - minNew);
            if (kMax < 2) return MatchResult.Fail("可比较内容不足");

            double[] coarse = new double[kMax + 1];
            for (int i = 0; i <= kMax; i++) coarse[i] = double.MaxValue;
            int candidateCount = 0;

            for (int k = 1; k <= kMax; k++)
            {
                int y0, c0, count;
                if (prepend)
                {
                    int d = F - k;
                    int i0 = Math.Max(0, leadSkip - d);
                    int i1 = k - trailSkip;
                    count = i1 - i0;
                    if (count < minOverlap) continue;
                    y0 = d + i0;
                    c0 = i0;
                }
                else
                {
                    int y1 = Math.Min(k, F - trailSkip);
                    count = y1 - leadSkip;
                    if (count < minOverlap) continue;
                    y0 = leadSkip;
                    c0 = C - k + leadSkip;
                }
                if (y0 < 0 || c0 < 0 || y0 + count > F || c0 + count > C) continue;
                double s = ScoreSegment(fSigs, cSigs, fStart + y0, c0, count, CoarseStep);
                if (s >= double.MaxValue) continue;
                coarse[k] = s;
                candidateCount++;
            }
            if (candidateCount == 0) return MatchResult.Fail("未找到重叠区域（滚动过快，已忽略该帧）", 0, double.MaxValue);

            // 粗筛：保留最优的若干候选
            int[] topK = new int[MaxTopCandidates];
            double[] topS = new double[MaxTopCandidates];
            int topN = 0;
            for (int k = 1; k <= kMax; k++)
            {
                double s = coarse[k];
                if (s >= double.MaxValue || s > RowDiffCap * 1.8) continue;
                if (topN < MaxTopCandidates)
                {
                    int p = topN;
                    while (p > 0 && topS[p - 1] > s) { topK[p] = topK[p - 1]; topS[p] = topS[p - 1]; p--; }
                    topK[p] = k; topS[p] = s; topN++;
                }
                else if (s < topS[MaxTopCandidates - 1])
                {
                    int p = MaxTopCandidates - 1;
                    while (p > 0 && topS[p - 1] > s) { topK[p] = topK[p - 1]; topS[p] = topS[p - 1]; p--; }
                    topK[p] = k; topS[p] = s;
                }
            }
            if (topN == 0) return MatchResult.Fail("画面相似度不足（已忽略该帧）", 0, double.MaxValue);

            // 精修：候选附近逐行重新打分
            double bestScore = double.MaxValue;
            int bestK = 0;
            List<int> refinedK = new List<int>(topN * 4);
            List<double> refinedS = new List<double>(topN * 4);
            for (int t = 0; t < topN; t++)
            {
                for (int dk = -RefineRadius; dk <= RefineRadius; dk++)
                {
                    int k = topK[t] + dk;
                    if (k < 1 || k > kMax) continue;
                    int y0, c0, count;
                    if (prepend)
                    {
                        int d = F - k;
                        int i0 = Math.Max(0, leadSkip - d);
                        int i1 = k - trailSkip;
                        count = i1 - i0;
                        if (count < minOverlap) continue;
                        y0 = d + i0; c0 = i0;
                    }
                    else
                    {
                        int y1 = Math.Min(k, F - trailSkip);
                        count = y1 - leadSkip;
                        if (count < minOverlap) continue;
                        y0 = leadSkip; c0 = C - k + leadSkip;
                    }
                    if (y0 < 0 || c0 < 0 || y0 + count > F || c0 + count > C) continue;
                    double s = ScoreSegment(fSigs, cSigs, fStart + y0, c0, count, 1);
                    refinedK.Add(k); refinedS.Add(s);
                    if (s < bestScore) { bestScore = s; bestK = k; }
                }
            }
            Dbg("MatchAxis " + (horizontal ? "横" : "纵") + (prepend ? "前插" : "追加") +
                " F=" + F + " C=" + C + " kMax=" + kMax + " leadSkip=" + leadSkip + " trailSkip=" + trailSkip +
                " 候选数=" + candidateCount + " 精修=" + DescribeRefined(refinedK, refinedS));
            if (bestK <= 0) return MatchResult.Fail("画面相似度不足（已忽略该帧）", 0, double.MaxValue);

            int exclude = Math.Max(12, minOverlap / 2);
            int chosenK = bestK;
            double chosenScore = bestScore;
            byte[] strip = prepend ? stripHead : stripTail;
            if (strip != null && stripLen > 0)
            {
                // ---- 2D 像素校验：直接在候选重叠区比像素，排除弱签名下的错位匹配 ----
                Dictionary<int, double> dedup = new Dictionary<int, double>();
                for (int i = 0; i < refinedK.Count; i++)
                {
                    double old;
                    if (!dedup.TryGetValue(refinedK[i], out old) || refinedS[i] < old) dedup[refinedK[i]] = refinedS[i];
                }
                List<int> ks = new List<int>(dedup.Keys);
                ks.Sort(delegate (int a, int b) { return dedup[a].CompareTo(dedup[b]); });
                int verifyCount = Math.Min(ks.Count, 14);
                double bestV = double.MaxValue;
                int bestKV = 0;
                for (int i = 0; i < verifyCount; i++)
                {
                    int kk = ks[i];
                    double v = VerifyOverlap(frame, fStart, kk, F, C, prepend, horizontal,
                        strip, stripLen, stripCross, leadSkip, trailSkip);
                    if (v >= double.MaxValue) continue;
                    if (v < bestV - 0.001 ||
                        (Math.Abs(v - bestV) <= 0.001 && bestKV > 0 && dedup[kk] < dedup[bestKV]))
                    {
                        bestV = v;
                        bestKV = kk;
                    }
                }
                if (bestKV <= 0) return MatchResult.Fail("像素校验失败（已忽略该帧）", 0, double.MaxValue);
                if (bestV > VerifyAccept)
                    return MatchResult.Fail("像素校验未通过（已忽略该帧）", bestKV, bestScore);

                double secondV = double.MaxValue;
                for (int i = 0; i < verifyCount; i++)
                {
                    int kk = ks[i];
                    if (Math.Abs(kk - bestKV) <= exclude) continue;
                    double v = VerifyOverlap(frame, fStart, kk, F, C, prepend, horizontal,
                        strip, stripLen, stripCross, leadSkip, trailSkip);
                    if (v < secondV) secondV = v;
                }
                if (bestV > 1.0 && secondV < double.MaxValue && secondV < bestV + 2.0)
                    return MatchResult.Fail("匹配存在歧义（已忽略该帧）", bestKV, bestScore);

                // 近优候选之间：优先延续上一次滚动步长（避免周期/重复内容处选错小重叠）
                chosenK = bestKV;
                chosenScore = bestV;
                int preferred = _lastStep > 0 ? _lastStep : -1;
                double bestCost = double.MaxValue;
                for (int i = 0; i < verifyCount; i++)
                {
                    int kk = ks[i];
                    double v = kk == bestKV ? bestV : VerifyOverlap(frame, fStart, kk, F, C, prepend, horizontal,
                        strip, stripLen, stripCross, leadSkip, trailSkip);
                    if (v > bestV + 1.0) continue;
                    int newRows = F - kk;
                    double cost = preferred > 0
                        ? Math.Abs(newRows - preferred) + v * 4.0
                        : (F - kk) * 0.01 + v * 4.0;
                    if (cost < bestCost) { bestCost = cost; chosenK = kk; chosenScore = v; }
                }
            }
            else
            {
                // 无像素校验数据时退化为签名匹配（自检/极端内存不足场景）
                if (bestScore > AcceptScore) return MatchResult.Fail("画面相似度不足（已忽略该帧）", bestK, bestScore);
                double second = double.MaxValue;
                for (int k = 1; k <= kMax; k++)
                {
                    if (Math.Abs(k - bestK) <= exclude) continue;
                    if (coarse[k] < second) second = coarse[k];
                }
                if (second < double.MaxValue && bestScore > 1.0 && second < bestScore + AmbiguityGap)
                    return MatchResult.Fail("匹配存在歧义（已忽略该帧）", bestK, bestScore);
                chosenK = bestK;
                chosenScore = bestScore;
                if (refinedK.Count > 1)
                {
                    int preferred = _lastStep > 0 ? _lastStep : -1;
                    double bestCost = double.MaxValue;
                    for (int i = 0; i < refinedK.Count; i++)
                    {
                        if (refinedS[i] > bestScore + 1.5) continue;
                        int kk = refinedK[i];
                        int newRows = F - kk;
                        double cost = preferred > 0
                            ? Math.Abs(newRows - preferred) + refinedS[i] * 4.0
                            : (F - kk) * 0.01 + refinedS[i] * 4.0;
                        if (cost < bestCost) { bestCost = cost; chosenK = kk; chosenScore = refinedS[i]; }
                    }
                }
            }

            MatchResult r = new MatchResult();
            r.Ok = true;
            r.Prepend = prepend;
            r.Horizontal = horizontal;
            r.K = chosenK;
            r.NewRows = F - chosenK;
            r.FrameStart = fStart;
            r.FrameLen = F;
            r.Score = chosenScore;
            return r;
        }

        /// <summary>
        /// 候选重叠区的 2D 像素平均绝对差校验（0≈完全一致）。strip 为画布端部灰度条带：
        /// 纵向=[行][宽]，横向=[列][高]，覆盖画布坐标 [stripStart, stripStart+stripLen)。
        /// </summary>
        private static double VerifyOverlap(FrameSig frame, int fStart, int k, int F, int C, bool prepend, bool horizontal,
            byte[] strip, int stripLen, int stripCross, int leadSkip, int trailSkip)
        {
            if (strip == null || frame == null || frame.Gray == null || stripLen <= 0 || stripCross <= 0)
                return double.MaxValue;
            int y0, c0, count;
            if (prepend)
            {
                int d = F - k;
                int i0 = Math.Max(0, leadSkip - d);
                int i1 = k - trailSkip;
                count = i1 - i0;
                if (count <= 0) return double.MaxValue;
                y0 = d + i0;
                c0 = i0;
            }
            else
            {
                int y1 = Math.Min(k, F - trailSkip);
                count = y1 - leadSkip;
                if (count <= 0) return double.MaxValue;
                y0 = leadSkip;
                c0 = C - k + leadSkip;
            }
            int stripStart = prepend ? 0 : C - stripLen;
            int frameFull = horizontal ? frame.Width : frame.Height;
            int frameCross = horizontal ? frame.Height : frame.Width;
            if (frameCross != stripCross) return double.MaxValue;
            int inset = Math.Max(1, stripCross * 8 / 100);
            if (inset * 2 >= stripCross) inset = 0;
            int span = stripCross - inset * 2;
            if (span < 8) { inset = 0; span = stripCross; }
            int rowStep = Math.Max(1, count / 96);
            int colStep = Math.Max(1, span / 120);
            long sum = 0;
            int n = 0;
            for (int i = 0; i < count; i += rowStep)
            {
                int canvasIdx = c0 + i;
                int stripIdx = canvasIdx - stripStart;
                if (stripIdx < 0 || stripIdx >= stripLen) continue;
                int frameIdx = fStart + y0 + i;
                if (frameIdx < 0 || frameIdx >= frameFull) continue;
                int cBase = stripIdx * stripCross;
                for (int j = inset; j < inset + span; j += colStep)
                {
                    int fIdx = horizontal
                        ? j * frame.Width + frameIdx
                        : frameIdx * frame.Width + j;
                    int d = frame.Gray[fIdx] - strip[cBase + j];
                    if (d < 0) d = -d;
                    sum += d;
                    n++;
                }
            }
            return n == 0 ? double.MaxValue : (double)sum / n;
        }

        /// <summary>逐行（逐列）比较平均截断 SAD，越小越相似。</summary>
        private static double ScoreSegment(byte[][] fSigs, IList<byte[]> cSigs, int f0, int c0, int count, int step)
        {
            if (count <= 0) return double.MaxValue;
            if (step < 1) step = 1;
            long sum = 0;
            int sampled = 0;
            for (int i = 0; i < count; i += step)
            {
                int d = RowDiff(fSigs[f0 + i], cSigs[c0 + i]);
                if (d > RowDiffCap) d = RowDiffCap;
                sum += d;
                sampled++;
            }
            return sampled == 0 ? double.MaxValue : (double)sum / sampled;
        }

        private static int RowDiff(byte[] a, byte[] b)
        {
            int s = 0;
            for (int i = 0; i < SigSamples; i++)
            {
                int d = a[i] - b[i];
                if (d < 0) d = -d;
                s += d;
            }
            return s / SigSamples;
        }

        /// <summary>裁掉帧首/帧尾的“未绘制/空白”同色带（窗口重绘未完成时新露出的条带）。
        /// 只按滚动方向裁剪（向下裁尾、向上裁头），避免影响正常内容。</summary>
        private static int TrimLeadingUniform(FrameSig sig, bool columns)
        {
            if (sig == null) return 0;
            bool[] u = columns ? sig.ColUniform : sig.RowUniform;
            byte[] mins = columns ? sig.ColMin : sig.RowMin;
            if (u == null || mins == null || u.Length < 24) return 0;
            int limit = Math.Max(8, u.Length / 2);
            int count = 0;
            while (count < limit && u[count] && mins[count] >= 250) count++;
            return count >= 12 ? count : 0;
        }

        private static int TrimTrailingUniform(FrameSig sig, bool columns)
        {
            if (sig == null) return 0;
            bool[] u = columns ? sig.ColUniform : sig.RowUniform;
            byte[] mins = columns ? sig.ColMin : sig.RowMin;
            if (u == null || mins == null || u.Length < 24) return 0;
            int n = u.Length;
            int limit = Math.Max(8, n / 2);
            int count = 0;
            while (count < limit && u[n - 1 - count] && mins[n - 1 - count] >= 250) count++;
            return count >= 12 ? count : 0;
        }

        /// <summary>用相邻帧的公共首/尾签名探测粘性页眉页脚（横向为左右固定栏）。
        /// 只在两帧的有效范围 [start, start+len) 内比较，避免未绘制带干扰。</summary>
        private static void DetectStickyBands(FrameSig prev, int prevStart, int prevLen, FrameSig cur,
            int curStart, int curLen, bool columns, out int lead, out int trail)
        {
            lead = 0;
            trail = 0;
            if (prev == null || cur == null) return;
            byte[][] p = columns ? prev.ColSigs : prev.RowSigs;
            byte[][] c = columns ? cur.ColSigs : cur.RowSigs;
            int pFull = columns ? prev.Width : prev.Height;
            int cFull = columns ? cur.Width : cur.Height;
            if (p == null || c == null || pFull <= 8 || cFull <= 8) return;
            if (prevStart < 0) prevStart = 0;
            if (curStart < 0) curStart = 0;
            if (prevStart + prevLen > pFull) prevLen = pFull - prevStart;
            if (curStart + curLen > cFull) curLen = cFull - curStart;
            if (prevLen <= 8 || curLen <= 8) return;
            int common = Math.Min(prevLen, curLen);
            int maxBand = Math.Max(8, common / 2);
            if (maxBand > 300) maxBand = 300;
            while (lead < maxBand && RowDiff(p[prevStart + lead], c[curStart + lead]) <= 3) lead++;
            while (trail < maxBand &&
                   RowDiff(p[prevStart + prevLen - 1 - trail], c[curStart + curLen - 1 - trail]) <= 3) trail++;
        }

        // ==================================================================
        // 画布：初始化 / 增量扩展
        // ==================================================================

        private bool InitCanvasAndApply(MatchResult m, FrameSig sig, Bitmap prepared)
        {
            bool horizontal = m.Horizontal;
            int cross = horizontal ? _singleFrame.Height : _width;
            int initialLen = horizontal ? _singleFrame.Width : _singleFrame.Height;
            if (cross <= 0 || initialLen <= 0) return false;

            _mode = horizontal ? AxisMode.Horizontal : AxisMode.Vertical;
            _fixedHeight = _singleFrame.Height;

            int maxCap = MaxCapacityForCross(cross);
            long capL = Math.Max(2048L, (long)initialLen * 2 + 1024);
            if (capL > maxCap) capL = maxCap;
            if (capL < initialLen) capL = initialLen;
            _cap = (int)capL;
            _buf = CreateBuffer(horizontal, cross, _cap);
            _start = Math.Max(0, (_cap - initialLen) / 2);
            _len = 0;
            _axisSigs = new List<byte[]>(Math.Min(_cap, MaxDimension + 1024));

            // 首帧入画布
            if (horizontal) BlitHorizontal(_singleFrame, 0, initialLen, _buf, _start);
            else BlitVertical(_singleFrame, 0, initialLen, _buf, _start);
            byte[][] singleSigs = horizontal ? _singleSig.ColSigs : _singleSig.RowSigs;
            for (int i = 0; i < initialLen; i++) _axisSigs.Add(singleSigs[i]);
            _len = initialLen;
            _appended = 1;
            _prevSig = _singleSig;
            _prevSigStart = 0;
            _prevSigLen = initialLen;

            // 释放单帧暂存
            _singleFrame.Dispose();
            _singleFrame = null;
            _singleSig = null;

            return ApplyMatch(m, sig, prepared);
        }

        private bool ApplyMatch(MatchResult m, FrameSig sig, Bitmap prepared)
        {
            bool horizontal = m.Horizontal;
            int rawLen = horizontal ? sig.Width : sig.Height;
            int fStart = Math.Max(0, m.FrameStart);
            int F = m.FrameLen > 0 ? m.FrameLen : rawLen;
            if (fStart + F > rawLen) return false;
            int add = F - m.K;
            int skip = m.Prepend ? Math.Min(Math.Max(0, m.LeadingRaw), Math.Max(0, add - 1)) : 0;
            int rows = add - skip;
            if (rows <= 0)
            {
                _status = "滚动距离过小，已忽略该帧";
                return false;
            }

            int cross = horizontal ? _fixedHeight : _width;
            long newLen = (long)_len + rows;
            if (newLen > MaxDimension)
            {
                _full = true;
                _status = "已达长截图高度上限（" + MaxDimension + "px），已停止拼接";
                return false;
            }
            if ((long)cross * newLen * 4 > MaxBytes)
            {
                _full = true;
                _status = "已达长截图内存上限（约 200MB），已停止拼接";
                return false;
            }

            byte[][] fSigs = horizontal ? sig.ColSigs : sig.RowSigs;
            if (fSigs == null || fSigs.Length < fStart + F) return false;

            if (!m.Prepend)
            {
                EnsureCapacity(0, rows);
                if (horizontal) BlitHorizontal(prepared, fStart + m.K, rows, _buf, _start + _len);
                else BlitVertical(prepared, fStart + m.K, rows, _buf, _start + _len);
                for (int i = 0; i < rows; i++) _axisSigs.Add(fSigs[fStart + m.K + i]);
                _len += rows;
            }
            else
            {
                // 前插：新增内容是有效帧的首部 rows 行（跳过粘性页眉/左栏 skip 行），
                // 即有效帧区间 [skip, F-K)，衔接在画布头部之前。
                EnsureCapacity(rows, 0);
                int srcStart = fStart + skip;
                if (horizontal) BlitHorizontal(prepared, srcStart, rows, _buf, _start - rows);
                else BlitVertical(prepared, srcStart, rows, _buf, _start - rows);
                byte[][] inserted = new byte[rows][];
                for (int i = 0; i < rows; i++) inserted[i] = fSigs[srcStart + i];
                _axisSigs.InsertRange(0, inserted);
                _start -= rows;
                _len += rows;
            }

            _prevSig = sig;
            _prevSigStart = fStart;
            _prevSigLen = F;
            _lastStep = rows;
            _appended++;
            try
            {
                Size sz = ResultSize;
                _status = "已拼接 " + sz.Width + " × " + sz.Height;
            }
            catch
            {
                _status = "已拼接";
            }
            return true;
        }

        private int MaxCapacityForCross(int cross)
        {
            long byMemory = (300L * 1024 * 1024) / Math.Max(1L, (long)cross * 4);
            long byDim = MaxDimension + 4096L;
            return (int)Math.Min(byMemory, byDim);
        }

        private Bitmap CreateBuffer(bool horizontal, int cross, int cap)
        {
            if (horizontal) return new Bitmap(cap, cross, PixelFormat.Format32bppArgb);
            return new Bitmap(cross, cap, PixelFormat.Format32bppArgb);
        }

        private void EnsureCapacity(int before, int after)
        {
            if (_buf != null && _start >= before && _cap - _start - _len >= after) return;
            int cross = _horizontalFixedCross();
            int maxCap = MaxCapacityForCross(cross);
            long need = (long)before + _len + after;
            long capL = Math.Max((long)_cap * 2, need + 1024);
            if (capL > maxCap) capL = maxCap;
            if (capL < need)
            {
                _full = true;
                _status = "已达长截图内存上限（约 200MB），已停止拼接";
                throw new InvalidOperationException("长截图缓冲区容量不足");
            }
            int newCap = (int)capL;
            int newStart = Math.Max(before, newCap / 4);
            int room = newCap - _len - after;
            if (newStart > room) newStart = Math.Max(room, before);
            Bitmap nb = CreateBuffer(_mode == AxisMode.Horizontal, cross, newCap);
            try
            {
                if (_len > 0)
                {
                    if (_mode == AxisMode.Horizontal) BlitHorizontal(_buf, _start, _len, nb, newStart);
                    else BlitVertical(_buf, _start, _len, nb, newStart);
                }
            }
            catch
            {
                nb.Dispose();
                throw;
            }
            if (_buf != null) _buf.Dispose();
            _buf = nb;
            _cap = newCap;
            _start = newStart;
        }

        private int _horizontalFixedCross()
        {
            return _mode == AxisMode.Horizontal ? _fixedHeight : _width;
        }

        // ==================================================================
        // 像素搬运（LockBits 逐行拷贝，保证逐像素精确）
        // ==================================================================

        private static void BlitVertical(Bitmap src, int srcY, int rows, Bitmap dst, int dstY)
        {
            if (rows <= 0) return;
            int srcW = src.Width, dstW = dst.Width;
            int w = Math.Min(srcW, dstW);
            int bytes = w * 4;
            BitmapData sd = null, dd = null;
            try
            {
                sd = src.LockBits(new Rectangle(0, 0, srcW, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                dd = dst.LockBits(new Rectangle(0, 0, dstW, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int r = 0; r < rows; r++)
                {
                    IntPtr sp = IntPtr.Add(sd.Scan0, (srcY + r) * sd.Stride);
                    IntPtr dp = IntPtr.Add(dd.Scan0, (dstY + r) * dd.Stride);
                    CopyBytes(sp, dp, bytes);
                }
            }
            finally
            {
                if (sd != null) src.UnlockBits(sd);
                if (dd != null) dst.UnlockBits(dd);
            }
        }

        private static void BlitHorizontal(Bitmap src, int srcX, int cols, Bitmap dst, int dstX)
        {
            if (cols <= 0) return;
            int h = Math.Min(src.Height, dst.Height);
            int bytes = cols * 4;
            BitmapData sd = null, dd = null;
            try
            {
                sd = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                dd = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++)
                {
                    IntPtr sp = IntPtr.Add(sd.Scan0, y * sd.Stride + srcX * 4);
                    IntPtr dp = IntPtr.Add(dd.Scan0, y * dd.Stride + dstX * 4);
                    CopyBytes(sp, dp, bytes);
                }
            }
            finally
            {
                if (sd != null) src.UnlockBits(sd);
                if (dd != null) dst.UnlockBits(dd);
            }
        }

        private static byte[] _copyScratch;

        private static void CopyBytes(IntPtr src, IntPtr dst, int bytes)
        {
            if (bytes <= 0) return;
            if (bytes <= 1024)
            {
                if (_copyScratch == null || _copyScratch.Length < 1024) _copyScratch = new byte[1024];
                Marshal.Copy(src, _copyScratch, 0, bytes);
                Marshal.Copy(_copyScratch, 0, dst, bytes);
                return;
            }
            byte[] buf = new byte[bytes];
            Marshal.Copy(src, buf, 0, bytes);
            Marshal.Copy(buf, 0, dst, bytes);
        }

        private Bitmap Snapshot()
        {
            if (_len <= 0)
            {
                if (_singleFrame == null) return null;
                return CloneBitmap32(_singleFrame);
            }
            int cross = _mode == AxisMode.Horizontal ? _fixedHeight : _width;
            Bitmap result = _mode == AxisMode.Horizontal
                ? new Bitmap(_len, cross, PixelFormat.Format32bppArgb)
                : new Bitmap(cross, _len, PixelFormat.Format32bppArgb);
            try
            {
                if (_mode == AxisMode.Horizontal) BlitHorizontal(_buf, _start, _len, result, 0);
                else BlitVertical(_buf, _start, _len, result, 0);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static Bitmap CloneBitmap32(Bitmap src)
        {
            Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            try
            {
                BlitVertical(src, 0, src.Height, b, 0);
                return b;
            }
            catch
            {
                b.Dispose();
                throw;
            }
        }

        private static Bitmap ScaleBitmap(Bitmap src, int maxSide)
        {
            int max = Math.Max(src.Width, src.Height);
            if (max <= maxSide) return CloneBitmap32(src);
            float scale = (float)maxSide / max;
            int w = Math.Max(1, (int)Math.Round(src.Width * scale));
            int h = Math.Max(1, (int)Math.Round(src.Height * scale));
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(src, new Rectangle(0, 0, w, h));
                }
                return dst;
            }
            catch
            {
                dst.Dispose();
                throw;
            }
        }

        // ==================================================================
        // 帧预处理 / 灰度签名
        // ==================================================================

        private bool PrepareFrame(Bitmap frame, out Bitmap prepared, out bool own)
        {
            prepared = null;
            own = false;
            if (frame == null || frame.Width <= 0 || frame.Height <= 0) return false;
            int tw = _width;
            bool resize = frame.Width != tw || frame.PixelFormat != PixelFormat.Format32bppArgb;
            int th;
            if (_mode == AxisMode.Horizontal)
            {
                th = _fixedHeight;
                if (frame.Height != _fixedHeight) resize = true;
            }
            else
            {
                th = Math.Max(1, (int)Math.Round((double)frame.Height * tw / frame.Width));
            }
            if (!resize)
            {
                prepared = frame;
                return true;
            }
            Bitmap b = new Bitmap(tw, Math.Max(1, th), PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(frame, new Rectangle(0, 0, tw, Math.Max(1, th)));
                }
                prepared = b;
                own = true;
                return true;
            }
            catch
            {
                b.Dispose();
                throw;
            }
        }

        private FrameSig ComputeSignatures(Bitmap bmp)
        {
            if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return null;
            int w = bmp.Width, h = bmp.Height;
            BitmapData bd = null;
            try
            {
                bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int stride = bd.Stride;
                int total = stride * h;
                byte[] data = new byte[total];
                Marshal.Copy(bd.Scan0, data, 0, total);

                byte[][] rowSigs = new byte[h][];
                bool[] rowUniform = new bool[h];
                byte[] rowMin = new byte[h];
                byte[] gray = new byte[w * h];
                // 采样列内缩 8%，避开目标窗口的滚动条/边框（滚动时滚动条滑块位置会变，但不是内容）
                int insetX = (w * 8) / 100;
                if (insetX < 1) insetX = 1;
                if (insetX * 2 >= w) insetX = 0;
                int spanX = w - insetX * 2;
                for (int y = 0; y < h; y++)
                {
                    byte[] sig = new byte[SigSamples];
                    int row = y * stride;
                    int grayRow = y * w;
                    int min = 255, max = 0;
                    for (int x = 0; x < w; x++)
                    {
                        int o = row + x * 4;
                        gray[grayRow + x] = (byte)((data[o] * 29 + data[o + 1] * 150 + data[o + 2] * 77) >> 8);
                    }
                    for (int j = 0; j < SigSamples; j++)
                    {
                        int x = insetX + ((2 * j + 1) * spanX) / (2 * SigSamples);
                        if (x >= w) x = w - 1;
                        byte v = gray[grayRow + x];
                        sig[j] = v;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    rowSigs[y] = sig;
                    rowUniform[y] = (max - min) <= 2;
                    rowMin[y] = (byte)min;
                }
                byte[][] colSigs = new byte[w][];
                bool[] colUniform = new bool[w];
                byte[] colMin = new byte[w];
                // 采样行内缩 8%，避开窗口/页面固定边缘（横向滚动时横向滚动条同理）
                int insetY = (h * 8) / 100;
                if (insetY < 1) insetY = 1;
                if (insetY * 2 >= h) insetY = 0;
                int spanY = h - insetY * 2;
                for (int x = 0; x < w; x++)
                {
                    byte[] sig = new byte[SigSamples];
                    int col = x * 4;
                    int min = 255, max = 0;
                    for (int j = 0; j < SigSamples; j++)
                    {
                        int y = insetY + ((2 * j + 1) * spanY) / (2 * SigSamples);
                        if (y >= h) y = h - 1;
                        int o = y * stride + col;
                        byte v = (byte)((data[o] * 29 + data[o + 1] * 150 + data[o + 2] * 77) >> 8);
                        sig[j] = v;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    colSigs[x] = sig;
                    colUniform[x] = (max - min) <= 2;
                    colMin[x] = (byte)min;
                }
                FrameSig fs = new FrameSig();
                fs.Width = w;
                fs.Height = h;
                fs.RowSigs = rowSigs;
                fs.ColSigs = colSigs;
                fs.RowUniform = rowUniform;
                fs.ColUniform = colUniform;
                fs.RowMin = rowMin;
                fs.ColMin = colMin;
                fs.Gray = gray;
                return fs;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (bd != null) bmp.UnlockBits(bd);
            }
        }

        private sealed class FrameSig
        {
            public int Width;
            public int Height;
            public byte[][] RowSigs;   // 每行 32 点灰度签名
            public byte[][] ColSigs;   // 每列 32 点灰度签名
            public bool[] RowUniform;  // 该行采样点是否基本同色（用于识别未绘制/空白边缘带）
            public bool[] ColUniform;  // 该列采样点是否基本同色
            public byte[] RowMin;      // 每行采样点最小值（用于判断是否“纯白未绘制带”）
            public byte[] ColMin;      // 每列采样点最小值
            public byte[] Gray;        // 全分辨率灰度（用于候选匹配的 2D 像素校验）
        }

        private sealed class MatchResult
        {
            public bool Ok;
            public bool Prepend;
            public bool Horizontal;
            public int K;
            public int NewRows;
            public int LeadingRaw;
            public int FrameStart;   // 有效帧在原始帧中的起始行/列（跳过未绘制边缘带）
            public int FrameLen;     // 有效帧长度（原始帧高度/宽度减去未绘制边缘带）
            public double Score;
            public string Reason;

            public static MatchResult Fail(string reason)
            {
                MatchResult r = new MatchResult();
                r.Ok = false;
                r.Reason = reason;
                return r;
            }

            public static MatchResult Fail(string reason, int k, double score)
            {
                MatchResult r = Fail(reason);
                r.K = k;
                r.Score = score;
                return r;
            }
        }

        // ==================================================================
        // 离线自检：合成大图 → 切重叠帧 → 还原比对
        // ==================================================================

        /// <summary>离线自检：返回摘要文本，失败时以 FAIL 开头或抛异常。</summary>
        public static string SelfTest()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                int seed = 20261019;

                // ---- 1. 纵向向下 ----
                using (Bitmap big = MakeSynthetic(600, 2200, seed))
                {
                    int frames;
                    double ms;
                    List<int> offs = MakeOffsets(2200, 700, 200);
                    using (Bitmap r = StitchFrames(big, false, 700, offs, out frames, out ms))
                    {
                        Require(frames == offs.Count, "纵向：应拼接 " + offs.Count + " 帧，实际 " + frames);
                        Require(r != null && r.Width == big.Width && r.Height == big.Height,
                            "纵向：结果尺寸 " + (r == null ? "null" : r.Width + "x" + r.Height) + "，期望 " + big.Width + "x" + big.Height);
                        int maxDiff;
                        int diff = ComparePixels(big, r, out maxDiff);
                        Require(diff == 0, "纵向：还原误差 " + diff + " 像素（最大通道差 " + maxDiff + "）");
                        Require(ms < 100.0, string.Format("纵向：平均每帧 {0:F1}ms，超过 100ms 性能目标", ms));
                        sb.Append("纵向 " + frames + " 帧 OK, ");
                    }
                    sb.Append("纵向耗时 " + ms.ToString("F1") + "ms/帧, ");
                }

                // ---- 2. 横向向右 ----
                using (Bitmap big = MakeSynthetic(2200, 600, seed + 1))
                {
                    int frames;
                    double ms;
                    List<int> offs = MakeOffsets(2200, 700, 200);
                    using (Bitmap r = StitchFrames(big, true, 700, offs, out frames, out ms))
                    {
                        Require(frames == offs.Count, "横向：应拼接 " + offs.Count + " 帧，实际 " + frames);
                        Require(r != null && r.Width == big.Width && r.Height == big.Height,
                            "横向：结果尺寸 " + (r == null ? "null" : r.Width + "x" + r.Height) + "，期望 " + big.Width + "x" + big.Height);
                        int maxDiff;
                        int diff = ComparePixels(big, r, out maxDiff);
                        Require(diff == 0, "横向：还原误差 " + diff + " 像素（最大通道差 " + maxDiff + "）");
                        sb.Append("横向 " + frames + " 帧 OK, ");
                    }
                }

                // ---- 3. 纵向反向（向上滚动，前插）----
                using (Bitmap big = MakeSynthetic(600, 2200, seed + 2))
                {
                    int frames;
                    double ms;
                    List<int> offs = MakeOffsets(2200, 700, 200);
                    offs.Reverse();
                    using (Bitmap r = StitchFrames(big, false, 700, offs, out frames, out ms))
                    {
                        Require(frames == offs.Count, "反向：应拼接 " + offs.Count + " 帧，实际 " + frames);
                        int maxDiff;
                        int diff = ComparePixels(big, r, out maxDiff);
                        Require(diff == 0, "反向：还原误差 " + diff + " 像素（最大通道差 " + maxDiff + "）");
                        sb.Append("反向 " + frames + " 帧 OK, ");
                    }
                }

                // ---- 4. 横向反向（向左滚动，前插）----
                using (Bitmap big = MakeSynthetic(2200, 600, seed + 3))
                {
                    int frames;
                    double ms;
                    List<int> offs = MakeOffsets(2200, 700, 200);
                    offs.Reverse();
                    using (Bitmap r = StitchFrames(big, true, 700, offs, out frames, out ms))
                    {
                        Require(frames == offs.Count, "横向反向：应拼接 " + offs.Count + " 帧，实际 " + frames);
                        int maxDiff;
                        int diff = ComparePixels(big, r, out maxDiff);
                        Require(diff == 0, "横向反向：还原误差 " + diff + " 像素（最大通道差 " + maxDiff + "）");
                        sb.Append("横向反向 OK, ");
                    }
                }

                // ---- 5. 跳帧鲁棒性（每次步进 400px，重叠只有 300px）----
                using (Bitmap big = MakeSynthetic(600, 2200, seed + 4))
                {
                    int frames;
                    double ms;
                    List<int> offs = new List<int>();
                    for (int y = 0; y + 700 <= 2200; y += 400) offs.Add(y);
                    if (offs[offs.Count - 1] + 700 < 2200) offs.Add(2200 - 700);
                    using (Bitmap r = StitchFrames(big, false, 700, offs, out frames, out ms))
                    {
                        Require(frames == offs.Count, "跳帧：应拼接 " + offs.Count + " 帧，实际 " + frames);
                        int maxDiff;
                        int diff = ComparePixels(big, r, out maxDiff);
                        Require(diff == 0, "跳帧：还原误差 " + diff + " 像素");
                        sb.Append("跳帧 " + frames + " 帧 OK, ");
                    }
                }

                // ---- 6. 粘性页眉（固定工具栏）容忍 ----
                using (Bitmap big = MakeSynthetic(600, 2200, seed + 5))
                using (Bitmap header = MakeSynthetic(600, 56, seed + 6))
                {
                    using (Bitmap expected = CloneBitmap32(big))
                    {
                        using (Graphics g = Graphics.FromImage(expected))
                        {
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.DrawImage(header, new Rectangle(0, 0, header.Width, header.Height));
                        }
                        int frames;
                        double ms;
                        List<int> offs = MakeOffsets(2200, 700, 200);
                        using (Bitmap r = StitchWithStickyHeader(big, header, offs, out frames, out ms))
                        {
                            Require(frames == offs.Count, "粘性页眉：应拼接 " + offs.Count + " 帧，实际 " + frames);
                            int maxDiff;
                            int diff = ComparePixels(expected, r, out maxDiff);
                            Require(diff == 0, "粘性页眉：还原误差 " + diff + " 像素（最大通道差 " + maxDiff + "）");
                            sb.Append("粘性页眉 " + frames + " 帧 OK, ");
                        }
                    }
                }

                // ---- 7. 重复帧 / 无重叠帧忽略，之后仍能继续 ----
                using (Bitmap big = MakeSynthetic(600, 2200, seed + 7))
                using (Bitmap other = MakeSynthetic(600, 2200, seed + 8))
                using (LongShotStitcher st = new LongShotStitcher(600))
                {
                    using (Bitmap f0 = Crop(big, 0, 0, 600, 700)) Require(st.AddFrame(f0), "重复帧：首帧失败");
                    using (Bitmap f1 = Crop(big, 0, 200, 600, 700)) Require(st.AddFrame(f1), "重复帧：第 2 帧失败");
                    using (Bitmap dup = Crop(big, 0, 200, 600, 700))
                        Require(!st.AddFrame(dup), "重复帧：相同画面未被忽略");
                    Require(st.AppendedFrames == 2, "重复帧：帧数应保持 2，实际 " + st.AppendedFrames);
                    using (Bitmap foreign = Crop(other, 0, 900, 600, 700))
                        Require(!st.AddFrame(foreign), "无重叠帧：未被忽略");
                    Require(st.AppendedFrames == 2, "无重叠帧：帧数应保持 2，实际 " + st.AppendedFrames);
                    Size before = st.ResultSize;
                    foreach (int y in new int[] { 400, 600, 800, 1000, 1200, 1400, 1500 })
                    {
                        using (Bitmap f = Crop(big, 0, y, 600, 700))
                            Require(st.AddFrame(f), "忽略后恢复：偏移 " + y + " 帧未被接受");
                    }
                    using (Bitmap r = st.TakeResult())
                    {
                        Require(r.Width == 600 && r.Height == 2200, "忽略后恢复：结果尺寸 " + r.Width + "x" + r.Height);
                        int maxDiff;
                        int diff = ComparePixels(big, r, out maxDiff);
                        Require(diff == 0, "忽略后恢复：还原误差 " + diff + " 像素");
                    }
                    sb.Append("重复帧/无重叠忽略 OK, ");
                }

                // ---- 8. 尺寸上限保护 ----
                using (LongShotStitcher st = new LongShotStitcher(600))
                {
                    Require(st.IsEmpty && st.Canvas == null, "首帧前：Canvas 应为 null");
                    using (Bitmap f0 = MakeSynthetic(600, 700, seed + 9))
                    {
                        Require(st.AddFrame(f0), "上限：首帧失败");
                        Require(st.AppendedFrames == 1, "上限：首帧后帧数应为 1");
                        Require(st.ResultSize.Width == 600 && st.ResultSize.Height == 700, "上限：单帧结果尺寸错误");
                    }
                }

                // ---- 9. 窗口重绘滞后：中间帧底部有未绘制白色带（向下滚动）----
                using (Bitmap big = MakeSynthetic(600, 2200, seed + 10))
                {
                    int frames;
                    double ms;
                    List<int> offs = new List<int> { 0, 250, 500, 750, 1000, 1500 };
                    using (Bitmap r = StitchPartialPaint(big, offs, 700, 120, false, out frames, out ms))
                    {
                        Require(frames == offs.Count, "重绘滞后·向下：应拼接 " + offs.Count + " 帧，实际 " + frames);
                        int maxDiff;
                        int diff = ComparePixels(big, r, out maxDiff);
                        Require(diff == 0, "重绘滞后·向下：还原误差 " + diff + " 像素（最大通道差 " + maxDiff + "）");
                        sb.Append("重绘滞后·向下 " + frames + " 帧 OK, ");
                    }
                }

                // ---- 10. 窗口重绘滞后：中间帧顶部有未绘制白色带（向上滚动/前插）----
                using (Bitmap big = MakeSynthetic(600, 2200, seed + 11))
                {
                    int frames;
                    double ms;
                    List<int> offs = new List<int> { 1500, 1000, 750, 500, 250, 0 };
                    using (Bitmap r = StitchPartialPaint(big, offs, 700, 120, true, out frames, out ms))
                    {
                        Require(frames == offs.Count, "重绘滞后·向上：应拼接 " + offs.Count + " 帧，实际 " + frames);
                        int maxDiff;
                        int diff = ComparePixels(big, r, out maxDiff);
                        Require(diff == 0, "重绘滞后·向上：还原误差 " + diff + " 像素（最大通道差 " + maxDiff + "）");
                        sb.Append("重绘滞后·向上 " + frames + " 帧 OK, ");
                    }
                }

                sb.Append("还原误差 0");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "FAIL: " + ex.Message;
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static Bitmap StitchFrames(Bitmap big, bool horizontal, int frameSize, IList<int> offsets,
            out int frames, out double msPerFrame)
        {
            int fw = horizontal ? frameSize : big.Width;
            int fh = horizontal ? big.Height : frameSize;
            frames = 0;
            msPerFrame = 0;
            using (LongShotStitcher st = new LongShotStitcher(fw))
            {
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < offsets.Count; i++)
                {
                    int off = offsets[i];
                    Rectangle rect = horizontal
                        ? new Rectangle(off, 0, fw, fh)
                        : new Rectangle(0, off, fw, fh);
                    using (Bitmap frame = big.Clone(rect, PixelFormat.Format32bppArgb))
                    {
                        if (st.AddFrame(frame)) frames++;
                    }
                }
                sw.Stop();
                msPerFrame = frames > 0 ? sw.Elapsed.TotalMilliseconds / frames : 0;
                return st.TakeResult();
            }
        }

        /// <summary>模拟“窗口重绘滞后”：中间帧的边缘一条带尚未绘制（纯白），验证拼接器按方向裁剪而不是拼进空白。</summary>
        private static Bitmap StitchPartialPaint(Bitmap big, IList<int> offsets, int frameH, int blankRows,
            bool blankTop, out int frames, out double msPerFrame)
        {
            frames = 0;
            msPerFrame = 0;
            using (LongShotStitcher st = new LongShotStitcher(big.Width))
            {
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < offsets.Count; i++)
                {
                    using (Bitmap frame = Crop(big, 0, offsets[i], big.Width, frameH))
                    {
                        // 首帧与末帧完整（开始截图/结束时窗口已重绘完成），中间帧模拟重绘滞后
                        bool partial = i > 0 && i < offsets.Count - 1;
                        if (partial)
                        {
                            using (Graphics g = Graphics.FromImage(frame))
                            using (SolidBrush white = new SolidBrush(Color.White))
                            {
                                Rectangle band = blankTop
                                    ? new Rectangle(0, 0, frame.Width, blankRows)
                                    : new Rectangle(0, frame.Height - blankRows, frame.Width, blankRows);
                                g.FillRectangle(white, band);
                            }
                        }
                        if (st.AddFrame(frame)) frames++;
                    }
                }
                sw.Stop();
                msPerFrame = frames > 0 ? sw.Elapsed.TotalMilliseconds / frames : 0;
                return st.TakeResult();
            }
        }

        private static Bitmap StitchWithStickyHeader(Bitmap big, Bitmap header, IList<int> offsets,
            out int frames, out double msPerFrame)
        {
            frames = 0;
            msPerFrame = 0;
            using (LongShotStitcher st = new LongShotStitcher(big.Width))
            {
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < offsets.Count; i++)
                {
                    using (Bitmap frame = Crop(big, 0, offsets[i], big.Width, 700))
                    {
                        using (Graphics g = Graphics.FromImage(frame))
                        {
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.DrawImage(header, new Rectangle(0, 0, header.Width, header.Height));
                        }
                        if (st.AddFrame(frame)) frames++;
                    }
                }
                sw.Stop();
                msPerFrame = frames > 0 ? sw.Elapsed.TotalMilliseconds / frames : 0;
                return st.TakeResult();
            }
        }

        private static List<int> MakeOffsets(int total, int frameSize, int step)
        {
            List<int> list = new List<int>();
            for (int p = 0; p + frameSize <= total; p += step) list.Add(p);
            if (list.Count == 0) list.Add(0);
            if (list[list.Count - 1] + frameSize < total) list.Add(total - frameSize);
            return list;
        }

        private static Bitmap Crop(Bitmap src, int x, int y, int w, int h)
        {
            return src.Clone(new Rectangle(x, y, w, h), PixelFormat.Format32bppArgb);
        }

        /// <summary>合成一张带随机纹理、文字状斑点、条带与渐变的测试大图（保证每行可区分）。</summary>
        private static Bitmap MakeSynthetic(int w, int h, int seed)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = null;
            try
            {
                bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                int stride = bd.Stride;
                byte[] data = new byte[stride * h];
                Random rnd = new Random(seed);
                for (int y = 0; y < h; y++)
                {
                    int bandType = (y / 37) % 6;
                    int baseVal = 30 + ((y * 13) % 170);
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int v;
                        switch (bandType)
                        {
                            case 0:
                                v = baseVal + rnd.Next(0, 70);
                                break;
                            case 1:
                                v = (x % 23 < 14) ? baseVal - 30 + rnd.Next(0, 30) : 246;
                                break;
                            case 2:
                                v = 240 - (int)(Math.Abs(Math.Sin(x * 0.045 + y * 0.021)) * 90) + rnd.Next(0, 40);
                                break;
                            case 3:
                                v = ((x / 9 + y / 11) % 2 == 0) ? baseVal : 250 - rnd.Next(0, 36);
                                break;
                            case 4:
                                v = baseVal + ((x * 31 + y * 17) % 60);
                                break;
                            default:
                                v = 20 + rnd.Next(0, 120);
                                break;
                        }
                        if (v < 0) v = 0;
                        else if (v > 255) v = 255;
                        int o = row + x * 4;
                        data[o] = (byte)v;
                        data[o + 1] = (byte)v;
                        data[o + 2] = (byte)v;
                        data[o + 3] = 255;
                    }
                }
                Marshal.Copy(data, 0, bd.Scan0, data.Length);
            }
            finally
            {
                if (bd != null) bmp.UnlockBits(bd);
            }
            return bmp;
        }

        /// <summary>逐像素比较（忽略 alpha 全 255 的差异，仅比较 RGB）；返回不同像素数。</summary>
        private static int ComparePixels(Bitmap a, Bitmap b, out int maxDiff)
        {
            maxDiff = 255;
            if (a == null || b == null) return int.MaxValue;
            if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;
            maxDiff = 0;
            BitmapData da = null, db = null;
            try
            {
                da = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                db = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int stride = da.Stride;
                int total = stride * a.Height;
                byte[] ba = new byte[total];
                byte[] bb = new byte[total];
                Marshal.Copy(da.Scan0, ba, 0, total);
                Marshal.Copy(db.Scan0, bb, 0, total);
                int diff = 0;
                int bytes = b.Width * 4;
                for (int y = 0; y < a.Height; y++)
                {
                    int row = y * stride;
                    for (int i = 0; i < bytes; i += 4)
                    {
                        int o = row + i;
                        int d0 = ba[o] - bb[o];
                        int d1 = ba[o + 1] - bb[o + 1];
                        int d2 = ba[o + 2] - bb[o + 2];
                        if (d0 < 0) d0 = -d0;
                        if (d1 < 0) d1 = -d1;
                        if (d2 < 0) d2 = -d2;
                        int m = Math.Max(d0, Math.Max(d1, d2));
                        if (m != 0)
                        {
                            diff++;
                            if (m > maxDiff) maxDiff = m;
                        }
                    }
                }
                return diff;
            }
            finally
            {
                if (da != null) a.UnlockBits(da);
                if (db != null) b.UnlockBits(db);
            }
        }
    }
}
