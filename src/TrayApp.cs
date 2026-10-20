using System;
using System.Diagnostics;
using System.Collections.Generic;
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
        private const int RecordHotkeyId = 0x5A12;
        private const int OcrHotkeyId = 0x5A13;
        private const int TranslateHotkeyId = 0x5A14;
        private const int PrivacyHotkeyId = 0x5A15;

        private readonly AppConfig _cfg;
        private readonly ConfigStore _store;
        private readonly string[] _args;
        private bool _firstRun;
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _captureItem;
        private ToolStripMenuItem _autoStartItem;
        private ToolStripMenuItem _recordItem;
        private OverlayForm _overlay;
        private SettingsForm _settings;
        private bool _hotkeyRegistered;
        private bool _recordHotkeyRegistered;
        private readonly Timer _captureTimer;
        private bool _capturePending;
        private bool _recordPending;
        private bool _exitRequested;
        private CaptureIntent _captureIntent;
        private Rectangle? _pendingInitialRect;
        private readonly HashSet<int> _quickHotkeys = new HashSet<int>();
        private readonly Dictionary<int, ToolStripMenuItem> _quickItems = new Dictionary<int, ToolStripMenuItem>();

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
                if (_recordPending)
                {
                    _recordPending = false;
                    DoStartRecording();
                }
                else
                {
                    DoStartCapture();
                }
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
            else if (Program.HasArg(_args, "--record"))
            {
                BeginInvoke((Action)RequestRecording);
            }
            else if (Program.HasArg(_args, "--settings"))
            {
                BeginInvoke((Action)delegate { ShowSettings(false); });
            }
            else if (CaptureCommand.FromArgs(_args) != CaptureIntent.Screenshot)
            {
                BeginInvoke((Action)delegate { RequestCapture(CaptureCommand.FromArgs(_args)); });
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

            ToolStripMenuItem delayMenu = new ToolStripMenuItem("延时截图");
            ToolStripMenuItem delay3 = new ToolStripMenuItem("延时 3 秒截图");
            delay3.Click += delegate { RequestDelayedCapture(3); };
            ToolStripMenuItem delay5 = new ToolStripMenuItem("延时 5 秒截图");
            delay5.Click += delegate { RequestDelayedCapture(5); };
            ToolStripMenuItem delay10 = new ToolStripMenuItem("延时 10 秒截图");
            delay10.Click += delegate { RequestDelayedCapture(10); };
            delayMenu.DropDownItems.Add(delay3);
            delayMenu.DropDownItems.Add(delay5);
            delayMenu.DropDownItems.Add(delay10);
            _menu.Items.Add(delayMenu);

            ToolStripMenuItem lastRectItem = new ToolStripMenuItem("截取上次区域");
            lastRectItem.Click += delegate
            {
                Rectangle lastRect;
                if (TryGetLastCaptureRect(out lastRect))
                {
                    _pendingInitialRect = lastRect;
                    RequestCapture();
                }
                else
                {
                    Toast.Show("暂无上次截图区域");
                }
            };
            _menu.Items.Add(lastRectItem);

            _recordItem = new ToolStripMenuItem("录制屏幕  (Ctrl+Shift+P)");
            _recordItem.Click += delegate { RequestRecording(); };
            _menu.Items.Add(_recordItem);

            _menu.Items.Add(new ToolStripSeparator());

            AddQuickMenu(OcrHotkeyId, "提取文字", CaptureIntent.Ocr);
            AddQuickMenu(TranslateHotkeyId, "翻译选区", CaptureIntent.Translate);
            AddQuickMenu(PrivacyHotkeyId, "智能打码", CaptureIntent.Privacy);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem importFileItem = new ToolStripMenuItem("导入图片编辑…");
            importFileItem.Click += delegate { ImportImageFromFile(); };
            _menu.Items.Add(importFileItem);

            ToolStripMenuItem importClipItem = new ToolStripMenuItem("粘贴剪贴板图片编辑");
            importClipItem.Click += delegate { ImportImageFromClipboard(); };
            _menu.Items.Add(importClipItem);

            ToolStripMenuItem stitchItem = new ToolStripMenuItem("拼图工具…");
            stitchItem.Click += delegate { ShowImageComposer(); };
            _menu.Items.Add(stitchItem);

            ToolStripMenuItem historyItem = new ToolStripMenuItem("截图历史记录…");
            historyItem.Click += delegate { ShowHistory(); };
            _menu.Items.Add(historyItem);

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
                if (_recordItem != null)
                {
                    _recordItem.Text = string.IsNullOrWhiteSpace(_cfg.RecordHotkey)
                        ? "录制屏幕"
                        : "录制屏幕  (" + _cfg.RecordHotkey + ")";
                }
                if (_autoStartItem != null) _autoStartItem.Checked = Autostart.IsEnabled();
                UpdateQuickMenu(OcrHotkeyId, "提取文字", _cfg.OcrHotkey);
                UpdateQuickMenu(TranslateHotkeyId, "翻译选区", _cfg.TranslateHotkey);
                UpdateQuickMenu(PrivacyHotkeyId, "智能打码", _cfg.PrivacyHotkey);
                if (_tray != null) _tray.Text = AppInfo.Name + "（" + _cfg.Hotkey + " 截图，" + (string.IsNullOrWhiteSpace(_cfg.RecordHotkey) ? "录制" : _cfg.RecordHotkey + " 录制") + "）";
            }
            catch { }
        }

        private void AddQuickMenu(int id, string text, CaptureIntent intent)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += delegate { RequestCapture(intent); };
            _quickItems.Add(id, item);
            _menu.Items.Add(item);
        }

        private void UpdateQuickMenu(int id, string label, string shortcut)
        {
            ToolStripMenuItem item;
            if (_quickItems.TryGetValue(id, out item))
                item.Text = label + (string.IsNullOrWhiteSpace(shortcut) ? "" : "  (" + shortcut + ")");
        }

        private bool TryRegisterHotkey(int id, HotkeySpec spec)
        {
            bool ok = Native.RegisterHotKey(Handle, id,
                HotkeyUtil.Modifiers(spec) | Native.MOD_NOREPEAT, (uint)spec.Key);
            if (ok)
            {
                if (id == HotkeyId) _hotkeyRegistered = true;
                else _recordHotkeyRegistered = true;
            }
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
                if (_recordHotkeyRegistered)
                {
                    Native.UnregisterHotKey(Handle, RecordHotkeyId);
                    _recordHotkeyRegistered = false;
                }
                HotkeySpec spec;
                if (!HotkeyUtil.TryParse(_cfg.Hotkey, out spec))
                {
                    Log.Write("热键格式无效: " + _cfg.Hotkey);
                    Toast.Show("热键格式无效：" + _cfg.Hotkey + "，请在设置中重新设置", 4000);
                    return;
                }
                bool mainOk = TryRegisterHotkey(HotkeyId, spec);
                if (mainOk)
                {
                    Log.Write("热键注册成功: " + _cfg.Hotkey);
                    UpdateMenuState();
                    return;
                }
                // 热键被占用时自动降级，避免用户需要手动修复启动状态。
                Log.Write("热键注册失败: " + _cfg.Hotkey);
                string[] fallbacks = new string[] { "Alt+Shift+A", "Ctrl+Alt+A", "Alt+Shift+S", "Ctrl+Alt+S" };
                foreach (string candidate in fallbacks)
                {
                    if (string.Equals(candidate, _cfg.Hotkey, StringComparison.OrdinalIgnoreCase)) continue;
                    HotkeySpec fallbackSpec;
                    if (!HotkeyUtil.TryParse(candidate, out fallbackSpec)) continue;
                    if (TryRegisterHotkey(HotkeyId, fallbackSpec))
                    {
                        string old = _cfg.Hotkey;
                        _cfg.Hotkey = candidate;
                        try { _store.Save(_cfg); } catch { }
                        Log.Write("热键 " + old + " 被占用，已自动改用 " + candidate);
                        Toast.Show("热键 " + old + " 被其他程序占用，已自动改用 " + candidate + "，可在设置中修改", 6000);
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
            finally
            {
                RegisterRecordHotkey();
                RegisterQuickHotkeys();
                UpdateMenuState();
            }
        }

        private void UnregisterQuickHotkeys()
        {
            foreach (int id in _quickHotkeys) Native.UnregisterHotKey(Handle, id);
            _quickHotkeys.Clear();
        }

        private void RegisterQuickHotkeys()
        {
            UnregisterQuickHotkeys();
            RegisterQuickHotkey(OcrHotkeyId, "提取文字", _cfg.OcrHotkey);
            RegisterQuickHotkey(TranslateHotkeyId, "翻译选区", _cfg.TranslateHotkey);
            RegisterQuickHotkey(PrivacyHotkeyId, "智能打码", _cfg.PrivacyHotkey);
        }

        private void RegisterQuickHotkey(int id, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            HotkeySpec spec;
            if (HotkeyUtil.TryParse(value, out spec) &&
                Native.RegisterHotKey(Handle, id, HotkeyUtil.Modifiers(spec) | Native.MOD_NOREPEAT, (uint)spec.Key))
            {
                _quickHotkeys.Add(id);
                return;
            }
            Log.Write(label + "快捷键不可用: " + value);
            Toast.Show(label + "快捷键 " + value + " 不可用，请在设置中更换；仍可从托盘菜单使用。", 5000);
        }

        private void RegisterRecordHotkey()
        {
            try
            {
                if (_recordHotkeyRegistered)
                {
                    Native.UnregisterHotKey(Handle, RecordHotkeyId);
                    _recordHotkeyRegistered = false;
                }
                if (string.IsNullOrWhiteSpace(_cfg.RecordHotkey)) return;
                HotkeySpec spec;
                if (!HotkeyUtil.TryParse(_cfg.RecordHotkey, out spec))
                {
                    Log.Write("录制热键格式无效: " + _cfg.RecordHotkey);
                    return;
                }
                if (TryRegisterHotkey(RecordHotkeyId, spec))
                {
                    Log.Write("录制热键注册成功: " + _cfg.RecordHotkey);
                    return;
                }
                string[] fallbacks = new string[] { "Ctrl+Shift+P", "Alt+Shift+P", "Ctrl+Alt+P" };
                foreach (string candidate in fallbacks)
                {
                    if (string.Equals(candidate, _cfg.RecordHotkey, StringComparison.OrdinalIgnoreCase)) continue;
                    HotkeySpec fallback;
                    if (!HotkeyUtil.TryParse(candidate, out fallback)) continue;
                    if (TryRegisterHotkey(RecordHotkeyId, fallback))
                    {
                        string old = _cfg.RecordHotkey;
                        _cfg.RecordHotkey = candidate;
                        try { _store.Save(_cfg); } catch { }
                        Log.Write("录制热键 " + old + " 被占用，已自动改用 " + candidate);
                        Toast.Show("录制热键 " + old + " 被占用，已自动改用 " + candidate, 5000);
                        return;
                    }
                }
                Log.Write("录制热键注册失败: " + _cfg.RecordHotkey);
                Toast.Show("录制热键 " + _cfg.RecordHotkey + " 注册失败，可在设置中修改", 4500);
            }
            catch (Exception ex)
            {
                Log.Write("注册录制热键异常: " + ex.Message);
            }
        }

        private void RequestRecording()
        {
            if (_overlay != null && !_overlay.IsDisposed)
            {
                try { _overlay.Activate(); } catch { }
                return;
            }
            if (_settings != null && !_settings.IsDisposed)
            {
                Toast.Show("请先关闭设置窗口再录制");
                return;
            }
            if (_capturePending || _recordPending) return;
            _recordPending = true;
            _captureTimer.Start();
        }

        private void DoStartRecording()
        {
            if (_overlay != null && !_overlay.IsDisposed) return;
            try
            {
                Toast.HideCurrent();
                Rectangle vs = DpiUtil.VirtualScreen;
                Bitmap screenshot = ImageUtil.Capture(vs);
                OverlayForm overlay = new OverlayForm(screenshot, vs, _cfg, _store, delegate { ShowSettings(false); });
                overlay.RecordMode = true;
                _overlay = overlay;
                overlay.FormClosed += delegate
                {
                    if (ReferenceEquals(_overlay, overlay)) _overlay = null;
                };
                overlay.Show();
                overlay.Activate();
                Toast.Show("请框选录制区域，再选择声音并点击开始录制", 2500);
            }
            catch (Exception ex)
            {
                Log.Write("启动录制失败: " + ex);
                Toast.Show("启动录制失败：" + ex.Message, 4000);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HotkeyId) { RequestCapture(); return; }
                if (id == RecordHotkeyId) { RequestRecording(); return; }
                if (id == OcrHotkeyId) { RequestCapture(CaptureIntent.Ocr); return; }
                if (id == TranslateHotkeyId) { RequestCapture(CaptureIntent.Translate); return; }
                if (id == PrivacyHotkeyId) { RequestCapture(CaptureIntent.Privacy); return; }
            }
            if (m.Msg == AppInfo.WM_APP_TRIGGER_CAPTURE)
            {
                RequestCapture();
                return;
            }
            if (m.Msg == AppInfo.WM_APP_SHOW_TOAST)
            {
                Toast.Show("截图助手已在运行，按 " + _cfg.Hotkey + " 开始截图");
                return;
            }
            if (m.Msg == AppInfo.WM_APP_OPEN_SETTINGS)
            {
                ShowSettings(false);
                return;
            }
            if (m.Msg == AppInfo.WM_APP_TRIGGER_RECORD)
            {
                RequestRecording();
                return;
            }
            if (m.Msg == AppInfo.WM_APP_TRIGGER_OCR) { RequestCapture(CaptureIntent.Ocr); return; }
            if (m.Msg == AppInfo.WM_APP_TRIGGER_TRANSLATE) { RequestCapture(CaptureIntent.Translate); return; }
            if (m.Msg == AppInfo.WM_APP_TRIGGER_PRIVACY) { RequestCapture(CaptureIntent.Privacy); return; }
            base.WndProc(ref m);
        }

        private void RequestCapture()
        {
            RequestCapture(CaptureIntent.Screenshot);
        }

        private void RequestCapture(CaptureIntent intent)
        {
            if (_capturePending || _recordPending) return;
            if (_overlay != null && !_overlay.IsDisposed)
            {
                try { _overlay.Activate(); } catch { }
                ApplyCaptureIntent(_overlay, intent);
                return;
            }
            if (_settings != null && !_settings.IsDisposed)
            {
                Toast.Show("请先关闭设置窗口再截图");
                return;
            }
            _capturePending = true;
            _captureIntent = intent;
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
                if (_pendingInitialRect.HasValue)
                {
                    overlay.ApplyInitialSelection(_pendingInitialRect.Value);
                    _pendingInitialRect = null;
                }
                ApplyCaptureIntent(overlay, _captureIntent);
            }
            catch (Exception ex)
            {
                Log.Write("启动截图失败: " + ex);
                Toast.Show("截图失败：" + ex.Message, 4000);
            }
        }

        private static void ApplyCaptureIntent(OverlayForm overlay, CaptureIntent intent)
        {
            switch (intent)
            {
                case CaptureIntent.Ocr: overlay.StartActionAfterSelection(ToolbarAction.Ocr); break;
                case CaptureIntent.Translate: overlay.StartActionAfterSelection(ToolbarAction.Translate); break;
                case CaptureIntent.Privacy: overlay.StartPrivacyAfterSelection(); break;
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
                // 模态窗口关闭只会隐藏；所有者须释放其字体和下拉菜单资源。
                using (SettingsForm form = new SettingsForm(_cfg, _store, firstRun))
                {
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
                        // 用户取消了首次配置，下次启动继续引导。
                        _firstRun = true;
                    }
                    else if (wasFirstRun)
                    {
                        Toast.Show("配置完成！按 " + _cfg.Hotkey + " 开始截图", 3000);
                    }
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

        private void RequestDelayedCapture(int seconds, Rectangle? initialRect = null)
        {
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
            Toast.Show("将在 " + seconds + " 秒后开始截图…", Math.Min(seconds * 1000, 3000));
            Timer timer = new Timer();
            timer.Interval = seconds * 1000;
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                _pendingInitialRect = initialRect;
                RequestCapture(CaptureIntent.Screenshot);
            };
            timer.Start();
        }

        private bool TryGetLastCaptureRect(out Rectangle rect)
        {
            rect = Rectangle.Empty;
            if (string.IsNullOrWhiteSpace(_cfg.LastCaptureRect)) return false;
            string[] parts = _cfg.LastCaptureRect.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4)
            {
                int x, y, w, h;
                if (int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y) &&
                    int.TryParse(parts[2], out w) && int.TryParse(parts[3], out h))
                {
                    if (w > 10 && h > 10)
                    {
                        rect = new Rectangle(x, y, w, h);
                        return true;
                    }
                }
            }
            return false;
        }

        private void OpenEditorWithImage(Bitmap image)
        {
            if (image == null) return;
            if (_overlay != null && !_overlay.IsDisposed)
            {
                try
                {
                    _overlay.Activate();
                    _overlay.LoadImageToCanvas(image);
                    return;
                }
                catch { }
            }
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
                overlay.LoadImageToCanvas(image);
            }
            catch (Exception ex)
            {
                Log.Write("打开编辑器失败: " + ex);
                Toast.Show("载入图片失败：" + ex.Message, 4000);
            }
        }

        private void ImportImageFromFile()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要编辑的图片";
                dlg.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        Bitmap img = ImageImport.LoadFile(dlg.FileName);
                        OpenEditorWithImage(img);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("载入图片失败: " + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        private void ImportImageFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsImage())
                {
                    Bitmap img = ImageImport.FromClipboard();
                    if (img != null)
                    {
                        OpenEditorWithImage(img);
                        return;
                    }
                }
                Toast.Show("剪贴板中没有图片");
            }
            catch (Exception ex)
            {
                Toast.Show("读取剪贴板失败: " + ex.Message);
            }
        }

        private void ShowImageComposer()
        {
            try
            {
                using (ImageComposerForm form = new ImageComposerForm())
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        Bitmap composed = form.TakeResult();
                        if (composed != null)
                        {
                            OpenEditorWithImage(composed);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Toast.Show("打开拼图工具失败：" + ex.Message);
            }
        }

        private void ShowHistory()
        {
            try
            {
                HistoryForm form = new HistoryForm(_store, _cfg, delegate(Bitmap img)
                {
                    OpenEditorWithImage(img);
                });
                form.Show();
                form.Activate();
            }
            catch (Exception ex)
            {
                Toast.Show("打开截图历史失败：" + ex.Message);
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
                "2. 工具栏：矩形、椭圆、表情、箭头、画笔、马赛克、文字、放大镜(L)、翻译、文字提取、长截图；右侧撤销/保存/钉桌面/关闭/完成。\r\n" +
                "3. 右键工具按钮可设置颜色、线宽、字号；右键马赛克按钮可智能打码，优先本地高精度识别，自定义规则过滤。\r\n" +
                "4. 文字翻译会直接在原位置覆盖译文（Ctrl+Z 可撤销）；文字提取在侧边面板显示可复制文本/表格格式。\r\n" +
                "5. 文字提取无需 Key；翻译及联网补漏在「API 与翻译」配置。\r\n" +
                "6. 视频录制：托盘菜单「录制屏幕」或按 " + (string.IsNullOrWhiteSpace(_cfg.RecordHotkey) ? "（未设置）" : _cfg.RecordHotkey) + "，框选后选择声音并点击开始；F8 暂停/继续，Esc 停止。\r\n" +
                "7. 拖入图片文件或在画布按 Ctrl+V 可直接载入图片继续编辑；托盘菜单支持延时截图、截取上次区域、拼图与历史记录。\r\n" +
                "8. 按 V 选择对象，拖动/缩放，Delete 删除；Ctrl+Z 撤销、Ctrl+Y 重做。译文可双击编辑。\r\n" +
                "9. 完成 = 复制到剪贴板；保存 = 保存到设置的文件夹。\r\n\r\n" +
                "配置文件：config.json（位于程序目录）\r\n保存目录：" + _cfg.EnsureSaveDir(),
                "使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                AppInfo.FullName + "  v" + AppInfo.Version + "\r\n\r\n" +
                "截图标注、本地文字提取、原位翻译、智能打码与屏幕录制\r\n" +
                "绿色免安装 · 保留 ocr 目录 · 可选开机自启\r\n\r\n" +
                "截图热键：" + _cfg.Hotkey + "\r\n" +
                "录制热键：" + (string.IsNullOrWhiteSpace(_cfg.RecordHotkey) ? "未设置" : _cfg.RecordHotkey) + "\r\n" +
                "配置文件：" + _store.ConfigPath + "\r\n" +
                "保存目录：" + _cfg.EffectiveSaveDir() + "\r\n\r\n" +
                "文字识别在本机完成；联网功能仅使用设置中的兼容接口。\r\n" +
                "接口凭据仅保存于当前用户配置。",
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
                if (_recordHotkeyRegistered)
                {
                    Native.UnregisterHotKey(Handle, RecordHotkeyId);
                    _recordHotkeyRegistered = false;
                }
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
                UnregisterQuickHotkeys();
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
                if (_recordHotkeyRegistered)
                {
                    Native.UnregisterHotKey(Handle, RecordHotkeyId);
                    _recordHotkeyRegistered = false;
                }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                if (_menu != null) { _menu.Dispose(); _menu = null; }
                _captureTimer.Dispose();
                UnregisterQuickHotkeys();
            }
            catch { }
            base.OnFormClosing(e);
        }
    }
}


