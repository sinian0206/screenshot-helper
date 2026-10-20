using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace XiaoJie
{
    public enum AiPanelMode
    {
        Ocr,
        Translate
    }

    /// <summary>
    /// 内嵌在截图编辑器里的 AI 结果面板（文字提取 / 文字翻译）。
    /// 由 OverlayForm 创建、设定位置/大小并 Add 到 Controls，然后调用 Start()。
    /// 面板自己负责调用 DeepSeekClient、流式刷新、复制、关闭、错误提示。
    /// 关闭时触发 CloseRequested，由 OverlayForm 移除并释放。
    /// 负责队友：ai-ui
    /// </summary>
    public sealed class AiResultPanel : Panel
    {
        private readonly Bitmap _aiImage;
        private readonly AiPanelMode _mode;
        private readonly AppConfig _cfg;
        private readonly DeepSeekClient _client;
        private readonly float _s;

        private readonly int _titleH;
        private readonly int _bottomH;
        private readonly int _margin;

        private Font _fontTitle;
        private Font _fontIcon;
        private Font _fontText;
        private Font _fontSmall;
        private Font _fontButton;
        private Font _fontUsage;

        private TextBox _ocrBox;
        private TextBox _originalBox;
        private TextBox _translationBox;
        private Label _originalLabel;
        private Label _translationLabel;
        private Label _usageLabel;
        private Button _copyOcrAllBtn;
        private Button _copyTableBtn;
        private Button _formatBtn;
        private string _currentFormat = "lines";
        private LocalOcrResult _lastOcrResult;
        private Button _copyOriginalBtn;
        private Button _copyTranslationBtn;
        private Button _copyAllBtn;
        private Button _closeBtn;
        private AiCloseButton _closeGlyph;

        private AiLoadingOverlay _ocrLoading;
        private AiLoadingOverlay _originalLoading;
        private AiLoadingOverlay _translationLoading;
        private AiErrorView _ocrError;
        private AiErrorView _originalError;
        private AiErrorView _translationError;

        private readonly System.Windows.Forms.Timer _animTimer;
        private int _spinAngle;
        private bool _built;
        private bool _started;
        private bool _closeRaised;
        private bool _translationStage;
        private CancellationTokenSource _cts;
        private AiUsage _usage;
        private Region _roundRegion;

        private Rectangle _ocrBorder;
        private Rectangle _originalBorder;
        private Rectangle _translationBorder;

        public AiResultPanel(Bitmap aiImage, AiPanelMode mode, AppConfig cfg, DeepSeekClient client, float uiScale)
        {
            _aiImage = aiImage;
            _mode = mode;
            _cfg = cfg;
            _client = client;
            _s = uiScale <= 0f ? 1f : (uiScale < 0.5f ? 0.5f : (uiScale > 4f ? 4f : uiScale));

            _titleH = (int)Math.Round(48 * _s);
            _bottomH = (int)Math.Round(58 * _s);
            _margin = (int)Math.Round(16 * _s);

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Color.White;
            Size = new Size((int)Math.Round(520 * _s), (int)Math.Round(560 * _s));

            BuildUi();
            _built = true;
            LayoutChildren();
            UpdateRoundedRegion();

            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 40;
            _animTimer.Tick += OnAnimationTick;
        }

        /// <summary>请求关闭面板（由 OverlayForm 移除并释放）。</summary>
        public event EventHandler CloseRequested;

        internal string SearchableText
        {
            get
            {
                if (_lastOcrResult != null && !string.IsNullOrWhiteSpace(_lastOcrResult.Text))
                    return _lastOcrResult.Text;
                return _originalBox != null ? _originalBox.Text : "";
            }
        }

        /// <summary>显示已经完成的原位翻译，不再次发起识别或联网请求。</summary>
        internal void ShowCompletedTranslation(string original, string translation)
        {
            _started = true;
            HideAllOverlays();
            _originalBox.Text = NormalizeNewlines(original);
            _translationBox.Text = NormalizeNewlines(translation);
            _translationStage = true;
            UpdateUsageLabel();
        }

        // ===================== 公开方法 =====================

        /// <summary>开始请求（由 OverlayForm 调用一次）。</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            if (!IsHandleCreated)
            {
                try { CreateControl(); } catch { }
            }
            BeginRequest();
        }

        /// <summary>取消进行中的请求（由 OverlayForm 在删除面板前调用）。</summary>
        public void CancelWork()
        {
            CancelCurrent();
            HideAllOverlays();
            if (_animTimer != null) _animTimer.Stop();
        }

        // ===================== 界面构建 =====================

        private void BuildUi()
        {
            _fontTitle = UiTheme.Font(15f * _s, FontStyle.Bold);
            _fontIcon = UiTheme.Font(11.5f * _s, FontStyle.Bold);
            _fontText = UiTheme.Font(14f * _s);
            _fontSmall = UiTheme.Font(12f * _s);
            _fontButton = UiTheme.Font(13f * _s);
            _fontUsage = UiTheme.Font(12f * _s);

            _closeGlyph = new AiCloseButton();
            _closeGlyph.Name = "CloseGlyph";
            _closeGlyph.Click += OnCloseClicked;
            Controls.Add(_closeGlyph);

            if (_mode == AiPanelMode.Ocr)
            {
                _ocrBox = CreateReadOnlyBox();
                Controls.Add(_ocrBox);
                _ocrLoading = new AiLoadingOverlay(_s, UiTheme.Font(13f * _s));
                Controls.Add(_ocrLoading);
                _ocrError = new AiErrorView(_s);
                _ocrError.Retry += OnRetryClicked;
                Controls.Add(_ocrError);

                _copyOcrAllBtn = MakeButton("复制全部", true);
                _copyOcrAllBtn.Click += delegate { CopyText(AllText()); };

                _copyTableBtn = MakeButton("复制表格", false);
                _copyTableBtn.Click += delegate
                {
                    if (_lastOcrResult == null) { Toast.Show("暂无可复制的内容"); return; }
                    var res = OcrTextFormatter.Format(_lastOcrResult, "table", false);
                    if (ClipUtil.CopyText(res.Text))
                        Toast.Show(string.IsNullOrEmpty(res.Warning) ? "已复制表格" : res.Warning);
                };

                _formatBtn = MakeButton("合并段落", false);
                _formatBtn.Click += delegate
                {
                    if (_lastOcrResult == null) return;
                    if (_currentFormat == "lines")
                    {
                        _currentFormat = "paragraphs";
                        _formatBtn.Text = "保留换行";
                    }
                    else
                    {
                        _currentFormat = "lines";
                        _formatBtn.Text = "合并段落";
                    }
                    var fmt = OcrTextFormatter.Format(_lastOcrResult, _currentFormat, true);
                    _ocrBox.Text = fmt.Text;
                };

                _closeBtn = MakeButton("关闭", false);
                _closeBtn.Click += OnCloseClicked;
                Controls.Add(_copyOcrAllBtn);
                Controls.Add(_copyTableBtn);
                Controls.Add(_formatBtn);
                Controls.Add(_closeBtn);
            }
            else
            {
                _originalLabel = MakeSmallLabel("原文");
                _originalBox = CreateReadOnlyBox();
                Controls.Add(_originalLabel);
                Controls.Add(_originalBox);

                _translationLabel = MakeSmallLabel("译文");
                _translationBox = CreateReadOnlyBox();
                Controls.Add(_translationLabel);
                Controls.Add(_translationBox);

                _originalLoading = new AiLoadingOverlay(_s, UiTheme.Font(13f * _s));
                Controls.Add(_originalLoading);
                _translationLoading = new AiLoadingOverlay(_s, UiTheme.Font(13f * _s));
                Controls.Add(_translationLoading);

                _originalError = new AiErrorView(_s);
                _originalError.Retry += OnRetryClicked;
                Controls.Add(_originalError);
                _translationError = new AiErrorView(_s);
                _translationError.Retry += OnRetryClicked;
                Controls.Add(_translationError);

                _copyOriginalBtn = MakeButton("复制原文", false);
                _copyOriginalBtn.Click += delegate { CopyText(_originalBox != null ? _originalBox.Text : ""); };
                _copyTranslationBtn = MakeButton("复制译文", false);
                _copyTranslationBtn.Click += delegate { CopyText(_translationBox != null ? _translationBox.Text : ""); };
                _copyAllBtn = MakeButton("复制全部", true);
                _copyAllBtn.Click += delegate { CopyText(AllText()); };
                _closeBtn = MakeButton("关闭", false);
                _closeBtn.Click += OnCloseClicked;
                Controls.Add(_copyOriginalBtn);
                Controls.Add(_copyTranslationBtn);
                Controls.Add(_copyAllBtn);
                Controls.Add(_closeBtn);
            }

            _usageLabel = new Label();
            _usageLabel.Name = "UsageLabel";
            _usageLabel.AutoSize = false;
            _usageLabel.TextAlign = ContentAlignment.MiddleRight;
            _usageLabel.ForeColor = UiTheme.TextGray;
            _usageLabel.BackColor = Color.White;
            _usageLabel.Font = _fontUsage;
            _usageLabel.AutoEllipsis = true;
            _usageLabel.Text = "";
            _usageLabel.Visible = _cfg == null || _cfg.ShowUsage;
            Controls.Add(_usageLabel);
        }

        private TextBox CreateReadOnlyBox()
        {
            TextBox box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.WordWrap = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Color.White;
            box.ForeColor = UiTheme.TextDark;
            box.Font = _fontText;
            box.HideSelection = false;
            box.ShortcutsEnabled = true;
            box.TabStop = true;
            return box;
        }

        private Button MakeButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.Font = _fontButton;
            button.BackColor = primary ? UiTheme.Signal : Color.White;
            button.ForeColor = primary ? UiTheme.SignalText : UiTheme.TextDark;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = primary ? UiTheme.Signal : SettingsColors.InputBorder;
            button.FlatAppearance.MouseOverBackColor = primary ? UiTheme.ConfirmBgHover : UiTheme.HoverBg;
            button.FlatAppearance.MouseDownBackColor = primary ? SettingsColors.GreenPressed : SettingsColors.PressedBg;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.TabStop = true;
            return button;
        }

        private Label MakeSmallLabel(string text)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.Text = text;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = UiTheme.TextGray;
            label.BackColor = Color.White;
            label.Font = _fontSmall;
            return label;
        }

        // ===================== 布局 =====================

        private void LayoutChildren()
        {
            if (!_built || Width <= 20 || Height <= 20) return;
            int w = Width;
            int h = Height;
            int margin = _margin;

            int closeSize = (int)Math.Round(32 * _s);
            _closeGlyph.Bounds = new Rectangle(w - margin - closeSize, (_titleH - closeSize) / 2, closeSize, closeSize);

            int contentW = w - margin * 2;
            int bodyTop = _titleH + (int)Math.Round(8 * _s);
            int bodyBottom = h - _bottomH - (int)Math.Round(6 * _s);
            if (bodyBottom - bodyTop < (int)Math.Round(80 * _s)) bodyBottom = bodyTop + (int)Math.Round(80 * _s);
            int textPad = (int)Math.Round(7 * _s);
            int overlayPad = (int)Math.Round(1 * _s);

            if (_mode == AiPanelMode.Ocr)
            {
                _ocrBorder = new Rectangle(margin, bodyTop, contentW, bodyBottom - bodyTop);
                Rectangle inner = InsetRect(_ocrBorder, textPad);
                _ocrBox.Bounds = inner;
                _ocrLoading.Bounds = InsetRect(inner, overlayPad);
                _ocrError.Bounds = _ocrLoading.Bounds;
            }
            else
            {
                int labelH = (int)Math.Round(18 * _s);
                int labelGap = (int)Math.Round(3 * _s);
                int paneGap = (int)Math.Round(10 * _s);
                int paneH = (bodyBottom - bodyTop - labelH * 2 - labelGap * 2 - paneGap) / 2;
                if (paneH < (int)Math.Round(50 * _s)) paneH = (int)Math.Round(50 * _s);

                int y = bodyTop;
                _originalLabel.Bounds = new Rectangle(margin + (int)Math.Round(2 * _s), y, contentW - 4, labelH);
                y += labelH + labelGap;
                _originalBorder = new Rectangle(margin, y, contentW, paneH);
                Rectangle originalInner = InsetRect(_originalBorder, textPad);
                _originalBox.Bounds = originalInner;
                _originalLoading.Bounds = InsetRect(originalInner, overlayPad);
                _originalError.Bounds = _originalLoading.Bounds;

                y += paneH + paneGap;
                _translationLabel.Bounds = new Rectangle(margin + (int)Math.Round(2 * _s), y, contentW - 4, labelH);
                y += labelH + labelGap;
                _translationBorder = new Rectangle(margin, y, contentW, paneH);
                Rectangle translationInner = InsetRect(_translationBorder, textPad);
                _translationBox.Bounds = translationInner;
                _translationLoading.Bounds = InsetRect(translationInner, overlayPad);
                _translationError.Bounds = _translationLoading.Bounds;
            }

            int btnH = (int)Math.Round(36 * _s);
            int btnY = h - _bottomH + (_bottomH - btnH) / 2;
            int gap = (int)Math.Round(8 * _s);
            int x = margin;
            if (_mode == AiPanelMode.Ocr)
            {
                int w1 = (int)Math.Round(80 * _s);
                int w2 = (int)Math.Round(80 * _s);
                int w3 = (int)Math.Round(80 * _s);
                int w4 = (int)Math.Round(60 * _s);
                _copyOcrAllBtn.Bounds = new Rectangle(x, btnY, w1, btnH);
                x += w1 + gap;
                _copyTableBtn.Bounds = new Rectangle(x, btnY, w2, btnH);
                x += w2 + gap;
                _formatBtn.Bounds = new Rectangle(x, btnY, w3, btnH);
                x += w3 + gap;
                _closeBtn.Bounds = new Rectangle(x, btnY, w4, btnH);
                x += w4;
            }
            else
            {
                int w1 = (int)Math.Round(74 * _s);
                int w2 = (int)Math.Round(52 * _s);
                _copyOriginalBtn.Bounds = new Rectangle(x, btnY, w1, btnH);
                x += w1 + gap;
                _copyTranslationBtn.Bounds = new Rectangle(x, btnY, w1, btnH);
                x += w1 + gap;
                _copyAllBtn.Bounds = new Rectangle(x, btnY, w1, btnH);
                x += w1 + gap;
                _closeBtn.Bounds = new Rectangle(x, btnY, w2, btnH);
                x += w2;
            }

            int usageLeft = x + (int)Math.Round(10 * _s);
            int usageW = Math.Max((int)Math.Round(120 * _s), w - margin - usageLeft);
            _usageLabel.Bounds = new Rectangle(usageLeft, btnY, usageW, btnH);
        }

        private static Rectangle InsetRect(Rectangle rect, int padding)
        {
            return new Rectangle(rect.X + padding, rect.Y + padding,
                Math.Max(1, rect.Width - padding * 2), Math.Max(1, rect.Height - padding * 2));
        }

        private void UpdateRoundedRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            Region old = _roundRegion;
            using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(0, 0, Width, Height), 16f * _s))
                _roundRegion = new Region(path);
            Region = _roundRegion;
            if (old != null)
            {
                try { old.Dispose(); } catch { }
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            UpdateRoundedRegion();
            LayoutChildren();
        }

        // ===================== 请求流程 =====================

        private void BeginRequest()
        {
            CancelCurrent();
            HideAllOverlays();
            ClearTexts();
            _usage = null;
            _translationStage = false;
            UpdateUsageLabel();

            if (_client == null || _aiImage == null || _aiImage.Width <= 0)
            {
                ShowError("AI 面板初始化失败：缺少图片或客户端，请重试。");
                return;
            }
            if (_mode != AiPanelMode.Ocr && !_client.HasApiKey)
            {
                ShowError("尚未配置 API Key。请在托盘图标 →「设置…」→「API 与翻译」中填写所用接口的 Key 后再试。");
                return;
            }

            if (_mode == AiPanelMode.Ocr) ShowLoading(_ocrLoading, "正在本地识别…");
            else ShowLoading(_originalLoading, "正在本地识别…");

            CancellationTokenSource cts = new CancellationTokenSource();
            _cts = cts;
            _ = RunAsync(cts);
        }

        private async Task RunAsync(CancellationTokenSource cts)
        {
            CancellationToken ct = cts.Token;
            try
            {
                if (_mode == AiPanelMode.Ocr)
                {
                    var ocrRes = await _client.OcrResultAsync(_aiImage,
                        piece => PostUi(() => AppendTextToOcr(piece)),
                        usage => PostUi(() => OnUsageReceived(usage, false)),
                        ct).ConfigureAwait(false);
                    PostUi(() => { _lastOcrResult = ocrRes; });
                }
                else
                {
                    string target = _cfg != null ? _cfg.TranslateTarget : "中文";
                    await _client.TranslateImageAsync(_aiImage, target,
                        piece => PostUi(() => OnOriginalDelta(piece)),
                        piece => PostUi(() => OnTranslationDelta(piece)),
                        usage => PostUi(() => OnUsageReceived(usage, true)),
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 用户取消/面板关闭：静默
            }
            catch (AiException ex)
            {
                PostUi(() => ShowError(ex.Message));
            }
            catch (Exception ex)
            {
                Log.Write("AI 面板请求异常: " + ex);
                PostUi(() => ShowError("发生错误：" + ex.Message));
            }
            finally
            {
                PostUi(FinishRequest);
                if (ReferenceEquals(_cts, cts)) _cts = null;
                try { cts.Dispose(); } catch { }
            }
        }

        private void CancelCurrent()
        {
            CancellationTokenSource cts = _cts;
            _cts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
        }

        private void FinishRequest()
        {
            HideAllLoading();
            if (_animTimer != null) _animTimer.Stop();
            UpdateUsageLabel();
        }

        // ===================== 流式回调（UI 线程） =====================

        private void AppendTextToOcr(string piece)
        {
            if (_ocrBox == null || _ocrBox.IsDisposed) return;
            HideLoading(_ocrLoading);
            _ocrBox.AppendText(NormalizeNewlines(piece));
            ScrollToEnd(_ocrBox);
        }

        private void OnOriginalDelta(string piece)
        {
            HideLoading(_originalLoading);
            if (_originalBox == null || _originalBox.IsDisposed) return;
            _originalBox.AppendText(NormalizeNewlines(piece));
            ScrollToEnd(_originalBox);
        }

        private void OnTranslationDelta(string piece)
        {
            HideLoading(_originalLoading);
            HideLoading(_translationLoading);
            if (!_translationStage)
            {
                _translationStage = true;
                if (_originalBox != null && _originalBox.TextLength == 0 && _originalLoading != null)
                    _originalLoading.Visible = false;
            }
            if (_translationBox == null || _translationBox.IsDisposed) return;
            _translationBox.AppendText(NormalizeNewlines(piece));
            ScrollToEnd(_translationBox);
        }

        /// <summary>
        /// WinForms 多行 TextBox 只把 \r\n 识别为换行（单独的 \n 会被吞掉），
        /// 而模型流式返回的换行通常是 \n，这里统一成 CRLF 再追加。
        /// </summary>
        private static string NormalizeNewlines(string piece)
        {
            if (string.IsNullOrEmpty(piece)) return piece ?? "";
            if (piece.IndexOf('\n') < 0 && piece.IndexOf('\r') < 0) return piece;
            string normalized = piece.Replace("\r\n", "\n").Replace('\r', '\n');
            return normalized.Replace("\n", "\r\n");
        }

        private void OnUsageReceived(AiUsage usage, bool translateFlow)
        {
            if (usage == null) return;
            _usage = usage;
            UpdateUsageLabel();
            if (translateFlow && !_translationStage)
            {
                // 第一次 usage 来自 OCR 阶段结束，随后进入翻译阶段
                _translationStage = true;
                HideLoading(_originalLoading);
                ShowLoading(_translationLoading, "正在翻译…");
            }
        }

        // ===================== 状态/错误/复制 =====================

        private void ShowError(string message)
        {
            HideAllLoading();
            if (_animTimer != null) _animTimer.Stop();
            AiErrorView view;
            if (_mode == AiPanelMode.Ocr) view = _ocrError;
            else if (!_translationStage) view = _originalError;
            else view = _translationError;
            if (view == null || view.IsDisposed) return;
            HideErrorViews();
            view.SetError(message);
            view.Visible = true;
            view.BringToFront();
        }

        private void OnRetryClicked(object sender, EventArgs e)
        {
            if (!_started) return;
            BeginRequest();
        }

        private void ClearTexts()
        {
            if (_ocrBox != null) _ocrBox.Text = "";
            if (_originalBox != null) _originalBox.Text = "";
            if (_translationBox != null) _translationBox.Text = "";
        }

        private string AllText()
        {
            if (_mode == AiPanelMode.Ocr) return _ocrBox != null ? _ocrBox.Text : "";
            string original = _originalBox != null ? _originalBox.Text.Trim() : "";
            string translation = _translationBox != null ? _translationBox.Text.Trim() : "";
            if (original.Length == 0) return translation;
            if (translation.Length == 0) return original;
            return original + "\r\n\r\n" + translation;
        }

        private void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                Toast.Show("暂无可复制的内容");
                return;
            }
            bool ok = ClipUtil.CopyText(text);
            Toast.Show(ok ? "已复制" : "复制失败，请重试");
        }

        private void UpdateUsageLabel()
        {
            if (_usageLabel == null || _usageLabel.IsDisposed) return;
            bool show = _cfg == null || _cfg.ShowUsage;
            _usageLabel.Visible = show;
            _usageLabel.Text = show && _usage != null ? _usage.ToString() : "";
        }

        private static void ScrollToEnd(TextBox box)
        {
            if (box == null || box.IsDisposed) return;
            try
            {
                box.SelectionStart = box.TextLength;
                box.SelectionLength = 0;
                box.ScrollToCaret();
            }
            catch { }
        }

        // ===================== 加载动画 =====================

        private void ShowLoading(AiLoadingOverlay overlay, string message)
        {
            if (overlay == null || overlay.IsDisposed) return;
            overlay.Message = message;
            overlay.Angle = _spinAngle;
            overlay.Visible = true;
            overlay.BringToFront();
            if (_animTimer != null && !_animTimer.Enabled) _animTimer.Start();
        }

        private void HideLoading(AiLoadingOverlay overlay)
        {
            if (overlay == null || overlay.IsDisposed) return;
            overlay.Visible = false;
        }

        private void HideAllLoading()
        {
            HideLoading(_ocrLoading);
            HideLoading(_originalLoading);
            HideLoading(_translationLoading);
        }

        private void HideErrorViews()
        {
            if (_ocrError != null && !_ocrError.IsDisposed) _ocrError.Visible = false;
            if (_originalError != null && !_originalError.IsDisposed) _originalError.Visible = false;
            if (_translationError != null && !_translationError.IsDisposed) _translationError.Visible = false;
        }

        private void HideAllOverlays()
        {
            HideAllLoading();
            HideErrorViews();
        }

        private void OnAnimationTick(object sender, EventArgs e)
        {
            _spinAngle = (_spinAngle + 24) % 360;
            bool any = false;
            if (_ocrLoading != null && !_ocrLoading.IsDisposed && _ocrLoading.Visible)
            {
                _ocrLoading.Angle = _spinAngle;
                _ocrLoading.Invalidate();
                any = true;
            }
            if (_originalLoading != null && !_originalLoading.IsDisposed && _originalLoading.Visible)
            {
                _originalLoading.Angle = _spinAngle;
                _originalLoading.Invalidate();
                any = true;
            }
            if (_translationLoading != null && !_translationLoading.IsDisposed && _translationLoading.Visible)
            {
                _translationLoading.Angle = _spinAngle;
                _translationLoading.Invalidate();
                any = true;
            }
            if (!any && _animTimer != null) _animTimer.Stop();
        }

        // ===================== 关闭/释放 =====================

        private void OnCloseClicked(object sender, EventArgs e)
        {
            if (_closeRaised) return;
            _closeRaised = true;
            CancelWork();
            EventHandler handler = CloseRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>后台线程回调统一回到 UI 线程；控件已释放时丢弃。</summary>
        private void PostUi(Action action)
        {
            if (action == null || IsDisposed || Disposing) return;
            try
            {
                if (!IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (Pen border = new Pen(UiTheme.ToolbarBorder, 1f))
            using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 16f * _s))
                g.DrawPath(border, path);

            using (Pen separator = new Pen(SettingsColors.Divider, 1f))
            {
                g.DrawLine(separator, _margin, _titleH + 0.5f, Width - _margin, _titleH + 0.5f);
                g.DrawLine(separator, 0, Height - _bottomH + 0.5f, Width, Height - _bottomH + 0.5f);
            }

            int iconSize = (int)Math.Round(22 * _s);
            Rectangle iconRect = new Rectangle(_margin, (_titleH - iconSize) / 2, iconSize, iconSize);
            using (GraphicsPath iconPath = ImageUtil.RoundedRect(iconRect, 2f * _s))
            using (SolidBrush brush = new SolidBrush(UiTheme.Signal))
                g.FillPath(brush, iconPath);
            string glyph = _mode == AiPanelMode.Ocr ? "文" : "译";
            TextRenderer.DrawText(g, glyph, _fontIcon, iconRect, UiTheme.SignalText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            int titleX = iconRect.Right + (int)Math.Round(10 * _s);
            int titleW = Math.Max(1, Width - titleX - _margin - (int)Math.Round(40 * _s));
            Rectangle titleRect = new Rectangle(titleX, 0, titleW, _titleH);
            TextRenderer.DrawText(g, _mode == AiPanelMode.Ocr ? "文字提取" : "文字翻译", _fontTitle, titleRect,
                UiTheme.TextDark, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            if (_mode == AiPanelMode.Ocr)
            {
                DrawTextBorder(g, _ocrBorder);
            }
            else
            {
                DrawTextBorder(g, _originalBorder);
                DrawTextBorder(g, _translationBorder);
            }
        }

        private static void DrawTextBorder(Graphics g, Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (Pen pen = new Pen(SettingsColors.InputBorder, 1f))
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelWork();
                if (_animTimer != null)
                {
                    _animTimer.Stop();
                    _animTimer.Tick -= OnAnimationTick;
                    _animTimer.Dispose();
                }
                if (_aiImage != null)
                {
                    try { _aiImage.Dispose(); } catch { }
                }
            }
            base.Dispose(disposing);
            if (disposing)
            {
                if (_roundRegion != null)
                {
                    try { Region = null; } catch { }
                    try { _roundRegion.Dispose(); } catch { }
                    _roundRegion = null;
                }
                DisposeFonts();
            }
        }

        private void DisposeFonts()
        {
            Font[] fonts = new Font[] { _fontTitle, _fontIcon, _fontText, _fontSmall, _fontButton, _fontUsage };
            foreach (Font font in fonts)
            {
                if (font == null) continue;
                try { font.Dispose(); } catch { }
            }
            _fontTitle = _fontIcon = _fontText = _fontSmall = _fontButton = _fontUsage = null;
        }
    }

    // =====================================================================
    // 内部自绘控件
    // =====================================================================

    /// <summary>右上角关闭按钮：自绘 ✕，悬停浅灰圆底。</summary>
    internal sealed class AiCloseButton : Control
    {
        private bool _hover;

        public AiCloseButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hover)
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(243, 243, 243)))
                    g.FillEllipse(brush, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            }
            float pad = Math.Max(5f, Width * 0.3f);
            float width = Math.Max(1.6f, Width / 16f);
            using (Pen pen = new Pen(_hover ? UiTheme.TextDark : Color.FromArgb(120, 120, 120), width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, pad, pad, Width - pad, Height - pad);
                g.DrawLine(pen, Width - pad, pad, pad, Height - pad);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }
    }

    /// <summary>加载中遮罩：白色底 + 旋转弧 + 中文提示。</summary>
    internal sealed class AiLoadingOverlay : Control
    {
        private readonly float _scale;
        private readonly Font _messageFont;
        private string _message = "";

        public AiLoadingOverlay(float scale, Font messageFont)
        {
            _scale = scale <= 0f ? 1f : scale;
            _messageFont = messageFont;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.White;
            Visible = false;
            TabStop = false;
        }

        public string Message
        {
            get { return _message; }
            set { _message = value ?? ""; Invalidate(); }
        }

        public int Angle { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float size = Math.Max(20f, 28f * _scale);
            float cx = Width / 2f;
            float cy = Height / 2f - 10f * _scale;
            if (cy - size / 2f < 4f) cy = size / 2f + 4f;
            RectangleF arcRect = new RectangleF(cx - size / 2f, cy - size / 2f, size, size);
            float thickness = Math.Max(2f, 2.6f * _scale);
            using (Pen track = new Pen(Color.FromArgb(234, 234, 234), thickness))
                g.DrawEllipse(track, arcRect);
            using (Pen pen = new Pen(UiTheme.Green, thickness))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, arcRect, Angle, 100f);
            }

            if (!string.IsNullOrEmpty(_message))
            {
                int textTop = (int)Math.Round(cy + size / 2f + 12f * _scale);
                Rectangle textRect = new Rectangle(0, textTop, Width, Math.Max(20, (int)Math.Round(26 * _scale)));
                TextRenderer.DrawText(g, _message, _messageFont, textRect, UiTheme.TextGray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _messageFont != null)
            {
                try { _messageFont.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>错误状态遮罩：红色中文错误信息 +「重试」按钮。</summary>
    internal sealed class AiErrorView : Control
    {
        private readonly float _scale;
        private readonly Label _messageLabel;
        private readonly Button _retryBtn;
        private readonly Font _messageFont;
        private readonly Font _buttonFont;

        public AiErrorView(float scale)
        {
            _scale = scale <= 0f ? 1f : scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.White;
            Visible = false;
            TabStop = false;

            _messageFont = UiTheme.Font(13f * _scale);
            _buttonFont = UiTheme.Font(13f * _scale);

            _messageLabel = new Label();
            _messageLabel.AutoSize = false;
            _messageLabel.TextAlign = ContentAlignment.MiddleCenter;
            _messageLabel.ForeColor = UiTheme.ActionRed;
            _messageLabel.BackColor = Color.White;
            _messageLabel.Font = _messageFont;
            _messageLabel.Text = "";
            Controls.Add(_messageLabel);

            _retryBtn = new Button();
            _retryBtn.Text = "重试";
            _retryBtn.FlatStyle = FlatStyle.Flat;
            _retryBtn.Font = _buttonFont;
            _retryBtn.Size = new Size((int)Math.Round(86 * _scale), (int)Math.Round(32 * _scale));
            _retryBtn.BackColor = Color.White;
            _retryBtn.ForeColor = UiTheme.ActionGreen;
            _retryBtn.FlatAppearance.BorderSize = 1;
            _retryBtn.FlatAppearance.BorderColor = UiTheme.Green;
            _retryBtn.FlatAppearance.MouseOverBackColor = UiTheme.GreenLight;
            _retryBtn.FlatAppearance.MouseDownBackColor = Color.FromArgb(220, 242, 231);
            _retryBtn.UseVisualStyleBackColor = false;
            _retryBtn.Cursor = Cursors.Hand;
            _retryBtn.Click += delegate
            {
                EventHandler handler = Retry;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            Controls.Add(_retryBtn);
        }

        public event EventHandler Retry;

        public void SetError(string message)
        {
            _messageLabel.Text = message ?? "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int margin = (int)Math.Round(14 * _scale);
            int btnW = (int)Math.Round(86 * _scale);
            int btnH = (int)Math.Round(32 * _scale);
            int btnY = Math.Max(0, Height - btnH - margin);
            _retryBtn.Bounds = new Rectangle((Width - btnW) / 2, btnY, btnW, btnH);
            int labelTop = margin / 2;
            int labelHeight = Math.Max(1, btnY - labelTop - margin / 2);
            _messageLabel.Bounds = new Rectangle(margin, labelTop,
                Math.Max(1, Width - margin * 2), labelHeight);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_messageFont != null) { try { _messageFont.Dispose(); } catch { } }
                if (_buttonFont != null) { try { _buttonFont.Dispose(); } catch { } }
            }
            base.Dispose(disposing);
        }
    }
}
