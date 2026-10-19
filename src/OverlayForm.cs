using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 全屏截图编辑器：显示冻结画面、选区、标注工具栏、AI 面板、长截图入口。
    /// </summary>
    public sealed class OverlayForm : Form
    {
        private enum DragMode
        {
            None,
            Selecting,
            Moving,
            Resizing,
            Shape,
            Freehand
        }

        private readonly AppConfig _cfg;
        private readonly ConfigStore _store;
        private readonly Bitmap _screenshot;
        private readonly Rectangle _virtualScreen;
        private readonly float _scale;
        private readonly EditorToolbar _toolbar;
        private readonly DeepSeekClient _client;
        private readonly Action _openSettings;
        private MosaicCanvas _mosaic;

        private Rectangle _selection;
        private bool _hasSelection;
        private bool _selectionLocked;
        private EditorTool _tool = EditorTool.None;

        private readonly List<Annotation> _annotations = new List<Annotation>();
        private readonly List<Annotation> _redoList = new List<Annotation>();

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
        private float _mosaicBrush = 26f;
        private int _numberCounter = 0;
        private string _pendingEmoji = null;

        private TextBox _textBox;
        private AiResultPanel _aiPanel;
        private CancellationTokenSource _aiCts;
        private bool _busy;
        private bool _closing;

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
            _scale = DpiUtil.SystemScale;
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
            Text = "微信截图助手 - 截图";
            Bounds = virtualScreen;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _toolbar = new EditorToolbar(_scale);
            _toolbar.Visible = false;
            _toolbar.ToolClicked += OnToolClicked;
            _toolbar.ActionClicked += OnActionClicked;
            _toolbar.ToolContextRequested += OnToolContextRequested;
            Controls.Add(_toolbar);
        }

        public float UiScale { get { return _scale; } }

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
            g.DrawImageUnscaled(_screenshot, 0, 0);

            if (!_hasSelection)
            {
                Rectangle hover = _hoverWindowRect;
                if (hover.Width > 0)
                {
                    FillMaskExcept(g, hover);
                    using (Pen pen = new Pen(UiTheme.Green, Math.Max(1f, 1.5f * _scale)))
                    {
                        pen.DashStyle = DashStyle.Dash;
                        pen.DashPattern = new float[] { 4f, 3f };
                        g.DrawRectangle(pen, hover.X, hover.Y, hover.Width, hover.Height);
                    }
                    if (_cfg.ShowDimensions)
                        DrawSizeLabel(g, hover, true);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(UiTheme.Mask)) g.FillRectangle(b, ClientRectangle);
                }
                if (_cfg.ShowColorValue && _mouseInside) DrawColorChip(g);
                return;
            }

            FillMaskExcept(g, _selection);
            DrawAnnotations(g, _selection);
            DrawSelectionChrome(g);
            if (_cfg.ShowDimensions) DrawSizeLabel(g, _selection, false);
            if (_cfg.ShowColorValue && _mouseInside && _drag == DragMode.Selecting) DrawColorChip(g);
        }

        private void FillMaskExcept(Graphics g, Rectangle hole)
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            Rectangle r = Rectangle.Intersect(hole, new Rectangle(0, 0, w, h));
            using (SolidBrush b = new SolidBrush(UiTheme.Mask))
            {
                if (r.Top > 0) g.FillRectangle(b, 0, 0, w, r.Top);
                if (r.Bottom < h) g.FillRectangle(b, 0, r.Bottom, w, h - r.Bottom);
                if (r.Left > 0) g.FillRectangle(b, 0, r.Top, r.Left, Math.Max(0, r.Height));
                if (r.Right < w) g.FillRectangle(b, r.Right, r.Top, w - r.Right, Math.Max(0, r.Height));
            }
        }

        private void DrawAnnotations(Graphics g, Rectangle clip)
        {
            GraphicsState state = g.Save();
            try
            {
                g.SetClip(clip);
                if (_mosaic == null) _mosaic = new MosaicCanvas(_screenshot, _cfg.MosaicBlock);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (Annotation a in _annotations) a.Draw(g, _mosaic);
                if (_ghost != null) _ghost.Draw(g, _mosaic);
            }
            finally
            {
                g.Restore(state);
            }
        }

        private void DrawSelectionChrome(Graphics g)
        {
            float pw = Math.Max(1.5f, 2f * _scale);
            using (Pen pen = new Pen(UiTheme.Green, pw))
                g.DrawRectangle(pen, _selection.X - pw / 2f, _selection.Y - pw / 2f,
                    _selection.Width + pw, _selection.Height + pw);
            if (_selectionLocked) return;
            foreach (Rectangle r in GetHandleRects())
            {
                using (SolidBrush b = new SolidBrush(UiTheme.Green)) g.FillRectangle(b, r);
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
                float y = rect.Top - size.Height - 3 * _scale;
                if (y < 2 * _scale) y = rect.Top + 4 * _scale;
                if (x < 2 * _scale) x = 2 * _scale;
                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    g.DrawString(text, font, shadow, x + 1, y + 1);
                using (SolidBrush brush = new SolidBrush(UiTheme.Green))
                    g.DrawString(text, font, brush, x, y);
                if (windowHint && !string.IsNullOrEmpty(_hoverWindowTitle))
                {
                    string title = _hoverWindowTitle;
                    if (title.Length > 30) title = title.Substring(0, 30) + "…";
                    using (Font tf = new Font(UiTheme.UiFontName, 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        Size ts = TextRenderer.MeasureText(title, tf);
                        float tx = rect.Left + 2 * _scale;
                        float ty = y - ts.Height - 3 * _scale;
                        if (ty < 2 * _scale) ty = rect.Bottom + 4 * _scale;
                        using (SolidBrush shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                            g.DrawString(title, tf, shadow, tx + 1, ty + 1);
                        using (SolidBrush brush = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                            g.DrawString(title, tf, brush, tx, ty);
                    }
                }
            }
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
            using (Font font = new Font("Consolas", px, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                Size textSize = TextRenderer.MeasureText(hex, font);
                int pad = (int)Math.Round(7 * _scale);
                int sw = (int)Math.Round(15 * _scale);
                int w = textSize.Width + sw + pad * 2 + 4;
                int h = textSize.Height + pad;
                int x = _mousePos.X + (int)Math.Round(18 * _scale);
                int y = _mousePos.Y + (int)Math.Round(20 * _scale);
                if (x + w > ClientSize.Width - 4) x = _mousePos.X - w - (int)Math.Round(10 * _scale);
                if (y + h > ClientSize.Height - 4) y = _mousePos.Y - h - (int)Math.Round(10 * _scale);
                Rectangle box = new Rectangle(x, y, w, h);
                using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(box.X, box.Y, box.Width, box.Height), 5 * _scale))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(235, 24, 24, 24)))
                    g.FillPath(bg, path);
                Rectangle swatch = new Rectangle(x + pad, y + (h - sw) / 2, sw, sw);
                using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, swatch);
                using (Pen p = new Pen(Color.FromArgb(200, 255, 255, 255))) g.DrawRectangle(p, swatch);
                TextRenderer.DrawText(g, hex, font,
                    new Point(x + pad + sw + 4, y + (h - textSize.Height) / 2), Color.White,
                    TextFormatFlags.NoPrefix);
            }
        }

        // ============================ 鼠标 ============================

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _mousePos = e.Location;
            _mouseInside = true;
            if (e.Button == MouseButtons.Right)
            {
                if (_textBox != null) { CancelText(); return; }
                CloseSession();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (_textBox != null)
            {
                if (_textBox.Bounds.Contains(e.Location)) return;
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
            ClearAnnotations();
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
            _mousePos = e.Location;
            _mouseInside = true;
            switch (_drag)
            {
                case DragMode.Selecting:
                    _selection = NormalizeRect(_dragStart, e.Location);
                    Invalidate();
                    return;
                case DragMode.Moving:
                    {
                        int dx = e.X - _dragStart.X;
                        int dy = e.Y - _dragStart.Y;
                        Rectangle r = new Rectangle(_startRect.X + dx, _startRect.Y + dy, _startRect.Width, _startRect.Height);
                        _selection = ClampToScreen(r);
                        Invalidate();
                        return;
                    }
                case DragMode.Resizing:
                    _selection = ResizeSelection(_startRect, _resizeHandle, e.Location);
                    Invalidate();
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
            _mousePos = e.Location;
            switch (_drag)
            {
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
                        Cursor = Cursors.SizeAll;
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
            if (e.Button == MouseButtons.Left && _hasSelection && _tool == EditorTool.None && _selection.Contains(e.Location) && _textBox == null)
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
            if (_textBox != null && (ModifierKeys & Keys.Control) == Keys.Control)
            {
                _textSize = Math.Max(10f, Math.Min(72f, _textSize + (e.Delta > 0 ? 2f : -2f)));
                UpdateTextBoxFont();
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
            _toolbar.RefreshLayout();
            int gap = (int)Math.Round(14 * _scale);
            int x = _selection.Left + (_selection.Width - _toolbar.Width) / 2;
            int y = _selection.Bottom + gap;
            if (y + _toolbar.Height > ClientSize.Height - 4)
                y = _selection.Top - gap - _toolbar.Height;
            if (y < 4) y = Math.Max(4, ClientSize.Height - _toolbar.Height - 4);
            x = Math.Max(4, Math.Min(ClientSize.Width - _toolbar.Width - 4, x));
            _toolbar.Location = new Point(x, y);
            _toolbar.Visible = true;
            _toolbar.BringToFront();
        }

        private void UpdateToolbarState()
        {
            _toolbar.CanUndo = _annotations.Count > 0;
            _toolbar.CanRedo = _redoList.Count > 0;
            _toolbar.ActiveTool = _tool;
            _toolbar.Refresh();
        }

        private void OnToolClicked(object sender, EventArgs e)
        {
            CommitText();
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
            _pendingEmoji = null;
            _toolbar.ActiveTool = tool;
            UpdateCursor();
            Invalidate();
        }

        private void OnActionClicked(object sender, EventArgs e)
        {
            CommitText();
            ToolbarAction action = _toolbar.LastAction;
            switch (action)
            {
                case ToolbarAction.Translate: StartAi(AiPanelMode.Translate); break;
                case ToolbarAction.Ocr: StartAi(AiPanelMode.Ocr); break;
                case ToolbarAction.LongShot: StartLongShot(); break;
                case ToolbarAction.Undo: Undo(); break;
                case ToolbarAction.Save: SaveAndClose(); break;
                case ToolbarAction.Pin: PinAndClose(); break;
                case ToolbarAction.Forward: ForwardAndClose(); break;
                case ToolbarAction.Close: CloseSession(); break;
                case ToolbarAction.Confirm: ConfirmAndClose(); break;
            }
        }

        private void OnToolContextRequested(object sender, EventArgs e)
        {
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
                ToolStripMenuItem smart = new ToolStripMenuItem("一键智能打码（AI 识别头像/姓名/手机号等）");
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
                    if (_textBox != null) _textBox.ForeColor = c;
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
                    UpdateTextBoxFont();
                };
                menu.Items.Add(item);
            }
        }

        private void AddMosaicSizeItems(ContextMenuStrip menu)
        {
            float[] sizes = new float[] { 16f, 26f, 40f, 60f };
            string[] labels = new string[] { "小笔刷", "中笔刷", "大笔刷", "特大笔刷" };
            for (int i = 0; i < sizes.Length; i++)
            {
                float size = sizes[i] * _scale;
                ToolStripMenuItem item = new ToolStripMenuItem(labels[i]);
                if (Math.Abs(_mosaicBrush - size) < 0.01f) item.Checked = true;
                item.Click += delegate { _mosaicBrush = size; };
                menu.Items.Add(item);
            }
        }

        // ============================ 标注操作 ============================

        private void AddAnnotation(Annotation annotation)
        {
            if (annotation == null) return;
            _annotations.Add(annotation);
            _redoList.Clear();
            _selectionLocked = true;
            UpdateToolbarState();
            Invalidate();
        }

        private void Undo()
        {
            if (_annotations.Count == 0) return;
            Annotation last = _annotations[_annotations.Count - 1];
            _annotations.RemoveAt(_annotations.Count - 1);
            _redoList.Add(last);
            _selectionLocked = _annotations.Count > 0;
            UpdateToolbarState();
            Invalidate();
        }

        private void Redo()
        {
            if (_redoList.Count == 0) return;
            Annotation last = _redoList[_redoList.Count - 1];
            _redoList.RemoveAt(_redoList.Count - 1);
            _annotations.Add(last);
            _selectionLocked = true;
            UpdateToolbarState();
            Invalidate();
        }

        private void ClearAnnotations()
        {
            _annotations.Clear();
            _redoList.Clear();
            _selectionLocked = false;
            UpdateToolbarState();
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
            else Invalidate();
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
            if (p.X < _selection.Left) p.X = _selection.Left;
            if (p.Y < _selection.Top) p.Y = _selection.Top;
            if (p.X > _selection.Right - 20) p.X = Math.Max(_selection.Left, _selection.Right - 20);
            if (p.Y > _selection.Bottom - 20) p.Y = Math.Max(_selection.Top, _selection.Bottom - 20);
            _textBox = new TextBox();
            _textBox.BorderStyle = BorderStyle.FixedSingle;
            _textBox.BackColor = Color.FromArgb(250, 250, 250);
            _textBox.ForeColor = _color;
            _textBox.Font = new Font(UiTheme.UiFontName, _textSize, FontStyle.Regular, GraphicsUnit.Pixel);
            _textBox.Location = p;
            _textBox.Width = 120;
            _textBox.Height = _textBox.Font.Height + 8;
            _textBox.TextChanged += delegate { AutoSizeTextBox(); };
            _textBox.KeyDown += OnTextBoxKeyDown;
            Controls.Add(_textBox);
            _textBox.BringToFront();
            _textBox.Focus();
            Invalidate();
        }

        private void AutoSizeTextBox()
        {
            if (_textBox == null) return;
            Size size = TextRenderer.MeasureText(_textBox.Text.Length == 0 ? "文字" : _textBox.Text, _textBox.Font);
            int w = Math.Max(80, Math.Min(ClientSize.Width - _textBox.Left - 4, size.Width + 14));
            _textBox.Width = w;
        }

        private void UpdateTextBoxFont()
        {
            if (_textBox == null) return;
            _textBox.Font = new Font(UiTheme.UiFontName, _textSize, FontStyle.Regular, GraphicsUnit.Pixel);
            _textBox.Height = _textBox.Font.Height + 8;
            AutoSizeTextBox();
        }

        private void OnTextBoxKeyDown(object sender, KeyEventArgs e)
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

        private void CommitText()
        {
            if (_textBox == null) return;
            string text = _textBox.Text;
            Point location = _textBox.Location;
            Font font = _textBox.Font;
            float fontPx = font.SizeInPoints * 96f / 72f;
            if (Math.Abs(fontPx - _textSize) < 0.5f) fontPx = _textSize;
            RemoveTextBox();
            if (!string.IsNullOrWhiteSpace(text))
            {
                TextAnnotation annotation = new TextAnnotation
                {
                    Text = text.Replace("\r", "").Replace("\n", ""),
                    Location = new PointF(location.X + 2, location.Y + 2),
                    Color = _color,
                    FontPx = fontPx
                };
                AddAnnotation(annotation);
            }
            try { Activate(); Focus(); } catch { }
        }

        private void CancelText()
        {
            RemoveTextBox();
            Invalidate();
        }

        private void RemoveTextBox()
        {
            if (_textBox == null) return;
            Controls.Remove(_textBox);
            _textBox.Dispose();
            _textBox = null;
        }

        // ============================ 快捷键 ============================

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_textBox != null)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (keyData == Keys.Escape)
            {
                if (_aiPanel != null) { CloseAiPanel(); return true; }
                CloseSession();
                return true;
            }
            if (_hasSelection && _drag == DragMode.None)
            {
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
                    case Keys.C: CopyColorHex(); return true;
                    case Keys.Left:
                    case Keys.Right:
                    case Keys.Up:
                    case Keys.Down:
                        if (!_selectionLocked) { NudgeSelection(keyData, (ModifierKeys & Keys.Shift) == Keys.Shift); return true; }
                        break;
                }
                if (keyData == (Keys.Control | Keys.C)) { ConfirmAndClose(); return true; }
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
                        if (_mosaic == null) _mosaic = new MosaicCanvas(_screenshot, _cfg.MosaicBlock);
                        foreach (Annotation a in _annotations) a.Draw(g, _mosaic);
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
            return new Rectangle(_virtualScreen.X + _selection.X, _virtualScreen.Y + _selection.Y,
                _selection.Width, _selection.Height);
        }

        private string BuildSavePath()
        {
            string dir = _cfg.EnsureSaveDir();
            string ext = string.Equals(_cfg.SaveFormat, "jpg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
            string baseName = "微信截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
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
                MessageBox.Show(this, "保存截图失败：" + ex.Message, "微信截图助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void ConfirmAndClose()
        {
            if (_busy || _closing) return;
            try
            {
                using (Bitmap image = ComposeSelection())
                {
                    bool copied = false;
                    if (_cfg.AutoCopy || !_cfg.AutoSave) copied = ClipUtil.CopyImage(image);
                    string savedPath = null;
                    if (_cfg.AutoSave) SaveComposed(image, out savedPath);
                    CloseSession();
                    if (copied && !string.IsNullOrEmpty(savedPath)) Toast.Show("已复制到剪贴板，并保存到 " + Path.GetFileName(savedPath));
                    else if (copied) Toast.Show("已复制到剪贴板，可直接在微信中 Ctrl+V 发送");
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
            try
            {
                using (Bitmap image = ComposeSelection())
                {
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
            try
            {
                Bitmap image = ComposeSelection();
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

        private void ForwardAndClose()
        {
            if (_busy || _closing) return;
            try
            {
                using (Bitmap image = ComposeSelection())
                {
                    bool copied = ClipUtil.CopyImage(image);
                    CloseSession();
                    if (!copied) { Toast.Show("复制图片失败，请重试"); return; }
                    bool activated = WinUtil.ActivateWeChat();
                    Toast.Show(activated
                        ? "已复制到剪贴板，微信已切换到前台，Ctrl+V 发送给好友"
                        : "已复制到剪贴板，请打开微信聊天窗口 Ctrl+V 发送");
                }
            }
            catch (Exception ex)
            {
                ShowError("转发失败", ex);
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
                RemoveTextBox();
                Close();
            }
            catch { }
        }

        private void ShowError(string title, Exception ex)
        {
            Log.Write(title + ": " + ex);
            MessageBox.Show(this, title + "：" + ex.Message, "微信截图助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ============================ AI ============================

        private void StartAi(AiPanelMode mode)
        {
            if (!_hasSelection || _busy) return;
            if (_aiPanel != null) { CloseAiPanel(); return; }
            if (!_client.HasApiKey)
            {
                PromptConfigureApiKey();
                return;
            }
            CommitText();
            Bitmap image;
            try { image = ComposeSelection(); }
            catch (Exception ex) { ShowError("生成截图失败", ex); return; }
            AiResultPanel panel = new AiResultPanel(image, mode, _cfg, _client, _scale);
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
            panel.Start();
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
                "尚未配置 DeepSeek API Key。\n\n文字提取、文字翻译、一键智能打码需要 API Key（在 platform.deepseek.com 创建）。\n\n现在打开设置填写吗？",
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

        private void StartSmartMosaic()
        {
            if (!_hasSelection || _busy) return;
            if (!_client.HasApiKey) { PromptConfigureApiKey(); return; }
            CommitText();
            _busy = true;
            Bitmap image = ImageUtil.CloneRegion(_screenshot, _selection);
            if (_aiCts != null) { try { _aiCts.Cancel(); } catch { } }
            _aiCts = new CancellationTokenSource();
            CancellationToken token = _aiCts.Token;
            Toast.Show("正在识别隐私信息，请稍候…", 4000);
            Task.Run(async delegate
            {
                List<PrivacyRegion> regions = null;
                Exception error = null;
                try { regions = await _client.DetectPrivacyAsync(image, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error = ex; }
                Bitmap disposeImage = image;
                try
                {
                    if (IsDisposed) return;
                    BeginInvoke((Action)delegate
                    {
                        _busy = false;
                        disposeImage.Dispose();
                        if (_closing || IsDisposed) return;
                        if (error != null)
                        {
                            Toast.Show(FriendlyAiError(error), 4000);
                            return;
                        }
                        ApplyPrivacyRegions(regions);
                    });
                }
                catch { try { disposeImage.Dispose(); } catch { } }
            });
        }

        private static string FriendlyAiError(Exception ex)
        {
            AiException ai = ex as AiException;
            return ai != null ? ai.Message : ("识别失败：" + ex.Message);
        }

        private void ApplyPrivacyRegions(List<PrivacyRegion> regions)
        {
            if (regions == null || regions.Count == 0)
            {
                Toast.Show("未识别到需要打码的隐私信息");
                return;
            }
            GraphicsPath combined = new GraphicsPath();
            int count = 0;
            foreach (PrivacyRegion region in regions)
            {
                float x = _selection.Left + region.X * _selection.Width;
                float y = _selection.Top + region.Y * _selection.Height;
                float w = region.W * _selection.Width;
                float h = region.H * _selection.Height;
                if (w < 2 || h < 2) continue;
                combined.AddRectangle(new RectangleF(x - 2, y - 2, w + 4, h + 4));
                count++;
            }
            if (count == 0)
            {
                combined.Dispose();
                Toast.Show("未识别到需要打码的隐私信息");
                return;
            }
            MosaicAnnotation mosaic = new MosaicAnnotation { Mask = combined };
            AddAnnotation(mosaic);
            Toast.Show("已对 " + count + " 处隐私信息打码（Ctrl+Z 可撤销）");
        }

        // ============================ 长截图 ============================

        private void StartLongShot()
        {
            if (!_hasSelection || _busy) return;
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
                _closing = true;
                if (_aiCts != null) { _aiCts.Cancel(); _aiCts.Dispose(); _aiCts = null; }
                if (_client != null) _client.CancelAll();
                RemoveTextBox();
                if (_aiPanel != null) { _aiPanel.CancelWork(); Controls.Remove(_aiPanel); _aiPanel.Dispose(); _aiPanel = null; }
                if (_contextMenu != null) { _contextMenu.Dispose(); _contextMenu = null; }
                if (_mosaic != null) { _mosaic.Dispose(); _mosaic = null; }
                if (_ghost != null) { _ghost = null; }
            }
            catch { }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
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


