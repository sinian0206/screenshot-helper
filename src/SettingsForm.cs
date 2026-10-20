using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 设置窗口（信号控制台风）。由 TrayApp 以 ShowDialog 方式打开：
    ///   using (var f = new SettingsForm(cfg, store, firstRun)) { if (f.ShowDialog() == DialogResult.OK) ... }
    /// 约定：
    ///   - 对话框内部编辑 cfg 的副本 _working，点击“保存”时才写回传入的 cfg 并调用 store.Save(cfg)。
    ///   - 保存时处理开机自启：Autostart.SetEnabled(cfg.StartWithWindows)。
    ///   - 保存成功后触发 Saved 事件（在对话框关闭前）。
    ///   - firstRun=true 时显示欢迎引导卡片，并默认聚焦 API Key 输入框。
    /// 负责队友：ai-ui
    /// </summary>
    public sealed class SettingsForm : Form
    {
        // ===== 逻辑像素布局（96 DPI 基准，由 AutoScaleMode.Dpi 统一缩放）=====
        private const int WinW = 760;
        private const int WinHNormal = 700;
        private const int WinHFirstRun = 800;
        private const int TitleH = 46;
        private const int PageMarginX = 24;
        private const int PageTop = 108;
        private const int BottomH = 76;
        private const int PageW = WinW - PageMarginX * 2;

        private static readonly int[] FpsValues = new int[] { 5, 10, 15, 20, 30, 60 };
        private static readonly string[] FpsTexts = new string[] { "5 fps", "10 fps", "15 fps", "20 fps", "30 fps", "60 fps" };
        private static readonly string[] AudioModes = { "none", "system", "microphone", "mixed" };

        private readonly AppConfig _cfg;
        private readonly ConfigStore _store;
        private readonly bool _firstRun;
        private AppConfig _working;

        private Font _titleFont;
        private Font _hintFont;
        private readonly List<Label> _hintLabels = new List<Label>();

        private Panel _titlePanel;
        private SettingsTitleButton _closeBtn;
        private SettingsTabs _tabs;
        private Panel _pageAi;
        private Panel _pageShot;
        private Panel _pageLocal;
        private Label _localStatus;
        private SettingsSwitch _semanticPrivacySwitch;
        private SettingsSwitch _visionPrivacySwitch;
        private TextBox _customSensitiveBox;
        private TextBox _customIgnoredBox;
        private SettingsCard _welcomeCard;
        private Panel _bottomPanel;

        // ===== AI 设置页 =====        private SettingsField _apiKeyField;
        // ===== AI 设置页 =====
        private SettingsField _apiKeyField;
        private TextBox _apiKeyBox;
        private SettingsSwitch _showKeySwitch;
        private Label _apiKeyWarn;
        private SettingsField _baseUrlField;
        private TextBox _baseUrlBox;
        private SettingsField _visionField;
        private TextBox _visionModelBox;
        private SettingsField _textField;
        private TextBox _textModelBox;
        private SettingsField _timeoutField;
        private NumericUpDown _timeoutNum;
        private SettingsButton _testBtn;
        private Label _testStatus;
        private SettingsComboBox _translateCombo;
        private SettingsSwitch _showUsageSwitch;
        private SettingsField _translatePromptField;
        private TextBox _translatePromptBox;

        // ===== 截图与录制页 =====
        private HotkeyBox _hotkeyBox;
        private SettingsField _saveDirField;
        private TextBox _saveDirBox;
        private SettingsButton _browseBtn;
        private SettingsButton _openDirBtn;
        private SettingsComboBox _formatCombo;
        private SettingsField _mosaicField;
        private NumericUpDown _mosaicNum;
        private SettingsSwitch _autoCopySwitch;
        private SettingsSwitch _autoSaveSwitch;
        private SettingsSwitch _showColorSwitch;
        private SettingsSwitch _showDimensionsSwitch;
        private SettingsSwitch _pinTopSwitch;
        private SettingsSwitch _autostartSwitch;
        private SettingsComboBox _fpsCombo;
        private SettingsField _qualityField;
        private NumericUpDown _qualityNum;
        private SettingsSwitch _includeCursorSwitch;
        private HotkeyBox _recordHotkeyBox;
        private HotkeyBox _ocrHotkeyBox;
        private HotkeyBox _translateHotkeyBox;
        private HotkeyBox _privacyHotkeyBox;
        private SettingsComboBox _recordFormatCombo;
        private SettingsComboBox _recordAudioCombo;
        private SettingsSwitch _historyEnabledSwitch;
        private SettingsField _historyMaxCountField;
        private NumericUpDown _historyMaxCountNum;
        private SettingsField _historyMaxMBField;
        private NumericUpDown _historyMaxMBNum;

        // ===== 底部按钮 =====
        private SettingsButton _saveBtn;
        private SettingsButton _cancelBtn;
        private SettingsButton _resetBtn;

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
            if (string.IsNullOrWhiteSpace(_working.Hotkey)) _working.Hotkey = "Alt+A";

            SuspendLayout();
            Name = "SettingsForm";
            Text = AppInfo.Name + " · 设置";
            Font = new Font(UiTheme.UiFontName, 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = SettingsColors.PageBg;
            DoubleBuffered = true;
            ClientSize = new Size(WinW, firstRun ? WinHFirstRun : WinHNormal);
            RebuildFonts();
            BuildUi();
            LoadValues();
            ShowPage(0);
            ResumeLayout(true);
        }

        /// <summary>保存成功后触发（在窗口关闭前）。</summary>
        public event EventHandler Saved;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        // =====================================================================
        // 界面构建
        // =====================================================================

        private void BuildUi()
        {
            BuildTitleBar();
            BuildTabs();
            BuildPages();
            BuildBottomBar();
        }

        private void BuildTitleBar()
        {
            _titlePanel = new Panel();
            _titlePanel.Name = "TitleBar";
            _titlePanel.Bounds = new Rectangle(1, 1, WinW - 2, TitleH);
            _titlePanel.BackColor = Color.White;
            _titlePanel.Paint += OnTitleBarPaint;
            _titlePanel.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) SettingsWindowDrag.BeginDrag(_titlePanel);
            };
            Controls.Add(_titlePanel);

            _closeBtn = new SettingsTitleButton();
            _closeBtn.Name = "CloseButton";
            _closeBtn.Bounds = new Rectangle(_titlePanel.Width - 16 - 30, 8, 30, 30);
            _closeBtn.Click += delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            _titlePanel.Controls.Add(_closeBtn);
        }

        private void OnTitleBarPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = SettingsDpi.Scale(_titlePanel);
            int iconSize = (int)Math.Round(22 * s);
            Rectangle iconRect = new Rectangle((int)Math.Round(18 * s), (_titlePanel.Height - iconSize) / 2,
                iconSize, iconSize);
            SettingsIcons.DrawAppIcon(g, iconRect);

            Rectangle titleRect = new Rectangle(iconRect.Right + (int)Math.Round(10 * s), 0,
                _titlePanel.Width - iconRect.Right - (int)Math.Round(80 * s), _titlePanel.Height);
            TextRenderer.DrawText(g, Text, _titleFont, titleRect, SettingsColors.TitleForeground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            using (Pen pen = new Pen(SettingsColors.Divider, Math.Max(1f, s)))
                g.DrawLine(pen, 0, _titlePanel.Height - 1, _titlePanel.Width, _titlePanel.Height - 1);
        }

        private void BuildTabs()
        {
            _tabs = new SettingsTabs();
            _tabs.Name = "Tabs";
            _tabs.Bounds = new Rectangle(PageMarginX, 58, 440, 38);
            _tabs.SetItems("本地识别", "API 与翻译", "截图与录制");
            _tabs.SelectedIndexChanged += delegate { ShowPage(_tabs.SelectedIndex); };
            Controls.Add(_tabs);
        }

        private void BuildPages()
        {
            int pageH = ClientSize.Height - PageTop - BottomH - 1 - 16;
            _pageAi = new Panel();
            _pageAi.Name = "AiPage";
            _pageAi.Bounds = new Rectangle(PageMarginX, PageTop, PageW, pageH);
            _pageAi.BackColor = SettingsColors.PageBg;
            Controls.Add(_pageAi);

            _pageShot = new Panel();
            _pageShot.Name = "ShotPage";
            _pageShot.Bounds = new Rectangle(PageMarginX, PageTop, PageW, pageH);
            _pageShot.BackColor = SettingsColors.PageBg;
            Controls.Add(_pageShot);

            _pageLocal = new Panel();
            _pageLocal.Name = "LocalOcrPage";
            _pageLocal.Bounds = new Rectangle(PageMarginX, PageTop, PageW, pageH);
            _pageLocal.BackColor = SettingsColors.PageBg;
            Controls.Add(_pageLocal);

            BuildAiPage(_pageAi);
            BuildShotPage(_pageShot);
            BuildLocalPage(_pageLocal);
        }

        private void ShowPage(int index)
        {
            if (_pageAi == null || _pageShot == null) return;
            bool ai = index == 1;
            _pageAi.Visible = ai;
            _pageShot.Visible = index == 2;
            _pageLocal.Visible = index == 0;
            if (_tabs != null && _tabs.SelectedIndex != index) _tabs.SelectedIndex = index;
            if (_saveBtn != null) _saveBtn.Focus();
        }

        /// <summary>窗口超出当前屏幕工作区时自动缩小，页面内部改为可滚动，避免底栏/按钮被截断。</summary>
        private void FitToScreen()
        {
            try
            {
                Rectangle work = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : Rectangle.Empty;
                if (work.Width > 100 && work.Height > 100)
                {
                    int maxW = Math.Max(560, work.Width - 24);
                    int maxH = Math.Max(420, work.Height - 24);
                    int w = Math.Min(ClientSize.Width, maxW);
                    int h = Math.Min(ClientSize.Height, maxH);
                    if (w != ClientSize.Width || h != ClientSize.Height)
                        ClientSize = new Size(w, h);
                }
            }
            catch (Exception ex)
            {
                Log.Write("设置窗口自适应屏幕失败: " + ex.Message);
            }
            ApplyViewport();
        }

        /// <summary>按当前客户区重新摆放标题栏/页面/底栏；页面内容放不下时启用滚动。</summary>
        private void ApplyViewport()
        {
            if (_titlePanel == null || _bottomPanel == null || _pageAi == null || _pageShot == null) return;
            // 通过标题栏高度与设计高度的比例推断当前缩放（兼容 AutoScaleMode.Dpi 与测试缩放）
            float s = _titlePanel.Height / (float)TitleH;
            if (s < 0.5f || s > 4f) s = 1f;
            int width = ClientSize.Width;
            int bottomTop = ClientSize.Height - (int)Math.Round((BottomH + 1) * s);
            int pageTop = (int)Math.Round(PageTop * s);
            int pageLeft = (int)Math.Round(PageMarginX * s);
            int pageWidth = Math.Max(200, width - pageLeft * 2);
            int pageH = Math.Max(120, bottomTop - pageTop - (int)Math.Round(16 * s));

            _titlePanel.Width = Math.Max(100, width - (int)Math.Round(2 * s));
            if (_closeBtn != null) _closeBtn.Left = _titlePanel.Width - (int)Math.Round(46 * s);
            _bottomPanel.Top = bottomTop;
            _bottomPanel.Width = Math.Max(100, width - (int)Math.Round(2 * s));
            if (_resetBtn != null) _resetBtn.Left = (int)Math.Round(18 * s);
            if (_saveBtn != null) _saveBtn.Left = width - (int)Math.Round(24 * s) - _saveBtn.Width;
            if (_cancelBtn != null) _cancelBtn.Left = _saveBtn.Left - (int)Math.Round(12 * s) - _cancelBtn.Width;

            ApplyPageViewport(_pageAi, pageLeft, pageTop, pageWidth, pageH);
            ApplyPageViewport(_pageShot, pageLeft, pageTop, pageWidth, pageH);
            ApplyPageViewport(_pageLocal, pageLeft, pageTop, pageWidth, pageH);
        }

        private static void ApplyPageViewport(Panel page, int left, int top, int width, int height)
        {
            int contentH = 0;
            int contentW = 0;
            Point offset = page.AutoScrollPosition;
            foreach (Control child in page.Controls)
            {
                if (child == null || child.IsDisposed) continue;
                contentH = Math.Max(contentH, child.Bottom - offset.Y);
                contentW = Math.Max(contentW, child.Right - offset.X);
            }
            bool scroll = contentH > height + 8; // 8px 容差：避免缩放取整误差导致出现不必要的滚动条
            if (scroll)
            {
                // 预留纵向滚动条宽度，避免出现横向滚动条/右侧内容被裁掉
                width += SystemInformation.VerticalScrollBarWidth;
            }
            page.Bounds = new Rectangle(left, top, width, height);
            page.AutoScroll = scroll || contentW > width;
            page.AutoScrollMinSize = page.AutoScroll ? new Size(contentW, contentH) : Size.Empty;
        }

        // =====================================================================
        // AI 设置页
        // =====================================================================

        private void BuildLocalPage(Panel page)
        {
            SettingsCard engine = CreateCard(page, "离线文字识别", 0, 0, PageW, 178);
            _localStatus = MakeLabel(engine, "", 20, 48, 640, 30);
            _localStatus.Name = "LocalOcrStatus";
            Action refresh = delegate
            {
                _localStatus.Text = LocalOcr.IsAvailable
                    ? "组件已就绪 · 本地离线识别 · 简体中文与英文"
                    : "组件缺失 · 请把完整的 ocr 文件夹放在程序旁边";
                _localStatus.ForeColor = LocalOcr.IsAvailable ? SettingsColors.Green : SettingsColors.Red;
            };
            refresh();
            MakeHint(engine, "文字和坐标均在本机识别。提取文字不需要 Key，也不会上传截图。", 20, 86, 665, 24);
            MakeHint(engine, "请保留程序旁的 ocr 文件夹；模型需要额外存储空间。", 20, 114, 665, 24);
            var refreshButton = MakeButton(engine, "检查组件", SettingsButtonStyle.Secondary, 548, 136, 130, 30);
            refreshButton.Click += delegate { refresh(); };

            SettingsCard privacy = CreateCard(page, "智能打码", 0, 194, PageW, 268);
            MakeHint(privacy, "手机号、证件号、银行卡、邮箱及带标签的账号等先由本地规则检测。", 20, 48, 665, 26);
            _semanticPrivacySwitch = MakeSwitchRow(privacy, "联网语义补漏", 20, 92);
            _semanticPrivacySwitch.Name = "SemanticPrivacySwitch";
            MakeHint(privacy, "有 Key 时发送 OCR 文字，补充姓名、住址等判断。", 270, 88, 400, 36);
            _visionPrivacySwitch = MakeSwitchRow(privacy, "检测头像与二维码", 20, 146);
            _visionPrivacySwitch.Name = "VisionPrivacySwitch";
            MakeHint(privacy, "开启后上传选区；需单独填写支持图片的视觉模型。", 270, 142, 400, 36);
            MakeHint(privacy, "识别可能遗漏。打码结果先显示在编辑器中，请检查后再复制或保存。", 20, 204, 665, 38);

            SettingsCard customRules = CreateCard(page, "自定义隐私规则", 0, 474, PageW, 230);
            MakeLabel(customRules, "自定义敏感词（每行一条）：", 20, 44, 300, 24);
            SettingsField sensitiveField = MakeTextField(customRules, 20, 70, 316, 110, "例如：\n内部机密\n张三\n13800000000", true);
            _customSensitiveBox = (TextBox)sensitiveField.Inner;
            _customSensitiveBox.Name = "CustomSensitiveBox";

            MakeLabel(customRules, "白名单忽略词（每行一条，不打码）：", 354, 44, 320, 24);
            SettingsField ignoredField = MakeTextField(customRules, 354, 70, 316, 110, "例如：\n公开电话\n公司地址\n客服热线", true);
            _customIgnoredBox = (TextBox)ignoredField.Inner;
            _customIgnoredBox.Name = "CustomIgnoredBox";

            MakeHint(customRules, "匹配敏感词的文字将自动打码；白名单内的词条会被排除不打码。", 20, 190, 665, 26);
        }

        private void BuildAiPage(Panel page)
        {
            int card1Y = _firstRun ? 100 : 0;
            SettingsCard card1 = CreateCard(page, "API 连接", 0, card1Y, PageW, 266);
            SettingsCard card2 = CreateCard(page, "AI 行为", 0, card1Y + 282, PageW, 217);

            MakeLabel(card1, "API Key", 18, 48, 84, 36);
            _apiKeyField = MakeTextField(card1, 106, 48, 330, 36, "请输入 API Key（sk- 开头）", false, true);
            _apiKeyField.Name = "ApiKeyField";
            _apiKeyBox = (TextBox)_apiKeyField.Inner;
            _apiKeyBox.Name = "ApiKeyBox";
            _showKeySwitch = MakeSwitchOnly(card1, 450, 57);
            _showKeySwitch.Name = "ShowKeySwitch";
            _showKeySwitch.CheckedChanged += delegate
            {
                _apiKeyBox.UseSystemPasswordChar = !_showKeySwitch.Checked;
            };
            MakeLabel(card1, "显示", 492, 48, 44, 36);
            Label credentialHint = MakeHint(card1, "凭据由所填服务管理", 540, 48, 130, 36);
            credentialHint.Name = "CredentialHint";

            _apiKeyWarn = MakeHint(card1, "", 106, 86, 570, 20);
            _apiKeyWarn.Name = "ApiKeyWarn";

            MakeLabel(card1, "Base URL", 18, 112, 84, 36);
            _baseUrlField = MakeTextField(card1, 106, 112, 380, 36, "输入接口地址");
            _baseUrlField.Name = "BaseUrlField";
            _baseUrlBox = (TextBox)_baseUrlField.Inner;
            _baseUrlBox.Name = "BaseUrlBox";
            MakeHint(card1, "使用兼容的聊天补全接口；API Key 须与所填服务对应。",
                106, 150, 570, 18);

            MakeLabel(card1, "视觉模型", 18, 172, 84, 36);
            _visionField = MakeTextField(card1, 106, 172, 210, 36, "可空，启用图片检测时填写");
            _visionField.Name = "VisionModelField";
            _visionModelBox = (TextBox)_visionField.Inner;
            _visionModelBox.Name = "VisionModelBox";
            MakeLabel(card1, "文本模型", 336, 172, 84, 36);
            _textField = MakeTextField(card1, 424, 172, 242, 36, "输入模型名称");
            _textField.Name = "TextModelField";
            _textModelBox = (TextBox)_textField.Inner;
            _textModelBox.Name = "TextModelBox";

            MakeLabel(card1, "超时（秒）", 18, 216, 84, 36);
            _timeoutField = MakeNumberField(card1, 106, 216, 110, 36, 10, 600, 120, 5);
            _timeoutField.Name = "TimeoutField";
            _timeoutNum = (NumericUpDown)_timeoutField.Inner;
            _timeoutNum.Name = "TimeoutNum";

            _testBtn = MakeButton(card1, "测试连接", SettingsButtonStyle.Secondary, 232, 217, 110, 34);
            _testBtn.Name = "TestButton";
            _testBtn.Click += OnTestConnectionClick;
            _testStatus = MakeLabel(card1, "未测试", 356, 216, 330, 36);
            _testStatus.Name = "TestStatus";
            _testStatus.ForeColor = SettingsColors.TextSecondary;
            _testStatus.AutoEllipsis = true;

            // ===== AI 行为 =====
            MakeLabel(card2, "翻译目标语言", 18, 48, 110, 36);
            _translateCombo = MakeCombo(card2, 134, 48, 200, 36, SettingsComboStyle.DropDown,
                "中文", "English", "日本語", "한국어", "自动");
            _translateCombo.Name = "TranslateCombo";
            MakeHint(card2, "可输入自定义语言，如 繁體中文", 344, 48, 340, 36);

            _showUsageSwitch = MakeSwitchRow(card2, "显示 token 用量", 18, 90);
            _showUsageSwitch.Name = "ShowUsageSwitch";
            MakeHint(card2, "AI 面板底部显示本次请求的 token 消耗", 270, 90, 360, 24);

            MakeLabel(card2, "翻译附加提示词（可选）", 18, 126, 500, 20);
            _translatePromptField = MakeTextField(card2, 18, 150, 677, 52, "", true);
            _translatePromptField.Name = "TranslatePromptField";
            _translatePromptBox = (TextBox)_translatePromptField.Inner;
            _translatePromptBox.Name = "TranslatePromptBox";

            if (_firstRun) BuildWelcomeCard(page);
        }

        private void BuildWelcomeCard(Panel page)
        {
            _welcomeCard = new SettingsCard();
            _welcomeCard.Name = "WelcomePanel";
            _welcomeCard.Bounds = new Rectangle(0, 0, PageW, 84);
            _welcomeCard.BackColor = SettingsColors.GreenLight;
            _welcomeCard.OutlineColor = Color.FromArgb(210, 238, 224);
            page.Controls.Add(_welcomeCard);

            Label title = new Label();
            title.AutoSize = false;
            title.Bounds = new Rectangle(20, 12, PageW - 40, 24);
            title.Font = new Font(Font, FontStyle.Bold);
            title.ForeColor = SettingsColors.Green;
            title.BackColor = Color.Transparent;
            title.Text = "欢迎使用" + AppInfo.Name;
            _welcomeCard.Controls.Add(title);

            Label steps = new Label();
            steps.AutoSize = false;
            steps.Bounds = new Rectangle(20, 38, PageW - 40, 38);
            steps.Font = _hintFont != null ? _hintFont : Font;
            steps.ForeColor = SettingsColors.TextSecondary;
            steps.BackColor = Color.Transparent;
            steps.Text = "本地文字提取和隐私规则检测可直接使用。翻译时填写兼容接口的 Base URL、模型和 API Key。" +
                         "截图、录制与开机自启可在对应页面调整。";
            _welcomeCard.Controls.Add(steps);
        }

        // =====================================================================
        // 截图与录制页
        // =====================================================================

        private void BuildShotPage(Panel page)
        {
            SettingsCard card1 = CreateCard(page, "截图", 0, 0, PageW, 290);
            SettingsCard card2 = CreateCard(page, "视频录制", 0, 306, PageW, 294);
            SettingsCard card3 = CreateCard(page, "快捷操作", 0, 616, PageW, 220);

            MakeLabel(card1, "全局热键", 18, 48, 84, 36);
            _hotkeyBox = MakeHotkeyBox(card1, 106, 48, 240, 36);
            _hotkeyBox.Name = "HotkeyBox";
            MakeHint(card1, "默认 Alt+A，点击后按新组合键", 360, 48, 330, 36);

            MakeLabel(card1, "保存目录", 18, 94, 84, 36);
            _saveDirField = MakeTextField(card1, 106, 94, 380, 36, "留空则保存到图片\\截图助手");
            _saveDirField.Name = "SaveDirField";
            _saveDirBox = (TextBox)_saveDirField.Inner;
            _saveDirBox.Name = "SaveDirBox";
            _browseBtn = MakeButton(card1, "浏览…", SettingsButtonStyle.Secondary, 498, 95, 86, 34);
            _browseBtn.Name = "BrowseButton";
            _browseBtn.Click += OnBrowseClick;
            _openDirBtn = MakeButton(card1, "打开目录", SettingsButtonStyle.Secondary, 592, 95, 86, 34);
            _openDirBtn.Name = "OpenDirButton";
            _openDirBtn.Click += OnOpenDirClick;

            MakeLabel(card1, "保存格式", 18, 140, 84, 36);
            _formatCombo = MakeCombo(card1, 106, 140, 130, 36, SettingsComboStyle.DropDownList, "PNG", "JPG");
            _formatCombo.Name = "FormatCombo";
            _formatCombo.SelectedIndex = 0;
            MakeLabel(card1, "马赛克块大小", 256, 140, 110, 36);
            _mosaicField = MakeNumberField(card1, 372, 140, 100, 36, 2, 64, 20, 1);
            _mosaicField.Name = "MosaicField";
            _mosaicNum = (NumericUpDown)_mosaicField.Inner;
            _mosaicNum.Name = "MosaicNum";
            MakeHint(card1, "像素（2~64）", 480, 140, 180, 36);

            _autoCopySwitch = MakeSwitchRow(card1, "完成时自动复制", 18, 188);
            _autoCopySwitch.Name = "AutoCopySwitch";
            _autoSaveSwitch = MakeSwitchRow(card1, "完成时自动保存", 18, 220);
            _autoSaveSwitch.Name = "AutoSaveSwitch";
            _showColorSwitch = MakeSwitchRow(card1, "截图时显示色值", 18, 252);
            _showColorSwitch.Name = "ShowColorSwitch";
            _showDimensionsSwitch = MakeSwitchRow(card1, "显示选区尺寸", 360, 188);
            _showDimensionsSwitch.Name = "ShowDimensionsSwitch";
            _pinTopSwitch = MakeSwitchRow(card1, "钉图窗口置顶", 360, 220);
            _pinTopSwitch.Name = "PinTopSwitch";
            _autostartSwitch = MakeSwitchRow(card1, "开机自动启动", 360, 252);
            _autostartSwitch.Name = "AutostartSwitch";

            // ===== 视频录制 =====
            MakeLabel(card2, "录制帧率", 18, 48, 84, 36);
            _fpsCombo = MakeCombo(card2, 106, 48, 120, 36, SettingsComboStyle.DropDownList, FpsTexts);
            _fpsCombo.Name = "FpsCombo";
            _fpsCombo.SelectedIndex = 2;
            MakeLabel(card2, "录制质量", 256, 48, 84, 36);
            _qualityField = MakeNumberField(card2, 342, 48, 110, 36, 60, 95, 85, 1);
            _qualityField.Name = "QualityField";
            _qualityNum = (NumericUpDown)_qualityField.Inner;
            _qualityNum.Name = "QualityNum";
            MakeHint(card2, "编码质量（60~95）", 468, 48, 200, 36);

            _includeCursorSwitch = MakeSwitchRow(card2, "录制时显示鼠标", 18, 90);
            _includeCursorSwitch.Name = "IncludeCursorSwitch";
            MakeHint(card2, "录制视频时包含鼠标指针", 270, 90, 340, 24);

            MakeLabel(card2, "录制热键", 18, 126, 84, 36);
            _recordHotkeyBox = MakeHotkeyBox(card2, 106, 126, 240, 36);
            _recordHotkeyBox.Name = "RecordHotkeyBox";
            MakeHint(card2, "默认 Ctrl+Shift+P；按 Delete 清空可禁用录制热键", 356, 126, 330, 36);

            MakeLabel(card2, "视频格式", 18, 172, 84, 36);
            _recordFormatCombo = MakeCombo(card2, 106, 172, 170, 36, SettingsComboStyle.DropDownList, "MP4 (H.264)", "AVI (MJPEG)");
            _recordFormatCombo.Name = "RecordFormatCombo";
            MakeLabel(card2, "音频来源", 296, 172, 84, 36);
            _recordAudioCombo = MakeCombo(card2, 382, 172, 280, 36, SettingsComboStyle.DropDownList, "不录制音频", "系统声音", "麦克风", "系统声音与麦克风");
            _recordAudioCombo.Name = "RecordAudioCombo";
            MakeHint(card2, "框选后可调整本次音源，点击开始才录制；MP4 支持音频和暂停。", 18, 224, 650, 44);

            MakeLabel(card3, "提取文字", 18, 48, 84, 36);
            _ocrHotkeyBox = MakeHotkeyBox(card3, 106, 48, 240, 36);
            _ocrHotkeyBox.Name = "OcrHotkeyBox";
            MakeLabel(card3, "翻译选区", 18, 94, 84, 36);
            _translateHotkeyBox = MakeHotkeyBox(card3, 106, 94, 240, 36);
            _translateHotkeyBox.Name = "TranslateHotkeyBox";
            MakeLabel(card3, "智能打码", 18, 140, 84, 36);
            _privacyHotkeyBox = MakeHotkeyBox(card3, 106, 140, 240, 36);
            _privacyHotkeyBox.Name = "PrivacyHotkeyBox";
            MakeHint(card3, "按热键后框选即执行。\r\n按 Delete 清空可分别禁用。", 366, 48, 300, 82);

            SettingsCard card4 = CreateCard(page, "截图历史记录", 0, 848, PageW, 140);
            _historyEnabledSwitch = MakeSwitchRow(card4, "启用截图历史记录", 18, 48);
            _historyEnabledSwitch.Name = "HistoryEnabledSwitch";

            MakeLabel(card4, "最大数量", 260, 48, 70, 36);
            _historyMaxCountField = MakeNumberField(card4, 334, 48, 80, 36, 10, 1000, 100, 10);
            _historyMaxCountField.Name = "HistoryMaxCountField";
            _historyMaxCountNum = (NumericUpDown)_historyMaxCountField.Inner;
            _historyMaxCountNum.Name = "HistoryMaxCountNum";
            MakeHint(card4, "条", 420, 48, 30, 36);

            MakeLabel(card4, "存储容量", 470, 48, 70, 36);
            _historyMaxMBField = MakeNumberField(card4, 544, 48, 80, 36, 50, 5000, 500, 50);
            _historyMaxMBField.Name = "HistoryMaxMBField";
            _historyMaxMBNum = (NumericUpDown)_historyMaxMBField.Inner;
            _historyMaxMBNum.Name = "HistoryMaxMBNum";
            MakeHint(card4, "MB", 630, 48, 30, 36);

            MakeHint(card4, "开启后，截图完成或保存时将自动归档；超出限制时自动清理最早的记录。", 18, 96, 665, 28);
        }

        // =====================================================================
        // 底部按钮栏
        // =====================================================================

        private void BuildBottomBar()
        {
            _bottomPanel = new Panel();
            _bottomPanel.Name = "BottomBar";
            _bottomPanel.Bounds = new Rectangle(1, ClientSize.Height - BottomH - 1, WinW - 2, BottomH);
            _bottomPanel.BackColor = Color.White;
            _bottomPanel.Paint += delegate(object sender, PaintEventArgs e)
            {
                float s = SettingsDpi.Scale(_bottomPanel);
                using (Pen pen = new Pen(SettingsColors.Divider, Math.Max(1f, s)))
                    e.Graphics.DrawLine(pen, 0, 0, _bottomPanel.Width, 0);
            };
            Controls.Add(_bottomPanel);

            _resetBtn = new SettingsButton();
            _resetBtn.Name = "ResetButton";
            _resetBtn.Text = "恢复默认";
            _resetBtn.ButtonStyle = SettingsButtonStyle.Text;
            _resetBtn.Bounds = new Rectangle(18, 21, 110, 34);
            _resetBtn.Click += OnResetClick;
            _bottomPanel.Controls.Add(_resetBtn);

            _cancelBtn = new SettingsButton();
            _cancelBtn.Name = "CancelButton";
            _cancelBtn.Text = "取消";
            _cancelBtn.ButtonStyle = SettingsButtonStyle.Secondary;
            _cancelBtn.Bounds = new Rectangle(WinW - 2 - 24 - 96 - 12 - 104, 21, 96, 34);
            _cancelBtn.Click += delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            _bottomPanel.Controls.Add(_cancelBtn);

            _saveBtn = new SettingsButton();
            _saveBtn.Name = "SaveButton";
            _saveBtn.Text = "保存";
            _saveBtn.ButtonStyle = SettingsButtonStyle.Primary;
            _saveBtn.Bounds = new Rectangle(WinW - 2 - 24 - 104, 21, 104, 34);
            _saveBtn.Click += OnSaveClick;
            _bottomPanel.Controls.Add(_saveBtn);

            AcceptButton = _saveBtn;
            CancelButton = _cancelBtn;
        }

        // =====================================================================
        // 控件工厂
        // =====================================================================

        private SettingsCard CreateCard(Control parent, string title, int x, int y, int w, int h)
        {
            SettingsCard card = new SettingsCard();
            card.Title = title;
            card.Bounds = new Rectangle(x, y, w, h);
            parent.Controls.Add(card);
            return card;
        }

        private Label MakeLabel(Control parent, string text, int x, int y, int w, int h)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.Bounds = new Rectangle(x, y, w, h);
            label.Text = text;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = SettingsColors.TextPrimary;
            label.BackColor = Color.Transparent;
            parent.Controls.Add(label);
            return label;
        }

        private Label MakeHint(Control parent, string text, int x, int y, int w, int h)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.Bounds = new Rectangle(x, y, w, h);
            label.Text = text;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = SettingsColors.TextSecondary;
            label.BackColor = Color.Transparent;
            if (_hintFont != null) label.Font = _hintFont;
            parent.Controls.Add(label);
            _hintLabels.Add(label);
            return label;
        }

        private SettingsField MakeTextField(Control parent, int x, int y, int w, int h,
            string placeholder, bool multiline = false, bool password = false)
        {
            TextBox box = new TextBox();
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Color.White;
            box.ForeColor = SettingsColors.TextPrimary;
            box.Font = Font;
            if (multiline)
            {
                box.Multiline = true;
                box.ScrollBars = ScrollBars.Vertical;
                box.AcceptsReturn = true;
            }
            else
            {
                box.MaxLength = 1024;
            }
            if (password) box.UseSystemPasswordChar = true;
            SettingsField field = new SettingsField(box);
            field.Bounds = new Rectangle(x, y, w, h);
            field.Placeholder = placeholder;
            parent.Controls.Add(field);
            return field;
        }

        private SettingsField MakeNumberField(Control parent, int x, int y, int w, int h,
            int min, int max, int value, int increment)
        {
            NumericUpDown num = new NumericUpDown();
            num.BorderStyle = BorderStyle.None;
            num.BackColor = Color.White;
            num.ForeColor = SettingsColors.TextPrimary;
            num.Font = Font;
            num.Minimum = min;
            num.Maximum = max;
            num.Increment = increment;
            if (value < min) value = min;
            if (value > max) value = max;
            num.Value = value;
            SettingsField field = new SettingsField(num);
            field.Bounds = new Rectangle(x, y, w, h);
            parent.Controls.Add(field);
            return field;
        }

        private SettingsComboBox MakeCombo(Control parent, int x, int y, int w, int h,
            SettingsComboStyle style, params string[] items)
        {
            SettingsComboBox combo = new SettingsComboBox();
            combo.Bounds = new Rectangle(x, y, w, h);
            combo.Font = Font;
            combo.ComboStyle = style;
            combo.SetItems(items);
            parent.Controls.Add(combo);
            return combo;
        }

        private SettingsSwitch MakeSwitchOnly(Control parent, int x, int y)
        {
            SettingsSwitch toggle = new SettingsSwitch();
            toggle.Bounds = new Rectangle(x, y, 34, 18);
            parent.Controls.Add(toggle);
            return toggle;
        }

        private SettingsSwitch MakeSwitchRow(Control parent, string text, int x, int y)
        {
            SettingsSwitch toggle = MakeSwitchOnly(parent, x, y);
            Label label = MakeLabel(parent, text, x + 42, y, 200, 24);
            label.Tag = toggle;
            return toggle;
        }

        private HotkeyBox MakeHotkeyBox(Control parent, int x, int y, int w, int h)
        {
            HotkeyBox box = new HotkeyBox();
            box.Bounds = new Rectangle(x, y, w, h);
            box.Font = Font;
            // HotkeyBox 自带方形描边，用圆角 Region 裁剪使其与其它输入框风格一致
            SettingsRoundClip.Apply(box, 7f);
            parent.Controls.Add(box);
            return box;
        }

        private SettingsButton MakeButton(Control parent, string text, SettingsButtonStyle style,
            int x, int y, int w, int h)
        {
            SettingsButton button = new SettingsButton();
            button.Text = text;
            button.ButtonStyle = style;
            button.Bounds = new Rectangle(x, y, w, h);
            parent.Controls.Add(button);
            return button;
        }

        // =====================================================================
        // 字体 / 生命周期
        // =====================================================================

        private void RebuildFonts()
        {
            if (_titleFont != null)
            {
                try { _titleFont.Dispose(); } catch { }
                _titleFont = null;
            }
            if (_hintFont != null)
            {
                try { _hintFont.Dispose(); } catch { }
                _hintFont = null;
            }
            _titleFont = new Font(Font, FontStyle.Bold);
            _hintFont = new Font(Font.FontFamily, Math.Max(9f, Font.Size), Font.Style, GraphicsUnit.Point);
        }

        private void ApplyHintFont()
        {
            if (_hintFont == null) return;
            foreach (Label label in _hintLabels)
            {
                if (label == null || label.IsDisposed) continue;
                label.Font = _hintFont;
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            RebuildFonts();
            ApplyHintFont();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // DPI 自动缩放完成后：先按屏幕工作区自适应，再修正自绘输入框/下拉框内部子控件尺寸
            FitToScreen();
            RelayoutFields(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RelayoutFields(this);
            if (_apiKeyBox != null && !_apiKeyBox.IsDisposed)
            {
                _apiKeyBox.Focus();
                if (_firstRun) _apiKeyBox.SelectAll();
            }
        }

        private static void RelayoutFields(Control root)
        {
            if (root == null) return;
            foreach (Control child in root.Controls)
            {
                SettingsField field = child as SettingsField;
                if (field != null) field.Relayout();
                SettingsComboBox combo = child as SettingsComboBox;
                if (combo != null) combo.Relayout();
                RelayoutFields(child);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CancelTest();
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(SettingsColors.WindowBorder))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelTest();
                if (_titleFont != null)
                {
                    try { _titleFont.Dispose(); } catch { }
                    _titleFont = null;
                }
                if (_hintFont != null)
                {
                    try { _hintFont.Dispose(); } catch { }
                    _hintFont = null;
                }
            }
            base.Dispose(disposing);
        }

        // =====================================================================
        // 值加载 / 保存
        // =====================================================================

        private void LoadValues()
        {
            AppConfig cfg = _working;

            _apiKeyBox.Text = cfg.ApiKey ?? "";
            bool decryptFailed = _cfg != null && _cfg.ApiKeyDecryptFailed;
            if (decryptFailed)
            {
                _apiKeyBox.Text = "";
                _apiKeyWarn.ForeColor = SettingsColors.Red;
                _apiKeyWarn.Text = "API Key 无法解密（可能更换了电脑/用户），请重新填写";
            }
            else
            {
                _apiKeyWarn.ForeColor = SettingsColors.TextSecondary;
                _apiKeyWarn.Text = "Key 在本机加密保存；联网请求时发送到所配置的 API 服务商。";
            }
            _showKeySwitch.Checked = false;
            _apiKeyBox.UseSystemPasswordChar = true;

            _baseUrlBox.Text = cfg.BaseUrl ?? "";
            _visionModelBox.Text = cfg.VisionModel ?? "";
            _textModelBox.Text = cfg.TextModel ?? "";
            _timeoutNum.Value = ClampDecimal(_timeoutNum, cfg.TimeoutSec);
            SetTestStatus("未测试", SettingsColors.TextSecondary);

            _hotkeyBox.HotkeyText = string.IsNullOrWhiteSpace(cfg.Hotkey) ? "Alt+A" : cfg.Hotkey;
            _saveDirBox.Text = cfg.SaveDir ?? "";
            _formatCombo.SelectedIndex = IsJpeg(cfg.SaveFormat) ? 1 : 0;
            _mosaicNum.Value = ClampDecimal(_mosaicNum, (int)Math.Round(cfg.MosaicBlock));

            _translateCombo.Text = string.IsNullOrWhiteSpace(cfg.TranslateTarget) ? "中文" : cfg.TranslateTarget;
            _showUsageSwitch.Checked = cfg.ShowUsage;

            _autoCopySwitch.Checked = cfg.AutoCopy;
            _autoSaveSwitch.Checked = cfg.AutoSave;
            _showColorSwitch.Checked = cfg.ShowColorValue;
            _showDimensionsSwitch.Checked = cfg.ShowDimensions;
            _pinTopSwitch.Checked = cfg.PinAlwaysOnTop;
            _autostartSwitch.Checked = cfg.StartWithWindows;

            _fpsCombo.SelectedIndex = FpsIndexOf(cfg.RecordFps);
            _qualityNum.Value = ClampDecimal(_qualityNum, cfg.RecordQuality);
            _includeCursorSwitch.Checked = cfg.RecordIncludeCursor;
            _recordHotkeyBox.HotkeyText = cfg.RecordHotkey ?? "";
            _ocrHotkeyBox.HotkeyText = cfg.OcrHotkey ?? "";
            _translateHotkeyBox.HotkeyText = cfg.TranslateHotkey ?? "";
            _privacyHotkeyBox.HotkeyText = cfg.PrivacyHotkey ?? "";
            _recordFormatCombo.SelectedIndex = string.Equals(cfg.RecordFormat, "mp4", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            _recordAudioCombo.SelectedIndex = Math.Max(0, Array.IndexOf(AudioModes, cfg.RecordAudioMode));

            _translatePromptBox.Text = cfg.TranslateExtraPrompt ?? "";
            _semanticPrivacySwitch.Checked = cfg.SemanticPrivacy;
            _visionPrivacySwitch.Checked = cfg.VisionPrivacy;
            _customSensitiveBox.Text = cfg.CustomSensitiveTerms ?? "";
            _customIgnoredBox.Text = cfg.CustomIgnoredTerms ?? "";
            _historyEnabledSwitch.Checked = cfg.HistoryEnabled;
            _historyMaxCountNum.Value = ClampDecimal(_historyMaxCountNum, cfg.HistoryMaxCount);
            _historyMaxMBNum.Value = ClampDecimal(_historyMaxMBNum, cfg.HistoryMaxMB);
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

        private static int FpsIndexOf(int fps)
        {
            for (int i = 0; i < FpsValues.Length; i++)
            {
                if (FpsValues[i] == fps) return i;
            }
            return 2; // 默认 15
        }

        private void SaveFromControls()
        {
            _working.ApiKey = _apiKeyBox.Text.Trim();
            _working.ApiKeyDecryptFailed = false;
            _working.BaseUrl = _baseUrlBox.Text.Trim();
            _working.VisionModel = _visionModelBox.Text.Trim();
            _working.TextModel = _textModelBox.Text.Trim();
            _working.TimeoutSec = (int)_timeoutNum.Value;
            _working.SemanticPrivacy = _semanticPrivacySwitch.Checked;
            _working.VisionPrivacy = _visionPrivacySwitch.Checked;
            _working.Hotkey = (_hotkeyBox.HotkeyText ?? "").Trim();
            _working.SaveDir = _saveDirBox.Text.Trim();
            _working.SaveFormat = _formatCombo.SelectedIndex == 1 ? "jpg" : "png";
            _working.TranslateTarget = (_translateCombo.Text ?? "").Trim();
            _working.ShowUsage = _showUsageSwitch.Checked;
            _working.MosaicBlock = (float)_mosaicNum.Value;
            _working.TranslateExtraPrompt = _translatePromptBox.Text ?? "";
            _working.AutoCopy = _autoCopySwitch.Checked;
            _working.AutoSave = _autoSaveSwitch.Checked;
            _working.ShowColorValue = _showColorSwitch.Checked;
            _working.ShowDimensions = _showDimensionsSwitch.Checked;
            _working.PinAlwaysOnTop = _pinTopSwitch.Checked;
            _working.StartWithWindows = _autostartSwitch.Checked;
            int fpsIndex = _fpsCombo.SelectedIndex;
            _working.RecordFps = fpsIndex >= 0 && fpsIndex < FpsValues.Length ? FpsValues[fpsIndex] : 15;
            _working.RecordQuality = (int)_qualityNum.Value;
            _working.RecordIncludeCursor = _includeCursorSwitch.Checked;
            _working.RecordHotkey = (_recordHotkeyBox.HotkeyText ?? "").Trim();
            _working.OcrHotkey = (_ocrHotkeyBox.HotkeyText ?? "").Trim();
            _working.TranslateHotkey = (_translateHotkeyBox.HotkeyText ?? "").Trim();
            _working.PrivacyHotkey = (_privacyHotkeyBox.HotkeyText ?? "").Trim();
            _working.RecordFormat = _recordFormatCombo.SelectedIndex == 0 ? "mp4" : "avi";
            _working.RecordAudioMode = AudioModes[Math.Max(0, _recordAudioCombo.SelectedIndex)];
            _working.CustomSensitiveTerms = _customSensitiveBox.Text ?? "";
            _working.CustomIgnoredTerms = _customIgnoredBox.Text ?? "";
            _working.HistoryEnabled = _historyEnabledSwitch.Checked;
            _working.HistoryMaxCount = (int)_historyMaxCountNum.Value;
            _working.HistoryMaxMB = (int)_historyMaxMBNum.Value;
            _working.FirstRunDone = true;
        }

        // =====================================================================
        // 保存 / 取消 / 恢复默认
        // =====================================================================

        private void OnSaveClick(object sender, EventArgs e)
        {
            try
            {
                if (!ValidateInputs()) return;

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
            if (_apiKeyBox.Text.Trim().Length > 0 && _baseUrlBox.Text.Trim().Length == 0)
                return FailValidation("请填写接口 Base URL。", _baseUrlField);
            if (_visionPrivacySwitch.Checked && _apiKeyBox.Text.Trim().Length > 0 && _visionModelBox.Text.Trim().Length == 0)
                return FailValidation("请填写支持图片输入的视觉模型名称；未启用图片检测时可留空。", _visionField);
            if (_apiKeyBox.Text.Trim().Length > 0 && _textModelBox.Text.Trim().Length == 0)
                return FailValidation("请填写文本模型名称。", _textField);

            HotkeySpec spec;
            if (!HotkeyUtil.TryParse(_hotkeyBox.HotkeyText, out spec))
                return FailValidation("全局热键无效：请点击热键框后按下组合键（例如 Alt+A，F1~F24 也可单独使用）。", _hotkeyBox);

            if (_translateCombo.Text.Trim().Length == 0)
                return FailValidation("请填写翻译目标语言（例如 中文 / English）。", _translateCombo);

            int block = (int)_mosaicNum.Value;
            if (block < 2 || block > 64)
                return FailValidation("马赛克块大小必须在 2~64 像素之间。", _mosaicField);

            string recordHotkey = (_recordHotkeyBox.HotkeyText ?? "").Trim();
            if (recordHotkey.Length > 0)
            {
                HotkeySpec recordSpec;
                if (!HotkeyUtil.TryParse(recordHotkey, out recordSpec))
                    return FailValidation("录制热键无效：请点击热键框后按下组合键，或按 Delete 清空以禁用录制热键。", _recordHotkeyBox);
            }
            var shortcuts = new[] { _hotkeyBox, _recordHotkeyBox, _ocrHotkeyBox, _translateHotkeyBox, _privacyHotkeyBox };
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HotkeyBox box in shortcuts)
            {
                string value = (box.HotkeyText ?? "").Trim();
                if (value.Length == 0) continue;
                HotkeySpec shortcut;
                if (!HotkeyUtil.TryParse(value, out shortcut)) return FailValidation("快捷键格式无效。", box);
                string identity = HotkeyUtil.Modifiers(shortcut) + ":" + (int)shortcut.Key;
                if (!used.Add(identity)) return FailValidation("不同操作不能使用同一个快捷键。", box);
            }
            if (_recordFormatCombo.SelectedIndex == 1 && _recordAudioCombo.SelectedIndex > 0)
                return FailValidation("录制音频请选择 MP4 格式。", _recordFormatCombo);
            try
            {
                PrivacyRules.Canonicalize(_customSensitiveBox.Text);
                PrivacyRules.Canonicalize(_customIgnoredBox.Text);
            }
            catch (Exception ex)
            {
                return FailValidation("隐私规则格式错误：" + ex.Message, _customSensitiveBox);
            }
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
                "确定要恢复所有设置为默认值吗？\r\n接口地址和模型将恢复为内置默认值。\r\n当前为自定义服务时会清空 Key，请重新填写兼容服务的 Key；内置服务 Key 会保留。",
                "恢复默认设置", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;
            AppConfig defaults = AppConfig.CreateDefaultsForReset(_baseUrlBox.Text, _apiKeyBox.Text.Trim());
            _working = defaults;
            LoadValues();
            SetTestStatus("未测试", SettingsColors.TextSecondary);
        }

        // =====================================================================
        // 浏览 / 打开目录
        // =====================================================================

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择截图 / 录制文件保存目录";
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

        // =====================================================================
        // 测试连接
        // =====================================================================

        private async void OnTestConnectionClick(object sender, EventArgs e)
        {
            if (_testing) return;
            if (_apiKeyBox.Text.Trim().Length == 0)
            {
                SetTestStatus("请先填写 API Key。", SettingsColors.Red);
                _apiKeyBox.Focus();
                return;
            }
            string baseUrl = _baseUrlBox.Text.Trim();
            if (baseUrl.Length == 0)
            {
                SetTestStatus("请先填写 Base URL。", SettingsColors.Red);
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
            SetTestStatus("正在测试连接…", SettingsColors.TextSecondary);

            CancellationTokenSource cts = new CancellationTokenSource();
            _testCts = cts;
            try
            {
                AiUsage usage = await new DeepSeekClient(temp).TestConnectionAsync(cts.Token);
                if (cts.IsCancellationRequested || IsDisposed) return;
                double seconds = (usage != null ? usage.ElapsedMs : 0) / 1000.0;
                SetTestStatus("连接成功，耗时 " + seconds.ToString("0.0") + " 秒", SettingsColors.Green);
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) SetTestStatus("已取消测试。", SettingsColors.TextSecondary);
            }
            catch (AiException ex)
            {
                if (!IsDisposed && !cts.IsCancellationRequested) SetTestStatus(ex.Message, SettingsColors.Red);
            }
            catch (Exception ex)
            {
                Log.Write("测试连接异常: " + ex);
                if (!IsDisposed && !cts.IsCancellationRequested)
                    SetTestStatus("测试失败：" + ex.Message, SettingsColors.Red);
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
    }
}
