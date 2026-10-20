using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 区域屏幕录制（系统 H.264/AAC MP4 或 MJPEG AVI）。
    ///
    /// 由截图编辑器/托盘热键在 UI 线程调用：显示“选区绿色边框 + 选区外控制条”，
    /// 按 fps 定时抓取选区画面写入 .avi，用户点「停止」或按 Esc 结束并保存。
    /// 录制只是旁观捕获，不注入任何输入、不改变目标窗口焦点。
    ///
    /// 实现要点：
    ///   * 选区边框：单个“环形 Region”置顶点击穿透窗口（实现同长截图的 LongShotBorder），
    ///     避免细长窗口被系统放大后侵入画面；
    ///   * 控制条：TopMost + WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW，放在选区外，
    ///     不抢焦点；已录时长、统计、暂停与停止按钮，仅重绘变化区域；
    ///   * 阻塞式录制：内部 Application.DoEvents + Thread.Sleep 泵消息，直到停止；
    ///   * 抓帧：ImageUtil.Capture(region) 物理像素；失败/变慢时复用上一帧补齐固定帧率时间线；
    ///   * 光标：可选（cfg.RecordIncludeCursor）用 GetCursorInfo + DrawIconEx 把系统当前光标画进帧，
    ///     光标在选区内才画，控制条/边框在选区外所以不会入镜；
    ///   * 文件：保存到 cfg.EnsureSaveDir()，不覆盖已有文件；
    ///   * 收尾：保证编码器 Finalize/Close；异常时保留已经写入的部分并明确报告错误；
    ///     辅助窗口、键盘钩子、GDI 资源全部释放。
    /// </summary>
    public static class ScreenRecorder
    {
        /// <summary>
        /// 在指定屏幕物理像素区域录制。本方法在 UI 线程调用并阻塞到录制结束（内部泵消息）。
        /// 成功返回保存的视频绝对路径；取消/失败返回 null（已 Toast 提示原因）。
        /// </summary>
        public static string RecordRegion(Rectangle screenRegion, AppConfig cfg)
        {
            if (cfg == null) cfg = new AppConfig();
            if (screenRegion.Width < 4 || screenRegion.Height < 4)
            {
                Toast.Show("录制失败：选区太小（至少 4 × 4 像素）");
                return null;
            }
            try
            {
                using (RecordingSession session = new RecordingSession(screenRegion, cfg))
                {
                    return session.Execute();
                }
            }
            catch (Exception ex)
            {
                Log.Write("屏幕录制异常: " + ex);
                Toast.Show("录制失败：" + ex.Message);
                return null;
            }
        }
    }

    /// <summary>一次录屏会话：管理媒体写入、抓帧节奏、暂停、辅助窗口和资源清理。</summary>
    internal sealed class RecordingSession : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int VK_ESCAPE = 0x1B;
        private const int MaxFillFramesPerPump = 64;

        private readonly Rectangle _region;
        private readonly AppConfig _cfg;
        private readonly float _scale;
        private readonly int _fps;
        private readonly int _quality;
        private readonly bool _includeCursor;
        private readonly double _frameIntervalMs;
        private readonly int _autoStopMs;      // 仅自动化测试用（环境变量 XJ_RECORD_AUTO_STOP_MS）
        private readonly Func<Rectangle, Bitmap> _capture;
        private readonly Func<Form, bool> _excludeFromCapture;

        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Stopwatch _wallClock = new Stopwatch();
        private AviWriter _writer;
        private Mp4RecorderWriter _mp4Writer;
        private RecorderBorder _border;
        private RecorderBar _bar;
        private string _savePath;
        private string _error;
        private bool _fatal;
        private bool _finished;
        private bool _stopRequested;
        private bool _disposed;
        private bool _paused;
        private bool _pauseKeyDown;
        private bool _fileLimitReached;
        private bool _pumpingFrames;

        private IntPtr _keyboardHook = IntPtr.Zero;
        private HookProc _keyboardProc;

        private double _nextFrameDueMs;
        private int _writtenFrames;
        private int _repeatedFrames;
        private int _captureFailures;
        private long _lastBarTick = -1000;

        private static readonly bool DebugEnabled = Environment.GetEnvironmentVariable("XJ_RECORD_DEBUG") == "1";

        public RecordingSession(Rectangle region, AppConfig cfg)
        {
            _region = region;
            _cfg = cfg;
            _capture = ImageUtil.Capture;
            _excludeFromCapture = CaptureExclusion.TryExclude;
            Point center = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
            _scale = DpiUtil.ScaleAtPoint(center);
            if (_scale <= 0.5f || _scale > 4f) _scale = 1f;

            int fps = cfg.RecordFps;
            if (fps < 5) fps = 5;
            if (fps > 60) fps = 60;
            _fps = fps;
            _frameIntervalMs = 1000.0 / _fps;

            int quality = cfg.RecordQuality;
            if (quality < 30) quality = 30;
            if (quality > 100) quality = 100;
            _quality = quality;
            _includeCursor = cfg.RecordIncludeCursor;

            int autoStop;
            if (int.TryParse(Environment.GetEnvironmentVariable("XJ_RECORD_AUTO_STOP_MS"), out autoStop) && autoStop > 0)
                _autoStopMs = Math.Min(autoStop, 600000);
            else
                _autoStopMs = 0;
        }

        internal RecordingSession(Rectangle region, AppConfig cfg, Func<Rectangle, Bitmap> capture)
            : this(region, cfg)
        {
            if (capture == null) throw new ArgumentNullException("capture");
            _capture = capture;
        }

        internal RecordingSession(Rectangle region, AppConfig cfg, Func<Rectangle, Bitmap> capture,
            Func<Form, bool> excludeFromCapture) : this(region, cfg, capture)
        {
            if (excludeFromCapture == null) throw new ArgumentNullException("excludeFromCapture");
            _excludeFromCapture = excludeFromCapture;
        }

        private static void Dbg(string message)
        {
            if (!DebugEnabled) return;
            try { Log.Write("[recorder] " + message); } catch { }
        }

        // ==================================================================
        // 主流程
        // ==================================================================

        public string Execute()
        {
            try
            {
                Log.Write(string.Format("开始录制: 选区 {0}，{1}fps，JPEG 质量 {2}，光标={3}，缩放={4:0.##}",
                    _region, _fps, _quality, _includeCursor ? "包含" : "不包含", _scale));

                // 1) 清掉可能残留的 Toast，避免被拍进第一帧
                try { Toast.HideCurrent(); } catch { }
                Application.DoEvents();
                Thread.Sleep(60);
                Application.DoEvents();

                // 2) 创建输出文件；音频只能由 MP4/AAC 路径承载，不能静默丢弃。
                string dir = _cfg.EnsureSaveDir();
                if (string.IsNullOrEmpty(dir)) throw new IOException("保存目录无效");
                bool mp4 = !string.Equals(_cfg.RecordFormat, "avi", StringComparison.OrdinalIgnoreCase);
                if (!mp4 && !string.IsNullOrEmpty(_cfg.RecordAudioMode) && _cfg.RecordAudioMode != "none")
                    throw new InvalidOperationException("录制声音需要选择 MP4 格式");
                _savePath = BuildUniquePath(dir, mp4 ? ".mp4" : ".avi");
                if (mp4)
                    _mp4Writer = new Mp4RecorderWriter(_savePath, _region.Width, _region.Height,
                        _fps, _quality, _cfg.RecordAudioMode);
                else
                {
                    _writer = new AviWriter(_savePath, _region.Width, _region.Height, _fps, _quality);
                    if (_writer.LastError != null) Log.Write("AVI 头部写入警告: " + _writer.LastError.Message);
                }

                // 3) 辅助窗口（边框在选区外；控制条尽量放选区外）
                CreateBorder();
                CreateBar();
                InstallKeyboardHook();

                // 4) 录制循环：首帧立即抓，然后按 fps 节奏抓帧
                _clock.Start();
                _wallClock.Start();
                if (_mp4Writer != null) _mp4Writer.SetPaused(false);
                _nextFrameDueMs = 0;
                PumpFrames(0);

                while (!_finished)
                {
                    Application.DoEvents();
                    if (_stopRequested || _fatal) break;

                    long now = _clock.ElapsedMilliseconds;
                    if (_autoStopMs > 0 && _wallClock.ElapsedMilliseconds >= _autoStopMs)
                    {
                        Dbg("自动停止（XJ_RECORD_AUTO_STOP_MS=" + _autoStopMs + "）");
                        break;
                    }

                    if (!_paused) PumpFrames(now);
                    UpdateBar(now);

                    int sleepMs = 5;
                    double remain = _nextFrameDueMs - _clock.Elapsed.TotalMilliseconds;
                    if (remain > 0) sleepMs = (int)Math.Min(10, Math.Max(1, remain));
                    Thread.Sleep(sleepMs);
                }
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                Log.Write("录制过程异常: " + ex);
            }
            finally
            {
                _finished = true;
                Cleanup();
            }

            // ---- 结果判定（Cleanup 已保证 AVI Close、窗口/钩子释放）----
            try
            {
                double elapsedSec = _clock.Elapsed.TotalSeconds;
                if (_writtenFrames <= 0)
                {
                    TryDeleteEmptyFile();
                    string reason = string.IsNullOrEmpty(_error) ? "没有捕获到任何画面（可能锁屏或显示器异常）" : _error;
                    Log.Write("录制失败: " + reason);
                    Toast.Show("录制失败：" + reason);
                    return null;
                }

                if (!string.IsNullOrEmpty(_error))
                {
                    Log.Write("录制中断（已保留部分文件）: " + _error + " -> " + _savePath);
                    Toast.Show("录制中断：" + _error + "，已保留部分文件 " + Path.GetFileName(_savePath));
                    return null;
                }

                double actualFps = _writtenFrames / Math.Max(0.001, elapsedSec);
                long size = 0;
                try { size = new FileInfo(_savePath).Length; } catch { }
                Log.Write(string.Format(
                    "录制完成: {0} 帧，{1:0.00} 秒，平均 {2:0.0}fps，补帧 {3}，文件 {4}（{5:N1} KB）",
                    _writtenFrames, elapsedSec, actualFps, _repeatedFrames, _savePath, size / 1024.0));
                if (_fps >= 5 && actualFps < _fps * 0.7)
                    Log.Write("提示: 实际帧率明显低于配置帧率，可能因画面较大/编码较慢导致补帧");

                Toast.Show((_fileLimitReached ? "已达到 AVI 文件大小上限，已保存到 " : "已保存到 ") + _savePath, 3200);
                return _savePath;
            }
            catch (Exception ex)
            {
                Log.Write("录制收尾异常: " + ex);
                Toast.Show("录制失败：" + ex.Message);
                return null;
            }
        }

        private void PumpFrames(long now)
        {
            if (_finished || _stopRequested || _fatal || _paused || _pumpingFrames || now < _nextFrameDueMs) return;
            _pumpingFrames = true;
            try
            {
                int dueFrames = FramesAt(now, false);
                int repeatedBefore = _repeatedFrames;
                // 先用旧 JPEG 填上已经过去的时段，避免把新画面追溯到卡顿之前。
                // 单轮最多补 64 帧，剩余积压交给下一轮，时间线不跳跃。
                if (_writtenFrames > 0 && !FillTimeline(dueFrames - 1, false, MaxFillFramesPerPump)) return;
                if (_stopRequested || _fatal) return;
                bool wrote = CaptureAndWrite();
                long afterCapture = Math.Max(now, _clock.ElapsedMilliseconds);
                if (!wrote && !_fatal && !_stopRequested)
                {
                    if (_writtenFrames == 0)
                    {
                        // 没有首帧可复用时仍按帧间隔重试，不在 UI 消息泵中忙等。
                        _nextFrameDueMs = afterCapture + _frameIntervalMs;
                        return;
                    }
                }
                if (!_fatal && !_stopRequested)
                    FillTimeline(FramesAt(afterCapture, false), false,
                        MaxFillFramesPerPump - (_repeatedFrames - repeatedBefore));
            }
            finally { _pumpingFrames = false; }
        }

        private int FramesAt(double milliseconds, bool stopping)
        {
            double frames = milliseconds / _frameIntervalMs;
            double count = stopping ? Math.Ceiling(frames) : Math.Floor(frames) + 1;
            return (int)Math.Min(int.MaxValue, Math.Max(1, count));
        }

        private bool FillTimeline(int targetFrames, bool stopping, int maxFrames)
        {
            int added = 0;
            while (_writtenFrames < targetFrames && !_fatal && !_fileLimitReached &&
                (stopping || !_stopRequested) && added < maxFrames)
            {
                try
                {
                    if (_mp4Writer != null)
                    {
                        if (!_mp4Writer.RepeatLastFrame()) return false;
                    }
                    else if (!_writer.RepeatLastFrame()) return false;
                    _writtenFrames++;
                    _repeatedFrames++;
                    _nextFrameDueMs = _writtenFrames * _frameIntervalMs;
                    if (++added % 32 == 0)
                    {
                        UpdateBar(_clock.ElapsedMilliseconds);
                        Application.DoEvents();
                    }
                }
                catch (AviSizeLimitException)
                {
                    StopAtFileLimit();
                    return false;
                }
                catch (Exception ex)
                {
                    Fatal("补帧写入失败", ex);
                    return false;
                }
            }
            return _writtenFrames >= targetFrames;
        }

        private void StopAtFileLimit()
        {
            _fileLimitReached = true;
            _stopRequested = true;
            if (_writtenFrames == 0) _error = "单帧超过 AVI 文件大小上限";
            Log.Write("录制达到 AVI 文件大小上限，停止写入并保存已有帧");
        }

        /// <summary>抓取一帧、可选绘制光标、提交编码器。返回是否成功写入。</summary>
        private bool CaptureAndWrite()
        {
            if (_finished || _stopRequested || _fatal) return false;
            Bitmap frame = null;
            try
            {
                if (_finished || _stopRequested) return false;

                try
                {
                    frame = _capture(_region);
                }
                catch (Exception ex)
                {
                    // 单帧抓取失败（锁屏/显示切换等）跳过该帧继续录；连续失败过多才终止
                    _captureFailures++;
                    Log.Write("录制抓帧失败（已跳过，连续 " + _captureFailures + " 次）: " + ex.Message);
                    if (_captureFailures >= 10) Fatal("连续抓帧失败", ex);
                    return false;
                }
                if (frame == null)
                {
                    _captureFailures++;
                    if (_captureFailures >= 10) Fatal("连续抓帧失败", new InvalidOperationException("画面捕获返回空"));
                    return false;
                }
                _captureFailures = 0;
                if (_finished || _stopRequested) return false;

                if (_includeCursor) RecorderCursor.Stamp(frame, _region);

                try
                {
                    if (_mp4Writer != null) _mp4Writer.AddFrame(frame);
                    else _writer.AddFrame(frame);
                }
                catch (AviSizeLimitException)
                {
                    StopAtFileLimit();
                    return false;
                }
                catch (Exception ex)
                {
                    Fatal("写入录制帧失败", ex);
                    return false;
                }
                _writtenFrames++;
                _nextFrameDueMs = _writtenFrames * _frameIntervalMs;
                return true;
            }
            finally
            {
                if (frame != null) { try { frame.Dispose(); } catch { } }
            }
        }

        private void TogglePause()
        {
            if (_finished || _stopRequested || _fatal) return;
            _paused = !_paused;
            if (_paused) _clock.Stop();
            else _clock.Start();
            try { if (_mp4Writer != null) _mp4Writer.SetPaused(_paused); }
            catch (Exception ex) { Fatal("切换录制暂停状态失败", ex); }
            if (_bar != null && !_bar.IsDisposed) _bar.SetPaused(_paused);
        }

        private void Fatal(string what, Exception ex)
        {
            if (_fatal) return;
            _fatal = true;
            _error = what + "：" + ex.Message;
            Log.Write("录制致命错误: " + what + " -> " + ex);
        }

        private void UpdateBar(long now)
        {
            if (_bar == null || _bar.IsDisposed) return;
            if (now - _lastBarTick < 250) return;
            _lastBarTick = now;
            try
            {
                long bytes = _mp4Writer != null ? _mp4Writer.BytesWritten : (_writer != null ? _writer.BytesWritten : 0);
                _bar.SetState(_clock.Elapsed.TotalSeconds, _writtenFrames, bytes);
            }
            catch { }
        }

        // ==================================================================
        // 保存路径 / 收尾清理
        // ==================================================================

        private static string BuildUniquePath(string dir, string extension)
        {
            string baseName = "录屏_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, baseName + extension);
            int n = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(dir, baseName + "_" + n + extension);
                n++;
                if (n > 999) break;
            }
            return path;
        }

        private void TryDeleteEmptyFile()
        {
            try
            {
                if (!string.IsNullOrEmpty(_savePath) && File.Exists(_savePath))
                    File.Delete(_savePath);
            }
            catch (Exception ex) { Log.Write("删除空录制文件失败: " + ex.Message); }
        }

        private void CloseWriter()
        {
            if (_mp4Writer != null)
            {
                try { _mp4Writer.Close(); }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(_error)) _error = "MP4 文件收尾失败：" + ex.Message;
                    Log.Write("MP4 收尾失败: " + ex.Message);
                }
            }
            if (_writer == null) return;
            try
            {
                _writer.Close();
                if (_writer.LastError != null)
                {
                    if (string.IsNullOrEmpty(_error)) _error = "AVI 文件收尾失败：" + _writer.LastError.Message;
                    Log.Write("AVI 收尾失败: " + _writer.LastError.Message);
                }
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(_error)) _error = "AVI 文件收尾失败：" + ex.Message;
                Log.Write("AVI 收尾异常: " + ex);
            }
        }

        private void Cleanup()
        {
            _stopRequested = true;
            try { if (_clock.IsRunning) _clock.Stop(); } catch { }
            if (_bar != null)
            {
                try { _bar.SetStopping(); } catch { }
            }

            // 用户停止后不再抓屏；复用缓存帧补齐已录时间，避免最后一次编码耗时缩短成片。
            if ((_writer != null || _mp4Writer != null) && !_fatal && !_fileLimitReached && _writtenFrames > 0 && string.IsNullOrEmpty(_error))
                FillTimeline(FramesAt(_clock.Elapsed.TotalMilliseconds, true), true, int.MaxValue);

            // 先完成媒体文件索引，再拆除辅助窗口。
            CloseWriter();

            if (_bar != null)
            {
                try { _bar.Hide(); } catch { }
                try { _bar.Dispose(); } catch { }
                _bar = null;
            }
            if (_border != null)
            {
                try { _border.Hide(); } catch { }
                try { _border.Dispose(); } catch { }
                _border = null;
            }
            UninstallKeyboardHook();

            Application.DoEvents();
        }

        private void CreateBorder()
        {
            try
            {
                int t = Math.Max(2, DpiUtil.Scale(2, _scale));   // 2px 绿色边框（高 DPI 随缩放）
                Rectangle outer = new Rectangle(_region.X - t, _region.Y - t,
                    _region.Width + t * 2, _region.Height + t * 2);
                _border = new RecorderBorder(outer, _region, t);
                _border.Show();
                Application.DoEvents();
                Dbg("边框窗口: 外框 " + outer + "，线宽 " + t + "px，空洞 " + _region);
            }
            catch (Exception ex)
            {
                Log.Write("录制边框窗口创建失败: " + ex.Message);
                if (_border != null) { try { _border.Dispose(); } catch { } _border = null; }
            }
        }

        private void CreateBar()
        {
            try
            {
                string fileName = string.IsNullOrEmpty(_savePath) ? "录屏.avi" : Path.GetFileName(_savePath);
                _bar = new RecorderBar(_scale, _fps, fileName, delegate { _stopRequested = true; },
                    TogglePause, _excludeFromCapture);
                Rectangle work = Screen.FromRectangle(_region).WorkingArea;
                int gap = DpiUtil.Scale(12, _scale);
                Size size = _bar.Size;
                Point location;
                if (!TryPlaceOutside(work, size, gap, out location))
                {
                    location = FindPlaceOnOtherScreens(size, gap);
                    if (location.IsEmpty)
                    {
                        // 全屏时保持控制条位置稳定，由系统捕获排除机制移出录制画面。
                        location = new Point(work.Right - size.Width - gap, work.Bottom - size.Height - gap);
                        if (location.X < work.Left) location.X = work.Left;
                        if (location.Y < work.Top) location.Y = work.Top;
                    }
                }
                _bar.Location = location;
                bool overlaps = _bar.Bounds.IntersectsWith(_region);
                // 先创建句柄核对捕获排除能力；不支持时全屏控制条从不显示，避免录入黑块。
                IntPtr handle = _bar.Handle;
                if (overlaps && !_bar.ExcludedFromCapture)
                {
                    _bar.Dispose();
                    _bar = null;
                    Toast.Show("选区外无控制条空间；当前系统不支持排除捕获。F8 暂停/继续，Esc 停止", 1400);
                    Stopwatch hintClock = Stopwatch.StartNew();
                    while (hintClock.ElapsedMilliseconds < 1600)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                    Toast.HideCurrent();
                    Application.DoEvents();
                    return;
                }
                _bar.Show();
                Application.DoEvents();
                Dbg("控制条位置: " + location + "，尺寸 " + size);
            }
            catch (Exception ex)
            {
                Log.Write("录制控制条创建失败: " + ex.Message);
                if (_bar != null) { try { _bar.Dispose(); } catch { } _bar = null; }
            }
        }

        /// <summary>在选区所在工作区内找完全位于选区外、且不越界的位置（右/左/下/上优先）。</summary>
        private bool TryPlaceOutside(Rectangle work, Size size, int gap, out Point location)
        {
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

        /// <summary>多显示器时把控制条放到其它显示器右下角（一定不与选区重叠）。</summary>
        private Point FindPlaceOnOtherScreens(Size size, int gap)
        {
            try
            {
                Screen current = Screen.FromRectangle(_region);
                Screen[] screens = Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    if (screens[i].DeviceName == current.DeviceName) continue;
                    Rectangle work = screens[i].WorkingArea;
                    Point p = new Point(work.Right - size.Width - gap, work.Bottom - size.Height - gap);
                    Rectangle bounds = new Rectangle(p, size);
                    if (bounds.IntersectsWith(_region)) continue;
                    return p;
                }
            }
            catch { }
            return Point.Empty;
        }

        // ==================================================================
        // 低层键盘钩子：Esc 停止（录制期间不抢目标窗口焦点）
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
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private void InstallKeyboardHook()
        {
            try
            {
                _keyboardProc = KeyboardHookProc;
                IntPtr module = GetModuleHandle(null);
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
                if (_keyboardHook == IntPtr.Zero)
                    Log.Write("录制键盘钩子安装失败（Esc 不可用，请点停止按钮）: " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                Log.Write("录制键盘钩子异常: " + ex.Message);
            }
        }

        private void UninstallKeyboardHook()
        {
            try
            {
                if (_keyboardHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_keyboardHook);
                    _keyboardHook = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log.Write("录制键盘钩子注销异常: " + ex.Message);
            }
            _keyboardProc = null;
        }

        private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_finished && !_stopRequested)
            {
                try
                {
                    KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    bool down = (int)wParam == 0x100 || (int)wParam == 0x104;
                    if ((int)data.vkCode == 0x77)
                    {
                        if (down && !_pauseKeyDown) TogglePause();
                        _pauseKeyDown = down;
                        return (IntPtr)1;
                    }
                    if (down && (int)data.vkCode == VK_ESCAPE)
                    {
                        _stopRequested = true;
                        return (IntPtr)1;   // 吞掉 Esc，不影响目标窗口
                    }
                }
                catch { }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // ==================================================================
        // 释放
        // ==================================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _finished = true;
            Cleanup();
        }
    }

    /// <summary>使用 Windows Media Foundation 编码 H.264/AAC，音频由 WASAPI 捕获。</summary>
    internal sealed class Mp4RecorderWriter : IDisposable
    {
        private IntPtr _handle;
        private readonly string _path;
        private readonly int _fps;
        private readonly Bitmap _buffer;
        private readonly byte[] _pixels;
        private GCHandle _pinned;
        private int _frames;
        private bool _closed;
        private bool _hasFrame;

        public Mp4RecorderWriter(string path, int width, int height, int fps, int quality, string audioMode)
        {
            _path = path;
            _fps = fps;
            int mode;
            switch ((audioMode ?? "none").ToLowerInvariant())
            {
                case "none": mode = 0; break;
                case "system": mode = 1; break;
                case "microphone": mode = 2; break;
                case "mixed": mode = 3; break;
                default: throw new ArgumentException("录制声音模式无效", "audioMode");
            }
            try
            {
                Check(RcCreate(path, width, height, fps, quality, mode, out _handle));
                // H.264 要求偶数尺寸；补齐右/下边缘，保留选区最后一列和最后一行。
                _buffer = new Bitmap(Math.Max(2, (width + 1) & ~1), Math.Max(2, (height + 1) & ~1), PixelFormat.Format32bppArgb);
                _pixels = new byte[_buffer.Width * _buffer.Height * 4];
                _pinned = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
            }
            catch (DllNotFoundException)
            {
                throw new InvalidOperationException("缺少 RecorderNative.dll，请完整解压绿色版程序目录");
            }
            catch (BadImageFormatException)
            {
                throw new InvalidOperationException("MP4 录制组件需要 64 位 Windows 和对应的 RecorderNative.dll");
            }
            catch
            {
                if (_handle != IntPtr.Zero) { RcDestroy(_handle); _handle = IntPtr.Zero; }
                if (_buffer != null) _buffer.Dispose();
                throw;
            }
        }

        public long BytesWritten
        {
            get { try { return new FileInfo(_path).Length; } catch { return 0; } }
        }

        public void AddFrame(Bitmap frame)
        {
            if (_closed) throw new ObjectDisposedException("Mp4RecorderWriter");
            using (Graphics graphics = Graphics.FromImage(_buffer))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.Clear(Color.Black);
                graphics.DrawImageUnscaled(frame, 0, 0);
            }
            BitmapData data = _buffer.LockBits(new Rectangle(Point.Empty, _buffer.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < _buffer.Height; ++y)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), _pixels,
                        y * _buffer.Width * 4, _buffer.Width * 4);
            }
            finally { _buffer.UnlockBits(data); }
            Check(RcWriteVideo(_handle, _pinned.AddrOfPinnedObject(), _buffer.Width * 4, _frames));
            ++_frames;
            _hasFrame = true;
        }

        public bool RepeatLastFrame()
        {
            if (!_hasFrame || _closed) return false;
            Check(RcWriteVideo(_handle, _pinned.AddrOfPinnedObject(), _buffer.Width * 4, _frames));
            ++_frames;
            return true;
        }

        public void SetPaused(bool paused)
        {
            if (!_closed) Check(RcSetPaused(_handle, paused ? 1 : 0));
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            try { Check(RcFinish(_handle, (long)_frames * 10000000 / _fps)); }
            finally
            {
                if (_handle != IntPtr.Zero) { RcDestroy(_handle); _handle = IntPtr.Zero; }
                if (_pinned.IsAllocated) _pinned.Free();
                _buffer.Dispose();
            }
        }

        public void Dispose() { Close(); }

        private void Check(int result)
        {
            if (result >= 0) return;
            StringBuilder error = new StringBuilder(512);
            RcGetError(_handle, error, error.Capacity);
            string reason = error.Length > 0 ? error.ToString() : "系统媒体组件返回错误";
            throw new InvalidOperationException(reason + " (0x" + unchecked((uint)result).ToString("X8") + ")");
        }

        [DllImport("RecorderNative.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RcCreate(string path, int width, int height, int fps, int quality, int audioMode, out IntPtr handle);
        [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int RcWriteVideo(IntPtr handle, IntPtr pixels, int stride, long frameIndex);
        [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int RcSetPaused(IntPtr handle, int paused);
        [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int RcFinish(IntPtr handle, long duration100ns);
        [DllImport("RecorderNative.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RcGetError(IntPtr handle, StringBuilder buffer, int capacity);
        [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void RcDestroy(IntPtr handle);
    }

    /// <summary>
    /// 录制选区外沿的 2px 绿色边框：单个“环形 Region”置顶窗口。
    /// 环形区域严格位于选区外侧（选区内部不属于该窗口），不会拦截选区内鼠标、不会被录进画面；
    /// 采用一整块大窗口而不是 4 条细长窗口，避免 Windows 对超小顶层窗口的尺寸修正导致边框侵入画面。
    /// </summary>
    internal sealed class RecorderBorder : Form
    {
        private Region _ring;

        public RecorderBorder(Rectangle outer, Rectangle hole, int thickness)
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
            catch { /* 极端情况下退化成整块绿色小窗，不影响录制 */ }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

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

    /// <summary>紧凑录制控制条：显示时长、暂停和停止，统计及保存位置通过提示显示。不抢焦点。</summary>
    internal sealed class RecorderBar : Form
    {
        private readonly float _scale;
        private readonly int _fps;
        private readonly string _fileName;
        private readonly Action _onStop;
        private readonly Action _onPause;
        private readonly Func<Form, bool> _excludeFromCapture;
        private readonly Font _timeFont;
        private readonly Font _buttonFont;
        private readonly System.Windows.Forms.Timer _blinkTimer;
        private readonly ToolTip _tooltip;

        private bool _dotOn = true;
        private bool _hoverStop;
        private bool _stopping;
        private bool _paused;
        private bool _hoverPause;
        private Rectangle _stopRect;
        private Rectangle _pauseRect;
        private Rectangle _timeRect;
        private double _seconds;
        private int _frames;
        private long _bytes;

        public RecorderBar(float scale, int fps, string fileName, Action onStop)
            : this(scale, fps, fileName, onStop, null) { }

        public RecorderBar(float scale, int fps, string fileName, Action onStop, Action onPause)
            : this(scale, fps, fileName, onStop, onPause, CaptureExclusion.TryExclude) { }

        internal RecorderBar(float scale, int fps, string fileName, Action onStop, Action onPause,
            Func<Form, bool> excludeFromCapture)
        {
            _scale = scale > 0.5f ? scale : 1f;
            _fps = fps;
            _fileName = fileName ?? "";
            _onStop = onStop;
            _onPause = onPause;
            _excludeFromCapture = excludeFromCapture;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(S(360), S(44));
            _timeFont = UiTheme.Font(S(19f), FontStyle.Bold);
            _buttonFont = UiTheme.Font(S(13f));
            _timeRect = new Rectangle(S(30), S(7), S(108), S(30));
            _pauseRect = new Rectangle(S(146), S(4), S(94), S(37));
            _stopRect = new Rectangle(S(248), S(4), S(102), S(37));
            using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(0, 0, Width, Height), S(10f)))
            {
                Region = new Region(path);
            }

            _blinkTimer = new System.Windows.Forms.Timer();
            _blinkTimer.Interval = 500;
            _blinkTimer.Tick += delegate
            {
                _dotOn = !_dotOn;
                Invalidate(new Rectangle(S(11), S(17), S(10), S(10)));
            };
            _tooltip = new ToolTip { InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 10000,
                ForeColor = UiTheme.TextDark, BackColor = Color.White };
            UpdateTooltip();
        }

        private int S(float v)
        {
            return (int)Math.Round(v * _scale);
        }

        /// <summary>更新已录时长/帧数/文件大小并重绘。</summary>
        public void SetState(double seconds, int frames, long bytes)
        {
            if (IsDisposed) return;
            bool changedTime = FormatDuration(_seconds) != FormatDuration(seconds);
            bool changedStats = frames != _frames || FormatSize(bytes) != FormatSize(_bytes);
            _seconds = seconds;
            _frames = frames;
            _bytes = bytes;
            if (changedTime) Invalidate(_timeRect);
            if (changedStats) UpdateTooltip();
        }

        private void UpdateTooltip()
        {
            _tooltip.SetToolTip(this, string.Format("{0} fps · {1} 帧 · 约 {2}\n停止即保存到：{3}\nF8 暂停/继续，Esc 停止",
                _fps, _frames, FormatSize(_bytes), _fileName));
        }

        public void SetPaused(bool paused)
        {
            _paused = paused;
            if (paused) _blinkTimer.Stop();
            else _blinkTimer.Start();
            Invalidate(_pauseRect);
            Invalidate(new Rectangle(S(11), S(17), S(10), S(10)));
        }

        public bool ExcludedFromCapture { get; private set; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // WDA_EXCLUDEFROMCAPTURE 使全屏选区内的控制条也无需逐帧隐藏。
            ExcludedFromCapture = _excludeFromCapture(this);
        }

        /// <summary>停止已触发（等待保存收尾）时把按钮置灰。</summary>
        public void SetStopping()
        {
            if (IsDisposed) return;
            _stopping = true;
            try { _blinkTimer.Stop(); } catch { }
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

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            _blinkTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Color.White)) g.FillRectangle(bg, ClientRectangle);
            ImageUtil.DrawRounded(g, UiTheme.ToolbarBorder, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), S(10f), 1f);

            // 闪烁红点
            int dotSize = S(8);
            if (_dotOn && !_stopping)
            {
                using (SolidBrush dot = new SolidBrush(UiTheme.Red))
                    g.FillEllipse(dot, S(12), S(18), dotSize, dotSize);
            }
            // 已录时长 mm:ss
            string timeText = FormatDuration(_seconds);
            TextRenderer.DrawText(g, timeText, _timeFont,
                _timeRect, UiTheme.TextDark,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            // 停止按钮
            DrawStopButton(g, _stopRect);
            using (GraphicsPath path = ImageUtil.RoundedRect(_pauseRect, S(8f)))
            using (SolidBrush brush = new SolidBrush(_hoverPause ? Color.FromArgb(220, 235, 226) : Color.FromArgb(237, 244, 240)))
                g.FillPath(brush, path);
            TextRenderer.DrawText(g, _paused ? "继续 F8" : "暂停 F8", _buttonFont, _pauseRect,
                UiTheme.TextDark, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private void DrawStopButton(Graphics g, Rectangle rect)
        {
            float radius = S(8f);
            Color fill = _stopping ? UiTheme.DisabledBackground : (_hoverStop ? UiTheme.ActionRedHover : UiTheme.ActionRed);
            using (GraphicsPath path = ImageUtil.RoundedRect(rect, radius))
            using (SolidBrush brush = new SolidBrush(fill))
                g.FillPath(brush, path);
            string text = _stopping ? "保存中…" : "停止 Esc";
            TextRenderer.DrawText(g, text, _buttonFont, rect, _stopping ? UiTheme.Disabled : Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hover = _stopRect.Contains(e.Location);
            bool pauseHover = _pauseRect.Contains(e.Location);
            if (hover != _hoverStop || pauseHover != _hoverPause)
            {
                _hoverStop = hover;
                _hoverPause = pauseHover;
                Cursor = hover || pauseHover ? Cursors.Hand : Cursors.Default;
                Invalidate(_stopRect);
                Invalidate(_pauseRect);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverStop || _hoverPause)
            {
                _hoverStop = false;
                _hoverPause = false;
                Cursor = Cursors.Default;
                Invalidate(_stopRect);
                Invalidate(_pauseRect);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (!_stopping && _stopRect.Contains(e.Location))
            {
                if (_onStop != null) _onStop();
            }
            else if (!_stopping && _pauseRect.Contains(e.Location) && _onPause != null) _onPause();
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 0) seconds = 0;
            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:00}:{1:00}", (int)ts.TotalMinutes, ts.Seconds);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024L) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _blinkTimer.Stop(); } catch { }
                try { _blinkTimer.Dispose(); } catch { }
                if (_timeFont != null) _timeFont.Dispose();
                if (_buttonFont != null) _buttonFont.Dispose();
                if (_tooltip != null) _tooltip.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 把系统当前鼠标指针画进录制帧（cfg.RecordIncludeCursor）。
    /// 用 GetCursorInfo 取当前光标句柄与屏幕坐标，GetIconInfo 取热点，DrawIconEx 直接画到帧的 HDC；
    /// 光标不在选区内时不画；控制条/边框都在选区外，因此不会把“录制 UI”画进画面。
    /// </summary>
    internal static class RecorderCursor
    {
        private const int CURSOR_SHOWING = 0x0001;
        private const int DI_NORMAL = 0x0003;
        private const int SM_CXCURSOR = 13;
        private const int SM_CYCURSOR = 14;

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public int ptX;
            public int ptY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CURSORINFO pci);

        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("user32.dll")]
        private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
            int cxWidth, int cyHeight, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        /// <summary>把当前系统光标画到 frame 上（frame 坐标 = 屏幕坐标 - 选区左上角）。</summary>
        public static void Stamp(Bitmap frame, Rectangle screenRegion)
        {
            if (frame == null) return;
            bool gotIcon = false;
            ICONINFO iconInfo = new ICONINFO();
            try
            {
                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                if (!GetCursorInfo(ref ci)) return;
                if ((ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
                if (!screenRegion.Contains(ci.ptX, ci.ptY)) return;   // 光标不在录制区域，不画

                int hotX = 0, hotY = 0;
                gotIcon = GetIconInfo(ci.hCursor, out iconInfo);
                if (gotIcon)
                {
                    hotX = iconInfo.xHotspot;
                    hotY = iconInfo.yHotspot;
                }

                int cx = 0, cy = 0;
                // 按光标所在显示器 DPI 取系统光标物理像素尺寸（高 DPI 下 32px 会放大到 40/48px）
                try
                {
                    uint dpi = (uint)Math.Round(96.0 * DpiUtil.ScaleAtPoint(new Point(ci.ptX, ci.ptY)));
                    if (dpi >= 72 && dpi <= 480)
                    {
                        cx = GetSystemMetricsForDpi(SM_CXCURSOR, dpi);
                        cy = GetSystemMetricsForDpi(SM_CYCURSOR, dpi);
                    }
                }
                catch { }
                if (cx <= 0 || cy <= 0)
                {
                    cx = GetSystemMetrics(SM_CXCURSOR);
                    cy = GetSystemMetrics(SM_CYCURSOR);
                }
                if (cx <= 0 || cy <= 0) { cx = 32; cy = 32; }

                int x = ci.ptX - screenRegion.X - hotX;
                int y = ci.ptY - screenRegion.Y - hotY;
                using (Graphics g = Graphics.FromImage(frame))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        DrawIconEx(hdc, x, y, ci.hCursor, cx, cy, 0, IntPtr.Zero, DI_NORMAL);
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }
            }
            catch
            {
                // 光标绘制失败不影响录制（只是这一帧没有光标）
            }
            finally
            {
                if (gotIcon)
                {
                    if (iconInfo.hbmMask != IntPtr.Zero) { try { DeleteObject(iconInfo.hbmMask); } catch { } }
                    if (iconInfo.hbmColor != IntPtr.Zero) { try { DeleteObject(iconInfo.hbmColor); } catch { } }
                }
            }
        }
    }
}
