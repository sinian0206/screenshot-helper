using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 全屏截图编辑器：显示冻结画面、选区、标注工具栏、AI 面板、长截图入口。
    /// </summary>
    public sealed partial class OverlayForm : Form
    {
        private enum DragMode
        {
            None,
            Selecting,
            Moving,
            Resizing,
            Shape,
            Freehand,
            Object
        }

        private readonly AppConfig _cfg;
        private readonly ConfigStore _store;
        private readonly Bitmap _screenshot;
        private readonly Rectangle _virtualScreen;
        private float _scale;
        private EditorToolbar _toolbar;
        private readonly DeepSeekClient _client;
        private readonly Action _openSettings;
        private MosaicCanvas _mosaic;

        private Rectangle _selection;
        private bool _hasSelection;
        private bool _selectionLocked;
        private EditorTool _tool = EditorTool.None;

        private readonly List<Annotation> _annotations = new List<Annotation>();

        private DragMode _drag = DragMode.None;
        private Point _dragStart;
        private Rectangle _startRect;
        private int _resizeHandle = -1;
        private Annotation _ghost;
        private GraphicsPath _mosaicMask;
        private PointF _lastFreePoint;

        private Color _color = UiTheme.Red;
        private float _lineWidth = 3.5f;
        private float _textSize = 19f;
        private float _mosaicBrush = 40f;
        private int _numberCounter = 0;
        private string _pendingEmoji = null;

        private Panel _textBar;
        private TextBox _textInput;
        private Label _textHint;
        private PointF _textAnchor;
        private TextAnnotation _editingText;
        private AiResultPanel _aiPanel;
        private CancellationTokenSource _aiCts;
        private CancellationTokenSource _privacyCts;
        private int _privacyRequestId;
        private string _privacyStatus = "";
        private bool _applyingPrivacy;
        private bool _busy;
        private bool _closing;
        private bool _recordMode;

        private Point _mousePos;
        private bool _mouseInside;
        private string _lastHex = "";
        private Rectangle _hoverWindowRect = Rectangle.Empty;
        private string _hoverWindowTitle = "";
        private Rectangle _candidateWindow = Rectangle.Empty;
        private ContextMenuStrip _contextMenu;

        public OverlayForm(Bitmap screenshot, Rectangle virtualScreen, AppConfig cfg, ConfigStore store, Action openSettings)
        {
            _screenshot = screenshot;
            _virtualScreen = virtualScreen;
            _cfg = cfg;
            _store = store;
            _openSettings = openSettings;
            _client = new DeepSeekClient(cfg);
            _scale = DpiUtil.ScaleAtPoint(Cursor.Position);
            if (_scale < 0.75f) _scale = 0.75f;
            if (_scale > 3f) _scale = 3f;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = Color.Black;
            Cursor = Cursors.Cross;
            Text = "截图助手 - 截图";
            Bounds = virtualScreen;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            CreateToolbar();

            AllowDrop = true;
            DragEnter += delegate (object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += delegate (object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        try
                        {
                            Bitmap loaded = ImageImport.LoadFile(files[0]);
                            LoadImageToCanvas(loaded);
                            Toast.Show("已导入图片: " + Path.GetFileName(files[0]));
                        }
                        catch (Exception ex) { Toast.Show(ex.Message); }
                    }
                }
            };
        }

        private void CreateToolbar()
        {
            if (_toolbar != null)
            {
                Controls.Remove(_toolbar);
                _toolbar.Dispose();
            }
            _toolbar = new EditorToolbar(_scale);
            _toolbar.Visible = false;
            _toolbar.ToolClicked += OnToolClicked;
            _toolbar.ActionClicked += OnActionClicked;
            _toolbar.ToolContextRequested += OnToolContextRequested;
            Controls.Add(_toolbar);
        }

        public float UiScale { get { return _scale; } }

        /// <summary>录制模式：框选后进入准备窗口，明确开始后才录制（由 TrayApp 设置）。</summary>
        public bool RecordMode
        {
            get { return _recordMode; }
            set { _recordMode = value; }
        }

        /// <summary>自动化测试用：为 true 时显示不抢前台焦点。</summary>
        public bool SuppressActivation { get; set; }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return false; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (SuppressActivation) return;
            try
            {
                Activate();
                Focus();
                Native.SetForegroundWindow(Handle);
            }
            catch { }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 不擦除背景，避免闪烁
        }

        // ============================ 绘制 ============================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            DrawCaptureBackground(g);

            if (!_hasSelection)
            {
                Rectangle live = (_drag == DragMode.Selecting && _selection.Width > 0 && _selection.Height > 0)
                    ? _selection : Rectangle.Empty;
                if (live.Width > 0)
                {
                    DrawUnmaskedSelection(g, live);
                    using (Pen pen = new Pen(UiTheme.Green, Math.Max(1.5f, 2f * _scale)))
                        g.DrawRectangle(pen, live.X, live.Y, live.Width, live.Height);
                    if (_cfg.ShowDimensions) DrawSizeLabel(g, live, false);
                    if (_cfg.ShowColorValue && _mouseInside) DrawColorChip(g);
                    return;
                }
                Rectangle hover = _hoverWindowRect;
                if (hover.Width > 0)
                {
                    DrawUnmaskedSelection(g, hover);
                    using (Pen pen = new Pen(UiTheme.Green, Math.Max(1f, 1.5f * _scale)))
                    {
                        pen.DashStyle = DashStyle.Dash;
                        pen.DashPattern = new float[] { 4f, 3f };
                        g.DrawRectangle(pen, hover.X, hover.Y, hover.Width, hover.Height);
                    }
                    if (_cfg.ShowDimensions)
                        DrawSizeLabel(g, hover, true);
                }
                if (_cfg.ShowColorValue && _mouseInside) DrawColorChip(g);
                return;
            }

            DrawUnmaskedSelection(g, _selection);
            DrawAnnotations(g, _selection);
            DrawTextPreview(g);
            DrawSelectionChrome(g);
            DrawObjectSelection(g);
            if (_cfg.ShowDimensions) DrawSizeLabel(g, _selection, false);
            if (_cfg.ShowColorValue && _mouseInside && _drag == DragMode.Selecting) DrawColorChip(g);
            DrawPrivacyStatus(g);
            DrawEditorReview(g);
        }

        private void DrawPrivacyStatus(Graphics graphics)
        {
            if (string.IsNullOrEmpty(_privacyStatus)) return;
            using (Font font = UiTheme.Font(13f * _scale))
            {
                Size size = TextRenderer.MeasureText(_privacyStatus, font);
                Rectangle box = new Rectangle(12, 12, Math.Min(ClientSize.Width - 24, size.Width + 20), size.Height + 14);
                using (SolidBrush brush = new SolidBrush(UiTheme.BadgeBackground)) graphics.FillRectangle(brush, box);
                TextRenderer.DrawText(graphics, _privacyStatus, font, Rectangle.Inflate(box, -8, -5),
                    Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        /// <summary>文字输入时在画面上实时预览（透明文字 + 虚线编辑框），真正的输入框是下方小工具条。</summary>
        private void DrawTextPreview(Graphics g)
        {
            if (_textBar == null || _textInput == null) return;
            string text = _textInput.Text;
            if (string.IsNullOrEmpty(text)) text = "";
            float px = _textSize;
            TextAnnotation preview = new TextAnnotation { Text = text, Location = _textAnchor,
                FontPx = px, Color = _color, LayoutSize = _editingText == null ? SizeF.Empty : _editingText.LayoutSize };
            preview.Draw(g, null);
            using (Font font = new Font(UiTheme.UiFontName, px, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip;
                SizeF size = g.MeasureString(text.Length == 0 ? " " : text, font, int.MaxValue, format);
                RectangleF box = new RectangleF(_textAnchor.X, _textAnchor.Y,
                    Math.Max(px * 0.6f, size.Width + 4f), Math.Max(px, size.Height + 2f));
                if (preview.LayoutSize.Width > 0 && preview.LayoutSize.Height > 0) box = preview.GetBounds();
                using (Pen pen = new Pen(Color.FromArgb(230, UiTheme.Green), Math.Max(1f, 1.2f * _scale)))
                {
                    pen.DashStyle = DashStyle.Dash;
                    pen.DashPattern = new float[] { 3f, 2f };
                    g.DrawRectangle(pen, box.X - 2f, box.Y - 1f, box.Width + 4f, box.Height + 2f);
                }
                // 光标
                float caretX = Math.Min(box.Right, _textAnchor.X + size.Width + 1f);
                using (Pen caret = new Pen(_color, Math.Max(1f, 1.5f * _scale)))
                    g.DrawLine(caret, caretX, _textAnchor.Y + 1f, caretX, _textAnchor.Y + font.Height);
            }
        }

        private void DrawAnnotations(Graphics g, Rectangle clip)
        {
            GraphicsState state = g.Save();
            try
            {
                g.SetClip(clip, CombineMode.Intersect);
                MosaicCanvas mosaic = EnsureMosaic();
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (Annotation a in _annotations)
                    if (!ReferenceEquals(a, _editingText)) a.Draw(g, mosaic);
                if (_ghost != null) _ghost.Draw(g, mosaic);
            }
            finally
            {
                g.Restore(state);
            }
        }

        private void DrawSelectionChrome(Graphics g)
        {
            float pw = Math.Max(1.25f, 1.4f * _scale);
            // 白色外轨与系统蓝内轨形成轻量、跨背景可读的选区边界。
            using (Pen shadowPen = new Pen(Color.FromArgb(36, 0, 0, 0), Math.Max(4f, 5f * _scale)))
            {
                g.DrawRectangle(shadowPen, _selection.X - 2.5f * _scale, _selection.Y - 2f * _scale,
                    _selection.Width + 5f * _scale, _selection.Height + 5f * _scale);
            }
            using (Pen outer = new Pen(Color.FromArgb(245, 255, 255, 255), Math.Max(2.5f, 3f * _scale)))
            {
                g.DrawRectangle(outer, _selection.X - pw, _selection.Y - pw,
                    _selection.Width + pw * 2f, _selection.Height + pw * 2f);
            }
            using (Pen pen = new Pen(UiTheme.Telemetry, pw))
            {
                g.DrawRectangle(pen, _selection.X, _selection.Y, _selection.Width, _selection.Height);
            }
            if (_selectionLocked) return;

            float handleSize = Math.Max(8f, 9f * _scale);
            foreach (RectangleF center in GetHandleCenters())
            {
                RectangleF handle = new RectangleF(center.X - handleSize / 2f, center.Y - handleSize / 2f,
                    handleSize, handleSize);
                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
                    g.FillEllipse(shadow, handle.X + .5f, handle.Y + 1f, handle.Width, handle.Height);
                using (SolidBrush fill = new SolidBrush(Color.White))
                    g.FillEllipse(fill, handle);
                using (Pen ring = new Pen(UiTheme.Telemetry, Math.Max(1.5f, 1.8f * _scale)))
                    g.DrawEllipse(ring, handle);
            }
        }

        private RectangleF[] GetHandleCenters()
        {
            float x = _selection.Left, y = _selection.Top, r = _selection.Right, b = _selection.Bottom;
            float cx = _selection.Left + _selection.Width / 2f, cy = _selection.Top + _selection.Height / 2f;
            return new RectangleF[]
            {
                new RectangleF(x, y, 0, 0), new RectangleF(cx, y, 0, 0), new RectangleF(r, y, 0, 0),
                new RectangleF(r, cy, 0, 0), new RectangleF(r, b, 0, 0), new RectangleF(cx, b, 0, 0),
                new RectangleF(x, b, 0, 0), new RectangleF(x, cy, 0, 0)
            };
        }

        private Rectangle[] GetHandleRects()
        {
            float size = Math.Max(5f, (float)Math.Round(7 * _scale));
            RectangleF[] centers = GetHandleCenters();
            Rectangle[] rects = new Rectangle[centers.Length];
            for (int i = 0; i < centers.Length; i++)
            {
                rects[i] = new Rectangle(
                    (int)Math.Round(centers[i].X - size / 2f),
                    (int)Math.Round(centers[i].Y - size / 2f),
                    (int)Math.Round(size), (int)Math.Round(size));
            }
            return rects;
        }

        private int GetHandleAt(Point p)
        {
            if (!_hasSelection || _selectionLocked) return -1;
            Rectangle[] rects = GetHandleRects();
            int tol = (int)Math.Round(4 * _scale);
            for (int i = 0; i < rects.Length; i++)
            {
                Rectangle r = rects[i];
                r.Inflate(tol, tol);
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        private void DrawSizeLabel(Graphics g, Rectangle rect, bool windowHint)
        {
            string text = rect.Width + " × " + rect.Height;
            float px = Math.Max(12f, 13f * _scale);
            using (Font font = new Font(UiTheme.UiFontName, px, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                Size size = TextRenderer.MeasureText(text, font);
                float x = rect.Left + 2 * _scale;
                float y = rect.Top - size.Height - 11 * _scale;
                if (y < 2 * _scale) y = rect.Top + 4 * _scale;
                if (x < 2 * _scale) x = 2 * _scale;
                Rectangle badge = DrawInfoBadge(g, text, font, x, y);
                if (windowHint && !string.IsNullOrEmpty(_hoverWindowTitle))
                {
                    string title = _hoverWindowTitle;
                    if (title.Length > 30) title = title.Substring(0, 30) + "…";
                    using (Font tf = new Font(UiTheme.UiFontName, 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        Size ts = TextRenderer.MeasureText(title, tf);
                        float tx = rect.Left + 2 * _scale;
                        float ty = badge.Top - ts.Height - 11 * _scale;
                        if (ty < 2 * _scale) ty = rect.Bottom + 4 * _scale;
                        DrawInfoBadge(g, title, tf, tx, ty);
                    }
                }
            }
        }

        private Rectangle DrawInfoBadge(Graphics graphics, string text, Font font, float x, float y)
        {
            int paddingX = Math.Max(5, (int)Math.Round(8 * _scale));
            int paddingY = Math.Max(3, (int)Math.Round(4 * _scale));
            Size measured = TextRenderer.MeasureText(text, font);
            int width = Math.Min(measured.Width + paddingX * 2, Math.Max(1, ClientSize.Width - 4));
            int height = measured.Height + paddingY * 2;
            int left = Math.Max(2, Math.Min((int)x, ClientSize.Width - width - 2));
            int top = Math.Max(2, Math.Min((int)y, ClientSize.Height - height - 2));
            var bounds = new Rectangle(left, top, width, height);

            RectangleF boundsF = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            using (GraphicsPath path = ImageUtil.RoundedRect(boundsF, 8 * _scale))
            {
                using (GraphicsPath shadowPath = ImageUtil.RoundedRect(
                    new RectangleF(bounds.X + 1, bounds.Y + 2, bounds.Width, bounds.Height), 8 * _scale))
                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(24, 0, 0, 0)))
                    graphics.FillPath(shadow, shadowPath);
                using (SolidBrush background = new SolidBrush(UiTheme.InfoBadgeBackground))
                    graphics.FillPath(background, path);
                using (Pen borderPen = new Pen(Color.FromArgb(180, 209, 209, 214), 1f))
                    graphics.DrawPath(borderPen, path);
            }
            TextRenderer.DrawText(graphics, text, font, Rectangle.Inflate(bounds, -paddingX, -paddingY), UiTheme.TextDark,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            return bounds;
        }

        private void DrawColorChip(Graphics g)
        {
            if (!_mouseInside) return;
            if (_mousePos.X < 0 || _mousePos.Y < 0 || _mousePos.X >= _screenshot.Width || _mousePos.Y >= _screenshot.Height) return;
            Color c;
            try { c = _screenshot.GetPixel(_mousePos.X, _mousePos.Y); } catch { return; }
            string hex = "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            _lastHex = hex;
            float px = Math.Max(12f, 12.5f * _scale);
            using (Font font = new Font("Consolas", px, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                Size textSize = TextRenderer.MeasureText(hex, font);
                int padX = (int)Math.Round(8 * _scale);
                int padY = (int)Math.Round(5 * _scale);
                int sw = (int)Math.Round(14 * _scale);
                int w = textSize.Width + sw + padX * 2 + (int)Math.Round(6 * _scale);
                int h = Math.Max(textSize.Height, sw) + padY * 2;
                int x = _mousePos.X + (int)Math.Round(18 * _scale);
                int y = _mousePos.Y + (int)Math.Round(20 * _scale);
                if (x + w > ClientSize.Width - 4) x = _mousePos.X - w - (int)Math.Round(10 * _scale);
                if (y + h > ClientSize.Height - 4) y = _mousePos.Y - h - (int)Math.Round(10 * _scale);
                Rectangle box = new Rectangle(x, y, w, h);

                RectangleF boxF = new RectangleF(box.X, box.Y, box.Width, box.Height);
                using (GraphicsPath path = ImageUtil.RoundedRect(boxF, 6 * _scale))
                {
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(220, 20, 24, 34)))
                        g.FillPath(bg, path);
                    using (Pen borderPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1f))
                        g.DrawPath(borderPen, path);
                }

                // 颜色样本：3px 圆角小色块 + 1px 细边
                RectangleF swatchF = new RectangleF(x + padX, y + (h - sw) / 2f, sw, sw);
                using (GraphicsPath spath = ImageUtil.RoundedRect(swatchF, 3 * _scale))
                {
                    using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, spath);
                    using (Pen sp = new Pen(Color.FromArgb(120, 255, 255, 255), 1f)) g.DrawPath(sp, spath);
                }

                Rectangle textRect = new Rectangle(x + padX + sw + (int)Math.Round(6 * _scale), y, textSize.Width, h);
                TextRenderer.DrawText(g, hex, font, textRect, Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        // ============================ 鼠标 ============================

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _mousePos = e.Location;
            _mouseInside = true;
            bool handled = false;
            BeforeEditorMouseDown(e, ref handled);
            if (handled) return;
            if (e.Button == MouseButtons.Right)
            {
                if (_textBar != null) { CancelText(); return; }
                if (_translationBar != null) { CancelTranslationEdit(); return; }
                if (ShowObjectMenu(e.Location)) return;
                CloseSession();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (_busy) return;
            CancelPrivacy(false);
            if (_translationBar != null) { CommitTranslationEdit(); return; }
            if (_textBar != null)
            {
                if (_textBar.Bounds.Contains(e.Location)) return;
                CommitText();
                Invalidate();
                return;
            }
            _dragStart = e.Location;
            if (!_hasSelection)
            {
                _candidateWindow = _hoverWindowRect;
                _drag = DragMode.Selecting;
                _selection = new Rectangle(e.Location, Size.Empty);
                _toolbar.Visible = false;
                Invalidate();
                return;
            }
            if (!_selectionLocked)
            {
                int handle = GetHandleAt(e.Location);
                if (handle >= 0)
                {
                    _drag = DragMode.Resizing;
                    _resizeHandle = handle;
                    _startRect = _selection;
                    return;
                }
            }
            if (TryStartObjectManipulation(e.Location)) return;
            if (_selection.Contains(e.Location))
            {
                if (_tool == EditorTool.None)
                {
                    if (!_selectionLocked)
                    {
                        _drag = DragMode.Moving;
                        _startRect = _selection;
                    }
                    return;
                }
                StartDrawing(e.Location);
                return;
            }
            if (_selectionLocked) return;
            // 在选区外重新框选
            CloseAiPanel();
            ResetAnnotationDocument();
            _candidateWindow = Rectangle.Empty;
            _hasSelection = false;
            _selection = new Rectangle(e.Location, Size.Empty);
            _drag = DragMode.Selecting;
            _toolbar.Visible = false;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_busy) return;
            Rectangle previousSelection = _selection;
            Point previousMouse = _mousePos;
            _mousePos = e.Location;
            _mouseInside = true;
            switch (_drag)
            {
                case DragMode.Object:
                    UpdateObjectManipulation(e.Location);
                    return;
                case DragMode.Selecting:
                    _selection = NormalizeRect(_dragStart, e.Location);
                    if (!_hoverWindowRect.IsEmpty)
                    {
                        // Replace the window preview on the first drag frame only.
                        // The separate candidate is retained for a click selection.
                        _hoverWindowRect = Rectangle.Empty;
                        _hoverWindowTitle = "";
                        Invalidate();
                    }
                    else InvalidateSelectionChange(previousSelection, previousMouse);
                    return;
                case DragMode.Moving:
                    {
                        int dx = e.X - _dragStart.X;
                        int dy = e.Y - _dragStart.Y;
                        Rectangle r = new Rectangle(_startRect.X + dx, _startRect.Y + dy, _startRect.Width, _startRect.Height);
                        _selection = ClampToScreen(r);
                        InvalidateSelectionChange(previousSelection, previousMouse);
                        return;
                    }
                case DragMode.Resizing:
                    _selection = ResizeSelection(_startRect, _resizeHandle, e.Location);
                    InvalidateSelectionChange(previousSelection, previousMouse);
                    return;
                case DragMode.Shape:
                    UpdateShapeGhost(e.Location);
                    Invalidate();
                    return;
                case DragMode.Freehand:
                    AppendFreePoint(e.Location);
                    Invalidate();
                    return;
            }
            bool repaint = false;
            if (!_hasSelection)
            {
                UpdateHoverWindow();
                repaint = true;
            }
            UpdateCursor();
            if (repaint) Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_busy) return;
            _mousePos = e.Location;
            switch (_drag)
            {
                case DragMode.Object:
                    FinishObjectManipulation();
                    return;
                case DragMode.Selecting:
                    {
                        int dx = Math.Abs(e.X - _dragStart.X);
                        int dy = Math.Abs(e.Y - _dragStart.Y);
                        if (dx < 4 && dy < 4 && _candidateWindow.Width > 0)
                        {
                            _selection = _candidateWindow;
                        }
                        if (_selection.Width < 5 || _selection.Height < 5)
                        {
                            _hasSelection = false;
                            _selection = Rectangle.Empty;
                            _drag = DragMode.None;
                            Cursor = Cursors.Cross;
                            Invalidate();
                            return;
                        }
                        _selection = ClampToScreen(_selection);
                        _hasSelection = true;
                        _selectionLocked = false;
                        _numberCounter = 0;
                        _tool = EditorTool.None;
                        _toolbar.ActiveTool = EditorTool.None;
                        _toolbar.Visible = true;
                        LayoutToolbar();
                        _drag = DragMode.None;
                        UpdateToolbarState();
                        if (_recordMode)
                        {
                            StartRecordingRegion();
                            return;
                        }
                        Cursor = Cursors.SizeAll;
                        RunSelectionAction();
                        Invalidate();
                        return;
                    }
                case DragMode.Moving:
                case DragMode.Resizing:
                    _drag = DragMode.None;
                    LayoutToolbar();
                    Invalidate();
                    return;
                case DragMode.Shape:
                    CommitGhost();
                    return;
                case DragMode.Freehand:
                    CommitGhost();
                    return;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_busy) return;
            if (e.Button != MouseButtons.Left || !_hasSelection || _textBar != null) return;
            if (_drag == DragMode.Object) CancelObjectManipulation();
            int translationIndex;
            TranslationOverlayAnnotation translation = HitAnnotation(e.Location, out translationIndex) as TranslationOverlayAnnotation;
            if (translation != null) { BeginTranslationEdit(translation, translationIndex); return; }
            for (int i = _annotations.Count - 1; i >= 0; i--)
            {
                TextAnnotation text = _annotations[i] as TextAnnotation;
                if (text == null) continue;
                RectangleF bounds = text.GetBounds();
                bounds.Inflate(6 * _scale, 6 * _scale);
                if (bounds.Contains(e.Location))
                {
                    BeginEditExistingText(text);
                    return;
                }
            }
            if (_tool == EditorTool.None && _selection.Contains(e.Location))
            {
                ConfirmAndClose();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouseInside = false;
            if (_hoverWindowRect != Rectangle.Empty)
            {
                _hoverWindowRect = Rectangle.Empty;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_busy) return;
            if (_textBar != null && (ModifierKeys & Keys.Control) == Keys.Control)
            {
                _textSize = Math.Max(10f, Math.Min(72f, _textSize + (e.Delta > 0 ? 2f : -2f)));
                UpdateTextEditFont();
                return;
            }
        }

        private void UpdateCursor()
        {
            if (_drag != DragMode.None) return;
            if (!_hasSelection)
            {
                Cursor = Cursors.Cross;
                return;
            }
            if (_tool == EditorTool.Select || _selectedAnnotation != null)
            {
                int objectHandle = ObjectHandleAt(_mousePos);
                if (objectHandle >= 0) { Cursor = HandleCursor(objectHandle); return; }
                if (_tool == EditorTool.Select) { Cursor = Cursors.Default; return; }
            }
            if (!_selectionLocked)
            {
                int h = GetHandleAt(_mousePos);
                if (h >= 0)
                {
                    Cursor = HandleCursor(h);
                    return;
                }
            }
            if (_tool == EditorTool.None && _selection.Contains(_mousePos) && !_selectionLocked)
                Cursor = Cursors.SizeAll;
            else
                Cursor = Cursors.Cross;
        }

        private static Cursor HandleCursor(int handle)
        {
            switch (handle)
            {
                case 0:
                case 4: return Cursors.SizeNWSE;
                case 2:
                case 6: return Cursors.SizeNESW;
                case 1:
                case 5: return Cursors.SizeNS;
                case 3:
                case 7: return Cursors.SizeWE;
                default: return Cursors.SizeAll;
            }
        }

        private void UpdateHoverWindow()
        {
            if (_hasSelection) return;
            try
            {
                Point screenPoint = PointToScreen(_mousePos);
                Rectangle rect;
                string title;
                if (WinUtil.TryGetTopWindowAt(screenPoint, Handle, out rect, out title))
                {
                    rect.Offset(-_virtualScreen.X, -_virtualScreen.Y);
                    if (rect != _hoverWindowRect || title != _hoverWindowTitle)
                    {
                        _hoverWindowRect = rect;
                        _hoverWindowTitle = title;
                    }
                }
                else if (_hoverWindowRect != Rectangle.Empty)
                {
                    _hoverWindowRect = Rectangle.Empty;
                    _hoverWindowTitle = "";
                }
            }
            catch { }
        }

        // ============================ 选区几何 ============================

        private static Rectangle NormalizeRect(Point a, Point b)
        {
            return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        private Rectangle ClampToScreen(Rectangle r)
        {
            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            int x1 = Math.Max(0, r.Left);
            int y1 = Math.Max(0, r.Top);
            int x2 = Math.Min(bounds.Width, r.Right);
            int y2 = Math.Min(bounds.Height, r.Bottom);
            if (x2 <= x1) x2 = Math.Min(bounds.Width, x1 + 1);
            if (y2 <= y1) y2 = Math.Min(bounds.Height, y1 + 1);
            return Rectangle.FromLTRB(x1, y1, x2, y2);
        }

        private Rectangle ResizeSelection(Rectangle start, int handle, Point p)
        {
            int left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
            switch (handle)
            {
                case 0: left = p.X; top = p.Y; break;
                case 1: top = p.Y; break;
                case 2: right = p.X; top = p.Y; break;
                case 3: right = p.X; break;
                case 4: right = p.X; bottom = p.Y; break;
                case 5: bottom = p.Y; break;
                case 6: left = p.X; bottom = p.Y; break;
                case 7: left = p.X; break;
            }
            int min = (int)Math.Round(8 * _scale);
            if (right - left < min) { if (handle == 0 || handle == 6 || handle == 7) left = right - min; else right = left + min; }
            if (bottom - top < min) { if (handle == 0 || handle == 1 || handle == 2) top = bottom - min; else bottom = top + min; }
            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            left = Math.Max(0, left); top = Math.Max(0, top);
            right = Math.Min(bounds.Width, right); bottom = Math.Min(bounds.Height, bottom);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private void NudgeSelection(Keys key, bool large)
        {
            if (_selectionLocked) return;
            int step = large ? 10 : 1;
            int dx = 0, dy = 0;
            if (key == Keys.Left) dx = -step;
            else if (key == Keys.Right) dx = step;
            else if (key == Keys.Up) dy = -step;
            else if (key == Keys.Down) dy = step;
            Rectangle r = new Rectangle(_selection.X + dx, _selection.Y + dy, _selection.Width, _selection.Height);
            _selection = ClampToScreen(r);
            LayoutToolbar();
            Invalidate();
        }

        // ============================ 工具栏 ============================

        private void LayoutToolbar()
        {
            if (!_hasSelection) { _toolbar.Visible = false; return; }
            Rectangle selectedScreen = SelectionScreenRect();
            Point center = new Point(selectedScreen.Left + selectedScreen.Width / 2, selectedScreen.Top + selectedScreen.Height / 2);
            float scale = Math.Max(.75f, Math.Min(3f, DpiUtil.ScaleAtPoint(center)));
            if (Math.Abs(_scale - scale) > .01f)
            {
                _scale = scale;
                CreateToolbar();
                UpdateToolbarState();
            }
            Rectangle working = Rectangle.Intersect(Screen.FromPoint(center).WorkingArea, _virtualScreen);
            if (working.Width < 100 || working.Height < 80) working = _virtualScreen;
            _toolbar.MaximumLayoutWidth = Math.Max(100, working.Width - 8);
            int gap = (int)Math.Round(14 * _scale);
            Rectangle placement = DisplayGeometry.PlaceFloating(selectedScreen, _toolbar.Size, new[] { working }, gap, false);
            _toolbar.Location = DisplayGeometry.ScreenToImage(placement, _virtualScreen).Location;
            _toolbar.Visible = true;
            _toolbar.BringToFront();
        }

        private void UpdateToolbarState()
        {
            _toolbar.CanUndo = _undoHistory.Count > 0;
            _toolbar.CanRedo = _redoHistory.Count > 0;
            _toolbar.ActiveTool = _tool;
            _toolbar.Refresh();
        }

        private void OnToolClicked(object sender, EventArgs e)
        {
            if (_busy) { UpdateToolbarState(); return; }
            CancelPrivacy(false);
            CommitText();
            CommitTranslationEdit();
            EditorTool tool = _toolbar.ActiveTool;
            if (tool == EditorTool.Emoji && _pendingEmoji == null)
            {
                OpenEmojiPicker();
                return;
            }
            SetTool(tool);
        }

        private void SetTool(EditorTool tool)
        {
            _tool = tool;
            if (tool != EditorTool.Select && tool != EditorTool.None && tool != EditorTool.Text)
            { _selectedAnnotation = null; _selectedTranslationBlock = -1; }
            _pendingEmoji = null;
            _toolbar.ActiveTool = tool;
            UpdateCursor();
            Invalidate();
        }

        private void OnActionClicked(object sender, EventArgs e)
        {
            if (_busy && _toolbar.LastAction != ToolbarAction.Close) return;
            CancelPrivacy(false);
            CommitText();
            CommitTranslationEdit();
            ToolbarAction action = _toolbar.LastAction;
            bool handled = false;
            BeforeEditorAction(action, ref handled);
            if (handled) return;
            switch (action)
            {
                case ToolbarAction.Translate: StartTranslateInPlace(); break;
                case ToolbarAction.Ocr: StartAi(AiPanelMode.Ocr); break;
                case ToolbarAction.LongShot: StartLongShot(); break;
                case ToolbarAction.Undo: Undo(); break;
                case ToolbarAction.Redo: Redo(); break;
                case ToolbarAction.Save: SaveAndClose(); break;
                case ToolbarAction.Pin: PinAndClose(); break;
                case ToolbarAction.Close: CloseSession(); break;
                case ToolbarAction.Confirm: ConfirmAndClose(); break;
            }
        }

        private void OnToolContextRequested(object sender, EventArgs e)
        {
            if (_busy) return;
            CancelPrivacy(false);
            CommitText();
            EditorTool tool = _toolbar.ContextTool;
            if (tool == EditorTool.None) return;
            ShowToolMenu(tool, Cursor.Position);
        }

        private void ShowToolMenu(EditorTool tool, Point screenPoint)
        {
            if (_contextMenu != null) { _contextMenu.Dispose(); _contextMenu = null; }
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = UiTheme.Font(13f * _scale);
            menu.ShowImageMargin = true;
            _contextMenu = menu;

            if (tool == EditorTool.Mosaic)
            {
                ToolStripMenuItem smart = new ToolStripMenuItem("智能打码（本地文字检测，可配置联网补漏）");
                smart.Image = MakeSwatch(UiTheme.Green);
                smart.Click += delegate { StartSmartMosaic(); };
                menu.Items.Add(smart);
                menu.Items.Add(new ToolStripSeparator());
            }

            AddColorItems(menu, tool);
            menu.Items.Add(new ToolStripSeparator());

            if (tool == EditorTool.Text)
            {
                AddTextSizeItems(menu);
            }
            else if (tool == EditorTool.Mosaic)
            {
                AddMosaicSizeItems(menu);
            }
            else
            {
                AddWidthItems(menu);
            }

            if (tool == EditorTool.Number)
            {
                menu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem reset = new ToolStripMenuItem("重新开始编号");
                reset.Click += delegate { _numberCounter = 0; Toast.Show("序号已重置为 1"); };
                menu.Items.Add(reset);
            }

            if (tool == EditorTool.Emoji)
            {
                menu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem pick = new ToolStripMenuItem("重新选择表情…");
                pick.Click += delegate { OpenEmojiPicker(); };
                menu.Items.Add(pick);
            }

            menu.Closed += delegate
            {
                if (_contextMenu == menu) _contextMenu = null;
                // 延迟释放：Closed 事件仍处在 ToolStrip.OnItemClicked 调用栈内，立即 Dispose 会抛 ObjectDisposedException
                try { BeginInvoke((Action)delegate { try { menu.Dispose(); } catch { } }); }
                catch { try { menu.Dispose(); } catch { } }
            };
            menu.Show(screenPoint);
        }

        private static Image MakeSwatch(Color color)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush b = new SolidBrush(color)) g.FillRectangle(b, 1, 1, 14, 14);
                using (Pen p = new Pen(Color.FromArgb(150, 60, 60, 60))) g.DrawRectangle(p, 1, 1, 13, 13);
            }
            return bmp;
        }

        private void AddColorItems(ContextMenuStrip menu, EditorTool tool)
        {
            if (tool == EditorTool.Emoji || tool == EditorTool.Mosaic) return;
            string[] names = new string[] { "红色", "橙色", "黄色", "绿色", "蓝色", "紫色", "黑色", "白色" };
            Color[] colors = new Color[]
            {
                UiTheme.Red, Color.FromArgb(255, 152, 0), Color.FromArgb(255, 199, 0), UiTheme.Green,
                Color.FromArgb(30, 144, 255), Color.FromArgb(138, 43, 226), Color.FromArgb(51, 51, 51), Color.White
            };
            for (int i = 0; i < colors.Length; i++)
            {
                Color c = colors[i];
                ToolStripMenuItem item = new ToolStripMenuItem(names[i] + "  " + ColorTranslator.ToHtml(c));
                item.Image = MakeSwatch(c);
                if (_color.ToArgb() == c.ToArgb()) item.Checked = true;
                item.Click += delegate
                {
                    _color = c;
                    if (_textInput != null) _textInput.ForeColor = UiTheme.TextDark;
                };
                menu.Items.Add(item);
            }
        }

        private void AddWidthItems(ContextMenuStrip menu)
        {
            float[] widths = new float[] { 2f, 3.5f, 6f, 9f };
            string[] labels = new string[] { "细线", "标准", "粗线", "特粗" };
            for (int i = 0; i < widths.Length; i++)
            {
                float w = widths[i] * _scale;
                ToolStripMenuItem item = new ToolStripMenuItem(labels[i]);
                item.Tag = w;
                if (Math.Abs(_lineWidth - w) < 0.01f) item.Checked = true;
                item.Click += delegate
                {
                    _lineWidth = w;
                };
                menu.Items.Add(item);
            }
        }

        private void AddTextSizeItems(ContextMenuStrip menu)
        {
            float[] sizes = new float[] { 14f, 19f, 26f, 34f, 44f };
            for (int i = 0; i < sizes.Length; i++)
            {
                float size = sizes[i] * _scale;
                ToolStripMenuItem item = new ToolStripMenuItem("字号 " + sizes[i].ToString("0"));
                if (Math.Abs(_textSize - size) < 0.01f) item.Checked = true;
                item.Click += delegate
                {
                    _textSize = size;
                    UpdateTextEditFont();
                };
                menu.Items.Add(item);
            }
        }

        private void AddMosaicSizeItems(ContextMenuStrip menu)
        {
            float[] sizes = new float[] { 24f, 40f, 60f, 84f };
            string[] labels = new string[] { "小笔刷", "中笔刷", "大笔刷", "特大笔刷" };
            for (int i = 0; i < sizes.Length; i++)
            {
                float size = sizes[i] * _scale;
                ToolStripMenuItem item = new ToolStripMenuItem(labels[i]);
                if (Math.Abs(_mosaicBrush - size) < 0.01f) item.Checked = true;
                item.Click += delegate { _mosaicBrush = size; };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem blockTitle = new ToolStripMenuItem("马赛克块大小（影响外观）");
            blockTitle.Enabled = false;
            menu.Items.Add(blockTitle);
            int[] blocks = new int[] { 12, 20, 28, 40 };
            string[] blockLabels = new string[] { "12 px", "20 px", "28 px", "40 px" };
            for (int i = 0; i < blocks.Length; i++)
            {
                int value = blocks[i];
                ToolStripMenuItem item = new ToolStripMenuItem(blockLabels[i]);
                if (Math.Abs(_cfg.MosaicBlock - value) < 0.5f) item.Checked = true;
                item.Click += delegate
                {
                    _cfg.MosaicBlock = value;
                    ResetMosaicCanvas();
                    Toast.Show("马赛克块大小：" + value + "px");
                };
                menu.Items.Add(item);
            }
        }

        private void ResetMosaicCanvas()
        {
            if (_mosaic != null) { try { _mosaic.Dispose(); } catch { } _mosaic = null; }
            Invalidate();
        }

        private MosaicCanvas EnsureMosaic()
        {
            if (_mosaic == null) _mosaic = new MosaicCanvas(_screenshot, _cfg.MosaicBlock);
            return _mosaic;
        }

        // ============================ 标注操作 ============================

        private void AddAnnotation(Annotation annotation)
        {
            if (annotation == null) return;
            if (!_applyingPrivacy) CancelPrivacy(false);
            _annotations.Add(annotation);
            _ownedAnnotations.Add(annotation);
            int index = _annotations.Count - 1;
            PushCommand(new AnnotationEditCommand(() => _annotations.Remove(annotation),
                () => _annotations.Insert(Math.Min(index, _annotations.Count), annotation)));
        }

        private void Undo()
        {
            CancelPrivacy(false);
            if (_undoHistory.Count == 0) return;
            AnnotationEditCommand command = _undoHistory[_undoHistory.Count - 1];
            _undoHistory.RemoveAt(_undoHistory.Count - 1);
            command.Undo();
            _redoHistory.Add(command);
            DocumentChanged();
        }

        private void Redo()
        {
            CancelPrivacy(false);
            if (_redoHistory.Count == 0) return;
            AnnotationEditCommand command = _redoHistory[_redoHistory.Count - 1];
            _redoHistory.RemoveAt(_redoHistory.Count - 1);
            command.Redo();
            _undoHistory.Add(command);
            DocumentChanged();
        }

        private void ClearAnnotations()
        {
            CancelPrivacy(false);
            if (_annotations.Count == 0) return;
            Annotation[] previous = _annotations.ToArray();
            _annotations.Clear();
            PushCommand(new AnnotationEditCommand(() => _annotations.AddRange(previous), () => _annotations.Clear()));
        }

        private void StartDrawing(Point p)
        {
            switch (_tool)
            {
                case EditorTool.Rectangle:
                case EditorTool.Ellipse:
                    _ghost = new ShapeAnnotation
                    {
                        IsEllipse = _tool == EditorTool.Ellipse,
                        Rect = new RectangleF(p.X, p.Y, 0, 0),
                        Color = _color,
                        LineWidth = _lineWidth
                    };
                    _drag = DragMode.Shape;
                    break;
                case EditorTool.Arrow:
                    _ghost = new ArrowAnnotation { Start = p, End = p, Color = _color, LineWidth = _lineWidth };
                    _drag = DragMode.Shape;
                    break;
                case EditorTool.Pen:
                    {
                        PenAnnotation pen = new PenAnnotation { Color = _color, LineWidth = _lineWidth };
                        pen.Points.Add(p);
                        _ghost = pen;
                        _lastFreePoint = p;
                        _drag = DragMode.Freehand;
                        break;
                    }
                case EditorTool.Mosaic:
                    {
                        _mosaicMask = new GraphicsPath();
                        GraphicsPath first = MosaicAnnotation.BuildStrokeMask(new PointF[] { p }, _mosaicBrush);
                        _mosaicMask.AddPath(first, false);
                        first.Dispose();
                        _ghost = new MosaicAnnotation { Mask = _mosaicMask };
                        _lastFreePoint = p;
                        _drag = DragMode.Freehand;
                        break;
                    }
                case EditorTool.Text:
                    BeginTextEdit(p);
                    break;
                case EditorTool.Emoji:
                    if (_pendingEmoji != null) PlaceEmoji(_pendingEmoji, p);
                    break;
                case EditorTool.Number:
                    PlaceNumber(p);
                    break;
                case EditorTool.Loupe:
                    _ghost = new LoupeAnnotation
                    {
                        SourceRect = new RectangleF(p.X, p.Y, 0, 0),
                        DisplayRect = new RectangleF(p.X, p.Y, 0, 0),
                        BorderColor = _color,
                        BorderWidth = _lineWidth
                    };
                    _drag = DragMode.Shape;
                    break;
            }
        }

        private void UpdateShapeGhost(Point p)
        {
            ShapeAnnotation shape = _ghost as ShapeAnnotation;
            if (shape != null)
            {
                Rectangle r = NormalizeRect(_dragStart, p);
                shape.Rect = r;
                return;
            }
            LoupeAnnotation loupe = _ghost as LoupeAnnotation;
            if (loupe != null)
            {
                Rectangle r = NormalizeRect(_dragStart, p);
                loupe.SourceRect = r;
                float dw = Math.Max(60f, r.Width * loupe.Zoom);
                float dh = Math.Max(40f, r.Height * loupe.Zoom);
                float dx = r.Right + 30f;
                float dy = r.Bottom + 20f;
                if (dx + dw > _selection.Right) dx = r.Left - dw - 30f;
                if (dy + dh > _selection.Bottom) dy = r.Top - dh - 20f;
                loupe.DisplayRect = new RectangleF(dx, dy, dw, dh);
                return;
            }
            ArrowAnnotation arrow = _ghost as ArrowAnnotation;
            if (arrow != null) arrow.End = p;
        }

        private void AppendFreePoint(Point p)
        {
            float dx = p.X - _lastFreePoint.X;
            float dy = p.Y - _lastFreePoint.Y;
            if (dx * dx + dy * dy < 4f) return;
            PenAnnotation pen = _ghost as PenAnnotation;
            if (pen != null)
            {
                pen.Points.Add(p);
                _lastFreePoint = p;
                return;
            }
            MosaicAnnotation mosaic = _ghost as MosaicAnnotation;
            if (mosaic != null && _mosaicMask != null)
            {
                GraphicsPath segment = MosaicAnnotation.BuildStrokeMask(
                    new PointF[] { _lastFreePoint, p }, _mosaicBrush);
                _mosaicMask.AddPath(segment, false);
                segment.Dispose();
                mosaic.Mask = _mosaicMask;
                _lastFreePoint = p;
            }
        }

        private void CommitGhost()
        {
            if (_ghost == null)
            {
                _drag = DragMode.None;
                return;
            }
            Annotation ghost = _ghost;
            _ghost = null;
            bool valid = true;
            ShapeAnnotation shape = ghost as ShapeAnnotation;
            if (shape != null && (shape.Rect.Width < 2f || shape.Rect.Height < 2f)) valid = false;
            ArrowAnnotation arrow = ghost as ArrowAnnotation;
            if (arrow != null)
            {
                double len = Math.Sqrt(Math.Pow(arrow.End.X - arrow.Start.X, 2) + Math.Pow(arrow.End.Y - arrow.Start.Y, 2));
                if (len < 6) valid = false;
            }
            PenAnnotation pen = ghost as PenAnnotation;
            if (pen != null && pen.Points.Count < 1) valid = false;
            MosaicAnnotation mosaic = ghost as MosaicAnnotation;
            if (mosaic != null && (mosaic.Mask == null || mosaic.Mask.PointCount == 0)) valid = false;
            _mosaicMask = null;
            _drag = DragMode.None;
            if (valid) AddAnnotation(ghost);
            else { AnnotationGeometry.ReleaseCopy(ghost); Invalidate(); }
        }

        private void PlaceEmoji(string name, Point p)
        {
            EmojiItem item = EmojiAssets.Find(name);
            if (item == null || item.Image == null) { Toast.Show("表情资源加载失败"); return; }
            float size = 52f * _scale;
            EmojiAnnotation emoji = new EmojiAnnotation
            {
                Name = name,
                Image = item.Image,
                Rect = new RectangleF(p.X - size / 2f, p.Y - size / 2f, size, size)
            };
            AddAnnotation(emoji);
        }

        private void PlaceNumber(Point p)
        {
            _numberCounter++;
            NumberAnnotation number = new NumberAnnotation
            {
                Number = _numberCounter,
                Center = p,
                Color = _color,
                Radius = 13f * _scale
            };
            AddAnnotation(number);
        }

        private void OpenEmojiPicker()
        {
            Point screenPoint = _toolbar.Visible
                ? _toolbar.PointToScreen(new Point(0, -8))
                : PointToScreen(new Point(ClientSize.Width / 2, ClientSize.Height / 2));
            EmojiPicker.Show(screenPoint, delegate (string name)
            {
                if (_closing) return;
                try
                {
                    Activate();
                    Focus();
                }
                catch { }
                if (string.IsNullOrEmpty(name)) return;
                _pendingEmoji = name;
                _tool = EditorTool.Emoji;
                _toolbar.ActiveTool = EditorTool.Emoji;
                Toast.Show("点击截图放置表情贴纸");
            });
        }

        // ============================ 文字 ============================

        private void BeginTextEdit(Point p)
        {
            PointF anchor = new PointF(
                Math.Max(_selection.Left, Math.Min(_selection.Right - 12, p.X)),
                Math.Max(_selection.Top, Math.Min(_selection.Bottom - 12, p.Y)));
            BeginTextEditAt(anchor, null);
        }

        private void BeginEditExistingText(TextAnnotation existing)
        {
            if (existing == null) return;
            CancelPrivacy(false);
            CommitTranslationEdit();
            if (_textBar != null) CommitText();
            _textEditBefore = AnnotationGeometry.Copy(existing);
            BeginTextEditAt(existing.Location, existing);
        }

        private void BeginTextEditAt(PointF anchor, TextAnnotation existing)
        {
            RemoveTextBar();
            _textAnchor = anchor;
            _editingText = existing;
            int barW = Math.Min((int)Math.Round(430 * _scale), Math.Max((int)Math.Round(300 * _scale), _selection.Width));
            barW = Math.Min(barW, Math.Max(1, ClientSize.Width - 16));
            int barH = (int)Math.Round(46 * _scale);
            Panel bar = new Panel();
            bar.BackColor = Color.White;
            bar.Size = new Size(barW, barH);
            int bx = (int)Math.Round(anchor.X);
            int by = (int)Math.Round(anchor.Y + _textSize + 10 * _scale);
            if (by + barH > ClientSize.Height - 8) by = (int)Math.Round(anchor.Y - barH - 12 * _scale);
            if (by < 8) by = 8;
            bx = Math.Max(8, Math.Min(ClientSize.Width - barW - 8, bx));
            bar.Location = new Point(bx, by);
            bar.Paint += delegate (object sender, PaintEventArgs pe)
            {
                using (Pen pen = new Pen(UiTheme.Green, Math.Max(1.2f, 1.6f * _scale)))
                using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(0.5f, 0.5f, bar.Width - 1f, bar.Height - 1f), 8 * _scale))
                    pe.Graphics.DrawPath(pen, path);
            };

            TextBox input = new TextBox();
            input.BorderStyle = BorderStyle.None;
            input.Font = new Font(UiTheme.UiFontName, Math.Max(13f, 14f * _scale), FontStyle.Regular, GraphicsUnit.Pixel);
            input.ForeColor = UiTheme.TextDark;
            input.BackColor = Color.White;
            input.Location = new Point((int)Math.Round(10 * _scale), (int)Math.Round(12 * _scale));
            input.Text = existing != null ? existing.Text : "";
            input.KeyDown += OnTextInputKeyDown;
            input.TextChanged += delegate { Invalidate(); };
            bar.Controls.Add(input);

            Label hint = new Label();
            hint.AutoSize = false;
            hint.Font = new Font(UiTheme.UiFontName, Math.Max(12f, 12.5f * _scale), FontStyle.Regular, GraphicsUnit.Pixel);
            hint.ForeColor = UiTheme.TextMuted;
            hint.Text = "Enter 确认 · Esc 取消";
            hint.AutoEllipsis = true;
            hint.TextAlign = ContentAlignment.MiddleRight;
            hint.Size = new Size(TextRenderer.MeasureText(hint.Text, hint.Font).Width + (int)Math.Round(8 * _scale), barH);
            hint.Location = new Point(bar.Width - hint.Width - (int)Math.Round(8 * _scale), 0);
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bar.Controls.Add(hint);
            input.Width = Math.Max(1, hint.Left - input.Left - (int)Math.Round(8 * _scale));

            Controls.Add(bar);
            bar.BringToFront();
            _textBar = bar;
            _textInput = input;
            _textHint = hint;
            if (existing != null)
            {
                _color = existing.Color;
                _textSize = existing.FontPx;
            }
            input.Focus();
            input.SelectAll();
            Invalidate();
        }

        private void OnTextInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitText();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                CancelText();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void UpdateTextEditFont()
        {
            if (_textInput != null)
            {
                try { _textInput.Font = new Font(UiTheme.UiFontName, Math.Max(13f, 14f * _scale), FontStyle.Regular, GraphicsUnit.Pixel); }
                catch { }
            }
            Invalidate();
        }

        private void CommitText()
        {
            if (_textBar == null) return;
            string text = _textInput != null ? _textInput.Text : "";
            TextAnnotation existing = _editingText;
            RemoveTextBar();
            string clean = text.Replace("\r", "").Replace("\n", "");
            if (!string.IsNullOrWhiteSpace(clean))
            {
                TextAnnotation annotation = existing ?? new TextAnnotation();
                annotation.Text = clean;
                annotation.Location = _textAnchor;
                annotation.Color = _color;
                annotation.FontPx = _textSize;
                if (existing == null) AddAnnotation(annotation);
                else
                {
                    Annotation before = _textEditBefore;
                    _textEditBefore = null;
                    RecordAnnotationMutation(annotation, before);
                }
            }
            else if (existing != null)
            {
                AnnotationGeometry.ReleaseCopy(_textEditBefore); _textEditBefore = null;
                _selectedAnnotation = existing; DeleteSelectedAnnotation();
            }
            _editingText = null;
            try { Activate(); Focus(); } catch { }
            UpdateToolbarState();
            Invalidate();
        }

        private void CancelText()
        {
            TextAnnotation existing = _editingText;
            RemoveTextBar();
            AnnotationGeometry.ReleaseCopy(_textEditBefore); _textEditBefore = null;
            _editingText = null;
            try { Activate(); Focus(); } catch { }
            UpdateToolbarState();
            Invalidate();
        }

        private void RemoveTextBar()
        {
            if (_textBar == null) return;
            if (_textInput != null) { _textInput.KeyDown -= OnTextInputKeyDown; }
            Controls.Remove(_textBar);
            _textBar.Dispose();
            _textBar = null;
            _textInput = null;
            _textHint = null;
        }

        // ============================ 快捷键 ============================

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_translationBar != null)
            {
                if (keyData == Keys.Escape) { CancelTranslationEdit(); return true; }
                if (keyData == (Keys.Control | Keys.Enter)) { CommitTranslationEdit(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (_textBar != null)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }
            bool handled = false;
            BeforeEditorKey(keyData, ref handled);
            if (handled) return true;
            if (keyData == Keys.Escape)
            {
                if (_drag == DragMode.Object) { CancelObjectManipulation(); return true; }
                if (CancelPrivacy(true)) return true;
                if (_aiPanel != null) { CloseAiPanel(); return true; }
                if (_selectedAnnotation != null)
                { _selectedAnnotation = null; _selectedTranslationBlock = -1; Invalidate(); return true; }
                CloseSession();
                return true;
            }
            if (_busy) return true;
            CancelPrivacy(false);
            if (_hasSelection && _drag == DragMode.None)
            {
                if (HandleObjectKey(keyData)) return true;
                switch (keyData)
                {
                    case Keys.Enter: ConfirmAndClose(); return true;
                    case Keys.R: SetTool(EditorTool.Rectangle); return true;
                    case Keys.O: SetTool(EditorTool.Ellipse); return true;
                    case Keys.E: SetTool(EditorTool.Emoji); OpenEmojiPicker(); return true;
                    case Keys.A: SetTool(EditorTool.Arrow); return true;
                    case Keys.P: SetTool(EditorTool.Pen); return true;
                    case Keys.M: SetTool(EditorTool.Mosaic); return true;
                    case Keys.T: SetTool(EditorTool.Text); return true;
                    case Keys.N: SetTool(EditorTool.Number); return true;
                    case Keys.L: SetTool(EditorTool.Loupe); return true;
                    case Keys.C: CopyColorHex(); return true;
                    case Keys.Left:
                    case Keys.Right:
                    case Keys.Up:
                    case Keys.Down:
                        if (!_selectionLocked) { NudgeSelection(keyData, (ModifierKeys & Keys.Shift) == Keys.Shift); return true; }
                        break;
                }
                if (keyData == (Keys.Control | Keys.C)) { ConfirmAndClose(); return true; }
                if (keyData == (Keys.Control | Keys.V)) { PasteImageFromClipboard(); return true; }
                if (keyData == (Keys.Control | Keys.S)) { SaveAndClose(); return true; }
                if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
                if (keyData == (Keys.Control | Keys.Y)) { Redo(); return true; }
                if (keyData == (Keys.Control | Keys.A)) { ClearAnnotations(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CopyColorHex()
        {
            if (string.IsNullOrEmpty(_lastHex)) return;
            string hex = _lastHex;
            if (ClipUtil.CopyText(hex)) Toast.Show("已复制色值 " + hex);
        }

        private void PasteImageFromClipboard()
        {
            if (_busy) return;
            try
            {
                Bitmap pasted = ImageImport.FromClipboard();
                if (pasted != null)
                {
                    LoadImageToCanvas(pasted);
                    Toast.Show("已载入剪贴板图片继续编辑");
                }
            }
            catch (Exception ex)
            {
                Toast.Show(ex.Message);
            }
        }

        public void LoadImageToCanvas(Bitmap image)
        {
            if (image == null) return;
            try
            {
                int w = Math.Min(image.Width, ClientSize.Width);
                int h = Math.Min(image.Height, ClientSize.Height);
                int x = (ClientSize.Width - w) / 2;
                int y = (ClientSize.Height - h) / 2;

                using (Graphics g = Graphics.FromImage(_screenshot))
                {
                    g.DrawImage(image, x, y, w, h);
                }

                _selection = new Rectangle(x, y, w, h);
                _hasSelection = true;
                _selectionLocked = true;
                _annotations.Clear();
                Invalidate();
                LayoutToolbar();
                _toolbar.Visible = true;
            }
            catch (Exception ex)
            {
                ShowError("载入图片失败", ex);
            }
        }

        public void ApplyInitialSelection(Rectangle rect)
        {
            if (rect.Width > 0 && rect.Height > 0)
            {
                Rectangle screen = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
                rect.Intersect(screen);
                if (rect.Width > 10 && rect.Height > 10)
                {
                    _selection = rect;
                    _hasSelection = true;
                    _selectionLocked = true;
                    Invalidate();
                    LayoutToolbar();
                    _toolbar.Visible = true;
                }
            }
        }

        // ============================ 合成 / 输出 ============================

        private Bitmap ComposeSelection()
        {
            if (!_hasSelection || _selection.Width <= 0 || _selection.Height <= 0)
                throw new InvalidOperationException("请先框选截图区域");
            if (_annotations.Count == 0)
                return ImageUtil.CloneRegion(_screenshot, _selection);
            Bitmap full = (Bitmap)_screenshot.Clone();
            try
            {
                using (Graphics g = Graphics.FromImage(full))
                {
                    GraphicsState state = g.Save();
                    try
                    {
                        g.SetClip(_selection);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        MosaicCanvas mosaic = EnsureMosaic();
                        foreach (Annotation a in _annotations) a.Draw(g, mosaic);
                    }
                    finally
                    {
                        g.Restore(state);
                    }
                }
                return ImageUtil.CloneRegion(full, _selection);
            }
            finally
            {
                full.Dispose();
            }
        }

        private Rectangle SelectionScreenRect()
        {
            return DisplayGeometry.ImageToScreen(_selection, _virtualScreen);
        }

        private string BuildSavePath()
        {
            string dir = _cfg.EnsureSaveDir();
            string ext = string.Equals(_cfg.SaveFormat, "jpg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
            string baseName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, baseName + ext);
            int index = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(dir, baseName + "_" + index + ext);
                index++;
            }
            return path;
        }

        private bool SaveComposed(Bitmap bitmap, out string path)
        {
            path = "";
            try
            {
                path = BuildSavePath();
                ImageUtil.Save(bitmap, path, _cfg.SaveFormat);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("保存截图失败: " + ex);
                MessageBox.Show(this, "保存截图失败：" + ex.Message, "截图助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void RecordHistoryAndLastRect(Bitmap image)
        {
            try
            {
                if (_hasSelection && _selection.Width > 0 && _selection.Height > 0)
                {
                    _cfg.LastCaptureRect = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3}", _selection.X, _selection.Y, _selection.Width, _selection.Height);
                    _store.Save(_cfg);
                }
                HistoryManager.SaveItem(_store, _cfg, image,
                    _aiPanel != null ? _aiPanel.SearchableText : "");
            }
            catch { }
        }

        private void ConfirmAndClose()
        {
            if (_busy || _closing) return;
            if (!PrepareEditorOutput()) return;
            try
            {
                using (Bitmap image = ComposeSelection())
                {
                    RecordHistoryAndLastRect(image);
                    bool copied = false;
                    if (_cfg.AutoCopy || !_cfg.AutoSave) copied = ClipUtil.CopyImage(image);
                    string savedPath = null;
                    if (_cfg.AutoSave) SaveComposed(image, out savedPath);
                    CloseSession();
                    if (copied && !string.IsNullOrEmpty(savedPath)) Toast.Show("已复制到剪贴板，并保存到 " + Path.GetFileName(savedPath));
                    else if (copied) Toast.Show("已复制到剪贴板，可直接粘贴发送");
                    else if (!string.IsNullOrEmpty(savedPath)) Toast.Show("已保存到 " + savedPath);
                    else Toast.Show("截图已完成");
                }
            }
            catch (Exception ex)
            {
                ShowError("完成截图失败", ex);
            }
        }

        private void SaveAndClose()
        {
            if (_busy || _closing) return;
            if (!PrepareEditorOutput()) return;
            try
            {
                using (Bitmap image = ComposeSelection())
                {
                    RecordHistoryAndLastRect(image);
                    string path;
                    if (!SaveComposed(image, out path)) return;
                    CloseSession();
                    Toast.Show("已保存到 " + path);
                }
            }
            catch (Exception ex)
            {
                ShowError("保存截图失败", ex);
            }
        }

        private void PinAndClose()
        {
            if (_busy || _closing) return;
            if (!PrepareEditorOutput()) return;
            try
            {
                Bitmap image = ComposeSelection();
                RecordHistoryAndLastRect(image);
                Rectangle rect = SelectionScreenRect();
                CloseSession();
                PinForm pin = new PinForm(image, rect, _cfg.PinAlwaysOnTop);
                pin.Show();
            }
            catch (Exception ex)
            {
                ShowError("钉在桌面失败", ex);
            }
        }

        private void CloseSession()
        {
            if (_closing) return;
            _closing = true;
            try
            {
                CloseAiPanel();
                if (_aiCts != null) { try { _aiCts.Cancel(); } catch { } }
                RemoveTextBar();
                Close();
            }
            catch { }
        }

        private void ShowError(string title, Exception ex)
        {
            Log.Write(title + ": " + ex);
            MessageBox.Show(this, title + "：" + ex.Message, "截图助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ============================ AI ============================

        private void StartAi(AiPanelMode mode)
        {
            if (!_hasSelection || _busy) return;
            CancelPrivacy(false);
            DisposeEditorReview();
            if (_aiPanel != null) { CloseAiPanel(); return; }
            if (mode != AiPanelMode.Ocr && !_client.HasApiKey)
            {
                PromptConfigureApiKey();
                return;
            }
            CommitText();
            Bitmap image;
            try { image = ComposeSelection(); }
            catch (Exception ex) { ShowError("生成截图失败", ex); return; }
            AiResultPanel panel = new AiResultPanel(image, mode, _cfg, _client, _scale);
            OpenAiPanel(panel);
            panel.Start();
        }

        private void OpenAiPanel(AiResultPanel panel)
        {
            panel.CloseRequested += delegate { CloseAiPanel(); };
            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            int gap = (int)Math.Round(16 * _scale);
            int x = _selection.Right + gap;
            if (x + panel.Width > bounds.Width - 8) x = _selection.Left - gap - panel.Width;
            if (x < 8) x = Math.Max(8, (bounds.Width - panel.Width) / 2);
            int y = Math.Max(8, _selection.Top);
            if (y + panel.Height > bounds.Height - 8) y = Math.Max(8, bounds.Height - panel.Height - 8);
            panel.Location = new Point(x, y);
            Controls.Add(panel);
            panel.BringToFront();
            _aiPanel = panel;
            _toolbar.Visible = false;
        }

        private void CloseAiPanel()
        {
            if (_aiPanel == null) return;
            AiResultPanel panel = _aiPanel;
            _aiPanel = null;
            try
            {
                panel.CancelWork();
                Controls.Remove(panel);
                panel.Dispose();
            }
            catch { }
            if (_hasSelection)
            {
                _toolbar.Visible = true;
                LayoutToolbar();
            }
            try { Activate(); Focus(); } catch { }
            Invalidate();
        }

        private void PromptConfigureApiKey()
        {
            DialogResult result = MessageBox.Show(this,
                "文字翻译需要配置 API Key。本地文字提取和隐私规则检测可以离线使用。\n\n现在打开设置填写吗？",
                "需要配置 API Key", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (result != DialogResult.Yes) return;
            if (_openSettings == null) return;
            Hide();
            try { _openSettings(); } catch (Exception ex) { Log.Write("打开设置失败: " + ex.Message); }
            Show();
            try
            {
                Activate();
                Focus();
                Native.SetForegroundWindow(Handle);
            }
            catch { }
            Invalidate();
        }

        // ============================ 原位翻译 ============================

        private void StartTranslateInPlace()
        {
            if (!_hasSelection || _busy) return;
            CancelPrivacy(false);
            DisposeEditorReview();
            if (!_client.HasApiKey) { PromptConfigureApiKey(); return; }
            CommitText();
            Bitmap image;
            try { image = ComposeSelection(); }
            catch (Exception ex) { ShowError("生成截图失败", ex); return; }
            _busy = true;
            if (_aiCts != null) { try { _aiCts.Cancel(); } catch { } try { _aiCts.Dispose(); } catch { } }
            _aiCts = new CancellationTokenSource();
            CancellationToken token = _aiCts.Token;
            string target = string.IsNullOrWhiteSpace(_cfg.TranslateTarget) ? "中文" : _cfg.TranslateTarget;
            Toast.Show("正在进行原位翻译（识别 + 翻译），请稍候…", 6000);
            Task.Run(async delegate
            {
                List<ImageTextBlock> blocks = null;
                Exception error = null;
                try { blocks = await _client.TranslateInPlaceAsync(image, target, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error = ex; }
                finally { image.Dispose(); }
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)delegate
                    {
                        _busy = false;
                        if (_closing || IsDisposed || token.IsCancellationRequested) return;
                        if (error != null) { Toast.Show(FriendlyAiError(error), 5000); return; }
                        ApplyInPlaceTranslation(blocks, target);
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        private void ApplyInPlaceTranslation(List<ImageTextBlock> blocks, string targetLang)
        {
            if (blocks == null || blocks.Count == 0)
            {
                Toast.Show("未识别到可翻译的文字");
                return;
            }
            TranslationOverlayAnnotation overlay = new TranslationOverlayAnnotation();
            int count = 0;
            bool hasLegacyCoordinates = false;
            foreach (ImageTextBlock block in blocks)
                if (block != null && !block.HasLocalCoordinates) hasLegacyCoordinates = true;
            List<TextLineDetector.TextLine> detected = hasLegacyCoordinates
                ? TextLineDetector.Detect(_screenshot, _selection) : new List<TextLineDetector.TextLine>();
            RectangleF[] mapped = TextLineDetector.MapBlocks(detected, blocks, _selection);
            for (int i = 0; i < blocks.Count; i++)
            {
                ImageTextBlock block = blocks[i];
                if (block == null) continue;
                string original = (block.Text ?? "").Trim();
                string translation = (block.Translation ?? "").Trim();
                if (translation.Length == 0) continue;
                if (NormalizeForCompare(original) == NormalizeForCompare(translation)) continue;
                RectangleF rect;
                if (block.HasLocalCoordinates && block.HasRect)
                {
                    rect = PrivacyDetector.ToScreenRect(block.X, block.Y, block.W, block.H, _selection);
                }
                else if (i < mapped.Length && mapped[i] != RectangleF.Empty)
                {
                    rect = mapped[i];
                }
                else
                {
                    if (!block.HasRect) continue;
                    RectangleF approx = PrivacyDetector.ToScreenRect(block.X, block.Y, block.W, block.H, _selection);
                    if (approx.Width < 8f || approx.Height < 8f) continue;
                    rect = PrivacyDetector.InkRect.Refine(_screenshot, approx, _selection, 0.8f);
                }
                float grow = Math.Max(1f, rect.Height * 0.05f);
                rect = new RectangleF(rect.X - 2f, rect.Y - grow * 0.3f, rect.Width + 4f, rect.Height + grow);
                if (rect.Width < 8f || rect.Height < 8f) continue;
                Color background = SampleBackground(rect);
                overlay.Blocks.Add(new TranslationBlock
                {
                    Rect = rect,
                    SourceRect = rect,
                    Original = original,
                    Translation = translation,
                    Background = background,
                    Foreground = ContrastColor(background),
                    FontPx = Math.Max(9f, Math.Min(36f, rect.Height * 0.82f))
                });
                count++;
            }
            if (count == 0)
            {
                Toast.Show("无需替换：原文已经是「" + targetLang + "」或未识别到文字", 4500);
                return;
            }
            AddAnnotation(overlay);
            Toast.Show("已完成原位翻译 " + count + " 行（右键菜单可切换原文，Ctrl+Z 撤销）。", 5000);
        }

        private static string NormalizeForCompare(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (c == '.' || c == '。' || c == ',' || c == '，' || c == ':' || c == '：' ||
                    c == ';' || c == '；' || c == '!' || c == '！' || c == '?' || c == '？' ||
                    c == '"' || c == '\'' || c == '“' || c == '”' || c == '’' || c == '‘')
                    continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>从文字框外围采样背景色（取中位数附近值，抗文字渗色）。</summary>
        private Color SampleBackground(RectangleF screenRect)
        {
            List<int> rs = new List<int>();
            List<int> gs = new List<int>();
            List<int> bs = new List<int>();
            int x0 = (int)Math.Round(screenRect.Left);
            int y0 = (int)Math.Round(screenRect.Top);
            int x1 = (int)Math.Round(screenRect.Right);
            int y1 = (int)Math.Round(screenRect.Bottom);
            int[] offs = new int[] { -4, -3, -2, 2, 3, 4 };
            foreach (int off in offs)
            {
                int yy = y0 + off;
                if (yy >= 0 && yy < _screenshot.Height)
                {
                    for (int x = Math.Max(0, x0); x <= Math.Min(_screenshot.Width - 1, x1); x += Math.Max(1, (x1 - x0) / 8))
                    {
                        Color c = _screenshot.GetPixel(x, yy);
                        rs.Add(c.R); gs.Add(c.G); bs.Add(c.B);
                    }
                }
                int xx = x1 + off;
                if (xx >= 0 && xx < _screenshot.Width)
                {
                    for (int y = Math.Max(0, y0); y <= Math.Min(_screenshot.Height - 1, y1); y += Math.Max(1, (y1 - y0) / 8))
                    {
                        Color c = _screenshot.GetPixel(xx, y);
                        rs.Add(c.R); gs.Add(c.G); bs.Add(c.B);
                    }
                }
            }
            if (rs.Count == 0) return Color.White;
            rs.Sort(); gs.Sort(); bs.Sort();
            int mid = rs.Count / 2;
            return Color.FromArgb(rs[mid], gs[mid], bs[mid]);
        }

        private static Color ContrastColor(Color background)
        {
            // 译文背景由截图采样；用 sRGB 相对亮度选择对比更高的黑/白，避免中灰底白字。
            Func<byte, double> linear = value =>
            {
                double component = value / 255.0;
                return component <= .04045 ? component / 12.92 : Math.Pow((component + .055) / 1.055, 2.4);
            };
            double luminance = .2126 * linear(background.R) + .7152 * linear(background.G) + .0722 * linear(background.B);
            return (luminance + .05) / .05 >= 1.05 / (luminance + .05) ? Color.Black : Color.White;
        }

        private static string ShortenDebug(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private static string FriendlyAiError(Exception ex)
        {
            AiException ai = ex as AiException;
            return ai != null ? ai.Message : ("AI 请求失败：" + ex.Message);
        }

        // ============================ 智能打码（增强版） ============================

        private void StartSmartMosaic()
        {
            if (!_hasSelection || _busy || _closing) return;
            CancelPrivacy(false);
            DisposeEditorReview();
            CloseAiPanel();
            CommitText();
            Bitmap image;
            try { image = ComposeSelection(); }
            catch (Exception ex) { ShowError("生成截图失败", ex); return; }
            _busy = true;
            var cancellation = new CancellationTokenSource();
            _privacyCts = cancellation;
            CancellationToken token = cancellation.Token;
            int requestId = ++_privacyRequestId;
            Rectangle selection = _selection;
            MosaicAnnotation annotation = null;
            _privacyStatus = "本地识别与规则检测中 · Esc 取消";
            Invalidate();
            Task.Run(async delegate
            {
                PrivacyAnalysis analysis = null;
                Exception error = null;
                try
                {
                    analysis = await _client.AnalyzePrivacyAsync(image, async local =>
                    {
                        await PostPrivacyStageAsync(delegate
                        {
                            if (!IsPrivacyRequestCurrent(requestId, selection, token)) return;
                            ApplyPrivacyStage(local, ref annotation);
                            _busy = false;
                            bool network = _client.HasApiKey && (_cfg.VisionPrivacy ||
                                (_cfg.SemanticPrivacy && local.Lines.Exists(block => !block.Sensitive)));
                            _privacyStatus = network ? "本地检测已完成，联网补漏中 · Esc 取消；继续编辑将结束补漏" : "";
                            Invalidate();
                        }, token).ConfigureAwait(false);
                    }, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error = ex; }
                finally { image.Dispose(); }
                try
                {
                    await PostPrivacyStageAsync(delegate
                    {
                        if (!IsPrivacyRequestCurrent(requestId, selection, token))
                        {
                            if (requestId == _privacyRequestId) CancelPrivacy(false);
                            return;
                        }
                        _busy = false;
                        _privacyStatus = "";
                        _privacyCts = null;
                        if (error != null) Toast.Show(FriendlyAiError(error), 5000);
                        else
                        {
                            ApplyPrivacyStage(analysis, ref annotation);
                            int count = annotation == null ? 0 : annotation.Rects.Count;
                            Toast.Show((count > 0 ? "已遮挡 " + count + " 处，Ctrl+Z 可撤销。" : "本次未命中隐私规则。") +
                                "请检查遗漏。" + (analysis == null ? "" : analysis.Warning), 6500);
                        }
                        Invalidate();
                    }, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { }
                finally { cancellation.Dispose(); }
            });
        }

        private bool IsPrivacyRequestCurrent(int requestId, Rectangle selection, CancellationToken token)
        {
            return requestId == _privacyRequestId && !token.IsCancellationRequested && !_closing &&
                !IsDisposed && IsHandleCreated && _hasSelection && _selection == selection;
        }

        private async Task PostPrivacyStageAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<bool>();
            using (token.Register(() => completion.TrySetCanceled()))
            {
                if (IsDisposed || !IsHandleCreated) throw new OperationCanceledException(token);
                BeginInvoke((Action)delegate
                {
                    if (token.IsCancellationRequested) { completion.TrySetCanceled(); return; }
                    try { action(); completion.TrySetResult(true); }
                    catch (Exception error) { completion.TrySetException(error); }
                });
                await completion.Task.ConfigureAwait(false);
            }
        }

        private bool CancelPrivacy(bool showMessage)
        {
            if (_privacyCts == null) return false;
            CancellationTokenSource cancellation = _privacyCts;
            _privacyCts = null;
            ++_privacyRequestId;
            cancellation.Cancel();
            _privacyStatus = "";
            _busy = false;
            Invalidate();
            if (showMessage) Toast.Show("已取消本次检测，已应用的遮挡保留。", 4000);
            return true;
        }

        private void ApplyPrivacyStage(PrivacyAnalysis analysis, ref MosaicAnnotation annotation)
        {
            MergePrivacyReviewStage(analysis, ref annotation);
        }

        private void ApplyPrivacyAnalysis(PrivacyAnalysis analysis, List<PrivacyRegion> fallback, List<RectangleF> extraMasks, Exception error)
        {
            int lineCount = 0;
            int faceCount = 0;
            List<RectangleF> rects = PrivacyDetector.BuildMaskRects(_screenshot, analysis, _selection, 0.18f, out lineCount, out faceCount);
            // OCR/本地正则兜底：直接使用本地检测行坐标，逐行补充（可覆盖模型漏行/漏判）
            int ocrAdded = 0;
            if (extraMasks != null && extraMasks.Count > 0)
            {
                foreach (RectangleF mask in extraMasks)
                {
                    bool duplicate = false;
                    foreach (RectangleF existing in rects)
                    {
                        if (Math.Abs(existing.Y - mask.Y) < 4f && Math.Abs(existing.X - mask.X) < 8f)
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (!duplicate) { rects.Add(mask); ocrAdded++; }
                }
            }
            // 旧版区域检测兜底：仅在没有 AI 行/OCR 结果时使用，且映射到本地文字行避免坐标漂移
            if (rects.Count == 0 && fallback != null && fallback.Count > 0)
            {
                RectangleF[] mappedFallback = MapFallbackToLines(fallback);
                for (int i = 0; i < fallback.Count; i++)
                {
                    PrivacyRegion region = fallback[i];
                    if (region == null) continue;
                    RectangleF rect;
                    if (mappedFallback != null && i < mappedFallback.Length && mappedFallback[i] != RectangleF.Empty)
                    {
                        rect = mappedFallback[i];
                    }
                    else if (region.W > 0.002f && region.H > 0.002f)
                    {
                        rect = PrivacyDetector.ToScreenRect(region.X, region.Y, region.W, region.H, _selection);
                        rect = PrivacyDetector.InkRect.Refine(_screenshot, rect, _selection, 0.75f);
                    }
                    else continue;
                    float growY = Math.Max(3f, rect.Height * 0.10f);
                    rects.Add(new RectangleF(rect.X - 4f, rect.Y - growY * 0.3f, rect.Width + 8f, rect.Height + growY));
                }
                lineCount = rects.Count - faceCount;
            }
            Log.Write("智能打码: 文字命中=" + lineCount + " 人脸=" + faceCount + " OCR补漏=" + ocrAdded +
                " 兜底=" + (rects.Count - lineCount - faceCount) + " 总矩形=" + rects.Count);
            if (rects.Count == 0)
            {
                Toast.Show(error != null ? FriendlyAiError(error) : "本次未命中隐私文字规则。请检查遗漏，并用马赛克手动遮挡。" + (analysis == null ? "" : analysis.Warning), 7000);
                return;
            }
            MosaicCanvas mosaic = EnsureMosaic();
            mosaic.Block = Math.Max(28, (int)Math.Round(_cfg.MosaicBlock));
            MosaicAnnotation annotation = new MosaicAnnotation { Rects = new List<RectangleF>(rects) };
            AddAnnotation(annotation);
            string detail = "已对 " + rects.Count + " 处隐私信息打码";
            if (faceCount > 0) detail += "（含 " + faceCount + " 处人脸/头像）";
            if (ocrAdded > 0) detail += "，其中 " + ocrAdded + " 处由本地规则补充";
            detail += "，Ctrl+Z 可撤销；建议再检查一遍并手动补刷";
            if (analysis != null) detail += "。" + analysis.Warning;
            Toast.Show(detail, 6000);
        }

        /// <summary>录制模式：框选后先确认音频及格式，取消准备时返回重新框选状态。</summary>
        private void StartRecordingRegion()
        {
            if (!_hasSelection || _closing) return;
            Rectangle region = SelectionScreenRect();
            _toolbar.Visible = false;
            CloseAiPanel();
            Hide();
            Application.DoEvents();
            Thread.Sleep(80);
            RecordingFlowResult result = null;
            try
            {
                result = RecordingFlow.Run(region, _cfg, this);
            }
            catch (Exception ex)
            {
                Log.Write("录制失败: " + ex);
                Toast.Show("录制失败：" + ex.Message, 4500);
            }
            if (IsDisposed) return;
            if (result == null || !result.Started)
            {
                _hasSelection = false;
                _selection = Rectangle.Empty;
                _toolbar.Visible = false;
                Cursor = Cursors.Cross;
                Show();
                try { Activate(); Focus(); } catch { }
                Invalidate();
                return;
            }
            CloseSession();
            if (!string.IsNullOrEmpty(result.SavePath))
            {
                Toast.Show("录屏已保存到 " + result.SavePath, 5000);
            }
        }

        /// <summary>旧区域检测结果映射到本地文字行（避免模型坐标漂移导致蒙版错位）。</summary>
        private RectangleF[] MapFallbackToLines(List<PrivacyRegion> fallback)
        {
            List<TextLineDetector.TextLine> lines = TextLineDetector.Detect(_screenshot, _selection);
            if (lines.Count == 0) return null;
            List<ImageTextBlock> blocks = new List<ImageTextBlock>();
            foreach (PrivacyRegion region in fallback)
            {
                if (region == null) continue;
                blocks.Add(new ImageTextBlock { X = region.X, Y = region.Y, W = region.W, H = region.H });
            }
            return TextLineDetector.MapBlocks(lines, blocks, _selection);
        }

        private void StartLongShot()
        {
            if (!_hasSelection || _busy) return;
            CancelPrivacy(false);
            DisposeEditorReview();
            CommitText();
            Rectangle region = SelectionScreenRect();
            _busy = true;
            Hide();
            Bitmap result = null;
            try
            {
                result = LongShot.Run(region, ImageUtil.Capture);
            }
            catch (Exception ex)
            {
                Log.Write("长截图失败: " + ex);
            }
            if (IsDisposed || _closing)
            {
                if (result != null) result.Dispose();
                return;
            }
            _busy = false;
            if (result == null)
            {
                Show();
                try { Activate(); Focus(); } catch { }
                Invalidate();
                return;
            }
            CloseSession();
            LongShotResultForm form = new LongShotResultForm(result);
            form.Show();
        }

        // ============================ 释放 ============================

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                CancelPrivacy(false);
                _closing = true;
                if (_aiCts != null) { _aiCts.Cancel(); _aiCts.Dispose(); _aiCts = null; }
                if (_client != null) _client.CancelAll();
                RemoveTextBar();
                RemoveTranslationBar();
                DisposeEditorReview();
                if (_aiPanel != null) { _aiPanel.CancelWork(); Controls.Remove(_aiPanel); _aiPanel.Dispose(); _aiPanel = null; }
                if (_contextMenu != null) { _contextMenu.Dispose(); _contextMenu = null; }
                if (_mosaic != null) { _mosaic.Dispose(); _mosaic = null; }
                if (_ghost != null) { AnnotationGeometry.ReleaseCopy(_ghost); _ghost = null; _mosaicMask = null; }
            }
            catch { }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closing = true;
                CancelPrivacy(false);
                DisposeEditorReview();
                RemoveTextBar();
                ReleaseEditHistory();
                RemoveTranslationBar();
                if (_aiCts != null) { _aiCts.Cancel(); _aiCts.Dispose(); _aiCts = null; }
                if (_mosaic != null) { _mosaic.Dispose(); _mosaic = null; }
                if (_ghost != null) { AnnotationGeometry.ReleaseCopy(_ghost); _ghost = null; _mosaicMask = null; }
                if (_mosaicMask != null) { _mosaicMask.Dispose(); _mosaicMask = null; }
                if (_contextMenu != null) { _contextMenu.Dispose(); _contextMenu = null; }
                DisposeCaptureBackground();
                if (_screenshot != null && !_screenshotDisposed)
                {
                    _screenshotDisposed = true;
                    try { _screenshot.Dispose(); } catch { }
                }
                _client.CancelAll();
            }
            base.Dispose(disposing);
        }

        private bool _screenshotDisposed;
    }
}


