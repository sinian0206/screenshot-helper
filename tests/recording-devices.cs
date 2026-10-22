using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using XiaoJie;

internal static class RecordingDeviceTests
{
    private static int _failed;
    private static string _output;
    [STAThread]
    private static int Main(string[] args)
    {
        _output = args[0];
        Application.EnableVisualStyles();
        Run("设备枚举与参数选择不启动音源", NoImplicitCapture);
        Run("显式测试、切换音源和关闭回收测试资源", ExplicitTestAndCleanup);
        Run("测试错误可见且不阻塞静音录制", VisibleFailure);
        Run("设备选择与倒计时只传入本次会话", SessionOptions);
        Run("倒计时可取消，取消后无会话或输出", CountdownCancel);
        Run("倒计时完成后仅执行一次录制", CountdownStart);
        Run("100/150/200% 准备窗设备控件未越界", Layout);
        Run("原生枚举和选定无效设备错误", NativeDevices);
        Run("系统声合成音测试与内存 WAV", SyntheticAudio);
        Console.WriteLine("结果: " + (9 - _failed) + " 项通过，" + _failed + " 项失败");
        return _failed == 0 ? 0 : 1;
    }
    private static void Run(string name, Action test) { try { test(); Console.WriteLine("[PASS] " + name); } catch (Exception e) { ++_failed; Console.WriteLine("[FAIL] " + name + ": " + e); } }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    private static RecordReadyForm Ready(FakeAudio service, float scale = 1f) { return new RecordReadyForm(new Rectangle(-22000, -22000, 320, 180), new AppConfig(), scale, service); }
    private static void Show(Form form) { form.Location = new Point(-25000, -25000); form.Show(); Application.DoEvents(); }
    private static void NoImplicitCapture()
    {
        FakeAudio audio = new FakeAudio();
        using (RecordReadyForm form = Ready(audio))
        {
            Show(form);
            Field<ComboBox>(form, "_audio").SelectedIndex = 3;
            Field<ComboBox>(form, "_microphoneDevice").SelectedIndex = 1;
            Require(audio.Starts == 0, "仅选择设备就启动了音源");
            Require(Field<Button>(form, "_test").Enabled && !Field<Button>(form, "_listen").Enabled, "测试/试听状态不正确");
        }
    }
    private static void ExplicitTestAndCleanup()
    {
        FakeAudio audio = new FakeAudio();
        using (RecordReadyForm form = Ready(audio))
        {
            Show(form); Field<ComboBox>(form, "_audio").SelectedIndex = 2;
            Field<ComboBox>(form, "_microphoneDevice").SelectedIndex = 1;
            Field<Button>(form, "_test").PerformClick();
            Require(audio.Starts == 1 && audio.Mode == 2 && audio.Microphone == "mic-fixture", "显式测试没有使用所选麦克风");
            Field<ComboBox>(form, "_audio").SelectedIndex = 1;
            Require(audio.Probe.Disposed, "切换音源没有停止测试");
            Field<Button>(form, "_test").PerformClick();
            form.Close();
            Require(audio.Probe.Disposed, "关闭窗口没有释放测试句柄");
        }
    }
    private static void VisibleFailure()
    {
        FakeAudio audio = new FakeAudio { Fail = true };
        using (RecordReadyForm form = Ready(audio))
        {
            Show(form); Field<ComboBox>(form, "_audio").SelectedIndex = 1;
            Field<Button>(form, "_test").PerformClick();
            Require(Field<Label>(form, "_audioStatus").Text.Contains("fixture unavailable"), "测试错误没有显示");
            Field<ComboBox>(form, "_audio").SelectedIndex = 0;
            Field<Button>(form, "_start").PerformClick();
            Require(form.SelectedConfig != null && form.SelectedConfig.RecordAudioMode == "none", "音源失败阻塞静音录制");
        }
    }
    private static void SessionOptions()
    {
        FakeAudio audio = new FakeAudio(); AppConfig config = new AppConfig();
        using (RecordReadyForm form = new RecordReadyForm(new Rectangle(-22000, -22000, 320, 180), config, 1f, audio))
        {
            Show(form); Field<ComboBox>(form, "_audio").SelectedIndex = 3;
            Field<ComboBox>(form, "_microphoneDevice").SelectedIndex = 1;
            Field<ComboBox>(form, "_systemDevice").SelectedIndex = 1;
            Field<ComboBox>(form, "_countdown").SelectedIndex = 2;
            Field<Button>(form, "_start").PerformClick();
            Require(form.SelectedConfig.RecordMicrophoneDeviceId == "mic-fixture" && form.SelectedConfig.RecordSystemDeviceId == "system-fixture", "会话丢失设备");
            Require(form.SelectedConfig.RecordCountdownSec == 5 && config.RecordCountdownSec == 0 && config.RecordMicrophoneDeviceId == "", "会话选择修改全局参数");
        }
    }
    private static void CountdownCancel()
    {
        using (RecordingCountdownForm form = new RecordingCountdownForm(new Rectangle(-22000, -22000, 320, 180), 3, 1f))
        {
            Show(form); Require(!form.Completed, "倒计时立即完成");
            form.Close(); Require(!form.Completed, "取消倒计时被视为开始");
        }
        int calls = 0;
        AppConfig config = new AppConfig { RecordCountdownSec = 3, SaveDir = Path.Combine(_output, "never-created") };
        WithDialogs(delegate(RecordReadyForm ready) { Field<Button>(ready, "_start").PerformClick(); }, delegate(RecordingCountdownForm countdown) { countdown.Close(); }, delegate
        {
            RecordingFlowResult result = RecordingFlow.Run(new Rectangle(-22000, -22000, 320, 180), config, null, delegate { ++calls; return null; });
            Require(!result.Started && calls == 0 && !Directory.Exists(config.SaveDir), "取消倒计时产生了媒体输出");
        });
    }
    private static void CountdownStart()
    {
        int calls = 0; System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        WithDialogs(delegate(RecordReadyForm ready) { Field<Button>(ready, "_start").PerformClick(); }, null, delegate
        {
            RecordingFlowResult result = RecordingFlow.Run(new Rectangle(-22000, -22000, 320, 180), new AppConfig { RecordCountdownSec = 3 }, null,
                delegate { ++calls; Require(clock.ElapsedMilliseconds >= 2900, "倒计时未结束就开始捕获"); return "fixture.mp4"; });
            Require(result.Started && calls == 1 && result.SavePath == "fixture.mp4", "倒计时结束未单次开始");
        });
    }
    private static void WithDialogs(Action<RecordReadyForm> readyAction, Action<RecordingCountdownForm> countdownAction, Action run)
    {
        bool readyHandled = false, countdownHandled = false;
        using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 40 })
        {
            timer.Tick += delegate
            {
                foreach (Form form in Application.OpenForms)
                {
                    if (form is RecordReadyForm && !readyHandled) { readyHandled = true; readyAction((RecordReadyForm)form); break; }
                    if (form is RecordingCountdownForm && !countdownHandled && countdownAction != null) { countdownHandled = true; countdownAction((RecordingCountdownForm)form); break; }
                }
            };
            timer.Start(); run(); timer.Stop();
        }
    }
    private static void Layout()
    {
        foreach (float scale in new[] { 1f, 1.5f, 2f }) using (RecordReadyForm form = Ready(new FakeAudio(), scale))
        {
            Show(form);
            foreach (Control control in form.Controls) Require(form.ClientRectangle.Contains(control.Bounds), "控件越界: " + control.Text + " " + scale);
            using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(_output, "ready-devices-" + (int)(scale * 100) + ".png")); }
        }
    }
    private static void NativeDevices()
    {
        NativeAudioServices audio = new NativeAudioServices();
        Require(audio.List(true).Count >= 1 && audio.List(false).Count >= 1, "缺少默认设备选项");
        bool failed = false;
        try { using (IAudioProbe probe = audio.Start(1, "missing-device-fixture", "")) { } } catch (InvalidOperationException e) { failed = e.Message.Contains("设备"); }
        Require(failed, "失效设备未明确报错");
    }
    private static void SyntheticAudio()
    {
        NativeAudioServices audio = new NativeAudioServices();
        using (MemoryStream wave = AudioWave.CreateTone(440, 2))
        using (System.Media.SoundPlayer player = new System.Media.SoundPlayer(wave))
        using (IAudioProbe probe = audio.Start(1, "", ""))
        {
            player.PlayLooping();
            float peak = 0; AudioProbeSnapshot last = null;
            System.Diagnostics.Stopwatch wait = System.Diagnostics.Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 2500) { Thread.Sleep(30); last = probe.Poll(); peak = Math.Max(peak, last.Peak); }
            player.Stop();
            Require(peak > 0.05f, "合成440Hz音频没有出现在系统声电平中");
            byte[] pcm = probe.GetPcm();
            using (MemoryStream captured = AudioWave.WrapPcm(pcm)) { Require(captured.Length > 1000 && captured.ReadByte() == 'R', "没有得到可试听的内存WAV"); }
        }
    }
    private sealed class FakeAudio : IAudioServices
    {
        internal int Starts, Mode; internal string Microphone; internal bool Fail; internal FakeProbe Probe;
        public IList<AudioDeviceInfo> List(bool system) { return new[] { new AudioDeviceInfo("", "默认设备"), new AudioDeviceInfo(system ? "system-fixture" : "mic-fixture", "Fixture设备") }; }
        public IAudioProbe Start(int mode, string system, string microphone) { ++Starts; Mode = mode; Microphone = microphone; if (Fail) throw new InvalidOperationException("fixture unavailable"); return Probe = new FakeProbe(); }
    }
    private sealed class FakeProbe : IAudioProbe
    {
        internal bool Disposed;
        public AudioProbeSnapshot Poll() { return new AudioProbeSnapshot(0.3f, false); }
        public byte[] GetPcm() { return new byte[4800]; }
        public void Dispose() { Disposed = true; }
    }
}
