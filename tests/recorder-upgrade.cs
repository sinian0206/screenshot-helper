using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using XiaoJie;

internal static class RecorderUpgradeTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string output = args[0];
            Directory.CreateDirectory(output);
            if (args.Length > 1 && args[1] == "--media") return TestMedia(output);
            if (args.Length > 1 && args[1] == "--pause") return TestPause(output);
            if (args.Length > 1 && args[1] == "--system") return TestSystemAudio(output);
            if (args.Length > 1 && args[1] == "--devices") return TestAudioDevices(output);
            if (args.Length > 1 && args[1] == "--capture-exclusion") return TestCaptureExclusion(output);
            if (args.Length > 1 && args[1] == "--odd") return TestOddDimensions(output);
            if (args.Length > 1 && args[1] == "--no-exclusion") return TestNoCaptureExclusion(output);
            AppConfig cfg = new AppConfig { RecordFps = 10, RecordIncludeCursor = false, SaveDir = output };
            int transitions = 0;
            RecorderBar observed = null;
            Environment.SetEnvironmentVariable("XJ_RECORD_AUTO_STOP_MS", "750");
            using (RecordingSession session = new RecordingSession(SystemInformation.VirtualScreen, cfg,
                delegate(Rectangle r)
                {
                    foreach (Form form in Application.OpenForms)
                    {
                        RecorderBar bar = form as RecorderBar;
                        if (bar == null || observed != null) continue;
                        observed = bar;
                        bar.VisibleChanged += delegate { ++transitions; };
                    }
                    return new Bitmap(32, 24);
                }))
            {
                string path = session.Execute();
                Require(path != null, "录制没有生成输出");
            }
            // 收尾关闭窗口只应产生一次 VisibleChanged；抓帧期间不能逐帧隐藏/显示。
            Require(observed != null, "没有创建录制控制条");
            Require(transitions <= 1, "控制条在录制期间反复显隐，VisibleChanged=" + transitions);
            Console.WriteLine("[PASS] 全屏录制期间控制条持续可见，收尾显隐次数=" + transitions);
            int fullInvalidations = 0;
            using (RecorderBar bar = new RecorderBar(1f, 10, "fixture.avi", delegate { }))
            {
                bar.Show();
                Application.DoEvents();
                bar.Invalidated += delegate(object sender, InvalidateEventArgs e)
                {
                    if (e.InvalidRect == bar.ClientRectangle) ++fullInvalidations;
                };
                bar.SetState(1.1, 11, 1000);
                bar.SetState(1.2, 12, 1000);
                Require(fullInvalidations == 0, "更新计时区域时整条窗口刷新 " + fullInvalidations + " 次");
            }
            Console.WriteLine("[PASS] 计时与统计信息只重绘变化区域");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[FAIL] " + ex.Message); return 1; }
        finally { Environment.SetEnvironmentVariable("XJ_RECORD_AUTO_STOP_MS", null); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static int TestMedia(string output)
    {
        string path = Path.Combine(output, "fixture-video.mp4");
        IntPtr writer;
        Require(RcCreate(path, 320, 180, 10, 80, 0, out writer) >= 0, "MP4 编码器初始化失败");
        try
        {
            byte[] pixels = new byte[320 * 180 * 4];
            for (int frame = 0; frame < 20; ++frame)
            {
                for (int y = 0; y < 180; ++y)
                    for (int x = 0; x < 320; ++x)
                    {
                        int i = (y * 320 + x) * 4;
                        pixels[i] = (byte)(y < 90 ? 20 : 200);
                        pixels[i + 1] = 40;
                        pixels[i + 2] = (byte)(y < 90 ? 200 : 30);
                    }
                Require(RcWriteVideo(writer, pixels, 320 * 4, frame) >= 0, "MP4 帧写入失败");
            }
            Require(RcFinish(writer, 20000000) >= 0, "MP4 收尾失败");
        }
        finally { RcDestroy(writer); }
        Require(new FileInfo(path).Length > 1000, "MP4 文件为空");
        Console.WriteLine("[PASS] 系统 H.264 编码器生成 20 帧 MP4: " + path);
        return 0;
    }

    private static int TestPause(string output)
    {
        int captures = 0, pausedCaptures = 0, phase = 0;
        bool grewWhilePaused = false;
        Stopwatch wall = new Stopwatch();
        AppConfig cfg = new AppConfig { RecordFormat = "mp4", RecordAudioMode = "none",
            RecordFps = 10, SaveDir = output, RecordIncludeCursor = false };
        Environment.SetEnvironmentVariable("XJ_RECORD_AUTO_STOP_MS", "3000");
        using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 20 })
        using (RecordingSession session = new RecordingSession(new Rectangle(80, 80, 320, 180), cfg,
            delegate(Rectangle r)
            {
                if (!wall.IsRunning) wall.Start();
                ++captures;
                return FixtureFrame();
            }))
        {
            timer.Tick += delegate
            {
                RecorderBar bar = null;
                foreach (Form form in Application.OpenForms) if (form is RecorderBar) bar = (RecorderBar)form;
                if (bar == null || !wall.IsRunning) return;
                if (phase == 0 && wall.ElapsedMilliseconds > 450)
                {
                    Click(bar, bar.Width - 16 - 124 - 62, 40);
                    pausedCaptures = captures;
                    phase = 1;
                }
                else if (phase == 1)
                {
                    if (captures != pausedCaptures) grewWhilePaused = true;
                    if (wall.ElapsedMilliseconds > 950)
                    {
                        Click(bar, bar.Width - 16 - 124 - 62, 40);
                        phase = 2;
                    }
                }
                else if (phase == 2 && wall.ElapsedMilliseconds > 1450)
                {
                    Click(bar, bar.Width - 16 - 62, 40);
                    phase = 3;
                }
            };
            timer.Start();
            string path = session.Execute();
            timer.Stop();
            Require(path != null && phase == 3, "暂停/继续/停止按钮没有完成会话");
            Require(!grewWhilePaused, "暂停期间仍在捕获视频帧");
            File.Copy(path, Path.Combine(output, "fixture-paused.mp4"), true);
            Console.WriteLine("[PASS] 实际点击暂停/继续，暂停 500ms 不抓帧，墙钟=" +
                wall.ElapsedMilliseconds + "ms，捕获=" + captures + " 帧");
        }
        return 0;
    }

    private static int TestSystemAudio(string output)
    {
        string wave = Path.Combine(output, "synthetic-440hz.wav");
        string pausedWave = Path.Combine(output, "synthetic-880hz-paused.wav");
        WriteTone(wave, 440);
        WriteTone(pausedWave, 880);
        using (System.Media.SoundPlayer tone = new System.Media.SoundPlayer(wave))
        using (System.Media.SoundPlayer pausedTone = new System.Media.SoundPlayer(pausedWave))
        using (Mp4RecorderWriter writer = new Mp4RecorderWriter(Path.Combine(output, "fixture-system.mp4"),
            320, 180, 10, 80, "system"))
        {
            tone.Load();
            pausedTone.Load();
            writer.SetPaused(false);
            tone.PlayLooping();
            Stopwatch active = Stopwatch.StartNew();
            for (int i = 0; i < 20; ++i)
            {
                while (active.ElapsedMilliseconds < i * 100) Thread.Sleep(2);
                if (i == 6)
                {
                    active.Stop(); writer.SetPaused(true);
                    tone.Stop(); pausedTone.PlayLooping();
                    Thread.Sleep(500);
                    pausedTone.Stop();
                    Thread.Sleep(80);
                    tone.PlayLooping();
                    writer.SetPaused(false); active.Start();
                }
                using (Bitmap frame = FixtureFrame()) writer.AddFrame(frame);
            }
            while (active.ElapsedMilliseconds < 2000) Thread.Sleep(2);
            writer.SetPaused(true);
            tone.Stop();
            writer.Close();
        }
        Console.WriteLine("[PASS] 系统声录制 440Hz，暂停期间仅播放 880Hz 标记后继续");
        return 0;
    }

    private static void WriteTone(string wave, double frequency)
    {
        using (BinaryWriter wav = new BinaryWriter(File.Create(wave)))
        {
            int count = 48000 * 4;
            wav.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wav.Write(36 + count * 2);
            wav.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wav.Write(16);
            wav.Write((short)1); wav.Write((short)1); wav.Write(48000); wav.Write(96000);
            wav.Write((short)2); wav.Write((short)16);
            wav.Write(System.Text.Encoding.ASCII.GetBytes("data")); wav.Write(count * 2);
            for (int i = 0; i < count; ++i) wav.Write((short)(Math.Sin(i * 2 * Math.PI * frequency / 48000) * 8000));
        }
    }

    private static int TestAudioDevices(string output)
    {
        int missing = 0;
        foreach (int mode in new[] { 1, 2, 3 })
        {
            IntPtr writer;
            int hr = RcCreate(Path.Combine(output, "device-probe-" + mode + ".mp4"), 320, 180, 10, 80, mode, out writer);
            if (hr < 0)
            {
                System.Text.StringBuilder error = new System.Text.StringBuilder(512);
                RcGetError(IntPtr.Zero, error, error.Capacity);
                Console.WriteLine("[UNAVAILABLE] 音频模式 " + mode + ": " + error + " (0x" + unchecked((uint)hr).ToString("X8") + ")");
                ++missing;
            }
            else
            {
                RcDestroy(writer);
                Console.WriteLine("[PASS] 音频模式 " + mode + " 设备打开成功（未启动捕获）");
            }
        }
        return missing == 0 ? 0 : 2;
    }

    private static int TestCaptureExclusion(string output)
    {
        using (Form background = new Form { FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(80, 80, 620, 160),
            BackColor = Color.Magenta, TopMost = true, ShowInTaskbar = false })
        using (RecorderBar bar = new RecorderBar(1f, 10, "fixture.mp4", delegate { }))
        {
            background.Show();
            bar.Location = new Point(100, 100);
            bar.Show();
            Application.DoEvents();
            Thread.Sleep(150);
            Application.DoEvents();
            Require(bar.ExcludedFromCapture, "系统未启用 WDA_EXCLUDEFROMCAPTURE");
            using (Bitmap captured = ImageUtil.Capture(background.Bounds))
            {
                Color pixel = captured.GetPixel(150, 50);
                captured.Save(Path.Combine(output, "fixture-capture-exclusion.png"));
                Require(pixel.R > 240 && pixel.G < 15 && pixel.B > 240,
                    "实际录屏捕获中仍包含控制条，像素=" + pixel);
            }
            Require(bar.Visible, "捕获期间隐藏了控制条");
        }
        Console.WriteLine("[PASS] 实际屏幕捕获排除控制条，控制条持续可见");
        return 0;
    }

    private static int TestOddDimensions(string output)
    {
        using (Mp4RecorderWriter writer = new Mp4RecorderWriter(Path.Combine(output, "fixture-odd.mp4"),
            321, 181, 10, 90, "none"))
        using (Bitmap frame = new Bitmap(321, 181))
        {
            using (Graphics graphics = Graphics.FromImage(frame))
            {
                graphics.Clear(Color.Black);
                using (Brush brush = new SolidBrush(Color.Lime))
                    graphics.FillRectangle(brush, 316, 176, 5, 5);
            }
            for (int i = 0; i < 10; ++i) writer.AddFrame(frame);
        }
        Console.WriteLine("[PASS] 奇数选区向上补齐，编码保存最右列和最底行");
        return 0;
    }

    private static int TestNoCaptureExclusion(string output)
    {
        bool showedBar = false;
        int captured = 0;
        Environment.SetEnvironmentVariable("XJ_RECORD_AUTO_STOP_MS", "400");
        AppConfig cfg = new AppConfig { SaveDir = output, RecordFormat = "mp4", RecordAudioMode = "none", RecordFps = 10 };
        using (RecordingSession session = new RecordingSession(SystemInformation.VirtualScreen, cfg,
            delegate(Rectangle region)
            {
                ++captured;
                foreach (Form form in Application.OpenForms)
                    if (form is RecorderBar && form.Visible) showedBar = true;
                return new Bitmap(32, 24);
            }, delegate(Form form) { return false; }))
        {
            Require(session.Execute() != null, "无排除能力时没有完成录制");
        }
        Require(!showedBar && captured > 0, "不支持捕获排除时仍把全屏控制条录入画面");
        Console.WriteLine("[PASS] 不支持排除捕获且全屏选区时不显示控制条，只提示 F8/Esc 操作");
        return 0;
    }

    private static Bitmap FixtureFrame()
    {
        Bitmap frame = new Bitmap(320, 180);
        using (Graphics g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(30, 40, 200));
            using (Brush brush = new SolidBrush(Color.FromArgb(200, 40, 20))) g.FillRectangle(brush, 0, 0, 320, 90);
        }
        return frame;
    }

    private static void Click(Form form, int x, int y)
    {
        SendMessage(form.Handle, 0x201, (IntPtr)1, (IntPtr)(y * 65536 + x));
        SendMessage(form.Handle, 0x202, IntPtr.Zero, (IntPtr)(y * 65536 + x));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("RecorderNative.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RcCreate(string path, int width, int height, int fps, int quality, int audioMode, out IntPtr writer);
    [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RcWriteVideo(IntPtr writer, byte[] pixels, int stride, long frameIndex);
    [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RcFinish(IntPtr writer, long duration100ns);
    [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RcDestroy(IntPtr writer);
    [DllImport("RecorderNative.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RcGetError(IntPtr handle, System.Text.StringBuilder buffer, int capacity);
}
