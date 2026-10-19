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
    /// Run 自行显示提示条、绿色边框、拼接进度，处理鼠标滚轮/键盘：
    ///   - 用户滚动窗口内容时自动抓帧拼接（纵向优先，也支持横向）；
    ///   - Enter / 点击“完成”结束并返回结果图；
    ///   - Esc / 右键（选区内）/ 点击“取消”取消，返回 null。
    /// capture 回调返回指定屏幕矩形的最新画面（物理像素）。
    ///
    /// 交互实现要点（避免干扰用户滚动目标窗口）：
    ///   * 选区内部没有任何窗口：绿色边框是选区外侧 4 条 2px 的置顶“线条窗口”，
    ///     控制条放在选区外侧，均为 WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW，不抢焦点、不拦截选区内鼠标；
    ///   * 通过 WH_MOUSE_LL 低层鼠标钩子感知滚轮（150ms 静默后抓帧，连续滚动时最多 220ms 抓一次），
    ///     WH_KEYBOARD_LL 感知 PageDown/空格/方向键；另有 ~650ms 定时器兜底拖动滚动条；
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
        private const int DebounceMs = 150;        // 滚轮停止后多久抓帧
        private const int CaptureThrottleMs = 220; // 连续滚动时两次抓帧的最小间隔
        private const int FallbackCaptureMs = 650; // 定时器兜底抓帧间隔（键盘/拖动滚动条）
        private const int PreviewIntervalMs = 280; // 缩略图刷新间隔
        private const int MaxFrames = 400;         // 最多拼接帧数保护

        // ---- 钩子常量 ----
        private const int WH_MOUSE_LL = 14;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;
        private const int WM_RBUTTONDOWN = 0x0204;
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
        private System.Windows.Forms.Timer _timer;

        private LongShotBar _bar;
        private LongShotBorder _border;
        private bool _barOverlapsRegion;

        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private HookProc _mouseProc;
        private HookProc _keyboardProc;

        private bool _finished;
        private bool _cancelRequested;
        private bool _finishRequested;
        private bool _cancelled;
        private bool _autoFull;
        private bool _pendingCapture;
        private int _lastScrollTick;
        private int _lastCaptureTick;
        private byte[] _lastThumb;
        private Bitmap _preview;
        private int _lastPreviewTick;
        private string _statusText = "";
        private bool _disposed;

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

                _stitcher = new LongShotStitcher(Math.Max(1, _region.Width));
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
                    if (!_stitcher.AddFrame(first))
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
                    // 用户没有产生有效滚动：返回当前选区画面（若抓帧失败则退回首帧）
                    Bitmap fresh = CaptureRegion();
                    if (fresh != null)
                    {
                        Toast.Show("未检测到滚动，已返回当前选区画面");
                        return fresh;
                    }
                    Bitmap single = _stitcher.TakeResult();
                    if (single != null) Toast.Show("未检测到滚动，已返回当前选区画面");
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
            bool hidBar = false;
            try
            {
                // 极端情况下（选区几乎铺满屏幕）控制条只能压在选区内，抓帧前临时隐藏
                if (_barOverlapsRegion && _bar != null && !_bar.IsDisposed && _bar.Visible)
                {
                    _bar.Visible = false;
                    hidBar = true;
                    Application.DoEvents();
                    Thread.Sleep(30);
                }
                Application.DoEvents();
                return _capture(_region);
            }
            catch (Exception ex)
            {
                Log.Write("长截图抓帧失败: " + ex.Message);
                return null;
            }
            finally
            {
                if (hidBar && _bar != null && !_bar.IsDisposed)
                {
                    try { _bar.Visible = true; } catch { }
                }
            }
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_finished) return;
            try
            {
                int now = Environment.TickCount;
                bool due = false;
                if (_pendingCapture &&
                    ((now - _lastScrollTick) >= DebounceMs || (now - _lastCaptureTick) >= CaptureThrottleMs))
                {
                    due = true;
                }
                else if ((now - _lastCaptureTick) >= FallbackCaptureMs)
                {
                    due = true; // 兜底：PageDown/拖动滚动条/中键滚动
                }
                if (due)
                {
                    _pendingCapture = false;
                    CaptureAndStitch(now);
                }
            }
            catch (Exception ex)
            {
                Log.Write("长截图定时器异常: " + ex.Message);
            }
        }

        private void CaptureAndStitch(int now)
        {
            if (_finished) return;
            Bitmap frame = CaptureRegion();
            if (frame == null) return;
            _lastCaptureTick = now;
            int dumpNo = -1;
            if (_dumpDir != null)
            {
                dumpNo = _dumpIndex++;
                try { frame.Save(Path.Combine(_dumpDir, string.Format("frame_{0:00}.png", dumpNo)), ImageFormat.Png); }
                catch { }
            }
            try
            {
                byte[] thumb = ComputeThumb(frame);
                if (_lastThumb != null && !ThumbChanged(_lastThumb, thumb))
                {
                    Dbg("帧#" + dumpNo + " 内容未变化，跳过");
                    return; // 内容没有变化（例如光标闪烁/动画），不交给拼接器
                }
                _lastThumb = thumb;

                bool added = _stitcher.AddFrame(frame);
                Dbg("帧#" + dumpNo + " AddFrame=" + added + " 状态=" + _stitcher.Status + " 画布=" + _stitcher.ResultSize);
                if (added)
                {
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
                    _statusText = _stitcher.Status;
                    UpdateBar(false, now);
                }
            }
            finally
            {
                frame.Dispose();
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
                    Bitmap p = _stitcher.CreatePreview(240);
                    Bitmap old = _preview;
                    _preview = p;
                    if (old != null) old.Dispose();
                }
                catch { }
            }
            try { _bar.SetStatus(size, _preview, _statusText); } catch { }
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
                _bar = new LongShotBar(_scale,
                    delegate { _finishRequested = true; },
                    delegate { _cancelRequested = true; });
                Rectangle work = Screen.FromRectangle(_region).WorkingArea;
                int gap = DpiUtil.Scale(10, _scale);
                Size size = _bar.Size;
                Point location;
                if (!TryPlaceOutside(work, size, gap, out location))
                {
                    // 没有完全在选区外的位置：退而求其次，放到工作区右下角（可能压住选区，抓帧时临时隐藏）
                    location = new Point(work.Right - size.Width - gap, work.Bottom - size.Height - gap);
                    if (location.X < work.Left) location.X = work.Left;
                    if (location.Y < work.Top) location.Y = work.Top;
                }
                _bar.Location = location;
                _bar.Show();
                _barOverlapsRegion = _bar.Bounds.IntersectsWith(_region);
                if (_barOverlapsRegion) Log.Write("长截图控制条与选区重叠，抓帧时将临时隐藏");
            }
            catch (Exception ex)
            {
                Log.Write("长截图控制条创建失败: " + ex.Message);
                _bar = null;
            }
        }

        private bool TryPlaceOutside(Rectangle work, Size size, int gap, out Point location)
        {
            // 右 / 左 / 下 / 上，优先右侧并与选区顶部对齐
            Point[] candidates = new Point[]
            {
                new Point(_region.Right + gap, _region.Top),
                new Point(_region.Left - gap - size.Width, _region.Top),
                new Point(_region.Left, _region.Bottom + gap),
                new Point(_region.Left, _region.Top - gap - size.Height)
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                Point p = candidates[i];
                if (p.X < work.Left || p.Y < work.Top) continue;
                if (p.X + size.Width > work.Right || p.Y + size.Height > work.Bottom) continue;
                Rectangle bounds = new Rectangle(p, size);
                if (bounds.IntersectsWith(_region)) continue;
                location = p;
                return true;
            }
            location = Point.Empty;
            return false;
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
                    if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
                    {
                        _pendingCapture = true;
                        _lastScrollTick = Environment.TickCount;
                    }
                    else if (msg == WM_RBUTTONDOWN)
                    {
                        MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
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
                    if (vk == VK_ESCAPE)
                    {
                        _cancelRequested = true;
                        return (IntPtr)1;
                    }
                    if (vk == VK_RETURN)
                    {
                        _finishRequested = true;
                        return (IntPtr)1;
                    }
                    if (vk == VK_SPACE || vk == VK_PRIOR || vk == VK_NEXT || vk == VK_HOME || vk == VK_END ||
                        vk == VK_UP || vk == VK_DOWN || vk == VK_LEFT || vk == VK_RIGHT)
                    {
                        _pendingCapture = true;
                        _lastScrollTick = Environment.TickCount;
                    }
                }
                catch { }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
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

    /// <summary>长截图控制条：标题/提示/实时进度/缩略图/完成与取消按钮。不抢焦点。</summary>
    internal sealed class LongShotBar : Form
    {
        private readonly float _scale;
        private readonly Action _onFinish;
        private readonly Action _onCancel;
        private readonly Font _titleFont;
        private readonly Font _hintFont;
        private readonly Font _progressFont;
        private readonly Font _smallFont;
        private readonly Font _buttonFont;

        private Size _progress = Size.Empty;
        private Bitmap _preview;
        private string _status = "";
        private bool _hoverFinish;
        private bool _hoverCancel;
        private Rectangle _finishRect;
        private Rectangle _cancelRect;
        private Rectangle _thumbRect;

        public LongShotBar(float scale, Action onFinish, Action onCancel)
        {
            _scale = scale;
            _onFinish = onFinish;
            _onCancel = onCancel;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(S(420), S(168));
            _titleFont = UiTheme.Font(S(15f), FontStyle.Bold);
            _hintFont = UiTheme.Font(S(11.5f));
            _progressFont = UiTheme.Font(S(15f), FontStyle.Bold);
            _smallFont = UiTheme.Font(S(11.5f));
            _buttonFont = UiTheme.Font(S(12.5f));
            using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(0, 0, Width, Height), S(10f)))
            {
                Region = new Region(path);
            }
        }

        private int S(float v)
        {
            return (int)Math.Round(v * _scale);
        }

        public void SetStatus(Size progress, Bitmap preview, string status)
        {
            if (IsDisposed) return;
            _progress = progress;
            _preview = preview;
            _status = status ?? "";
            Invalidate();
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
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Color.White)) g.FillRectangle(bg, ClientRectangle);
            ImageUtil.DrawRounded(g, UiTheme.ToolbarBorder, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), S(10f), 1f);

            int pad = S(16);
            int textWidth = S(252);
            TextRenderer.DrawText(g, "滚动截取长图", _titleFont, new Rectangle(pad, S(12), textWidth, S(22)),
                UiTheme.TextDark, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "滚轮 / PageDown 滚动目标窗口，自动拼接", _hintFont,
                new Rectangle(pad, S(38), textWidth, S(18)), UiTheme.TextGray,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);

            string progressText = _progress.Width > 0
                ? "已拼接 " + _progress.Width + " × " + _progress.Height
                : "已拼接 …";
            TextRenderer.DrawText(g, progressText, _progressFont, new Rectangle(pad, S(60), textWidth, S(24)),
                UiTheme.Green, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);

            string hint = string.IsNullOrEmpty(_status) ? "Enter 完成，Esc 取消" : _status;
            Color hintColor = string.IsNullOrEmpty(_status) ? UiTheme.TextGray : UiTheme.Orange;
            TextRenderer.DrawText(g, hint, _smallFont, new Rectangle(pad, S(88), textWidth, S(20)),
                hintColor, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);

            // 缩略图
            _thumbRect = new Rectangle(Width - pad - S(116), S(12), S(116), S(96));
            using (SolidBrush box = new SolidBrush(Color.FromArgb(250, 250, 250)))
                g.FillRectangle(box, _thumbRect);
            using (Pen pen = new Pen(UiTheme.ToolbarBorder))
                g.DrawRectangle(pen, _thumbRect);
            if (_preview != null && _preview.Width > 0 && _preview.Height > 0)
            {
                Rectangle inner = new Rectangle(_thumbRect.X + 2, _thumbRect.Y + 2, _thumbRect.Width - 4, _thumbRect.Height - 4);
                float fit = Math.Min((float)inner.Width / _preview.Width, (float)inner.Height / _preview.Height);
                int w = Math.Max(1, (int)Math.Round(_preview.Width * fit));
                int h = Math.Max(1, (int)Math.Round(_preview.Height * fit));
                int x = inner.X + (inner.Width - w) / 2;
                int y = inner.Y + (inner.Height - h) / 2;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(_preview, new Rectangle(x, y, w, h));
            }

            // 按钮
            _finishRect = new Rectangle(pad, Height - pad - S(34), S(126), S(34));
            _cancelRect = new Rectangle(_finishRect.Right + S(10), _finishRect.Top, S(100), S(34));
            DrawButton(g, _finishRect, "完成 (Enter)", true, _hoverFinish);
            DrawButton(g, _cancelRect, "取消 (Esc)", false, _hoverCancel);
        }

        private void DrawButton(Graphics g, Rectangle rect, string text, bool primary, bool hover)
        {
            float radius = S(6f);
            if (primary)
            {
                Color fill = hover ? UiTheme.GreenDark : UiTheme.Green;
                using (GraphicsPath path = ImageUtil.RoundedRect(rect, radius))
                using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
                TextRenderer.DrawText(g, text, _buttonFont, rect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            else
            {
                Color fill = hover ? UiTheme.HoverBg : Color.White;
                using (GraphicsPath path = ImageUtil.RoundedRect(rect, radius))
                using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
                ImageUtil.DrawRounded(g, UiTheme.ToolbarBorder, rect, radius, 1f);
                TextRenderer.DrawText(g, text, _buttonFont, rect, UiTheme.IconColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool finish = _finishRect.Contains(e.Location);
            bool cancel = _cancelRect.Contains(e.Location);
            if (finish != _hoverFinish || cancel != _hoverCancel)
            {
                _hoverFinish = finish;
                _hoverCancel = cancel;
                Invalidate();
            }
            Cursor = (finish || cancel) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverFinish || _hoverCancel)
            {
                _hoverFinish = false;
                _hoverCancel = false;
                Invalidate();
            }
            Cursor = Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (_finishRect.Contains(e.Location))
            {
                if (_onFinish != null) _onFinish();
            }
            else if (_cancelRect.Contains(e.Location))
            {
                if (_onCancel != null) _onCancel();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_titleFont != null) _titleFont.Dispose();
                if (_hintFont != null) _hintFont.Dispose();
                if (_progressFont != null) _progressFont.Dispose();
                if (_smallFont != null) _smallFont.Dispose();
                if (_buttonFont != null) _buttonFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
