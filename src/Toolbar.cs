using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XiaoJie
{
    public enum EditorTool
    {
        None,
        Rectangle,
        Ellipse,
        Emoji,
        Arrow,
        Pen,
        Mosaic,
        Text,
        TextSelect,
        Number,
        Select = 10
    }

    public enum ToolbarAction
    {
        None,
        Translate,
        Ocr,
        LongShot,
        Undo,
        Save,
        Pin,
        Close,
        Confirm,
        Redo
    }

    /// <summary>截图编辑器工具栏，使用独立分组和 GDI+ 矢量图标。</summary>
    public sealed class EditorToolbar : Control
    {
        private sealed class Item
        {
            public ToolbarAction Action;
            public EditorTool Tool;
            public bool IsSeparator;
            public bool IsTool;
            public string Tip;
            public Rectangle Bounds;
        }

        private readonly float _scale;
        private readonly List<Item> _items = new List<Item>();
        private readonly ToolTip _tip = new ToolTip();
        private int _hoverIndex = -1;
        private int _tipIndex = -1;
        private EditorTool _activeTool = EditorTool.None;
        private bool _canUndo;
        private bool _canRedo;
        private int _maximumLayoutWidth = int.MaxValue;

        public event EventHandler ToolClicked;
        public event EventHandler ActionClicked;
        public event EventHandler ToolContextRequested;

        public EditorToolbar(float scale)
        {
            _scale = scale <= 0f ? 1f : scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = UiTheme.ConsoleBase;
            _tip.InitialDelay = 350;
            _tip.ReshowDelay = 120;
            _tip.ShowAlways = true;
            BuildItems();
            RefreshLayout();
        }

        public EditorTool ActiveTool
        {
            get { return _activeTool; }
            set
            {
                if (_activeTool == value) return;
                _activeTool = value;
                Invalidate();
            }
        }

        public bool CanUndo
        {
            get { return _canUndo; }
            set { if (_canUndo != value) { _canUndo = value; Invalidate(); } }
        }

        public bool CanRedo
        {
            get { return _canRedo; }
            set { if (_canRedo != value) { _canRedo = value; Invalidate(); } }
        }

        internal int MaximumLayoutWidth
        {
            get { return _maximumLayoutWidth; }
            set { _maximumLayoutWidth = Math.Max(100, value); RefreshLayout(); }
        }

        public float UiScale { get { return _scale; } }

        private void BuildItems()
        {
            AddTool(EditorTool.Rectangle, "矩形  (R)");
            AddTool(EditorTool.Ellipse, "椭圆  (O)");
            AddTool(EditorTool.Emoji, "表情  (E)");
            AddTool(EditorTool.Arrow, "箭头  (A)");
            AddTool(EditorTool.Pen, "画笔  (P)");
            AddTool(EditorTool.Mosaic, "马赛克  (M)");
            AddTool(EditorTool.Text, "文字  (T)");
            AddTool(EditorTool.TextSelect, "选择文字  (C) · 框选后复制");
            AddTool(EditorTool.Number, "序号  (N)");
            AddTool(EditorTool.Select, "选择对象  (V) · 拖动/缩放 · Delete 删除");
            _items.Add(new Item { IsSeparator = true });
            AddAction(ToolbarAction.Translate, "文字翻译  (点击后 AI 翻译)");
            AddAction(ToolbarAction.Ocr, "文字提取  (本地 OCR)");
            AddAction(ToolbarAction.LongShot, "长截图  (滚动截取)");
            _items.Add(new Item { IsSeparator = true });
            AddAction(ToolbarAction.Undo, "撤销  (Ctrl+Z)");
            AddAction(ToolbarAction.Redo, "重做  (Ctrl+Y)");
            AddAction(ToolbarAction.Save, "保存  (Ctrl+S)");
            AddAction(ToolbarAction.Pin, "钉在桌面");
            AddAction(ToolbarAction.Close, "关闭  (Esc)");
            AddAction(ToolbarAction.Confirm, "完成  (Enter / Ctrl+C)");
        }

        private void AddTool(EditorTool tool, string tip)
        {
            _items.Add(new Item { Tool = tool, Action = ToolbarAction.None, IsTool = true, Tip = tip });
        }

        private void AddAction(ToolbarAction action, string tip)
        {
            _items.Add(new Item { Action = action, Tip = tip });
        }

        /// <summary>测试用：按非分隔符顺序返回按钮矩形（0 起）。</summary>
        public Rectangle NonSeparatorBounds(int ord)
        {
            int n = 0;
            foreach (Item item in _items)
            {
                if (item.IsSeparator) continue;
                if (n == ord) return item.Bounds;
                n++;
            }
            return Rectangle.Empty;
        }

        internal Rectangle ActionBounds(ToolbarAction action)
        {
            Item item = _items.Find(candidate => !candidate.IsTool && !candidate.IsSeparator && candidate.Action == action);
            return item == null ? Rectangle.Empty : item.Bounds;
        }

        internal Rectangle ToolBounds(EditorTool tool)
        {
            Item item = _items.Find(candidate => candidate.IsTool && candidate.Tool == tool);
            return item == null ? Rectangle.Empty : item.Bounds;
        }

        public void RefreshLayout()
        {
            int btn = (int)Math.Round(40 * _scale);
            int padX = (int)Math.Round(8 * _scale);
            int padY = (int)Math.Round(7 * _scale);
            int gap = (int)Math.Round(3 * _scale);
            int sepGap = (int)Math.Round(7 * _scale);
            int x = padX;
            int y = padY;
            int widest = 0;
            foreach (Item item in _items)
            {
                if (item.IsSeparator)
                {
                    if (x + sepGap * 2 + btn > _maximumLayoutWidth - padX)
                    {
                        item.Bounds = Rectangle.Empty;
                        continue;
                    }
                    x += sepGap;
                    item.Bounds = new Rectangle(x, y + (int)Math.Round(6 * _scale), Math.Max(1, (int)Math.Round(_scale)), (int)Math.Round(22 * _scale));
                    x += item.Bounds.Width + sepGap;
                }
                else
                {
                    if (x > padX && x + btn > _maximumLayoutWidth - padX)
                    {
                        widest = Math.Max(widest, x - gap + padX);
                        x = padX;
                        y += btn + gap;
                    }
                    item.Bounds = new Rectangle(x, y, btn, btn);
                    x += btn + gap;
                }
            }
            int width = Math.Max(widest, x - gap + padX);
            int height = y + btn + padY;
            if (width != Width || height != Height) Size = new Size(width, height);
            using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(0, 0, Width, Height), 16 * _scale))
            {
                if (Region != null) Region.Dispose();
                Region = new Region(path);
            }
            Invalidate();
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                if (item.IsSeparator) continue;
                if (item.Bounds.Contains(p)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = HitTest(e.Location);
            if (idx != _hoverIndex)
            {
                _hoverIndex = idx;
                Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            if (idx >= 0 && idx != _tipIndex)
            {
                _tipIndex = idx;
                _tip.Show(_items[idx].Tip, this, e.X + 6, Height + 4, 8000);
            }
            else if (idx < 0 && _tipIndex >= 0)
            {
                _tipIndex = -1;
                _tip.Hide(this);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1) { _hoverIndex = -1; Invalidate(); }
            if (_tipIndex != -1) { _tipIndex = -1; _tip.Hide(this); }
            Cursor = Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int idx = HitTest(e.Location);
            if (idx < 0) return;
            Item item = _items[idx];
            if (e.Button == MouseButtons.Right)
            {
                if (item.IsTool)
                {
                    EventHandler h = ToolContextRequested;
                    if (h != null)
                    {
                        _pendingTool = item.Tool;
                        h(this, EventArgs.Empty);
                    }
                }
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (item.IsTool)
            {
                EditorTool next = _activeTool == item.Tool ? EditorTool.None : item.Tool;
                ActiveTool = next;
                EventHandler h = ToolClicked;
                if (h != null) h(this, EventArgs.Empty);
                Invalidate();
                return;
            }
            if (item.Action == ToolbarAction.Undo && !_canUndo) return;
            if (item.Action == ToolbarAction.Redo && !_canRedo) return;
            EventHandler ah = ActionClicked;
            if (ah != null)
            {
                _lastAction = item.Action;
                ah(this, EventArgs.Empty);
            }
        }

        private EditorTool _pendingTool = EditorTool.None;
        private ToolbarAction _lastAction = ToolbarAction.None;

        /// <summary>最近一次右键点中的工具（ToolContextRequested 时有效）。</summary>
        public EditorTool ContextTool { get { return _pendingTool; } }

        /// <summary>最近一次点击的按钮动作（ActionClicked 时有效）。</summary>
        public ToolbarAction LastAction { get { return _lastAction; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 浅色浮层：使用细描边、圆角和低强度阴影，保持工具按钮清晰可辨。
            RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (GraphicsPath path = ImageUtil.RoundedRect(rect, 16 * _scale))
            {
                using (SolidBrush b = new SolidBrush(UiTheme.ToolbarBg)) g.FillPath(b, path);
                using (Pen p = new Pen(UiTheme.ToolbarBorder, Math.Max(1f, 1f * _scale))) g.DrawPath(p, path);
            }
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                if (item.IsSeparator)
                {
                    using (Pen p = new Pen(UiTheme.ConsoleBorder, Math.Max(1f, _scale)))
                        g.DrawLine(p, item.Bounds.Left, item.Bounds.Top, item.Bounds.Left, item.Bounds.Bottom);
                    continue;
                }
                bool hover = i == _hoverIndex;
                bool active = item.IsTool && _activeTool == item.Tool;
                bool disabled = (item.Action == ToolbarAction.Undo && !_canUndo) || (item.Action == ToolbarAction.Redo && !_canRedo);
                RectangleF bg = new RectangleF(item.Bounds.X + 1, item.Bounds.Y + 2, item.Bounds.Width - 2, item.Bounds.Height - 4);

                Color iconColor;

                // 2. 按钮视觉分层渲染
                if (item.Action == ToolbarAction.Confirm)
                {
                    // 主操作使用高对比橙色信号块，深色图标避免白橙对比不足。
                    Color confirmBg = hover ? UiTheme.ConfirmBgHover : UiTheme.ConfirmBg;
                    using (SolidBrush b = new SolidBrush(confirmBg))
                    using (GraphicsPath path = ImageUtil.RoundedRect(bg, 11 * _scale))
                        g.FillPath(b, path);
                    iconColor = Color.White;
                }
                else if (item.Action == ToolbarAction.Close && hover)
                {
                    using (SolidBrush b = new SolidBrush(UiTheme.CloseHoverBg))
                    using (GraphicsPath path = ImageUtil.RoundedRect(bg, 10 * _scale))
                        g.FillPath(b, path);
                    iconColor = UiTheme.CloseHoverText;
                }
                else if (active)
                {
                    // 选中工具使用淡蓝底和系统蓝图标。
                    using (SolidBrush b = new SolidBrush(UiTheme.ActiveBg))
                    using (GraphicsPath path = ImageUtil.RoundedRect(bg, 10 * _scale))
                    {
                        g.FillPath(b, path);
                    }
                    iconColor = UiTheme.Telemetry;
                }
                else if (hover && !disabled)
                {
                    using (SolidBrush b = new SolidBrush(UiTheme.HoverBg))
                    using (GraphicsPath path = ImageUtil.RoundedRect(bg, 10 * _scale))
                        g.FillPath(b, path);
                    iconColor = UiTheme.ConsoleText;
                }
                else
                {
                    if (disabled) iconColor = UiTheme.ConsoleMuted;
                    else if (item.Action == ToolbarAction.Close) iconColor = UiTheme.Critical;
                    else iconColor = UiTheme.ConsoleText;
                }

                RectangleF iconBox = new RectangleF(
                    item.Bounds.X + (item.Bounds.Width - 22 * _scale) / 2f,
                    item.Bounds.Y + (item.Bounds.Height - 22 * _scale) / 2f,
                    22 * _scale, 22 * _scale);
                IconPainter.Draw(g, item.Action, item.Tool, iconBox, iconColor, _scale);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>工具栏矢量图标绘制（20×20 设计坐标，随 scale 缩放）。</summary>
    internal static class IconPainter
    {
        public static void Draw(Graphics g, ToolbarAction action, EditorTool tool, RectangleF box, Color color, float scale)
        {
            float s = box.Width / 20f;
            Func<float, float> X = delegate (float v) { return box.X + v * s; };
            Func<float, float> Y = delegate (float v) { return box.Y + v * s; };
            Func<float, float, PointF> P = delegate (float x, float y) { return new PointF(X(x), Y(y)); };
            float pw = Math.Max(1.3f, 1.6f * s);
            using (Pen pen = new Pen(color, pw))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                using (SolidBrush brush = new SolidBrush(color))
                {
                    switch (action)
                    {
                        case ToolbarAction.Translate: DrawTranslate(g, pen, brush, P, s); return;
                        case ToolbarAction.Ocr: DrawOcr(g, pen, P, s); return;
                        case ToolbarAction.LongShot: DrawLongShot(g, pen, P, s); return;
                        case ToolbarAction.Undo: DrawUndo(g, pen, P, s); return;
                        case ToolbarAction.Redo: DrawUndo(g, pen, (x, y) => P(20 - x, y), s); return;
                        case ToolbarAction.Save: DrawSave(g, pen, P, s); return;
                        case ToolbarAction.Pin: DrawPin(g, pen, brush, P, s); return;
                        case ToolbarAction.Close: DrawClose(g, pen, P, s); return;
                        case ToolbarAction.Confirm: DrawConfirm(g, pen, P, s); return;
                    }
                    switch (tool)
                    {
                        case EditorTool.Select:
                            g.DrawPolygon(pen, new[] { P(5, 3), P(5, 16), P(9, 12), P(13, 17), P(15, 16), P(11, 11), P(16, 10) });
                            return;
                        case EditorTool.Rectangle:
                            g.DrawRectangle(pen, X(3), Y(5), 14 * s, 10 * s);
                            return;
                        case EditorTool.Ellipse:
                            g.DrawEllipse(pen, X(3), Y(5), 14 * s, 10 * s);
                            return;
                        case EditorTool.Emoji:
                            g.DrawEllipse(pen, X(3), Y(3), 14 * s, 14 * s);
                            g.FillEllipse(brush, X(7.1f), Y(7.8f), 1.9f * s, 1.9f * s);
                            g.FillEllipse(brush, X(11.0f), Y(7.8f), 1.9f * s, 1.9f * s);
                            g.DrawArc(pen, X(6.2f), Y(8.0f), 7.6f * s, 6.0f * s, 20f, 140f);
                            return;
                        case EditorTool.Arrow:
                            g.DrawLine(pen, P(4.0f, 16.0f), P(15.6f, 4.4f));
                            g.DrawLine(pen, P(15.6f, 4.4f), P(10.4f, 4.9f));
                            g.DrawLine(pen, P(15.6f, 4.4f), P(15.1f, 9.6f));
                            return;
                        case EditorTool.Pen:
                            DrawPen(g, pen, P, s);
                            return;
                        case EditorTool.Mosaic:
                            DrawMosaicIcon(g, pen, brush, P, s);
                            return;
                        case EditorTool.Text:
                            g.DrawLine(pen, P(4.0f, 6.0f), P(16.0f, 6.0f));
                            g.DrawLine(pen, P(10.0f, 6.0f), P(10.0f, 16.0f));
                            return;
                        case EditorTool.TextSelect:
                            g.DrawRectangle(pen, X(3), Y(4), 14 * s, 12 * s);
                            g.DrawLine(pen, P(6, 8), P(14, 8));
                            g.DrawLine(pen, P(6, 11), P(13, 11));
                            g.DrawLine(pen, P(6, 14), P(11, 14));
                            return;
                        case EditorTool.Number:
                            g.DrawEllipse(pen, X(3.0f), Y(3.0f), 14 * s, 14 * s);
                            DrawGlyph(g, "1", color, X(10.0f), Y(10.0f), 9f * s);
                            return;
                    }
                }
            }
        }

        private static void DrawGlyph(Graphics g, string text, Color color, float cx, float cy, float px)
        {
            using (Font font = new Font(UiTheme.UiFontName, px, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(color))
            using (StringFormat fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(text, font, brush, new RectangleF(cx - px * 2, cy - px * 2, px * 4, px * 4), fmt);
            }
        }

        private static void DrawGlyphRect(Graphics g, string text, Color color, RectangleF rect, float px)
        {
            using (Font font = new Font(UiTheme.UiFontName, px, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(color))
            using (StringFormat fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(text, font, brush, rect, fmt);
            }
        }

        private static void DrawTranslate(Graphics g, Pen pen, SolidBrush brush, Func<float, float, PointF> P, float s)
        {
            using (GraphicsPath p1 = ImageUtil.RoundedRect(new RectangleF(P(2.6f, 2.6f).X, P(2.6f, 2.6f).Y, 10f * s, 10f * s), 1.6f * s))
                g.DrawPath(pen, p1);
            DrawGlyph(g, "A", pen.Color, P(7.6f, 7.6f).X, P(7.6f, 7.6f).Y, 7.2f * s);
            RectangleF front = new RectangleF(P(6.8f, 6.8f).X, P(6.8f, 6.8f).Y, 10.6f * s, 10.6f * s);
            using (GraphicsPath p2 = ImageUtil.RoundedRect(front, 1.6f * s))
            {
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, p2);
                g.DrawPath(pen, p2);
            }
            DrawGlyph(g, "文", pen.Color, front.X + front.Width / 2f, front.Y + front.Height / 2f, 7.4f * s);
        }

        private static void DrawOcr(Graphics g, Pen pen, Func<float, float, PointF> P, float s)
        {
            using (Pen dash = new Pen(pen.Color, pen.Width))
            {
                dash.DashStyle = DashStyle.Dash;
                dash.DashPattern = new float[] { 2.2f, 1.6f };
                using (GraphicsPath p = ImageUtil.RoundedRect(new RectangleF(P(2.8f, 2.8f).X, P(2.8f, 2.8f).Y, 14.4f * s, 14.4f * s), 2.2f * s))
                    g.DrawPath(dash, p);
            }
            DrawGlyph(g, "文", pen.Color, P(10f, 10.3f).X, P(10f, 10.3f).Y, 8f * s);
        }

        private static void DrawLongShot(Graphics g, Pen pen, Func<float, float, PointF> P, float s)
        {
            using (GraphicsPath p = ImageUtil.RoundedRect(new RectangleF(P(3.2f, 3.2f).X, P(3.2f, 3.2f).Y, 13.6f * s, 13.6f * s), 2.0f * s))
                g.DrawPath(pen, p);
            g.DrawLine(pen, P(10f, 6.2f), P(10f, 13.8f));
            g.DrawLine(pen, P(10f, 6.0f), P(7.7f, 8.3f));
            g.DrawLine(pen, P(10f, 6.0f), P(12.3f, 8.3f));
            g.DrawLine(pen, P(10f, 14f), P(7.7f, 11.7f));
            g.DrawLine(pen, P(10f, 14f), P(12.3f, 11.7f));
        }

        private static void DrawUndo(Graphics g, Pen pen, Func<float, float, PointF> P, float s)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddBezier(P(15.6f, 13.6f), P(16.2f, 7.0f), P(9.4f, 4.2f), P(4.9f, 8.9f));
                g.DrawPath(pen, path);
            }
            g.DrawLine(pen, P(4.8f, 9.0f), P(4.2f, 5.4f));
            g.DrawLine(pen, P(4.8f, 9.0f), P(8.6f, 6.6f));
        }

        private static void DrawSave(Graphics g, Pen pen, Func<float, float, PointF> P, float s)
        {
            g.DrawLine(pen, P(10f, 3.2f), P(10f, 11.6f));
            g.DrawLine(pen, P(10f, 12.4f), P(7.0f, 9.4f));
            g.DrawLine(pen, P(10f, 12.4f), P(13.0f, 9.4f));
            g.DrawLine(pen, P(4.2f, 13.2f), P(4.2f, 16.2f));
            g.DrawLine(pen, P(4.2f, 16.2f), P(15.8f, 16.2f));
            g.DrawLine(pen, P(15.8f, 16.2f), P(15.8f, 13.2f));
        }

        private static void DrawPin(Graphics g, Pen pen, SolidBrush brush, Func<float, float, PointF> P, float s)
        {
            g.DrawEllipse(pen, P(5.4f, 4.4f).X, P(5.4f, 4.4f).Y, 5.6f * s, 5.6f * s);
            g.DrawLine(pen, P(9.6f, 8.4f), P(14.2f, 13.0f));
            g.DrawLine(pen, P(12.6f, 6.4f), P(15.8f, 9.6f));
            g.DrawLine(pen, P(8.4f, 10.6f), P(10.8f, 13.0f));
            g.FillEllipse(brush, P(13.4f, 13.2f).X, P(13.4f, 13.2f).Y, 2.0f * s, 2.0f * s);
        }

        private static void DrawClose(Graphics g, Pen pen, Func<float, float, PointF> P, float s)
        {
            g.DrawLine(pen, P(5.6f, 5.6f), P(14.4f, 14.4f));
            g.DrawLine(pen, P(14.4f, 5.6f), P(5.6f, 14.4f));
        }

        private static void DrawConfirm(Graphics g, Pen pen, Func<float, float, PointF> P, float s)
        {
            float oldW = pen.Width;
            pen.Width = Math.Max(2.0f, 2.2f * s);
            g.DrawLines(pen, new PointF[] { P(4.8f, 10.4f), P(8.6f, 14.4f), P(15.4f, 5.6f) });
            pen.Width = oldW;
        }

        private static void DrawPen(Graphics g, Pen pen, Func<float, float, PointF> P, float s)
        {
            PointF[] pts = new PointF[]
            {
                P(3.6f, 16.4f), P(5.1f, 11.9f), P(12.8f, 4.2f), P(15.8f, 7.2f), P(8.1f, 14.9f)
            };
            g.DrawPolygon(pen, pts);
            g.DrawLine(pen, P(5.1f, 11.9f), P(8.1f, 14.9f));
            g.DrawLine(pen, P(12.8f, 4.2f), P(15.8f, 7.2f));
        }

        private static void DrawMosaicIcon(Graphics g, Pen pen, SolidBrush brush, Func<float, float, PointF> P, float s)
        {
            float[] v = new float[] { 3.4f, 8.2f, 13.0f };
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    RectangleF cell = new RectangleF(P(v[j], v[i]).X, P(v[j], v[i]).Y, 3.6f * s, 3.6f * s);
                    if ((i + j) % 2 == 0) g.FillRectangle(brush, cell);
                    else g.DrawRectangle(pen, cell.X + pen.Width / 2f, cell.Y + pen.Width / 2f, cell.Width - pen.Width, cell.Height - pen.Width);
                }
            }
        }
    }
}


