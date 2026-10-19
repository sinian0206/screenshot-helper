using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 设置窗口。由 TrayApp 以 ShowDialog 方式打开：
    ///   using (var f = new SettingsForm(cfg, store, firstRun)) { if (f.ShowDialog() == DialogResult.OK) ... }
    /// 约定：
    ///   - 对话框内部编辑 cfg 的副本 _working，点击“确定”时才写回传入的 cfg（cfg.CopyFrom(_working)）并调用 store.Save(cfg)。
    ///   - 点击“确定”时还要处理开机自启：Autostart.SetEnabled(cfg.StartWithWindows)。
    ///   - 保存成功后触发 Saved 事件（在对话框关闭前）。
    ///   - firstRun=true 时显示欢迎/引导文案，并默认聚焦 API Key 输入框。
    /// 负责队友：ai-ui
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private const int FormClientWidth = 704;
        private const int FormClientHeight = 598;
        private const int BottomBarHeight = 64;
        private const int AiTabHeight = 230;
        private const int ShotGroupHeight = 322;

        private readonly AppConfig _cfg;
        private readonly ConfigStore _store;
        private readonly bool _firstRun;
        private AppConfig _working;

        private TabControl _tabs;
        private Panel _welcomePanel;

        // AI 设置页
        private TextBox _apiKeyBox;
        private CheckBox _showKeyCheck;
        private Label _apiKeyWarn;
        private TextBox _baseUrlBox;
        private TextBox _visionModelBox;
        private TextBox _textModelBox;
        private NumericUpDown _timeoutNum;
        private Button _testBtn;
        private Label _testStatus;
        private TextBox _ocrPromptBox;
        private TextBox _translatePromptBox;

        // 截图与启动页
        private HotkeyBox _hotkeyBox;
        private TextBox _saveDirBox;
        private ComboBox _formatCombo;
        private NumericUpDown _mosaicNum;
        private ComboBox _translateCombo;
        private CheckBox _autoCopyCheck;
        private CheckBox _autoSaveCheck;
        private CheckBox _showColorCheck;
        private CheckBox _showDimensionsCheck;
        private CheckBox _pinTopCheck;
        private CheckBox _showUsageCheck;
        private CheckBox _autostartCheck;

        // 底部按钮
        private Button _okBtn;
        private Button _cancelBtn;
        private Button _resetBtn;

        private CancellationTokenSource _testCts;
        private bool _testing;

        public SettingsForm(AppConfig cfg, ConfigStore store)
            : this(cfg, store, false)
        {
        }

        public SettingsForm(AppConfig cfg, ConfigStore store, bool firstRun)
        {
            _cfg = cfg;
            _store = store;
            _firstRun = firstRun;
            _working = cfg != null ? cfg.Clone() : new AppConfig();
            _working.ApiKeyDecryptFailed = false;
            _working.Hotkey = string.IsNullOrWhiteSpace(_working.Hotkey) ? "Alt+A" : _working.Hotkey;

            SuspendLayout();
            Name = "SettingsForm";
            Text = firstRun ? "欢迎使用微信截图助手" : "微信截图助手 - 设置";
            Font = new Font(UiTheme.UiFontName, 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(250, 250, 250);
            DoubleBuffered = true;
            ClientSize = new Size(FormClientWidth, FormClientHeight);
            BuildUi();
            LoadValues();
            ResumeLayout(true);
        }

        /// <summary>保存成功后触发（在窗口关闭前）。</summary>
        public event EventHandler Saved;

        // ===================== 界面构建 =====================

        private void BuildUi()
        {
            int cw = ClientSize.Width;
            int ch = ClientSize.Height;
            int top;
            if (_firstRun)
            {
                _welcomePanel = BuildWelcomePanel(cw);
                Controls.Add(_welcomePanel);
                top = _welcomePanel.Height + 6;
            }
            else
            {
                top = 8;
            }

            _tabs = new TabControl();
            _tabs.Name = "Tabs";
            _tabs.Font = new Font(UiTheme.UiFontName, 9f);
            _tabs.Bounds = new Rectangle(12, top, cw - 24, ch - top - BottomBarHeight - 4);
            TabPage aiPage = new TabPage("AI 设置");
            aiPage.Name = "AiPage";
            aiPage.BackColor = Color.White;
            aiPage.UseVisualStyleBackColor = false;
            aiPage.Padding = new Padding(0);
            TabPage shotPage = new TabPage("截图与启动");
            shotPage.Name = "ShotPage";
            shotPage.BackColor = Color.White;
            shotPage.UseVisualStyleBackColor = false;
            shotPage.Padding = new Padding(0);
            BuildAiTab(aiPage);
            BuildShotTab(shotPage);
            _tabs.TabPages.Add(aiPage);
            _tabs.TabPages.Add(shotPage);
            _tabs.SelectedIndex = 0;
            Controls.Add(_tabs);

            BuildBottomBar(cw, ch);
            AcceptButton = _okBtn;
            CancelButton = _cancelBtn;
        }

        private Panel BuildWelcomePanel(int cw)
        {
            Panel panel = new Panel();
            panel.Name = "WelcomePanel";
            panel.Bounds = new Rectangle(0, 0, cw, 96);
            panel.BackColor = UiTheme.GreenLight;
            panel.Paint += delegate(object s, PaintEventArgs e)
            {
                using (SolidBrush brush = new SolidBrush(UiTheme.Green))
                    e.Graphics.FillRectangle(brush, 0, 0, 5, panel.Height);
            };

            Label title = new Label();
            title.AutoSize = false;
            title.Bounds = new Rectangle(18, 10, cw - 36, 26);
            title.Font = new Font(UiTheme.UiFontName, 11.5f, FontStyle.Bold);
            title.ForeColor = UiTheme.GreenDark;
            title.BackColor = Color.Transparent;
            title.Text = "欢迎使用微信截图助手（DeepSeek 版）";
            panel.Controls.Add(title);

            Label steps = new Label();
            steps.AutoSize = false;
            steps.Bounds = new Rectangle(18, 36, cw - 36, 54);
            steps.Font = new Font(UiTheme.UiFontName, 9f);
            steps.ForeColor = UiTheme.TextDark;
            steps.BackColor = Color.Transparent;
            steps.Text = "1) 填写 DeepSeek API Key（到 platform.deepseek.com 创建，sk- 开头）\r\n" +
                         "2) 设置截图热键与保存目录\r\n" +
                         "3) 点击「确定」即可开始使用";
            panel.Controls.Add(steps);
            return panel;
        }

        private void BuildAiTab(TabPage page)
        {
            GroupBox apiGroup = new GroupBox();
            apiGroup.Name = "ApiGroup";
            apiGroup.Text = "DeepSeek API 配置";
            apiGroup.Bounds = new Rectangle(10, 8, 652, AiTabHeight);
            apiGroup.BackColor = Color.White;

            Label lblApiKey = MakeLabel("API Key：", 12, 26, 78);
            _apiKeyBox = MakeTextBox(96, 24, 372);
            _apiKeyBox.Name = "ApiKeyBox";
            _apiKeyBox.UseSystemPasswordChar = true;
            _apiKeyBox.MaxLength = 256;
            _showKeyCheck = MakeCheckBox("显示", 476, 25, 60);
            _showKeyCheck.Name = "ShowKeyCheck";
            _showKeyCheck.CheckedChanged += delegate
            {
                _apiKeyBox.UseSystemPasswordChar = !_showKeyCheck.Checked;
            };
            LinkLabel getKeyLink = new LinkLabel();
            getKeyLink.Name = "GetKeyLink";
            getKeyLink.Text = "获取 API Key";
            getKeyLink.AutoSize = false;
            getKeyLink.Bounds = new Rectangle(542, 26, 110, 22);
            getKeyLink.Font = new Font(UiTheme.UiFontName, 9f);
            getKeyLink.LinkColor = UiTheme.GreenDark;
            getKeyLink.ActiveLinkColor = UiTheme.Green;
            getKeyLink.LinkBehavior = LinkBehavior.HoverUnderline;
            getKeyLink.Cursor = Cursors.Hand;
            getKeyLink.LinkClicked += delegate
            {
                try { Process.Start("https://platform.deepseek.com/api_keys"); }
                catch (Exception ex) { Log.Write("打开 API Key 页面失败: " + ex.Message); }
            };

            _apiKeyWarn = MakeSmallLabel("", 96, 54, 540);
            _apiKeyWarn.Name = "ApiKeyWarn";

            Label lblBase = MakeLabel("Base URL：", 12, 86, 78);
            _baseUrlBox = MakeTextBox(96, 84, 540);
            _baseUrlBox.Name = "BaseUrlBox";
            _baseUrlBox.MaxLength = 512;

            Label lblVision = MakeLabel("视觉模型：", 12, 118, 78);
            _visionModelBox = MakeTextBox(96, 116, 200);
            _visionModelBox.Name = "VisionModelBox";
            _visionModelBox.MaxLength = 128;
            Label lblText = MakeLabel("文本模型：", 306, 118, 78);
            _textModelBox = MakeTextBox(390, 116, 246);
            _textModelBox.Name = "TextModelBox";
            _textModelBox.MaxLength = 128;

            Label lblTimeout = MakeLabel("超时（秒）：", 12, 150, 84);
            _timeoutNum = new NumericUpDown();
            _timeoutNum.Name = "TimeoutNum";
            _timeoutNum.Bounds = new Rectangle(96, 150, 90, 26);
            _timeoutNum.Minimum = 10;
            _timeoutNum.Maximum = 600;
            _timeoutNum.Increment = 5;
            _timeoutNum.Value = 120;
            _timeoutNum.Font = new Font(UiTheme.UiFontName, 9f);
            Label lblTimeoutHint = MakeSmallLabel("10~600 秒", 194, 151, 180);

            _testBtn = MakeButton("测试连接", false, 104, 30);
            _testBtn.Name = "TestButton";
            _testBtn.Bounds = new Rectangle(96, 182, 104, 30);
            _testBtn.Click += OnTestConnectionClick;

            _testStatus = MakeSmallLabel("未测试", 210, 186, 426);
            _testStatus.Name = "TestStatus";
            _testStatus.AutoEllipsis = true;

            apiGroup.Controls.AddRange(new Control[]
            {
                lblApiKey, _apiKeyBox, _showKeyCheck, getKeyLink, _apiKeyWarn,
                lblBase, _baseUrlBox, lblVision, _visionModelBox, lblText, _textModelBox,
                lblTimeout, _timeoutNum, lblTimeoutHint, _testBtn, _testStatus
            });
            page.Controls.Add(apiGroup);

            GroupBox promptGroup = new GroupBox();
            promptGroup.Name = "PromptGroup";
            promptGroup.Text = "AI 附加提示词（可选）";
            promptGroup.Bounds = new Rectangle(10, 246, 652, 140);
            promptGroup.BackColor = Color.White;

            Label lblOcrPrompt = MakeLabel("文字提取附加提示词：", 12, 22, 300);
            _ocrPromptBox = MakeMultilineBox(12, 44, 306, 82);
            _ocrPromptBox.Name = "OcrPromptBox";
            Label lblTransPrompt = MakeLabel("翻译附加提示词：", 334, 22, 300);
            _translatePromptBox = MakeMultilineBox(334, 44, 306, 82);
            _translatePromptBox.Name = "TranslatePromptBox";
            promptGroup.Controls.AddRange(new Control[]
            {
                lblOcrPrompt, _ocrPromptBox, lblTransPrompt, _translatePromptBox
            });
            page.Controls.Add(promptGroup);
        }

        private void BuildShotTab(TabPage page)
        {
            GroupBox group = new GroupBox();
            group.Name = "ShotGroup";
            group.Text = "截图、保存与启动";
            group.Bounds = new Rectangle(10, 8, 652, ShotGroupHeight);
            group.BackColor = Color.White;

            Label lblHotkey = MakeLabel("全局热键：", 12, 26, 84);
            _hotkeyBox = new HotkeyBox();
            _hotkeyBox.Name = "HotkeyBox";
            _hotkeyBox.Bounds = new Rectangle(100, 24, 210, 28);
            Label lblHotkeyHint = MakeSmallLabel("默认 Alt+A，点击后按新组合键", 320, 28, 320);

            Label lblSaveDir = MakeLabel("保存目录：", 12, 66, 84);
            _saveDirBox = MakeTextBox(100, 64, 350);
            _saveDirBox.Name = "SaveDirBox";
            _saveDirBox.MaxLength = 512;
            Button browseBtn = MakeButton("浏览…", false, 84, 28);
            browseBtn.Name = "BrowseButton";
            browseBtn.Bounds = new Rectangle(462, 63, 84, 28);
            browseBtn.Click += OnBrowseClick;
            Button openDirBtn = MakeButton("打开目录", false, 84, 28);
            openDirBtn.Name = "OpenDirButton";
            openDirBtn.Bounds = new Rectangle(556, 63, 84, 28);
            openDirBtn.Click += OnOpenDirClick;

            Label lblFormat = MakeLabel("保存格式：", 12, 104, 84);
            _formatCombo = MakeCombo(100, 102, 110);
            _formatCombo.Name = "FormatCombo";
            _formatCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _formatCombo.Items.AddRange(new object[] { "PNG", "JPG" });
            _formatCombo.SelectedIndex = 0;

            Label lblMosaic = MakeLabel("马赛克块大小：", 230, 104, 112);
            _mosaicNum = new NumericUpDown();
            _mosaicNum.Name = "MosaicNum";
            _mosaicNum.Bounds = new Rectangle(348, 102, 80, 26);
            _mosaicNum.Minimum = 2;
            _mosaicNum.Maximum = 64;
            _mosaicNum.Increment = 1;
            _mosaicNum.Value = 12;
            _mosaicNum.Font = new Font(UiTheme.UiFontName, 9f);
            Label lblMosaicHint = MakeSmallLabel("像素（2~64）", 438, 105, 160);

            Label lblLang = MakeLabel("翻译目标语言：", 12, 142, 96);
            _translateCombo = MakeCombo(112, 140, 190);
            _translateCombo.Name = "TranslateCombo";
            _translateCombo.DropDownStyle = ComboBoxStyle.DropDown;
            _translateCombo.Items.AddRange(new object[] { "中文", "English", "日本語", "한국어", "自动" });
            _translateCombo.MaxLength = 64;
            Label lblLangHint = MakeSmallLabel("可输入自定义语言", 312, 143, 240);

            _autoCopyCheck = MakeCheckBox("完成时自动复制", 12, 180, 170);
            _autoCopyCheck.Name = "AutoCopyCheck";
            _autoSaveCheck = MakeCheckBox("完成时自动保存", 12, 208, 170);
            _autoSaveCheck.Name = "AutoSaveCheck";
            _showColorCheck = MakeCheckBox("截图时显示色值", 12, 236, 170);
            _showColorCheck.Name = "ShowColorCheck";
            _showDimensionsCheck = MakeCheckBox("显示选区尺寸", 12, 264, 170);
            _showDimensionsCheck.Name = "ShowDimensionsCheck";
            _pinTopCheck = MakeCheckBox("钉图窗口置顶", 240, 180, 190);
            _pinTopCheck.Name = "PinTopCheck";
            _showUsageCheck = MakeCheckBox("显示 token 用量", 240, 208, 190);
            _showUsageCheck.Name = "ShowUsageCheck";
            _autostartCheck = MakeCheckBox("开机自动启动", 240, 236, 190);
            _autostartCheck.Name = "AutostartCheck";
            Label lblSaveDirHint = MakeSmallLabel("提示：保存目录留空时使用图片\\微信截图助手目录；开机自启需当前用户权限。", 12, 294, 620);

            group.Controls.AddRange(new Control[]
            {
                lblHotkey, _hotkeyBox, lblHotkeyHint, lblSaveDir, _saveDirBox, browseBtn, openDirBtn,
                lblFormat, _formatCombo, lblMosaic, _mosaicNum, lblMosaicHint,
                lblLang, _translateCombo, lblLangHint,
                _autoCopyCheck, _autoSaveCheck, _showColorCheck, _showDimensionsCheck,
                _pinTopCheck, _showUsageCheck, _autostartCheck, lblSaveDirHint
            });
            page.Controls.Add(group);
        }

        private void BuildBottomBar(int cw, int ch)
        {
            Panel bottom = new Panel();
            bottom.Name = "BottomBar";
            bottom.Bounds = new Rectangle(0, ch - BottomBarHeight, cw, BottomBarHeight);
            bottom.BackColor = Color.White;
            Panel line = new Panel();
            line.Bounds = new Rectangle(0, 0, cw, 1);
            line.BackColor = UiTheme.ToolbarBorder;
            bottom.Controls.Add(line);

            _resetBtn = MakeButton("恢复默认", false, 96, 32);
            _resetBtn.Name = "ResetButton";
            _resetBtn.Bounds = new Rectangle(12, 17, 96, 32);
            _resetBtn.Click += OnResetClick;

            _okBtn = MakeButton("确定", true, 96, 32);
            _okBtn.Name = "OkButton";
            _okBtn.Bounds = new Rectangle(cw - 12 - 96 - 8 - 96, 17, 96, 32);
            _okBtn.Click += OnOkClick;

            _cancelBtn = MakeButton("取消", false, 96, 32);
            _cancelBtn.Name = "CancelButton";
            _cancelBtn.Bounds = new Rectangle(cw - 12 - 96, 17, 96, 32);
            _cancelBtn.Click += delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            bottom.Controls.AddRange(new Control[] { _resetBtn, _okBtn, _cancelBtn });
            Controls.Add(bottom);
        }

        // ===================== 小控件工厂 =====================

        private static Label MakeLabel(string text, int x, int y, int width)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.Bounds = new Rectangle(x, y, width, 22);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Text = text;
            label.BackColor = Color.Transparent;
            return label;
        }

        private static Label MakeSmallLabel(string text, int x, int y, int width)
        {
            Label label = MakeLabel(text, x, y, width);
            label.Font = new Font(UiTheme.UiFontName, 8.5f);
            label.ForeColor = UiTheme.TextGray;
            return label;
        }

        private static TextBox MakeTextBox(int x, int y, int width)
        {
            TextBox box = new TextBox();
            box.Bounds = new Rectangle(x, y, width, 26);
            box.Font = new Font(UiTheme.UiFontName, 9f);
            return box;
        }

        private static TextBox MakeMultilineBox(int x, int y, int width, int height)
        {
            TextBox box = MakeTextBox(x, y, width);
            box.Bounds = new Rectangle(x, y, width, height);
            box.Multiline = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.AcceptsReturn = true;
            return box;
        }

        private static ComboBox MakeCombo(int x, int y, int width)
        {
            ComboBox combo = new ComboBox();
            combo.Bounds = new Rectangle(x, y, width, 26);
            combo.Font = new Font(UiTheme.UiFontName, 9f);
            return combo;
        }

        private static CheckBox MakeCheckBox(string text, int x, int y, int width)
        {
            CheckBox check = new CheckBox();
            check.AutoSize = false;
            check.Bounds = new Rectangle(x, y, width, 24);
            check.Text = text;
            check.Font = new Font(UiTheme.UiFontName, 9f);
            check.BackColor = Color.White;
            return check;
        }

        private static Button MakeButton(string text, bool primary, int width, int height)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(width, height);
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = primary ? UiTheme.Green : Color.White;
            button.ForeColor = primary ? Color.White : UiTheme.TextDark;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(205, 205, 205);
            button.FlatAppearance.MouseOverBackColor = primary ? UiTheme.GreenDark : UiTheme.HoverBg;
            button.FlatAppearance.MouseDownBackColor = primary ? UiTheme.GreenDark : Color.FromArgb(230, 230, 230);
            button.Font = new Font(UiTheme.UiFontName, 9f);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        // ===================== 值加载 / 保存 =====================

        private void LoadValues()
        {
            AppConfig cfg = _working;
            _apiKeyBox.Text = cfg.ApiKey ?? "";
            bool decryptFailed = _cfg != null && _cfg.ApiKeyDecryptFailed;
            if (decryptFailed)
            {
                _apiKeyBox.Text = "";
                _apiKeyWarn.Text = "API Key 无法解密（可能更换了电脑/用户），请重新填写";
                _apiKeyWarn.ForeColor = UiTheme.Red;
            }
            else
            {
                _apiKeyWarn.Text = "API Key 仅保存在本机（Windows 加密），不会上传到其他服务器。";
                _apiKeyWarn.ForeColor = UiTheme.TextGray;
            }
            _showKeyCheck.Checked = false;
            _apiKeyBox.UseSystemPasswordChar = true;

            _baseUrlBox.Text = cfg.BaseUrl ?? "";
            _visionModelBox.Text = cfg.VisionModel ?? "";
            _textModelBox.Text = cfg.TextModel ?? "";
            _timeoutNum.Value = ClampDecimal(_timeoutNum, cfg.TimeoutSec);
            _testStatus.Text = "未测试";
            _testStatus.ForeColor = UiTheme.TextGray;

            _hotkeyBox.HotkeyText = cfg.Hotkey ?? "Alt+A";
            _saveDirBox.Text = cfg.SaveDir ?? "";
            _formatCombo.SelectedIndex = IsJpeg(cfg.SaveFormat) ? 1 : 0;
            _mosaicNum.Value = ClampDecimal(_mosaicNum, (int)Math.Round(cfg.MosaicBlock));
            _translateCombo.Text = string.IsNullOrWhiteSpace(cfg.TranslateTarget) ? "中文" : cfg.TranslateTarget;
            _ocrPromptBox.Text = cfg.OcrExtraPrompt ?? "";
            _translatePromptBox.Text = cfg.TranslateExtraPrompt ?? "";

            _autoCopyCheck.Checked = cfg.AutoCopy;
            _autoSaveCheck.Checked = cfg.AutoSave;
            _showColorCheck.Checked = cfg.ShowColorValue;
            _showDimensionsCheck.Checked = cfg.ShowDimensions;
            _pinTopCheck.Checked = cfg.PinAlwaysOnTop;
            _showUsageCheck.Checked = cfg.ShowUsage;
            _autostartCheck.Checked = cfg.StartWithWindows;
        }

        private static decimal ClampDecimal(NumericUpDown numeric, int value)
        {
            if (value < numeric.Minimum) return numeric.Minimum;
            if (value > numeric.Maximum) return numeric.Maximum;
            return value;
        }

        private static bool IsJpeg(string format)
        {
            return string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private void SaveFromControls()
        {
            _working.ApiKey = _apiKeyBox.Text.Trim();
            _working.ApiKeyDecryptFailed = false;
            _working.BaseUrl = _baseUrlBox.Text.Trim();
            _working.VisionModel = _visionModelBox.Text.Trim();
            _working.TextModel = _textModelBox.Text.Trim();
            _working.TimeoutSec = (int)_timeoutNum.Value;
            _working.Hotkey = (_hotkeyBox.HotkeyText ?? "").Trim();
            _working.SaveDir = _saveDirBox.Text.Trim();
            _working.SaveFormat = _formatCombo.SelectedIndex == 1 ? "jpg" : "png";
            _working.TranslateTarget = (_translateCombo.Text ?? "").Trim();
            _working.OcrExtraPrompt = _ocrPromptBox.Text ?? "";
            _working.TranslateExtraPrompt = _translatePromptBox.Text ?? "";
            _working.AutoCopy = _autoCopyCheck.Checked;
            _working.AutoSave = _autoSaveCheck.Checked;
            _working.ShowColorValue = _showColorCheck.Checked;
            _working.ShowDimensions = _showDimensionsCheck.Checked;
            _working.PinAlwaysOnTop = _pinTopCheck.Checked;
            _working.ShowUsage = _showUsageCheck.Checked;
            _working.StartWithWindows = _autostartCheck.Checked;
            _working.FirstRunDone = true;
        }

        // ===================== 确定 / 取消 / 恢复默认 =====================

        private void OnOkClick(object sender, EventArgs e)
        {
            try
            {
                if (!ValidateInputs()) return;

                if (_firstRun && string.IsNullOrWhiteSpace(_apiKeyBox.Text))
                {
                    DialogResult choice = MessageBox.Show(this,
                        "暂不填写 API Key 也可以使用基础截图功能，AI 功能需之后在设置中补充。仍然保存吗？",
                        "提示", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (choice != DialogResult.Yes) return;
                }

                SaveFromControls();
                Autostart.SetEnabled(_working.StartWithWindows);
                if (_store != null) _store.Save(_working);
                if (_cfg != null) _cfg.CopyFrom(_working);
                EventHandler handler = Saved;
                if (handler != null) handler(this, EventArgs.Empty);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                Log.Write("保存设置失败: " + ex);
                MessageBox.Show(this, "保存设置失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool ValidateInputs()
        {
            if (_baseUrlBox.Text.Trim().Length == 0)
                return FailValidation("请填写 Base URL（例如 https://api.deepseek.com）。", _baseUrlBox);
            if (_visionModelBox.Text.Trim().Length == 0)
                return FailValidation("请填写视觉模型名称（默认 deepseek-flash）。", _visionModelBox);
            if (_textModelBox.Text.Trim().Length == 0)
                return FailValidation("请填写文本模型名称（默认 deepseek-flash）。", _textModelBox);
            HotkeySpec spec;
            if (!HotkeyUtil.TryParse(_hotkeyBox.HotkeyText, out spec))
                return FailValidation("全局热键无效：请点击热键框后按下组合键（例如 Alt+A，F1~F24 也可单独使用）。", _hotkeyBox);
            if (_translateCombo.Text.Trim().Length == 0)
                return FailValidation("请填写翻译目标语言（例如 中文 / English）。", _translateCombo);
            int block = (int)_mosaicNum.Value;
            if (block < 2 || block > 64)
                return FailValidation("马赛克块大小必须在 2~64 像素之间。", _mosaicNum);
            return true;
        }

        private bool FailValidation(string message, Control focus)
        {
            MessageBox.Show(this, message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (focus != null) focus.Focus();
            return false;
        }

        private void OnResetClick(object sender, EventArgs e)
        {
            DialogResult choice = MessageBox.Show(this,
                "确定要恢复所有设置为默认值吗？\r\n（API Key 将保留，不会被清除）",
                "恢复默认设置", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;
            AppConfig defaults = new AppConfig();
            defaults.ApiKey = _apiKeyBox.Text.Trim();
            defaults.FirstRunDone = true;
            _working = defaults;
            LoadValues();
            _testStatus.Text = "未测试";
            _testStatus.ForeColor = UiTheme.TextGray;
        }

        // ===================== 浏览 / 打开目录 =====================

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择截图保存目录";
                dialog.ShowNewFolderButton = true;
                string current = _saveDirBox.Text.Trim();
                if (current.Length > 0 && Directory.Exists(current)) dialog.SelectedPath = current;
                else dialog.SelectedPath = AppConfig.DefaultSaveDir();
                try
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        _saveDirBox.Text = dialog.SelectedPath;
                }
                catch (Exception ex)
                {
                    Log.Write("选择保存目录失败: " + ex.Message);
                }
            }
        }

        private void OnOpenDirClick(object sender, EventArgs e)
        {
            try
            {
                string dir = _saveDirBox.Text.Trim();
                if (dir.Length == 0) dir = AppConfig.DefaultSaveDir();
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Write("打开保存目录失败: " + ex.Message);
                MessageBox.Show(this, "无法打开保存目录：" + ex.Message, "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ===================== 测试连接 =====================

        private async void OnTestConnectionClick(object sender, EventArgs e)
        {
            if (_testing) return;
            if (_apiKeyBox.Text.Trim().Length == 0)
            {
                SetTestStatus("请先填写 API Key。", UiTheme.Red);
                _apiKeyBox.Focus();
                return;
            }
            string baseUrl = _baseUrlBox.Text.Trim();
            if (baseUrl.Length == 0)
            {
                SetTestStatus("请先填写 Base URL。", UiTheme.Red);
                _baseUrlBox.Focus();
                return;
            }

            AppConfig temp = _working.Clone();
            temp.ApiKey = _apiKeyBox.Text.Trim();
            temp.BaseUrl = baseUrl;
            temp.VisionModel = _visionModelBox.Text.Trim();
            temp.TextModel = _textModelBox.Text.Trim();
            temp.TimeoutSec = (int)_timeoutNum.Value;

            _testing = true;
            _testBtn.Enabled = false;
            _testBtn.Text = "测试中…";
            SetTestStatus("正在测试连接…", UiTheme.TextGray);

            CancellationTokenSource cts = new CancellationTokenSource();
            _testCts = cts;
            try
            {
                AiUsage usage = await new DeepSeekClient(temp).TestConnectionAsync(cts.Token);
                if (cts.IsCancellationRequested || IsDisposed) return;
                double seconds = (usage != null ? usage.ElapsedMs : 0) / 1000.0;
                SetTestStatus("连接成功，耗时 " + seconds.ToString("0.0") + " 秒", UiTheme.Green);
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) SetTestStatus("已取消测试。", UiTheme.TextGray);
            }
            catch (AiException ex)
            {
                if (!IsDisposed && !cts.IsCancellationRequested) SetTestStatus(ex.Message, UiTheme.Red);
            }
            catch (Exception ex)
            {
                Log.Write("测试连接异常: " + ex);
                if (!IsDisposed && !cts.IsCancellationRequested)
                    SetTestStatus("测试失败：" + ex.Message, UiTheme.Red);
            }
            finally
            {
                if (ReferenceEquals(_testCts, cts)) _testCts = null;
                try { cts.Dispose(); } catch { }
                if (!IsDisposed && !Disposing)
                {
                    _testing = false;
                    if (_testBtn != null && !_testBtn.IsDisposed)
                    {
                        _testBtn.Text = "测试连接";
                        _testBtn.Enabled = true;
                    }
                }
            }
        }

        private void SetTestStatus(string text, Color color)
        {
            if (_testStatus == null || _testStatus.IsDisposed) return;
            _testStatus.Text = text;
            _testStatus.ForeColor = color;
        }

        private void CancelTest()
        {
            CancellationTokenSource cts = _testCts;
            _testCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
        }

        // ===================== 生命周期 =====================

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_firstRun && _apiKeyBox != null)
            {
                _apiKeyBox.Focus();
                _apiKeyBox.SelectAll();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CancelTest();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CancelTest();
            base.Dispose(disposing);
        }
    }
}
