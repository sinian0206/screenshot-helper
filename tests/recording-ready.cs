using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using XiaoJie;

internal static class RecordingReadyTests
{
    private const BindingFlags InstancePrivate = BindingFlags.NonPublic | BindingFlags.Instance;
    private static string _output;
    private static int _failed;
    private static int _passed;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        typeof(Toast).GetField("_ctx", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, new SilentToastContext());
        _output = args[0];
        Directory.CreateDirectory(_output);
        Run("等待显式开始时不创建目录、文件或媒体会话", TestExplicitStart);
        Run("四种声音选择传入会话且不改变全局设置", TestAudioModes);
        Run("AVI 加声音明确切换 MP4 并保留音源", TestAudioFormat);
        Run("重新选择、Esc 和关闭均取消准备且不启动媒体会话", TestCancel);
        Run("重新选择后再次拖框重新打开准备窗口", TestReselect);
        Run("确认后会话异常保留已开始状态", TestRecordingFailure);
        Run("开始按钮后实际 AVI 编码及保存，等待期间不计时", TestActualSession);
        Run("准备窗显示点击穿透选区边框并优先放在选区外", TestBorderAndPlacement);
        Run("紧凑控制条暂停、继续、停止实际事件及局部重绘", TestBarEvents);
        Run("100%、150%、200% 准备窗及控制条截图与高对比度", TestVisuals);
        Console.WriteLine("结果: " + _passed + " 项通过，" + _failed + " 项失败");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); ++_passed; Console.WriteLine("[PASS] " + name); }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            ++_failed;
            Console.WriteLine("[FAIL] " + name + ": " + error);
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static AppConfig Config()
    {
        return new AppConfig { SaveDir = Path.Combine(_output, "never-created-" + Guid.NewGuid().ToString("N")),
            RecordFormat = "mp4", RecordAudioMode = "none", RecordFps = 23, RecordIncludeCursor = false };
    }

    private static void TestExplicitStart()
    {
        AppConfig config = Config();
        int sessions = 0;
        RecordingFlowResult result = WithReady(delegate(RecordReadyForm ready)
        {
            Require(sessions == 0, "点击开始之前已经进入媒体会话");
            Require(!NativeRecorderLoaded(), "准备阶段加载了负责音频设备捕获的原生录制组件");
            Require(!Directory.Exists(config.SaveDir), "准备阶段创建了保存目录");
            Require((AppConfig)Get(ready, "_config") != config, "准备窗口直接持有全局配置实例");
            Require(ready.SelectedConfig == null, "确认前已提交参数");
            ((Button)Get(ready, "_start")).PerformClick();
        }, delegate
        {
            return RecordingFlow.Run(new Rectangle(100, 100, 320, 180), config, null,
                delegate(Rectangle region, AppConfig session)
                {
                    ++sessions;
                    Require(FindReady() == null, "准备窗口尚未销毁就开始捕获");
                    Require(region == new Rectangle(100, 100, 320, 180), "会话丢失物理选区");
                    Require(session.RecordFps == 23, "会话丢失既有帧率设置");
                    return "fixture.mp4";
                });
        });
        Require(sessions == 1 && result.Started && result.SavePath == "fixture.mp4", "开始按钮没有且仅启动一次会话");
        Require(!Directory.Exists(config.SaveDir), "准备流程创建了额外目录");
    }

    private static void TestAudioModes()
    {
        string[] modes = { "none", "system", "microphone", "mixed" };
        for (int i = 0; i < modes.Length; ++i)
        {
            int index = i;
            AppConfig config = Config();
            AppConfig observed = null;
            RecordingFlowResult result = WithReady(delegate(RecordReadyForm ready)
            {
                ((ComboBox)Get(ready, "_audio")).SelectedIndex = index;
                ((Button)Get(ready, "_start")).PerformClick();
            }, delegate
            {
                return RecordingFlow.Run(new Rectangle(100, 100, 320, 180), config, null,
                    delegate(Rectangle region, AppConfig session) { observed = session; return null; });
            });
            Require(result.Started && observed != null && observed.RecordAudioMode == modes[i], "声音选择未传递: " + modes[i]);
            Require(observed != config && config.RecordAudioMode == "none" && config.RecordFormat == "mp4", "本次录制改变了全局音频设置");
        }
    }

    private static void TestAudioFormat()
    {
        AppConfig config = Config();
        config.RecordFormat = "avi";
        config.RecordAudioMode = "system";
        WithReady(delegate(RecordReadyForm ready)
        {
            ComboBox format = (ComboBox)Get(ready, "_format");
            ComboBox audio = (ComboBox)Get(ready, "_audio");
            Require(format.SelectedIndex == 0 && audio.SelectedIndex == 1, "初始 AVI 音频配置未显式切换 MP4");
            Require(((Label)Get(ready, "_formatHint")).Text.Contains("AVI 不支持音频"), "格式切换缺少解释");
            format.SelectedIndex = 1;
            Require(format.SelectedIndex == 0 && audio.SelectedIndex == 1, "重新选 AVI 时丢弃了音轨");
            ((Button)Get(ready, "_start")).PerformClick();
        }, delegate
        {
            return RecordingFlow.Run(new Rectangle(100, 100, 320, 180), config, null,
                delegate(Rectangle region, AppConfig session)
                {
                    Require(session.RecordFormat == "mp4" && session.RecordAudioMode == "system", "编码器参数未保留声音");
                    return null;
                });
        });
        Require(config.RecordFormat == "avi" && config.RecordAudioMode == "system", "自动切换修改了全局配置");
    }

    private static void TestCancel()
    {
        for (int mode = 0; mode < 3; ++mode)
        {
            int action = mode, sessions = 0;
            AppConfig config = Config();
            RecordingFlowResult result = WithReady(delegate(RecordReadyForm ready)
            {
                if (action == 0) ((Button)Get(ready, "_cancel")).PerformClick();
                else if (action == 1)
                    typeof(Form).GetMethod("ProcessDialogKey", InstancePrivate).Invoke(ready, new object[] { Keys.Escape });
                else ready.Close();
            }, delegate
            {
                return RecordingFlow.Run(new Rectangle(100, 100, 320, 180), config, null,
                    delegate { ++sessions; return "unexpected.mp4"; });
            });
            Require(!result.Started && result.SavePath == null && sessions == 0, "取消准备启动了媒体会话，mode=" + mode);
            Require(!Directory.Exists(config.SaveDir), "取消准备创建了输出，mode=" + mode);
        }
    }

    private static void TestReselect()
    {
        AppConfig config = Config();
        using (OverlayForm overlay = new OverlayForm(new Bitmap(640, 360), new Rectangle(-22000, -22000, 640, 360), config, null, null))
        {
            overlay.RecordMode = true;
            overlay.SuppressActivation = true;
            overlay.Show();
            Application.DoEvents();
            for (int attempt = 0; attempt < 2; ++attempt)
            {
                int offset = attempt * 35;
                WithReady(delegate(RecordReadyForm ready)
                {
                    Require((bool)Get(overlay, "_hasSelection"), "框选结果没有进入准备阶段");
                    ((Button)Get(ready, "_cancel")).PerformClick();
                }, delegate
                {
                    Mouse(overlay, "OnMouseDown", 60 + offset, 50 + offset);
                    Mouse(overlay, "OnMouseMove", 300 + offset, 210 + offset);
                    Mouse(overlay, "OnMouseUp", 300 + offset, 210 + offset);
                    return 0;
                });
                Require(!overlay.IsDisposed && overlay.Visible && !(bool)Get(overlay, "_hasSelection"), "取消后未恢复重新框选状态");
                Require(!((Control)Get(overlay, "_toolbar")).Visible, "录屏重新框选时显示了截图工具栏");
            }
        }
        Require(!Directory.Exists(config.SaveDir), "重新框选时启动了输出");
    }

    private static void TestRecordingFailure()
    {
        RecordingFlowResult result = WithReady(delegate(RecordReadyForm ready)
        {
            ((Button)Get(ready, "_start")).PerformClick();
        }, delegate
        {
            return RecordingFlow.Run(new Rectangle(100, 100, 320, 180), Config(), null,
                delegate { throw new InvalidOperationException("fixture recording failure"); });
        });
        Require(result.Started && result.SavePath == null, "确认后的录制失败被当成取消准备");
    }

    private static void TestActualSession()
    {
        AppConfig config = Config();
        config.RecordFormat = "avi";
        config.RecordFps = 10;
        int captures = 0;
        RecordingSession observed = null;
        string oldAutoStop = Environment.GetEnvironmentVariable("XJ_RECORD_AUTO_STOP_MS");
        Environment.SetEnvironmentVariable("XJ_RECORD_AUTO_STOP_MS", "350");
        try
        {
            RecordingFlowResult result = WithReady(delegate(RecordReadyForm ready)
            {
                Require(observed == null && captures == 0 && !Directory.Exists(config.SaveDir), "开始前会话已计时或输出");
                ((Button)Get(ready, "_start")).PerformClick();
            }, delegate
            {
                return RecordingFlow.Run(new Rectangle(100, 100, 32, 24), config, null,
                    delegate(Rectangle region, AppConfig sessionConfig)
                    {
                        using (RecordingSession session = new RecordingSession(region, sessionConfig,
                            delegate(Rectangle selected)
                            {
                                ++captures;
                                Bitmap bitmap = new Bitmap(32, 24);
                                using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Navy);
                                return bitmap;
                            }))
                        {
                            observed = session;
                            return session.Execute();
                        }
                    });
            });
            Require(result.Started && result.SavePath != null && File.Exists(result.SavePath), "开始按钮之后未得到 AVI 成片");
            Require(captures >= 3 && new FileInfo(result.SavePath).Length > 1000, "实际 AVI 捕获或文件内容不足");
        }
        finally { Environment.SetEnvironmentVariable("XJ_RECORD_AUTO_STOP_MS", oldAutoStop); }
    }

    private static void TestBorderAndPlacement()
    {
        Rectangle region = new Rectangle(Screen.PrimaryScreen.WorkingArea.Left + 40, Screen.PrimaryScreen.WorkingArea.Top + 40, 320, 180);
        using (RecordReadyForm ready = new RecordReadyForm(region, Config(), 1f))
        {
            Require(!ready.Bounds.IntersectsWith(region), "准备窗没有优先放在选区外");
            ready.Show();
            Application.DoEvents();
            RecorderBorder border = (RecorderBorder)Get(ready, "_border");
            Require(border != null && border.Visible, "准备阶段缺少选区边框");
            Point center = new Point(region.Left + region.Width / 2 - border.Left, region.Top + region.Height / 2 - border.Top);
            Require(!border.Region.IsVisible(center), "边框窗口填充了选区内容");
            Require(ready.FormBorderStyle == FormBorderStyle.FixedDialog, "准备窗缺少可拖动系统标题栏");
            ready.Close();
        }
    }

    private static void TestBarEvents()
    {
        int pauses = 0, stops = 0, fullInvalidations = 0;
        using (RecorderBar bar = new RecorderBar(1f, 15, "fixture.mp4", delegate { ++stops; }, delegate { ++pauses; }, delegate { return true; }))
        {
            ShowOffscreen(bar);
            Require(bar.Size == new Size(360, 44), "控制条尺寸仍然过大: " + bar.Size);
            Rectangle pause = (Rectangle)Get(bar, "_pauseRect"), stop = (Rectangle)Get(bar, "_stopRect");
            Require(!pause.IntersectsWith(stop) && bar.ClientRectangle.Contains(pause) && bar.ClientRectangle.Contains(stop), "控制条按钮重叠或越界");
            bar.Invalidated += delegate(object sender, InvalidateEventArgs e) { if (e.InvalidRect == bar.ClientRectangle) ++fullInvalidations; };
            bar.SetState(1.1, 16, 1000);
            bar.SetState(1.2, 17, 1001);
            Require(fullInvalidations == 0, "时间和统计更新造成整条刷新");
            Click(bar, pause);
            bar.SetPaused(true);
            Click(bar, pause);
            Click(bar, stop);
            Require(pauses == 2 && stops == 1, "暂停/继续/停止点击没有触发");
            ToolTip tooltip = (ToolTip)Get(bar, "_tooltip");
            Require(tooltip.GetToolTip(bar).Contains("17 帧") && tooltip.GetToolTip(bar).Contains("fixture.mp4"), "统计及文件位置未进入提示");
            bar.SetStopping();
            Click(bar, stop);
            Require(stops == 1, "保存收尾时重复停止");
        }
    }

    private static void TestVisuals()
    {
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            string suffix = ((int)(scale * 100)).ToString();
            using (RecordReadyForm ready = new RecordReadyForm(new Rectangle(-22000, -22000, 320, 180), Config(), scale))
            {
                ShowOffscreen(ready);
                Save(ready, "ready-" + suffix);
                foreach (Control control in ready.Controls)
                {
                    if (string.IsNullOrEmpty(control.Text)) continue;
                    Ratio(control.ForeColor, control.BackColor, suffix + "/" + control.Text);
                    Button button = control as Button;
                    if (button != null)
                    {
                        Ratio(button.ForeColor, button.FlatAppearance.MouseOverBackColor, suffix + "/hover/" + button.Text);
                        Ratio(button.ForeColor, button.FlatAppearance.MouseDownBackColor, suffix + "/pressed/" + button.Text);
                    }
                }
            }
            using (RecorderBar bar = new RecorderBar(scale, 15, "fixture.mp4", delegate { }, delegate { }, delegate { return true; }))
            {
                ShowOffscreen(bar);
                Require(bar.Size == new Size((int)Math.Round(360 * scale), (int)Math.Round(44 * scale)), "控制条 DPI 尺寸错误");
                bar.SetState(125, 1875, 4 * 1024 * 1024);
                Save(bar, "bar-" + suffix);
                bar.SetPaused(true);
                Save(bar, "bar-paused-" + suffix);
                bar.SetStopping();
                Save(bar, "bar-stopping-" + suffix);
            }
        }
        Ratio(UiTheme.TextDark, Color.White, "bar/time");
        Ratio(UiTheme.TextDark, Color.FromArgb(237, 244, 240), "bar/pause");
        Ratio(UiTheme.TextDark, Color.FromArgb(220, 235, 226), "bar/pause-hover");
        Ratio(Color.White, UiTheme.ActionRed, "bar/stop");
        Ratio(Color.White, UiTheme.ActionRedHover, "bar/stop-hover");
        Ratio(UiTheme.Disabled, UiTheme.DisabledBackground, "bar/stopping");
    }

    private static T WithReady<T>(Action<RecordReadyForm> action, Func<T> run)
    {
        Exception failure = null;
        bool interacted = false;
        int ticks = 0;
        using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 40 })
        {
            timer.Tick += delegate
            {
                RecordReadyForm ready = FindReady();
                if (ready == null || ++ticks < 3) return;
                timer.Stop();
                interacted = true;
                try
                {
                    ready.Location = new Point(-25000, -25000);
                    action(ready);
                }
                catch (Exception ex) { failure = ex; ready.Close(); }
            };
            timer.Start();
            T result = run();
            timer.Stop();
            if (failure != null) throw failure;
            Require(interacted, "没有实际进入录制准备窗口");
            return result;
        }
    }

    private static RecordReadyForm FindReady()
    {
        foreach (Form form in Application.OpenForms) if (form is RecordReadyForm) return (RecordReadyForm)form;
        return null;
    }

    private static bool NativeRecorderLoaded()
    {
        using (Process process = Process.GetCurrentProcess())
            foreach (ProcessModule module in process.Modules)
                if (string.Equals(module.ModuleName, "RecorderNative.dll", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private sealed class SilentToastContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object state) { }
    }

    private static object Get(object target, string field) { return target.GetType().GetField(field, InstancePrivate).GetValue(target); }
    private static void Mouse(Form form, string method, int x, int y)
    {
        form.GetType().GetMethod(method, InstancePrivate).Invoke(form, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
    }
    private static void Click(Form form, Rectangle rect) { Mouse(form, "OnMouseUp", rect.Left + rect.Width / 2, rect.Top + rect.Height / 2); }
    private static void ShowOffscreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-25000, -25000);
        form.Show();
        Application.DoEvents();
    }
    private static void Save(Control control, string name)
    {
        using (Bitmap bitmap = new Bitmap(control.Width, control.Height))
        {
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
            bitmap.Save(Path.Combine(_output, name + ".png"), ImageFormat.Png);
        }
    }
    private static void Ratio(Color foreground, Color background, string name)
    {
        double a = Luminance(foreground), b = Luminance(background);
        double ratio = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        Require(ratio >= 4.5, name + " 对比度不足: " + ratio);
    }
    private static double Luminance(Color value) { return 0.2126 * Linear(value.R) + 0.7152 * Linear(value.G) + 0.0722 * Linear(value.B); }
    private static double Linear(byte value) { double c = value / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
}
