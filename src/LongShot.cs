using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 长截图（滚动截图）。由 OverlayForm 在用户点击“长截图”按钮后调用：
    ///   overlay.Hide();
    ///   Bitmap result = LongShot.Run(selectionScreenRect, ImageUtil.Capture);
    ///   overlay 关闭或恢复。
    /// Run 显示实时预览、捕获段、绿色边框，处理鼠标滚轮/键盘：
    ///   - 用户滚动窗口内容时自动抓帧拼接（纵向优先，也支持横向）；
    ///   - Enter / 点击“完成”结束并返回结果图；
    ///   - Esc / 右键（选区内）/ 点击“取消”取消，返回 null。
    /// capture 回调返回指定屏幕矩形的最新画面（物理像素）。
    ///
    /// 交互实现要点（避免干扰用户滚动目标窗口）：
    ///   * 绿色边框采用选区外的环形窗口；预览放在选区外或从捕获中排除，
    ///     均为 WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW，不抢目标窗口的焦点；
    ///   * 通过 WH_MOUSE_LL 低层鼠标钩子感知目标窗口滚轮和实际拖动，
    ///     WH_KEYBOARD_LL 感知目标窗口的 PageDown/空格/方向键；滚动信号合并后抓帧；
    ///   * 只把“内容确实变化”的帧交给拼接器，拼接器再按重叠阈值决定接受/忽略；
    ///   * 线程内用 Application.DoEvents 消息泵驱动，结束后确保钩子注销、辅助窗口全部释放。
    /// 负责队友：longshot
    /// </summary>
    public static class LongShot
    {
        /// <summary>启动交互式滚动截图。返回拼接结果（调用方释放）；取消返回 null。</summary>
        public static Bitmap Run(Rectangle region, Func<Rectangle, Bitmap> capture)
        {
            if (capture == null)
            {
                Toast.Show("长截图失败：缺少画面捕获回调");
                return null;
            }
            if (region.Width <= 0 || region.Height <= 0)
            {
                Toast.Show("长截图失败：选区无效");
                return null;
            }
            try
            {
                using (LongShotSession session = new LongShotSession(region, capture))
                {
                    return session.Execute();
                }
            }
            catch (Exception ex)
            {
                Log.Write("长截图异常: " + ex);
                Toast.Show("长截图失败：" + ex.Message);
                return null;
            }
        }
    }

    /// <summary>一次长截图交互会话：管理辅助窗口、钩子、定时器、拼接状态与结果。</summary>
    internal sealed class LongShotSession : IDisposable
    {
        // ---- 时间参数（毫秒）----
        private const int CaptureThrottleMs = 220; // 活跃捕获期内两次抓帧的最小间隔
        private const int ScrollSettleMs = 120;    // 等待目标窗口处理滚动并重绘
        private const int PauseAfterMs = 1500;     // 无任何滚动信号多久后进入“已暂停捕获”
        private const int PreviewIntervalMs = 280; // 缩略图刷新间隔
        private const int MaxFrames = 400;         // 最多拼接帧数保护

        // ---- 钩子常量 ----
        private const int WH_MOUSE_LL = 14;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_PRIOR = 0x21;
        private const int VK_NEXT = 0x22;
        private const int VK_END = 0x23;
        private const int VK_HOME = 0x24;
        private const int VK_LEFT = 0x25;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;

        private readonly Rectangle _region;
        private readonly Func<Rectangle, Bitmap> _capture;
        private float _scale = 1f;

        private LongShotStitcher _stitcher;
        private LongShotJournal _journal;
        private System.Windows.Forms.Timer _timer;

        private LongShotBar _bar;
        private LongShotBorder _border;
        private bool _manualPaused;

        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private HookProc _mouseProc;
        private HookProc _keyboardProc;

        private bool _finished;
        private bool _cancelRequested;
        private bool _finishRequested;
        private bool _cancelled;
        private bool _autoFull;
        private int _lastCaptureTick;
        private byte[] _lastThumb;
        private Bitmap _preview;
        private int _lastPreviewTick;
        private string _statusText = "";
        private int _statusTick;
        private bool _disposed;
        private bool _capturePending;
        private int _pendingSinceTick;
        private bool _captureInProgress;
        private IntPtr _targetWindow;
        private Point _lastDragPoint;

        /// <summary>捕获阶段：Idle=还没滚动；Active=最近有滚动信号，按节流抓帧；Paused=1.5s 无信号，绝不抓帧。</summary>
        private enum CapturePhase { Idle, Active, Paused }
        private CapturePhase _phase = CapturePhase.Idle;
        private int _lastScrollSignalTick;   // 最近一次滚动/拖拽信号（0=从未）
        private bool _leftDown;
        private bool _middleDown;

        private void SignalScroll()
        {
            if (_finished || _cancelRequested || _finishRequested || _manualPaused) return;
            _lastScrollSignalTick = Environment.TickCount;
            if (!_capturePending) _pendingSinceTick = _lastScrollSignalTick;
            _capturePending = true;
            if (_phase != CapturePhase.Active)
            {
                bool wasPaused = _phase == CapturePhase.Paused;
                _phase = CapturePhase.Active;
                _statusText = "";
                Dbg(wasPaused ? "检测到滚动信号，恢复捕获" : "检测到滚动信号，开始捕获");
                UpdateBar(false, _lastScrollSignalTick);
            }
        }

        // ---- 诊断（环境变量 XJ_LONGSHOT_DEBUG=1 / XJ_LONGSHOT_DUMP=<目录>）----
        private static readonly bool DebugEnabled = Environment.GetEnvironmentVariable("XJ_LONGSHOT_DEBUG") == "1";
        private string _dumpDir;
        private int _dumpIndex;

        private static void Dbg(string message)
        {
            if (!DebugEnabled) return;
            try { Log.Write("[longshot] " + message); } catch { }
        }

        public LongShotSession(Rectangle region, Func<Rectangle, Bitmap> capture)
        {
            _region = region;
            _capture = capture;
        }

        // ==================================================================
        // 主流程
        // ==================================================================

        public Bitmap Execute()
        {
            try
            {
                Point center = new Point(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
                _scale = DpiUtil.ScaleAtPoint(center);
                if (_scale <= 0.5f || _scale > 4f) _scale = 1f;

                string dumpDir = Environment.GetEnvironmentVariable("XJ_LONGSHOT_DUMP");
                if (!string.IsNullOrEmpty(dumpDir))
                {
                    try
                    {
                        Directory.CreateDirectory(dumpDir);
                        _dumpDir = dumpDir;
                        Dbg("启用抓帧转储: " + dumpDir);
                    }
                    catch (Exception ex) { Log.Write("长截图转储目录创建失败: " + ex.Message); }
                }

                // 清掉可能残留的 Toast，避免被拍进帧里
                try { Toast.HideCurrent(); } catch { }
                Application.DoEvents();
                Thread.Sleep(60);
                Application.DoEvents();

                _targetWindow = RootWindowAt(center);
                _journal = new LongShotJournal(Math.Max(1, _region.Width));
                _stitcher = _journal.Stitcher;
                _timer = new System.Windows.Forms.Timer();
                _timer.Interval = 60;
                _timer.Tick += OnTimerTick;

                // 首帧：先抓图，再显示辅助窗口，保证画面干净
                using (Bitmap first = CaptureRegion())
                {
                    if (first == null)
                    {
                        Toast.Show("长截图失败：无法捕获选区画面");
                        return null;
                    }
                    if (!_journal.AddFrame(first))
                    {
                        Toast.Show("长截图失败：" + _stitcher.Status);
                        return null;
                    }
                    _lastThumb = ComputeThumb(first);
                }
                _lastCaptureTick = Environment.TickCount;

                CreateBorderStrips();
                CreateBar();
                InstallHooks();
                _timer.Start();
                UpdateBar(true, Environment.TickCount);

                Log.Write("长截图开始: 选区 " + _region + "，缩放 " + _scale);

                // 消息泵（不使用嵌套 Application.Run / ShowDialog，避免抢焦点与二次消息循环异常）
                while (!_finished)
                {
                    Application.DoEvents();
                    if (_cancelRequested)
                    {
                        _cancelled = true;
                        break;
                    }
                    if (_finishRequested) break;
                    Thread.Sleep(3);
                }

                // 完成时若仍处于活跃捕获窗口内，安全冲刷最后一帧；
                // 若已进入「暂停捕获」，绝不补抓，保证结果高度 == 暂停时进度。
                if (_finishRequested && !_cancelled) FlushPendingFrame();
                if (_cancelRequested) _cancelled = true;

                _finished = true;
                if (_timer != null) _timer.Stop();
                RemoveAuxWindows();
                UninstallHooks();

                if (_cancelled)
                {
                    Log.Write("长截图已取消，已拼接 " + (_stitcher != null ? _stitcher.AppendedFrames : 0) + " 帧");
                    Toast.Show("已取消长截图");
                    return null;
                }
                return BuildResult();
            }
            catch (Exception ex)
            {
                Log.Write("长截图交互异常: " + ex);
                Toast.Show("长截图失败：" + ex.Message);
                return null;
            }
            finally
            {
                _finished = true;
                if (_timer != null)
                {
                    try { _timer.Stop(); } catch { }
                    try { _timer.Dispose(); } catch { }
                    _timer = null;
                }
                RemoveAuxWindows();
                UninstallHooks();
            }
        }

        private Bitmap BuildResult()
        {
            if (_stitcher == null) return null;
            try
            {
                if (_autoFull) Toast.Show("长截图已达上限，已自动结束");

                if (_stitcher.AppendedFrames <= 1)
                {
                    // 结束动作不能重新采集画面，否则会把暂停后变化的内容带入结果。
                    Bitmap single = _stitcher.TakeResult();
                    if (single != null) Toast.Show("未拼接到新增内容，已返回首帧画面");
                    return single;
                }

                Bitmap result = _stitcher.TakeResult();
                if (result == null)
                {
                    Toast.Show("长截图失败：未获得有效画面");
                    return null;
                }
                Log.Write("长截图完成: " + _stitcher.AppendedFrames + " 帧, " + result.Width + " × " + result.Height);
                return result;
            }
            catch (Exception ex)
            {
                Log.Write("长截图生成结果失败: " + ex);
                Toast.Show("长截图失败：" + ex.Message);
                return null;
            }
        }

        // ==================================================================
        // 抓帧 + 拼接 + 进度
        // ==================================================================

        private Bitmap CaptureRegion()
        {
            try
            {
                // 浮窗始终在选区外，或通过捕获排除显示；抓帧不改变窗口可见性。
                Application.DoEvents();
                if (_manualPaused || _cancelRequested || _finished) return null;
                return _capture(_region);
            }
            catch (Exception ex)
            {
                Log.Write("长截图抓帧失败: " + ex.Message);
                return null;
            }
        }

        private void ToggleManualPause()
        {
            _manualPaused = !_manualPaused;
            _capturePending = false;
            _phase = _manualPaused ? CapturePhase.Paused : CapturePhase.Idle;
            _statusText = _manualPaused ? "已暂停；可检查接缝、撤回末段" : "已继续；滚回最后保留的画面后再滚动";
            _statusTick = Environment.TickCount;
            UpdateBar(true, _statusTick);
        }

        private void ChangeLastSegment(bool redo)
        {
            if (_journal == null || _captureInProgress) return;
            _manualPaused = true;
            _capturePending = false;
            _phase = CapturePhase.Paused;
            bool changed = redo ? _journal.Redo() : _journal.Undo();
            _stitcher = _journal.Stitcher;
            _lastThumb = null;
            _statusText = changed ? (redo ? "已恢复末段" : "已撤回末段；可恢复，或滚回后继续重试") : "没有可调整的末段";
            _statusTick = Environment.TickCount;
            _lastPreviewTick = unchecked(_statusTick - PreviewIntervalMs);
            UpdateBar(true, _statusTick);
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_finished || _cancelRequested || _finishRequested || _captureInProgress || _manualPaused) return;
            try
            {
                int now = Environment.TickCount;
                if (_phase == CapturePhase.Active)
                {
                    if ((now - _lastScrollSignalTick) >= PauseAfterMs)
                    {
                        EnterPaused(now);
                        return;
                    }
                    // 每批输入只消费一次。按住鼠标和画面动画都不能自行续订抓帧。
                    if (_capturePending && (now - _lastCaptureTick) >= CaptureThrottleMs &&
                        ((now - _lastScrollSignalTick) >= ScrollSettleMs ||
                         (now - _pendingSinceTick) >= CaptureThrottleMs))
                        CaptureAndStitch(now);
                }
                // Idle / Paused：无滚动信号，绝不抓帧、绝不 AddFrame
            }
            catch (Exception ex)
            {
                Log.Write("长截图定时器异常: " + ex.Message);
            }
        }

        /// <summary>无滚动信号超过 PauseAfterMs：进入“已暂停捕获”，记录当前进度供验收核对。</summary>
        private void EnterPaused(int now)
        {
            _phase = CapturePhase.Paused;
            _capturePending = false;
            _statusText = "";
            Size size = Size.Empty;
            int frames = 0;
            try
            {
                if (_stitcher != null)
                {
                    size = _stitcher.ResultSize;
                    frames = _stitcher.AppendedFrames;
                }
            }
            catch { }
            try
            {
                Log.Write("长截图暂停捕获: 已拼接 " + size.Width + " × " + size.Height +
                    "，帧数 " + frames + "，无滚动信号 " + (now - _lastScrollSignalTick) + "ms");
            }
            catch { }
            Dbg("进入暂停捕获: 画布 " + size + " 帧数=" + frames);
            UpdateBar(true, now);
        }

        /// <summary>完成前：仅当仍在滚动信号活跃窗口内才补抓一帧；暂停状态不抓（保证高度不增长）。</summary>
        private void FlushPendingFrame()
        {
            try
            {
                if (_cancelRequested || _captureInProgress || _manualPaused || !_capturePending || _phase != CapturePhase.Active) return;
                int now = Environment.TickCount;
                if ((now - _lastScrollSignalTick) >= PauseAfterMs) return;
                // 完成可能紧接最后一次滚轮，等待其重绘；期间仍处理取消消息。
                int remaining = ScrollSettleMs - (now - _lastScrollSignalTick);
                if (remaining > 0)
                {
                    Thread.Sleep(remaining);
                    Application.DoEvents();
                    if (_cancelRequested || _finished) return;
                    now = Environment.TickCount;
                }
                Dbg("完成前冲刷最后一帧");
                CaptureAndStitch(now);
            }
            catch (Exception ex)
            {
                Log.Write("长截图完成前抓帧失败: " + ex.Message);
            }
        }

        private void CaptureAndStitch(int now)
        {
            if (_finished || _cancelRequested || _captureInProgress || !_capturePending ||
                _phase != CapturePhase.Active) return;
            // CaptureRegion 会泵送窗口消息。先消费本批信号并阻止重入，期间的新滚动留给下一批。
            _captureInProgress = true;
            _capturePending = false;
            Bitmap frame = null;
            try
            {
                frame = CaptureRegion();
                _lastCaptureTick = Environment.TickCount;
                if (frame == null || _cancelRequested || _finished || _manualPaused) return;
                int dumpNo = -1;
                if (_dumpDir != null)
                {
                    dumpNo = _dumpIndex++;
                    try { frame.Save(Path.Combine(_dumpDir, string.Format("frame_{0:00}.png", dumpNo)), ImageFormat.Png); }
                    catch { }
                }
                byte[] thumb = ComputeThumb(frame);
                if (_lastThumb != null && !ThumbChanged(_lastThumb, thumb))
                {
                    Dbg("帧#" + dumpNo + " 内容未变化，跳过");
                    return; // 内容没有变化（例如光标闪烁/动画），不交给拼接器
                }
                bool added = _journal != null ? _journal.AddFrame(frame) : _stitcher.AddFrame(frame);
                Dbg("帧#" + dumpNo + " AddFrame=" + added + " 状态=" + _stitcher.Status + " 画布=" + _stitcher.ResultSize);
                if (added)
                {
                    _lastThumb = thumb;
                    _statusText = "";
                    UpdateBar(true, now);
                    if (_stitcher.IsFull)
                    {
                        _autoFull = true;
                        _finishRequested = true;
                    }
                    else if (_stitcher.AppendedFrames >= MaxFrames)
                    {
                        _autoFull = true;
                        _statusText = "已拼接 " + MaxFrames + " 帧，自动结束";
                        _finishRequested = true;
                    }
                }
                else
                {
                    // 明确停在最后已确认的接缝，用户恢复后可滚回重试。
                    _manualPaused = true;
                    _capturePending = false;
                    _phase = CapturePhase.Paused;
                    _statusText = _journal != null && _journal.HistoryFull
                        ? "原帧历史达到内存上限；请完成本次长截图"
                        : "该帧未拼接，已暂停；滚回保留画面后继续重试";
                    _statusTick = now;
                    UpdateBar(false, now);
                }
            }
            finally
            {
                if (frame != null) frame.Dispose();
                _captureInProgress = false;
            }
        }

        private void UpdateBar(bool refreshPreview, int now)
        {
            if (_bar == null || _bar.IsDisposed) return;
            Size size;
            try { size = _stitcher.ResultSize; }
            catch { size = Size.Empty; }
            if (refreshPreview && (now - _lastPreviewTick) >= PreviewIntervalMs)
            {
                _lastPreviewTick = now;
                try
                {
                    Bitmap p = _stitcher.CreatePreview(1000);
                    Bitmap old = _preview;
                    _preview = p;
                    if (old != null) old.Dispose();
                }
                catch { }
            }
            string status;
            if (_manualPaused)
                status = string.IsNullOrEmpty(_statusText) ? "已暂停；点击继续后才会捕获" : _statusText;
            else if (_phase == CapturePhase.Paused)
                status = "已暂停捕获，滚动继续拼接 / Enter 完成";
            else if (_phase == CapturePhase.Idle)
                status = "滚动目标窗口开始截取（Enter 完成 / Esc 取消）";
            else if (!string.IsNullOrEmpty(_statusText) && (now - _statusTick) < 2500)
                status = _statusText;
            else
                status = "";
            try { _bar.SetStatus(size, _preview, status, _manualPaused,
                _journal != null ? _journal.SegmentSizes() : new Size[0],
                _journal != null ? _journal.SegmentBounds() : new Rectangle[0],
                _journal != null && _journal.CanUndo, _journal != null && _journal.CanRedo); } catch { }
        }

        /// <summary>32×32 灰度缩略签名，用于“内容是否变化”的快速判断。</summary>
        private static byte[] ComputeThumb(Bitmap bmp)
        {
            byte[] result = new byte[32 * 32];
            try
            {
                using (Bitmap small = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.Bilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(bmp, new Rectangle(0, 0, 32, 32));
                    }
                    System.Drawing.Imaging.BitmapData bd = small.LockBits(
                        new Rectangle(0, 0, 32, 32), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        byte[] data = new byte[bd.Stride * 32];
                        Marshal.Copy(bd.Scan0, data, 0, data.Length);
                        for (int y = 0; y < 32; y++)
                        {
                            for (int x = 0; x < 32; x++)
                            {
                                int o = y * bd.Stride + x * 4;
                                result[y * 32 + x] = (byte)((data[o] * 29 + data[o + 1] * 150 + data[o + 2] * 77) >> 8);
                            }
                        }
                    }
                    finally
                    {
                        small.UnlockBits(bd);
                    }
                }
            }
            catch
            {
                return null;
            }
            return result;
        }

        private static bool ThumbChanged(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return true;
            int changed = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int d = a[i] - b[i];
                if (d < 0) d = -d;
                if (d > 8)
                {
                    changed++;
                    if (changed >= 2) return true;
                }
            }
            return false;
        }

        // ==================================================================
        // 辅助窗口：选区外侧绿色边框 + 控制条
        // ==================================================================

        private void CreateBorderStrips()
        {
            try
            {
                int t = Math.Max(2, DpiUtil.Scale(2, _scale)); // 边框线宽（物理像素）
                // 用“一个环形 Region 的置顶点击穿透窗口”画整个边框：
                // 环形区域严格位于选区外侧（中间是真正的空洞，不属于该窗口），
                // 既不会遮挡/污染选区画面，也避免细长窗口被系统最小窗口尺寸撑大而侵入选区。
                Rectangle outer = new Rectangle(_region.X - t, _region.Y - t,
                    _region.Width + t * 2, _region.Height + t * 2);
                _border = new LongShotBorder(outer, _region, t);
                _border.Show();
                Application.DoEvents();
                Native.RECT rc;
                string actual = Native.GetWindowRect(_border.Handle, out rc)
                    ? (rc.ToRectangle().ToString() + " visible=" + _border.Visible)
                    : "GetWindowRect 失败";
                Log.Write("长截图边框窗口: 请求 " + outer + " 实际 " + actual + "（环形，线宽 " + t + "px，空洞=" + _region + "）");
            }
            catch (Exception ex)
            {
                Log.Write("长截图边框窗口创建失败: " + ex.Message);
                if (_border != null) { try { _border.Dispose(); } catch { } _border = null; }
            }
        }

        private void CreateBar()
        {
            try
            {
                Rectangle work = Screen.FromRectangle(_region).WorkingArea;
                float previewScale = Math.Min(_scale, Math.Min(work.Width / 480f, work.Height / 600f));
                Rectangle placement = Rectangle.Empty;
                // 预览可能放到另一块显示器，字体按实际放置屏幕缩放。
                foreach (Rectangle area in DisplayGeometry.WorkingAreas())
                {
                    float candidateScale = Math.Max(0.7f, Math.Min(DpiUtil.ScaleAtPoint(
                        new Point(area.X + area.Width / 2, area.Y + area.Height / 2)),
                        Math.Min(area.Width / 480f, area.Height / 600f)));
                    Size candidateSize = new Size((int)Math.Round(460 * candidateScale), (int)Math.Round(580 * candidateScale));
                    placement = DisplayGeometry.PlaceFloating(_region, candidateSize, new[] { area },
                        DpiUtil.Scale(10, candidateScale), true);
                    if (!placement.IsEmpty) { previewScale = candidateScale; break; }
                }
                _bar = new LongShotBar(previewScale,
                    delegate { _finishRequested = true; },
                    delegate { _cancelRequested = true; }, ToggleManualPause,
                    delegate { ChangeLastSegment(false); }, delegate { ChangeLastSegment(true); },
                    delegate { return _stitcher.TakeResult(); });
                int gap = DpiUtil.Scale(10, _scale);
                if (placement.IsEmpty)
                {
                    placement = DisplayGeometry.PlaceFloating(_region, _bar.Size,
                        DisplayGeometry.WorkingAreas(), gap, false);
                    if (!CaptureExclusion.TryExclude(_bar))
                    {
                        _bar.Dispose();
                        _bar = null;
                        Toast.Show("选区外无预览空间；当前系统不支持排除捕获。Enter 完成，Esc 取消");
                        Thread.Sleep(1000);
                        Toast.HideCurrent();
                        return;
                    }
                }
                _bar.Bounds = placement;
                CaptureExclusion.TryExclude(_bar);
                _bar.Show();
            }
            catch (Exception ex)
            {
                Log.Write("长截图控制条创建失败: " + ex.Message);
                if (_bar != null) { try { _bar.Dispose(); } catch { } }
                _bar = null;
            }
        }

        private void RemoveAuxWindows()
        {
            if (_border != null)
            {
                try { _border.Hide(); } catch { }
                try { _border.Dispose(); } catch { }
                _border = null;
            }
            if (_bar != null)
            {
                try { _bar.Hide(); } catch { }
                try { _bar.Dispose(); } catch { }
                _bar = null;
            }
        }

        // ==================================================================
        // 低层钩子：滚轮 / 键盘
        // ==================================================================

        private void InstallHooks()
        {
            try
            {
                _mouseProc = MouseHookProc;
                _keyboardProc = KeyboardHookProc;
                IntPtr module = GetModuleHandle(null);
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
                if (_mouseHook == IntPtr.Zero)
                    Log.Write("长截图鼠标钩子安装失败: " + Marshal.GetLastWin32Error());
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
                if (_keyboardHook == IntPtr.Zero)
                    Log.Write("长截图键盘钩子安装失败: " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                Log.Write("长截图钩子安装异常: " + ex.Message);
            }
        }

        private void UninstallHooks()
        {
            try
            {
                if (_mouseHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_mouseHook);
                    _mouseHook = IntPtr.Zero;
                }
                if (_keyboardHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_keyboardHook);
                    _keyboardHook = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log.Write("长截图钩子注销异常: " + ex.Message);
            }
            _mouseProc = null;
            _keyboardProc = null;
        }

        private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_finished)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    Point point = new Point(data.pt.X, data.pt.Y);
                    if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
                    {
                        if (IsTargetPoint(point)) SignalScroll();
                    }
                    else if (msg == WM_MOUSEMOVE)
                    {
                        if ((_leftDown || _middleDown) && IsTargetPoint(point) &&
                            (Math.Abs(point.X - _lastDragPoint.X) >= 2 || Math.Abs(point.Y - _lastDragPoint.Y) >= 2))
                        {
                            _lastDragPoint = point;
                            SignalScroll();
                        }
                    }
                    else if (msg == WM_LBUTTONDOWN)
                    {
                        _leftDown = IsTargetPoint(point);
                        _lastDragPoint = point;
                    }
                    else if (msg == WM_LBUTTONUP)
                    {
                        _leftDown = false;
                    }
                    else if (msg == WM_MBUTTONDOWN)
                    {
                        _middleDown = IsTargetPoint(point);
                        _lastDragPoint = point;
                    }
                    else if (msg == WM_MBUTTONUP)
                    {
                        _middleDown = false;
                    }
                    else if (msg == WM_RBUTTONDOWN)
                    {
                        if (_region.Contains(data.pt.X, data.pt.Y))
                        {
                            _cancelRequested = true;
                            return (IntPtr)1; // 拦截这次右键，避免结束前弹出目标窗口的右键菜单
                        }
                    }
                }
                catch { }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_finished)
            {
                try
                {
                    KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    int vk = (int)data.vkCode;
                    bool keyDown = wParam.ToInt32() == WM_KEYDOWN || wParam.ToInt32() == WM_SYSKEYDOWN;
                    if (vk == VK_ESCAPE)
                    {
                        if (keyDown) _cancelRequested = true;
                        return (IntPtr)1;
                    }
                    if (vk == VK_RETURN)
                    {
                        if (keyDown) _finishRequested = true;
                        return (IntPtr)1;
                    }
                    if (keyDown && _targetWindow != IntPtr.Zero &&
                        Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT) == _targetWindow &&
                        (vk == VK_SPACE || vk == VK_PRIOR || vk == VK_NEXT || vk == VK_HOME || vk == VK_END ||
                         vk == VK_UP || vk == VK_DOWN || vk == VK_LEFT || vk == VK_RIGHT))
                    {
                        SignalScroll();
                    }
                }
                catch { }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private static IntPtr RootWindowAt(Point point)
        {
            IntPtr window = Native.WindowFromPoint(new Native.POINT(point.X, point.Y));
            return window == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(window, Native.GA_ROOT);
        }

        private bool IsTargetPoint(Point point)
        {
            if (_bar != null && !_bar.IsDisposed && _bar.Bounds.Contains(point)) return false;
            // 滚动条可能在选区外，仍接受同一目标窗口的输入。
            return _targetWindow == IntPtr.Zero ? _region.Contains(point) : RootWindowAt(point) == _targetWindow;
        }

        // ==================================================================
        // P/Invoke
        // ==================================================================

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // ==================================================================
        // 释放
        // ==================================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _finished = true;
            if (_timer != null)
            {
                try { _timer.Stop(); } catch { }
                try { _timer.Dispose(); } catch { }
                _timer = null;
            }
            UninstallHooks();
            RemoveAuxWindows();
            if (_preview != null) { try { _preview.Dispose(); } catch { } _preview = null; }
            if (_journal != null) { try { _journal.Dispose(); } catch { } _journal = null; _stitcher = null; }
            if (_stitcher != null) { try { _stitcher.Dispose(); } catch { } _stitcher = null; }
        }
    }

    /// <summary>
    /// 选区外侧的绿色边框线：单个“环形 Region”置顶窗口。
    /// 环形区域严格位于选区外侧（选区内部不属于该窗口），因此不会拦截选区内鼠标、也不会被截图拍进去；
    /// 采用一整块大窗口而不是 4 条细长窗口，避免 Windows 对超小顶层窗口的尺寸修正导致边框侵入选区。
    /// 点击穿透：WS_EX_TRANSPARENT + 不抢焦点：WS_EX_NOACTIVATE。
    /// </summary>
    internal sealed class LongShotBorder : Form
    {
        private Region _ring;

        public LongShotBorder(Rectangle outer, Rectangle hole, int thickness)
        {
            if (thickness < 1) thickness = 1;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = UiTheme.Green;
            DoubleBuffered = true;
            Bounds = outer;
            try
            {
                int hx = hole.X - outer.X;
                int hy = hole.Y - outer.Y;
                if (hx < 0) hx = 0;
                if (hy < 0) hy = 0;
                if (hx + hole.Width > outer.Width) hole.Width = outer.Width - hx;
                if (hy + hole.Height > outer.Height) hole.Height = outer.Height - hy;
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.FillMode = FillMode.Alternate;
                    path.AddRectangle(new Rectangle(0, 0, outer.Width, outer.Height));
                    if (hole.Width > 0 && hole.Height > 0)
                        path.AddRectangle(new Rectangle(hx, hy, hole.Width, hole.Height));
                    _ring = new Region(path);
                    Region = _ring;
                }
            }
            catch { /* 极端情况下退化成整块绿色小窗，不影响功能 */ }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ring != null)
            {
                try { _ring.Dispose(); } catch { }
                _ring = null;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>实时预览、捕获段列表和可恢复的末段调整；不会抢占目标窗口焦点。</summary>
    internal sealed class LongShotBar : Form
    {
        private readonly float _scale;
        private readonly Action _onFinish, _onCancel, _onPause, _onUndo, _onRedo;
        private readonly Func<Bitmap> _snapshot;
        private readonly Font _titleFont, _font, _smallFont;
        private Bitmap _preview, _full;
        private Size _progress;
        private Size[] _segments = new Size[0];
        private Rectangle[] _segmentBounds = new Rectangle[0];
        private string _status = "";
        private bool _paused, _canUndo, _canRedo;
        private int _hover = -1;
        private int _selectedSegment = -1;
        private int _listStart = -1;
        private Point _offset;
        private readonly Rectangle[] _buttons = new Rectangle[5];
        private Rectangle _thumbRect, _listRect;

        public LongShotBar(float scale, Action finish, Action cancel, Action pause, Action undo, Action redo,
            Func<Bitmap> snapshot)
        {
            _scale = Math.Max(0.7f, scale);
            _onFinish = finish; _onCancel = cancel; _onPause = pause; _onUndo = undo; _onRedo = redo;
            _snapshot = snapshot;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            Text = "长截图实时预览";
            AccessibleName = Text;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(S(460), S(580));
            _titleFont = UiTheme.Font(S(16), FontStyle.Bold);
            _font = UiTheme.Font(S(13));
            _smallFont = UiTheme.Font(S(11));
        }

        private int S(float value) { return (int)Math.Round(value * _scale); }

        public void SetStatus(Size progress, Bitmap preview, string status, bool paused, Size[] segments,
            Rectangle[] segmentBounds, bool canUndo, bool canRedo)
        {
            if (IsDisposed) return;
            bool changed = progress != _progress || segments.Length != _segments.Length;
            _progress = progress; _preview = preview; _status = status ?? ""; _paused = paused;
            _segments = segments; _segmentBounds = segmentBounds; _canUndo = canUndo; _canRedo = canRedo;
            if (_selectedSegment >= segments.Length) _selectedSegment = segments.Length - 1;
            if (_full != null && changed) RefreshFullPreview();
            Invalidate();
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen outline = new Pen(UiTheme.ToolbarBorder)) g.DrawRectangle(outline, 0, 0, Width - 1, Height - 1);
            DrawLabel(g, "长截图实时预览", _titleFont, new Rectangle(S(16), S(10), Width - S(32), S(28)), UiTheme.TextDark);
            DrawLabel(g, "已拼接 " + _progress.Width + " × " + _progress.Height + " · " + _segments.Length + " 段", _font,
                new Rectangle(S(16), S(42), Width - S(32), S(24)), UiTheme.ActionGreen);
            DrawLabel(g, _status, _smallFont, new Rectangle(S(16), S(72), Width - S(32), S(36)), UiTheme.TextGray, true);
            int bottom = Height - S(125);
            _thumbRect = new Rectangle(S(16), S(115), Math.Max(S(100), Width - S(162)), Math.Max(S(80), bottom - S(115)));
            _listRect = new Rectangle(_thumbRect.Right + S(10), _thumbRect.Top, Width - _thumbRect.Right - S(26), _thumbRect.Height);
            using (SolidBrush fill = new SolidBrush(UiTheme.PanelBg)) g.FillRectangle(fill, _thumbRect);
            using (Pen outline = new Pen(UiTheme.ToolbarBorder)) g.DrawRectangle(outline, _thumbRect);
            DrawPreview(g);
            DrawLabel(g, "捕获段", _smallFont, new Rectangle(_listRect.X, _listRect.Y, _listRect.Width, S(22)), UiTheme.TextGray);
            int visible = Math.Max(1, (_listRect.Height - S(28)) / S(32));
            int first = FirstVisibleSegment(visible);
            for (int i = first; i < _segments.Length; i++)
            {
                Rectangle row = new Rectangle(_listRect.X, _listRect.Y + S(26) + (i - first) * S(32), _listRect.Width, S(30));
                if (i == _selectedSegment) using (SolidBrush fill = new SolidBrush(UiTheme.GreenLight)) g.FillRectangle(fill, row);
                DrawLabel(g, (i + 1) + " · " + _segments[i].Width + "×" + _segments[i].Height, _smallFont, row, UiTheme.TextDark);
            }
            DrawLabel(g, _full == null ? "点击预览看原始像素；点击段定位接缝" : "原始像素 · 滚轮上下查看；点击预览恢复适应",
                _smallFont, new Rectangle(S(16), bottom + S(6), Width - S(32), S(25)), UiTheme.TextGray);
            int gap = S(8), buttonWidth = (Width - S(32) - gap * 2) / 3;
            _buttons[0] = new Rectangle(S(16), Height - S(86), buttonWidth, S(32));
            _buttons[1] = new Rectangle(_buttons[0].Right + gap, _buttons[0].Y, buttonWidth, S(32));
            _buttons[2] = new Rectangle(_buttons[1].Right + gap, _buttons[0].Y, buttonWidth, S(32));
            _buttons[3] = new Rectangle(S(16), Height - S(46), (Width - S(40)) / 2, S(32));
            _buttons[4] = new Rectangle(_buttons[3].Right + gap, _buttons[3].Y, (Width - S(40)) / 2, S(32));
            DrawButton(g, 0, _paused ? "继续捕获" : "暂停捕获", true);
            DrawButton(g, 1, "撤回末段", _canUndo);
            DrawButton(g, 2, "恢复末段", _canRedo);
            DrawButton(g, 3, "完成 (Enter)", true);
            DrawButton(g, 4, "取消 (Esc)", true);
        }

        private void DrawPreview(Graphics g)
        {
            Bitmap source = _full ?? _preview;
            if (source == null) return;
            System.Drawing.Drawing2D.GraphicsState state = g.Save();
            try
            {
                g.SetClip(_thumbRect);
                float zoom = _full != null ? 1f : Math.Min((float)_thumbRect.Width / source.Width, (float)_thumbRect.Height / source.Height);
                int w = Math.Max(1, (int)Math.Round(source.Width * zoom));
                int h = Math.Max(1, (int)Math.Round(source.Height * zoom));
                int x = _thumbRect.X + Math.Max(0, (_thumbRect.Width - w) / 2) - (_full != null ? _offset.X : 0);
                int y = _thumbRect.Y + Math.Max(0, (_thumbRect.Height - h) / 2) - (_full != null ? _offset.Y : 0);
                g.InterpolationMode = _full != null ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
                if (_full != null) g.DrawImageUnscaled(source, x, y);
                else g.DrawImage(source, new Rectangle(x, y, w, h));
                float sx = (float)w / Math.Max(1, _progress.Width), sy = (float)h / Math.Max(1, _progress.Height);
                using (Pen seam = new Pen(Color.FromArgb(210, UiTheme.Orange), Math.Max(1, _scale)))
                {
                    seam.DashStyle = DashStyle.Dash;
                    for (int i = 1; i < _segmentBounds.Length; i++)
                    {
                        Rectangle part = _segmentBounds[i];
                        float px = x + part.X * sx, py = y + part.Y * sy;
                        if (part.Width < _progress.Width) g.DrawLine(seam, px, y, px, y + h);
                        else g.DrawLine(seam, x, py, x + w, py);
                    }
                }
            }
            finally { g.Restore(state); }
        }

        private void DrawButton(Graphics g, int index, string label, bool enabled)
        {
            Rectangle rect = _buttons[index];
            Color fill = index == 3 ? UiTheme.ActionGreen : (_hover == index ? UiTheme.HoverBg : Color.White);
            if (!enabled) fill = UiTheme.PanelBg;
            using (SolidBrush brush = new SolidBrush(fill)) g.FillRectangle(brush, rect);
            using (Pen outline = new Pen(UiTheme.ToolbarBorder)) g.DrawRectangle(outline, rect);
            DrawLabel(g, label, _font, rect, !enabled ? UiTheme.Disabled : index == 3 ? Color.White : UiTheme.TextDark);
        }

        private static void DrawLabel(Graphics g, string text, Font font, Rectangle rect, Color color, bool wrap = false)
        {
            TextRenderer.DrawText(g, text, font, rect, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | (wrap ? TextFormatFlags.WordBreak : TextFormatFlags.EndEllipsis));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hover = -1;
            for (int i = 0; i < _buttons.Length; i++) if (_buttons[i].Contains(e.Location)) hover = i;
            if (hover != _hover) { _hover = hover; Invalidate(); }
            Cursor = hover >= 0 || _thumbRect.Contains(e.Location) || _listRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (_buttons[0].Contains(e.Location)) _onPause();
            else if (_buttons[1].Contains(e.Location) && _canUndo) _onUndo();
            else if (_buttons[2].Contains(e.Location) && _canRedo) _onRedo();
            else if (_buttons[3].Contains(e.Location)) _onFinish();
            else if (_buttons[4].Contains(e.Location)) _onCancel();
            else if (_thumbRect.Contains(e.Location))
            {
                if (_full != null) { _full.Dispose(); _full = null; }
                else { if (!_paused) _onPause(); RefreshFullPreview(); }
                Invalidate();
            }
            else if (_listRect.Contains(e.Location))
            {
                int visible = Math.Max(1, (_listRect.Height - S(28)) / S(32));
                int first = FirstVisibleSegment(visible);
                if (e.Y < _listRect.Y + S(26)) return;
                int row = (e.Y - _listRect.Y - S(26)) / S(32);
                int index = first + row;
                if (row < 0 || index < 0 || index >= _segmentBounds.Length) return;
                if (!_paused) _onPause();
                _selectedSegment = index;
                if (_full == null) RefreshFullPreview();
                Rectangle part = _segmentBounds[index];
                _offset = new Point(Math.Max(0, part.X - S(24)), Math.Max(0, part.Y - S(24)));
                Invalidate();
            }
        }

        private void RefreshFullPreview()
        {
            Bitmap old = _full;
            _full = _snapshot();
            if (old != null) old.Dispose();
            _offset = Point.Empty;
        }

        private int FirstVisibleSegment(int visible)
        {
            return _listStart < 0 ? Math.Max(0, _segments.Length - visible) :
                Math.Max(0, Math.Min(Math.Max(0, _segments.Length - visible), _listStart));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_listRect.Contains(e.Location))
            {
                int visible = Math.Max(1, (_listRect.Height - S(28)) / S(32));
                _listStart = Math.Max(0, Math.Min(Math.Max(0, _segments.Length - visible),
                    FirstVisibleSegment(visible) + (e.Delta > 0 ? -3 : 3)));
                Invalidate(_listRect);
                return;
            }
            if (_full == null) return;
            if ((ModifierKeys & Keys.Shift) != 0)
                _offset.X = Math.Max(0, Math.Min(Math.Max(0, _full.Width - _thumbRect.Width), _offset.X - e.Delta));
            else _offset.Y = Math.Max(0, Math.Min(Math.Max(0, _full.Height - _thumbRect.Height), _offset.Y - e.Delta));
            Invalidate(_thumbRect);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _titleFont.Dispose(); _font.Dispose(); _smallFont.Dispose();
                if (_full != null) { _full.Dispose(); _full = null; }
            }
            base.Dispose(disposing);
        }
    }
}
