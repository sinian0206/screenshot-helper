using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 托盘主控窗口（隐藏窗口承载全局热键、托盘图标、单实例消息）。
    /// </summary>
    internal sealed class TrayApp : Form
    {
        private const int HotkeyId = 0x5A11;

        private readonly AppConfig _cfg;
        private readonly ConfigStore _store;
        private readonly string[] _args;
        private bool _firstRun;
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _captureItem;
        private ToolStripMenuItem _autoStartItem;
        private OverlayForm _overlay;
        private SettingsForm _settings;
        private bool _hotkeyRegistered;
        private readonly Timer _captureTimer;
        private bool _capturePending;
        private bool _exitRequested;

        public TrayApp(AppConfig cfg, ConfigStore store, string[] args, bool firstRun)
        {
            _cfg = cfg;
            _store = store;
            _args = args ?? new string[0];
            _firstRun = firstRun;

            Text = AppInfo.HiddenWindowTitle;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-30000, -30000);
            Size = new Size(1, 1);
            Opacity = 0;
            ShowIcon = false;

            _captureTimer = new Timer();
            _captureTimer.Interval = 90;
            _captureTimer.Tick += delegate
            {
                _captureTimer.Stop();
                DoStartCapture();
            };

            BuildTray();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RegisterHotkey();
            UpdateMenuState();
            if (_firstRun)
            {
                BeginInvoke((Action)delegate { ShowSettings(true); });
            }
            else if (Program.HasArg(_args, "--capture"))
            {
                BeginInvoke((Action)RequestCapture);
            }
            else if (Program.HasArg(_args, "--settings"))
            {
                BeginInvoke((Action)delegate { ShowSettings(false); });
            }
        }

        private void BuildTray()
        {
            _menu = new ContextMenuStrip();
            _menu.Font = new Font(UiTheme.UiFontName, 9f);
            _menu.ShowImageMargin = false;

            _captureItem = new ToolStripMenuItem("截图  (Alt+A)");
            _captureItem.Font = new Font(_menu.Font, FontStyle.Bold);
            _captureItem.Click += delegate { RequestCapture(); };
            _menu.Items.Add(_captureItem);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settings = new ToolStripMenuItem("设置…");
            settings.Click += delegate { ShowSettings(false); };
            _menu.Items.Add(settings);

            _autoStartItem = new ToolStripMenuItem("开机自启动");
            _autoStartItem.Click += delegate { ToggleAutostart(); };
            _menu.Items.Add(_autoStartItem);

            ToolStripMenuItem openDir = new ToolStripMenuItem("打开保存文件夹");
            openDir.Click += delegate { OpenSaveDir(); };
            _menu.Items.Add(openDir);

            ToolStripMenuItem help = new ToolStripMenuItem("使用说明");
            help.Click += delegate { ShowHelp(); };
            _menu.Items.Add(help);

            ToolStripMenuItem about = new ToolStripMenuItem("关于");
            about.Click += delegate { ShowAbout(); };
            _menu.Items.Add(about);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitApp(); };
            _menu.Items.Add(exit);

            _tray = new NotifyIcon();
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                _tray.Icon = icon != null ? icon : SystemIcons.Application;
            }
            catch { _tray.Icon = SystemIcons.Application; }
            _tray.Text = AppInfo.Name + "（" + _cfg.Hotkey + " 截图）";
            _tray.ContextMenuStrip = _menu;
            _tray.DoubleClick += delegate { RequestCapture(); };
            _tray.Visible = true;
        }

        private void UpdateMenuState()
        {
            try
            {
                if (_captureItem != null) _captureItem.Text = "截图  (" + _cfg.Hotkey + ")";
                if (_autoStartItem != null) _autoStartItem.Checked = Autostart.IsEnabled();
                if (_tray != null) _tray.Text = AppInfo.Name + "（" + _cfg.Hotkey + " 截图）";
            }
            catch { }
        }

        private bool TryRegisterHotkey(HotkeySpec spec)
        {
            bool ok = Native.RegisterHotKey(Handle, HotkeyId,
                HotkeyUtil.Modifiers(spec) | Native.MOD_NOREPEAT, (uint)spec.Key);
            if (ok) _hotkeyRegistered = true;
            return ok;
        }

        private void RegisterHotkey()
        {
            try
            {
                if (_hotkeyRegistered)
                {
                    Native.UnregisterHotKey(Handle, HotkeyId);
                    _hotkeyRegistered = false;
                }
                HotkeySpec spec;
                if (!HotkeyUtil.TryParse(_cfg.Hotkey, out spec))
                {
                    Log.Write("热键格式无效: " + _cfg.Hotkey);
                    Toast.Show("热键格式无效：" + _cfg.Hotkey + "，请在设置中重新设置", 4000);
                    return;
                }
                if (TryRegisterHotkey(spec))
                {
                    Log.Write("热键注册成功: " + _cfg.Hotkey);
                    UpdateMenuState();
                    return;
                }
                // 热键被占用时自动降级（Alt+A 常被微信/QQ 截图占用）
                Log.Write("热键注册失败: " + _cfg.Hotkey);
                string[] fallbacks = new string[] { "Alt+Shift+A", "Ctrl+Alt+A", "Alt+Shift+S", "Ctrl+Alt+S" };
                foreach (string candidate in fallbacks)
                {
                    if (string.Equals(candidate, _cfg.Hotkey, StringComparison.OrdinalIgnoreCase)) continue;
                    HotkeySpec fallbackSpec;
                    if (!HotkeyUtil.TryParse(candidate, out fallbackSpec)) continue;
                    if (TryRegisterHotkey(fallbackSpec))
                    {
                        string old = _cfg.Hotkey;
                        _cfg.Hotkey = candidate;
                        try { _store.Save(_cfg); } catch { }
                        Log.Write("热键 " + old + " 被占用，已自动改用 " + candidate);
                        Toast.Show("热键 " + old + " 被占用（常被微信截图占用），已自动改用 " + candidate + "，可在设置中修改", 6000);
                        UpdateMenuState();
                        return;
                    }
                }
                Toast.Show("全局热键 " + _cfg.Hotkey + " 注册失败（可能被其他软件占用），请到设置中更换", 5000);
                UpdateMenuState();
            }
            catch (Exception ex)
            {
                Log.Write("注册热键异常: " + ex.Message);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                RequestCapture();
                return;
            }
            if (m.Msg == AppInfo.WM_APP_TRIGGER_CAPTURE)
            {
                RequestCapture();
                return;
            }
            if (m.Msg == AppInfo.WM_APP_SHOW_TOAST)
            {
                Toast.Show("微信截图助手已在运行，按 " + _cfg.Hotkey + " 开始截图");
                return;
            }
            if (m.Msg == AppInfo.WM_APP_OPEN_SETTINGS)
            {
                ShowSettings(false);
                return;
            }
            base.WndProc(ref m);
        }

        private void RequestCapture()
        {
            if (_capturePending) return;
            if (_overlay != null && !_overlay.IsDisposed)
            {
                try { _overlay.Activate(); } catch { }
                return;
            }
            if (_settings != null && !_settings.IsDisposed)
            {
                Toast.Show("请先关闭设置窗口再截图");
                return;
            }
            _capturePending = true;
            _captureTimer.Start();
        }

        private void DoStartCapture()
        {
            _capturePending = false;
            if (_overlay != null && !_overlay.IsDisposed) return;
            try
            {
                Toast.HideCurrent();
                Rectangle vs = DpiUtil.VirtualScreen;
                Bitmap screenshot = ImageUtil.Capture(vs);
                OverlayForm overlay = new OverlayForm(screenshot, vs, _cfg, _store, delegate { ShowSettings(false); });
                _overlay = overlay;
                overlay.FormClosed += delegate
                {
                    if (ReferenceEquals(_overlay, overlay)) _overlay = null;
                };
                overlay.Show();
                overlay.Activate();
            }
            catch (Exception ex)
            {
                Log.Write("启动截图失败: " + ex);
                Toast.Show("截图失败：" + ex.Message, 4000);
            }
        }

        private void ShowSettings(bool firstRun)
        {
            bool wasFirstRun = firstRun;
            if (_settings != null && !_settings.IsDisposed)
            {
                try { _settings.Activate(); } catch { }
                return;
            }
            try
            {
                SettingsForm form = new SettingsForm(_cfg, _store, firstRun);
                _settings = form;
                form.Saved += delegate
                {
                    try
                    {
                        if (!_cfg.FirstRunDone)
                        {
                            _cfg.FirstRunDone = true;
                            _store.Save(_cfg);
                        }
                        RegisterHotkey();
                        UpdateMenuState();
                        Toast.Show("设置已保存");
                    }
                    catch (Exception ex)
                    {
                        Log.Write("应用设置失败: " + ex.Message);
                    }
                };
                form.FormClosed += delegate { _settings = null; };
                _firstRun = false;
                form.ShowDialog();
                if (!_cfg.FirstRunDone)
                {
                    // 用户取消了首次配置，下次启动继续引导
                    _firstRun = true;
                }
                else if (wasFirstRun)
                {
                    Toast.Show("配置完成！按 " + _cfg.Hotkey + " 开始截图", 3000);
                }
            }
            catch (Exception ex)
            {
                Log.Write("打开设置失败: " + ex);
                MessageBox.Show("打开设置窗口失败：" + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _settings = null;
                UpdateMenuState();
            }
        }

        private void ToggleAutostart()
        {
            try
            {
                _cfg.StartWithWindows = !Autostart.IsEnabled();
                Autostart.SetEnabled(_cfg.StartWithWindows);
                _store.Save(_cfg);
                UpdateMenuState();
                Toast.Show(_cfg.StartWithWindows ? "已开启开机自启动" : "已关闭开机自启动");
            }
            catch (Exception ex)
            {
                Log.Write("切换自启动失败: " + ex.Message);
                Toast.Show("设置开机自启失败：" + ex.Message);
            }
        }

        private void OpenSaveDir()
        {
            try
            {
                string dir = _cfg.EnsureSaveDir();
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                Toast.Show("打开保存文件夹失败：" + ex.Message);
            }
        }

        private void ShowHelp()
        {
            string helpFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "使用说明.txt");
            try
            {
                if (File.Exists(helpFile))
                {
                    Process.Start("notepad.exe", "\"" + helpFile + "\"");
                    return;
                }
            }
            catch { }
            MessageBox.Show(
                AppInfo.FullName + "\r\n\r\n" +
                "1. 按 " + _cfg.Hotkey + " 开始截图；拖拽框选，或直接单击窗口智能选中。\r\n" +
                "2. 工具栏：矩形、椭圆、表情、箭头、画笔、马赛克、文字、翻译、文字提取、长截图；右侧撤销/保存/钉桌面/转发/关闭/完成。\r\n" +
                "3. 右键工具按钮可设置颜色、线宽、字号；右键马赛克按钮可「一键智能打码」。\r\n" +
                "4. AI 功能（文字提取/翻译/一键打码）需要在托盘菜单「设置…」中填写 DeepSeek API Key。\r\n" +
                "5. 完成 = 复制到剪贴板（可在微信中 Ctrl+V 直接发送）；保存 = 保存到设置的文件夹。\r\n\r\n" +
                "配置文件：config.json（位于程序目录）\r\n保存目录：" + _cfg.EnsureSaveDir(),
                "使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                AppInfo.FullName + "  v" + AppInfo.Version + "\r\n\r\n" +
                "微信风格截图 + DeepSeek 文字提取 / 翻译 / 智能打码\r\n" +
                "绿色免安装 · 单文件 · 开机自启\r\n\r\n" +
                "截图热键：" + _cfg.Hotkey + "\r\n" +
                "配置文件：" + _store.ConfigPath + "\r\n" +
                "保存目录：" + _cfg.EffectiveSaveDir() + "\r\n\r\n" +
                "AI 能力由 DeepSeek API 提供（api.deepseek.com）\r\n" +
                "本工具非腾讯官方产品，与微信/腾讯无关。",
                "关于 " + AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExitApp()
        {
            if (_exitRequested) return;
            _exitRequested = true;
            try
            {
                if (_overlay != null && !_overlay.IsDisposed) { try { _overlay.Close(); } catch { } }
                if (_settings != null && !_settings.IsDisposed) { try { _settings.Close(); } catch { } }
                if (_hotkeyRegistered)
                {
                    Native.UnregisterHotKey(Handle, HotkeyId);
                    _hotkeyRegistered = false;
                }
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
                Log.Write("用户退出程序");
            }
            catch { }
            Close();
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
            {
                // 隐藏窗口不应被用户关闭；转成退出
                e.Cancel = true;
                ExitApp();
                return;
            }
            try
            {
                if (_hotkeyRegistered)
                {
                    Native.UnregisterHotKey(Handle, HotkeyId);
                    _hotkeyRegistered = false;
                }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                if (_menu != null) { _menu.Dispose(); _menu = null; }
                _captureTimer.Dispose();
            }
            catch { }
            base.OnFormClosing(e);
        }
    }
}


