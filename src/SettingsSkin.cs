using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>设置窗口的信号控制台配色。</summary>
    internal static class SettingsColors
    {
        public static readonly Color PageBg = Color.FromArgb(245, 245, 247);
        public static readonly Color CardBg = Color.FromArgb(255, 255, 255);
        public static readonly Color CardBorder = Color.FromArgb(229, 229, 234);
        public static readonly Color InputBorder = Color.FromArgb(209, 209, 214);
        public static readonly Color TextPrimary = UiTheme.TextDark;
        public static readonly Color TextSecondary = UiTheme.TextGray;
        public static readonly Color TextHint = UiTheme.TextMuted;
        public static readonly Color Green = Color.FromArgb(0, 102, 204);
        public static readonly Color GreenHover = Color.FromArgb(0, 87, 184);
        public static readonly Color GreenPressed = Color.FromArgb(0, 75, 158);
        public static readonly Color GreenLight = Color.FromArgb(234, 242, 255);
        public static readonly Color Red = UiTheme.Critical;
        public static readonly Color Divider = Color.FromArgb(239, 239, 244);
        public static readonly Color HoverBg = Color.FromArgb(247, 247, 249);
        public static readonly Color PressedBg = Color.FromArgb(238, 238, 242);
        public static readonly Color SwitchOff = Color.FromArgb(174, 174, 178);
        public static readonly Color WindowBorder = Color.FromArgb(209, 209, 214);
        public static readonly Color TabTrack = Color.FromArgb(233, 233, 237);
        public static readonly Color MenuHover = Color.FromArgb(234, 242, 255);
        public static readonly Color TitleForeground = UiTheme.TextDark;
    }

    /// <summary>DPI 缩放辅助（自绘控件按物理像素绘制）。</summary>
    internal static class SettingsDpi
    {
        public static float Scale(Control control)
        {
            if (control == null) return 1f;
            try
            {
                int dpi = control.DeviceDpi;
                if (dpi >= 72 && dpi <= 480) return dpi / 96f;
            }
            catch { }
            return 1f;
        }

        public static int Px(Control control, float logical)
        {
            return (int)Math.Round(logical * Scale(control));
        }
    }

    // =====================================================================
    // 暖纸色切角卡片（带分组标题和橙色状态轨）
    // =====================================================================

    internal sealed class SettingsCard : Panel
    {
        private string _title = "";
        private Font _titleFont;
        private Color _outlineColor = SettingsColors.CardBorder;

        public SettingsCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = SettingsColors.CardBg;
        }

        public string Title
        {
            get { return _title; }
            set { _title = value ?? ""; Invalidate(); }
        }

        /// <summary>卡片描边颜色（欢迎条等可以覆盖）。</summary>
        public Color OutlineColor
        {
            get { return _outlineColor; }
            set { _outlineColor = value; Invalidate(); }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_titleFont != null)
            {
                try { _titleFont.Dispose(); } catch { }
                _titleFont = null;
            }
            _titleFont = new Font(Font, FontStyle.Bold);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;
            float radius = SettingsDpi.Px(this, 14f);
            using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(0, 0, Width, Height), radius))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null)
                {
                    try { old.Dispose(); } catch { }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = SettingsDpi.Scale(this);
            using (Pen pen = new Pen(_outlineColor, Math.Max(1f, s)))
            using (GraphicsPath path = ImageUtil.RoundedRect(
                new RectangleF(0.5f * s, 0.5f * s, Width - s, Height - s), 14f * s))
            {
                g.DrawPath(pen, path);
            }
            if (!string.IsNullOrEmpty(_title) && _titleFont != null)
            {
                Rectangle titleRect = new Rectangle(SettingsDpi.Px(this, 18f), SettingsDpi.Px(this, 13f),
                    Width - SettingsDpi.Px(this, 36f), SettingsDpi.Px(this, 24f));
                TextRenderer.DrawText(g, _title, _titleFont, titleRect, SettingsColors.TextPrimary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _titleFont != null)
            {
                try { _titleFont.Dispose(); } catch { }
                _titleFont = null;
            }
            base.Dispose(disposing);
        }
    }

    // =====================================================================
    // 胶囊分组 Tab
    // =====================================================================

    internal sealed class SettingsTabs : Control
    {
        private readonly List<string> _items = new List<string>();
        private int _selectedIndex;
        private int _hoverIndex = -1;
        private Font _boldFont;

        public SettingsTabs()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = SettingsColors.PageBg;
            Cursor = Cursors.Hand;
            TabStop = false;
            Height = 40;
        }

        public event EventHandler SelectedIndexChanged;

        public void SetItems(params string[] items)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            if (_selectedIndex >= _items.Count) _selectedIndex = Math.Max(0, _items.Count - 1);
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                int index = value;
                if (index < 0) index = 0;
                if (index >= _items.Count) index = Math.Max(0, _items.Count - 1);
                if (_selectedIndex == index) return;
                _selectedIndex = index;
                Invalidate();
                EventHandler handler = SelectedIndexChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_boldFont != null)
            {
                try { _boldFont.Dispose(); } catch { }
            }
            _boldFont = new Font(Font, FontStyle.Bold);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_items.Count == 0) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = SettingsDpi.Scale(this);
            g.Clear(BackColor);

            float rTrack = 9f * s;
            float pad = 3f * s;
            RectangleF track = new RectangleF(0, 0, Width, Height);
            using (GraphicsPath trackPath = ImageUtil.RoundedRect(track, rTrack))
            using (SolidBrush trackBrush = new SolidBrush(SettingsColors.TabTrack))
                g.FillPath(trackBrush, trackPath);

            float segWidth = (Width - pad * 2f) / _items.Count;
            float segHeight = Height - pad * 2f;
            float rPill = 7f * s;
            for (int i = 0; i < _items.Count; i++)
            {
                RectangleF seg = new RectangleF(pad + i * segWidth, pad, segWidth, segHeight);
                if (i == _selectedIndex)
                {
                    using (GraphicsPath shadowPill = ImageUtil.RoundedRect(new RectangleF(seg.X, seg.Y + 1.2f, seg.Width, seg.Height), rPill))
                    using (SolidBrush sbrush = new SolidBrush(Color.FromArgb(55, 0, 0, 0)))
                        g.FillPath(sbrush, shadowPill);

                    using (GraphicsPath pill = ImageUtil.RoundedRect(seg, rPill))
                    using (SolidBrush brush = new SolidBrush(Color.White))
                        g.FillPath(brush, pill);
                    using (GraphicsPath pill = ImageUtil.RoundedRect(
                        new RectangleF(seg.X + 0.5f * s, seg.Y + 0.5f * s, seg.Width - s, seg.Height - s), rPill))
                    using (Pen pen = new Pen(SettingsColors.CardBorder, Math.Max(1f, s)))
                        g.DrawPath(pen, pill);
                }
                else if (i == _hoverIndex)
                {
                    using (GraphicsPath pill = ImageUtil.RoundedRect(seg, rPill))
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                        g.FillPath(brush, pill);
                }
                Font font = i == _selectedIndex && _boldFont != null ? _boldFont : Font;
                Color color = i == _selectedIndex ? SettingsColors.TextPrimary : SettingsColors.TextSecondary;
                Rectangle textRect = Rectangle.Round(seg);
                TextRenderer.DrawText(g, _items[i], font, textRect, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private int HitTest(Point point)
        {
            if (_items.Count == 0) return -1;
            float s = SettingsDpi.Scale(this);
            float pad = 4f * s;
            if (point.Y < pad || point.Y > Height - pad) return -1;
            float segWidth = (Width - pad * 2f) / _items.Count;
            if (segWidth <= 0) return -1;
            int index = (int)((point.X - pad) / segWidth);
            if (index < 0 || index >= _items.Count) return -1;
            return index;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = HitTest(e.Location);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int index = HitTest(e.Location);
            if (index >= 0) SelectedIndex = index;
        }
    }

    // =====================================================================
    // 控制台式状态开关
    // =====================================================================

    internal sealed class SettingsSwitch : Control
    {
        private bool _checked;

        public SettingsSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(34, 18);
            BackColor = SettingsColors.CardBg;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                EventHandler handler = CheckedChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float s = SettingsDpi.Scale(this);
            float h = Height;
            RectangleF track = new RectangleF(0, 0, Width, h);
            Color trackColor = !Enabled ? Color.FromArgb(232, 235, 239)
                : (_checked ? SettingsColors.Green : SettingsColors.SwitchOff);
            using (GraphicsPath path = ImageUtil.RoundedRect(track, h / 2f))
            using (SolidBrush brush = new SolidBrush(trackColor))
                g.FillPath(brush, path);

            float knob = h - 4f * s;
            float knobX = _checked ? Width - knob - 2f * s : 2f * s;
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(30, 0, 0, 0)))
                g.FillEllipse(shadow, knobX, 2.6f * s + 0.8f * s, knob, knob);
            using (SolidBrush knobBrush = new SolidBrush(Enabled ? Color.White : Color.FromArgb(245, 246, 248)))
                g.FillEllipse(knobBrush, knobX, 2f * s, knob, knob);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Enabled) Checked = !Checked;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                if (Enabled) Checked = !Checked;
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }
    }

    // =====================================================================
    // 扁平按钮（主按钮 / 次按钮 / 文字按钮）
    // =====================================================================

    internal enum SettingsButtonStyle
    {
        Primary,
        Secondary,
        Text
    }

    internal sealed class SettingsButton : Control, IButtonControl
    {
        private SettingsButtonStyle _style = SettingsButtonStyle.Secondary;
        private bool _hover;
        private bool _pressed;
        private bool _isDefault;

        public SettingsButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            BackColor = SettingsColors.CardBg;
            Cursor = Cursors.Hand;
            TabStop = true;
            Size = new Size(96, 34);
        }

        public SettingsButtonStyle ButtonStyle
        {
            get { return _style; }
            set { _style = value; Invalidate(); }
        }

        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value)
        {
            if (_isDefault == value) return;
            _isDefault = value;
            Invalidate();
        }

        public void PerformClick()
        {
            if (!Enabled) return;
            OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float s = SettingsDpi.Scale(this);
            float radius = 10f * s;
            RectangleF rect = new RectangleF(0.5f * s, 0.5f * s, Width - s, Height - s);

            Color backColor;
            Color borderColor = Color.Empty;
            Color textColor;
            if (!Enabled)
            {
                backColor = UiTheme.DisabledBackground;
                textColor = UiTheme.Disabled;
                if (_style == SettingsButtonStyle.Secondary) borderColor = Color.FromArgb(234, 237, 241);
            }
            else if (_style == SettingsButtonStyle.Primary)
            {
                backColor = _pressed ? SettingsColors.GreenPressed : (_hover ? SettingsColors.GreenHover : SettingsColors.Green);
                textColor = UiTheme.SignalText;
            }
            else if (_style == SettingsButtonStyle.Secondary)
            {
                backColor = _pressed ? SettingsColors.PressedBg : (_hover ? SettingsColors.HoverBg : Color.White);
                borderColor = _hover ? Color.FromArgb(205, 210, 216) : SettingsColors.InputBorder;
                textColor = SettingsColors.TextPrimary;
            }
            else
            {
                backColor = _pressed ? SettingsColors.PressedBg : (_hover ? SettingsColors.HoverBg : BackColor);
                textColor = _hover ? SettingsColors.TextPrimary : SettingsColors.TextSecondary;
            }

            using (GraphicsPath path = ImageUtil.RoundedRect(rect, radius))
            {
                using (SolidBrush brush = new SolidBrush(backColor))
                    g.FillPath(brush, path);
                if (borderColor != Color.Empty)
                {
                    using (Pen pen = new Pen(borderColor, Math.Max(1f, s)))
                        g.DrawPath(pen, path);
                }
            }

            Font font = _style == SettingsButtonStyle.Primary ? BoldFont() : Font;
            Rectangle textRect = new Rectangle(0, 0, Width, Height);
            TextRenderer.DrawText(g, Text, font, textRect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (Focused && _style != SettingsButtonStyle.Primary)
            {
                using (Pen pen = new Pen(Color.FromArgb(120, SettingsColors.Green), Math.Max(1f, s)))
                using (GraphicsPath path = ImageUtil.RoundedRect(
                    new RectangleF(1.5f * s, 1.5f * s, Width - 3f * s, Height - 3f * s), radius))
                    g.DrawPath(pen, path);
            }
        }

        private Font _boldFont;
        private Font BoldFont()
        {
            if (_boldFont == null)
            {
                _boldFont = new Font(Font, FontStyle.Bold);
            }
            return _boldFont;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_boldFont != null)
            {
                try { _boldFont.Dispose(); } catch { }
                _boldFont = null;
            }
            Invalidate();
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
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _pressed = true;
            Focus();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _pressed = false;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                _pressed = true;
                Invalidate();
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            // 只有按键按下事件确实到达本控件时才在松开时触发，避免和 Form 的 AcceptButton 重复触发
            if (_pressed && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                _pressed = false;
                Invalidate();
                PerformClick();
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _boldFont != null)
            {
                try { _boldFont.Dispose(); } catch { }
                _boldFont = null;
            }
            base.Dispose(disposing);
        }
    }

    // =====================================================================
    // 圆角输入框宿主（包装 TextBox / NumericUpDown）
    // =====================================================================

    internal sealed class SettingsField : Control
    {
        private bool _innerFocused;

        public SettingsField(Control inner)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = SettingsColors.CardBg;
            TabStop = false;
            Inner = inner;
            Controls.Add(inner);
            inner.GotFocus += delegate { _innerFocused = true; Invalidate(); };
            inner.LostFocus += delegate { _innerFocused = false; Invalidate(); };
        }

        /// <summary>被包装的输入控件。</summary>
        public Control Inner { get; private set; }

        /// <summary>为空时显示的占位提示。</summary>
        public string Placeholder { get; set; }

        /// <summary>多行输入（TextBox.Multiline=true）时用内边距铺满。</summary>
        public bool IsMultiline
        {
            get
            {
                TextBox box = Inner as TextBox;
                return box != null && box.Multiline;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutInner();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            LayoutInner();
            Invalidate();
        }

        private void LayoutInner()
        {
            if (Inner == null || Width <= 0 || Height <= 0) return;
            int left = SettingsDpi.Px(this, 12f);
            int right = SettingsDpi.Px(this, 12f);
            if (IsMultiline)
            {
                int padTop = SettingsDpi.Px(this, 7f);
                int padBottom = SettingsDpi.Px(this, 7f);
                Inner.Bounds = new Rectangle(left, padTop,
                    Math.Max(1, Width - left - right), Math.Max(1, Height - padTop - padBottom));
            }
            else
            {
                int innerWidth = Math.Max(1, Width - left - right);
                int innerHeight = Inner.Height;
                int top = Math.Max(0, (Height - innerHeight) / 2);
                Inner.Bounds = new Rectangle(left, top, innerWidth, innerHeight);
            }
        }

        /// <summary>DPI 缩放完成后由设置窗口统一调用，修正内部控件的间距/尺寸。</summary>
        public void Relayout()
        {
            LayoutInner();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            FocusInner();
        }

        public void FocusInner()
        {
            if (Inner == null) return;
            Inner.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = SettingsDpi.Scale(this);
            g.Clear(BackColor);
            RectangleF rect = new RectangleF(0.5f * s, 0.5f * s, Width - s, Height - s);
            using (GraphicsPath path = ImageUtil.RoundedRect(rect, 7f * s))
            {
                using (SolidBrush brush = new SolidBrush(Enabled ? Color.White : Color.FromArgb(250, 251, 252)))
                    g.FillPath(brush, path);
                Color border = !Enabled ? Color.FromArgb(234, 237, 241)
                    : (_innerFocused ? SettingsColors.Green : SettingsColors.InputBorder);
                using (Pen pen = new Pen(border, Math.Max(1f, s)))
                    g.DrawPath(pen, path);
            }

            if (!string.IsNullOrEmpty(Placeholder) && !_innerFocused && InnerIsEmpty())
            {
                Rectangle hintRect = new Rectangle(SettingsDpi.Px(this, 12f), 0,
                    Math.Max(1, Width - SettingsDpi.Px(this, 20f)), Height);
                TextRenderer.DrawText(g, Placeholder, Font, hintRect, SettingsColors.TextHint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis);
            }
        }

        private bool InnerIsEmpty()
        {
            TextBox box = Inner as TextBox;
            if (box != null) return box.TextLength == 0;
            return true;
        }
    }

    // =====================================================================
    // 扁平圆角下拉框（自绘；支持可编辑模式）
    // =====================================================================

    internal enum SettingsComboStyle
    {
        DropDownList,
        DropDown
    }

    internal sealed class SettingsComboBox : Control
    {
        private readonly List<string> _items = new List<string>();
        private readonly TextBox _editBox;
        private SettingsComboStyle _style = SettingsComboStyle.DropDownList;
        private int _selectedIndex = -1;
        private bool _hoverChevron;
        private bool _focused;
        private ContextMenuStrip _menu;

        public SettingsComboBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            BackColor = SettingsColors.CardBg;
            Height = 36;
            TabStop = true;

            _editBox = new TextBox();
            _editBox.BorderStyle = BorderStyle.None;
            _editBox.BackColor = Color.White;
            _editBox.Visible = false;
            _editBox.GotFocus += delegate { _focused = true; Invalidate(); };
            _editBox.LostFocus += delegate { _focused = false; Invalidate(); };
            _editBox.KeyDown += OnEditBoxKeyDown;
            Controls.Add(_editBox);
        }

        public event EventHandler SelectedIndexChanged;

        public string Placeholder { get; set; }

        public SettingsComboStyle ComboStyle
        {
            get { return _style; }
            set
            {
                _style = value;
                _editBox.Visible = value == SettingsComboStyle.DropDown;
                LayoutEditBox();
                Invalidate();
            }
        }

        public void SetItems(params string[] items)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            if (_selectedIndex >= _items.Count) _selectedIndex = _items.Count > 0 ? _items.Count - 1 : -1;
            SyncEditText();
            Invalidate();
        }

        public IList<string> Items { get { return _items; } }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                int index = value;
                if (index < -1) index = -1;
                if (index >= _items.Count) index = _items.Count - 1;
                if (_selectedIndex == index) return;
                _selectedIndex = index;
                SyncEditText();
                Invalidate();
                EventHandler handler = SelectedIndexChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        public new string Text
        {
            get
            {
                if (_style == SettingsComboStyle.DropDown) return _editBox.Text;
                return _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : "";
            }
            set
            {
                string text = value ?? "";
                if (_style == SettingsComboStyle.DropDown)
                {
                    _editBox.Text = text;
                    int match = _items.FindIndex(delegate(string item) { return string.Equals(item, text, StringComparison.Ordinal); });
                    _selectedIndex = match;
                    Invalidate();
                }
                else
                {
                    int match = _items.FindIndex(delegate(string item) { return string.Equals(item, text, StringComparison.Ordinal); });
                    SelectedIndex = match;
                }
            }
        }

        private void SyncEditText()
        {
            if (_style != SettingsComboStyle.DropDown) return;
            if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
                _editBox.Text = _items[_selectedIndex];
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_editBox != null) _editBox.Font = Font;
            LayoutEditBox();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutEditBox();
        }

        private void LayoutEditBox()
        {
            if (_editBox == null || Width <= 0 || Height <= 0) return;
            int chevron = SettingsDpi.Px(this, 30f);
            int left = SettingsDpi.Px(this, 12f);
            int innerHeight = Math.Max(16, _editBox.Height);
            int top = Math.Max(0, (Height - innerHeight) / 2);
            _editBox.Bounds = new Rectangle(left, top, Math.Max(1, Width - left - chevron), innerHeight);
        }

        /// <summary>DPI 缩放完成后由设置窗口统一调用，修正内部编辑框的尺寸。</summary>
        public void Relayout()
        {
            LayoutEditBox();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = SettingsDpi.Scale(this);
            g.Clear(BackColor);
            RectangleF rect = new RectangleF(0.5f * s, 0.5f * s, Width - s, Height - s);
            using (GraphicsPath path = ImageUtil.RoundedRect(rect, 7f * s))
            {
                using (SolidBrush brush = new SolidBrush(Enabled ? Color.White : Color.FromArgb(250, 251, 252)))
                    g.FillPath(brush, path);
                Color border = !Enabled ? Color.FromArgb(234, 237, 241)
                    : (_focused ? SettingsColors.Green : SettingsColors.InputBorder);
                using (Pen pen = new Pen(border, Math.Max(1f, s)))
                    g.DrawPath(pen, path);
            }

            if (_style == SettingsComboStyle.DropDownList)
            {
                string text = Text;
                Color color = SettingsColors.TextPrimary;
                if (text.Length == 0)
                {
                    text = Placeholder ?? "";
                    color = SettingsColors.TextHint;
                }
                Rectangle textRect = new Rectangle(SettingsDpi.Px(this, 12f), 0,
                    Math.Max(1, Width - SettingsDpi.Px(this, 44f)), Height);
                TextRenderer.DrawText(g, text, Font, textRect, color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis);
            }

            // 下拉箭头
            float cx = Width - 17f * s;
            float cy = Height / 2f + 1f * s;
            using (Pen pen = new Pen(_hoverChevron ? SettingsColors.TextPrimary : SettingsColors.TextSecondary,
                Math.Max(1.4f, 1.6f * s)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx - 4f * s, cy - 2f * s, cx, cy + 2.5f * s);
                g.DrawLine(pen, cx, cy + 2.5f * s, cx + 4f * s, cy - 2f * s);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hover = e.X >= Width - SettingsDpi.Px(this, 32f);
            if (hover != _hoverChevron)
            {
                _hoverChevron = hover;
                Invalidate();
            }
            Cursor = hover ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverChevron)
            {
                _hoverChevron = false;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_style == SettingsComboStyle.DropDown && e.X < Width - SettingsDpi.Px(this, 32f))
            {
                FocusEditBox();
                return;
            }
            OpenDropDown();
        }

        public void FocusEditBox()
        {
            if (_style == SettingsComboStyle.DropDown) _editBox.Focus();
            else Focus();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            _focused = true;
            Invalidate();
            if (_style == SettingsComboStyle.DropDown) _editBox.Focus();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (!_editBox.Focused) _focused = false;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.F4 ||
                (_style == SettingsComboStyle.DropDownList && e.KeyCode == Keys.Space))
            {
                OpenDropDown();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (_style == SettingsComboStyle.DropDownList && e.KeyCode == Keys.Up)
            {
                if (_selectedIndex > 0) SelectedIndex = _selectedIndex - 1;
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void OnEditBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.F4)
            {
                OpenDropDown();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void OpenDropDown()
        {
            if (!Enabled || _items.Count == 0) return;
            if (_menu != null && _menu.Visible)
            {
                _menu.Close(ToolStripDropDownCloseReason.CloseCalled);
                return;
            }
            if (_menu == null || _menu.IsDisposed) _menu = new ContextMenuStrip();
            ContextMenuStrip menu = _menu;
            // WinForms still reparents a dropdown after raising Closed. Keep it owned
            // by this control until Dispose instead of releasing it during that event.
            while (menu.Items.Count > 0) menu.Items[0].Dispose();
            menu.Font = Font;
            menu.ShowImageMargin = false;
            menu.Renderer = new SettingsMenuRenderer();
            menu.BackColor = Color.White;
            for (int i = 0; i < _items.Count; i++)
            {
                int index = i;
                ToolStripMenuItem item = new ToolStripMenuItem(_items[i]);
                item.Tag = index;
                item.Font = Font;
                item.Padding = new Padding(SettingsDpi.Px(this, 6f), SettingsDpi.Px(this, 3f),
                    SettingsDpi.Px(this, 22f), SettingsDpi.Px(this, 3f));
                item.Click += delegate
                {
                    if (_style == SettingsComboStyle.DropDown)
                    {
                        _editBox.Text = _items[index];
                        SelectedIndex = index;
                    }
                    else
                    {
                        SelectedIndex = index;
                    }
                };
                menu.Items.Add(item);
            }
            try
            {
                menu.Show(this, new Point(0, Height + SettingsDpi.Px(this, 4f)));
            }
            catch (Exception ex)
            {
                Log.Write("打开下拉菜单失败: " + ex.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_menu != null)
                {
                    try { _menu.Dispose(); } catch { }
                    _menu = null;
                }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class SettingsMenuRenderer : ToolStripProfessionalRenderer
    {
        public SettingsMenuRenderer() : base(new SettingsMenuColorTable())
        {
            RoundedEdges = false;
        }
    }

    internal sealed class SettingsMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.White; } }
        public override Color ImageMarginGradientBegin { get { return Color.White; } }
        public override Color ImageMarginGradientMiddle { get { return Color.White; } }
        public override Color ImageMarginGradientEnd { get { return Color.White; } }
        public override Color MenuBorder { get { return SettingsColors.InputBorder; } }
        public override Color MenuItemBorder { get { return SettingsColors.MenuHover; } }
        public override Color MenuItemSelected { get { return SettingsColors.MenuHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return SettingsColors.MenuHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return SettingsColors.MenuHover; } }
        public override Color MenuItemPressedGradientBegin { get { return SettingsColors.MenuHover; } }
        public override Color MenuItemPressedGradientEnd { get { return SettingsColors.MenuHover; } }
    }

    // =====================================================================
    // 标题栏按钮 / 图标 / 拖动辅助
    // =====================================================================

    internal sealed class SettingsTitleButton : Control
    {
        private bool _hover;

        public SettingsTitleButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = UiTheme.ConsoleBase;
            Cursor = Cursors.Hand;
            TabStop = false;
            Size = new Size(32, 32);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float s = SettingsDpi.Scale(this);
            if (_hover)
            {
                using (GraphicsPath path = ImageUtil.RoundedRect(
                    new RectangleF(0, 0, Width, Height), 8f * s))
                using (SolidBrush brush = new SolidBrush(SettingsColors.HoverBg))
                    g.FillPath(brush, path);
            }
            float pad = Width * 0.32f;
            using (Pen pen = new Pen(_hover ? UiTheme.Critical : UiTheme.ConsoleMuted,
                Math.Max(1.6f, 1.7f * s)))
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

    internal static class SettingsIcons
    {
        /// <summary>绘制应用图标：系统蓝圆角方块与白色取景框。</summary>
        public static void DrawAppIcon(Graphics g, Rectangle rect)
        {
            if (g == null || rect.Width <= 0 || rect.Height <= 0) return;
            float s = rect.Width / 22f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = ImageUtil.RoundedRect(rect, 7f * s))
            using (SolidBrush brush = new SolidBrush(SettingsColors.Green))
                g.FillPath(brush, path);
            using (Pen pen = new Pen(Color.White, Math.Max(1.3f, 1.7f * s)))
            {
                pen.LineJoin = LineJoin.Round;
                // 取景框四角
                float left = rect.X + 5.5f * s;
                float top = rect.Y + 5.5f * s;
                float right = rect.Right - 5.5f * s;
                float bottom = rect.Bottom - 5.5f * s;
                float len = 3.2f * s;
                g.DrawLine(pen, left, top + len, left, top);
                g.DrawLine(pen, left, top, left + len, top);
                g.DrawLine(pen, right - len, top, right, top);
                g.DrawLine(pen, right, top, right, top + len);
                g.DrawLine(pen, right, bottom - len, right, bottom);
                g.DrawLine(pen, right, bottom, right - len, bottom);
                g.DrawLine(pen, left + len, bottom, left, bottom);
                g.DrawLine(pen, left, bottom, left, bottom - len);
                // 中间小圆
                g.DrawEllipse(pen, rect.X + 8.6f * s, rect.Y + 8.6f * s, 4.8f * s, 4.8f * s);
            }
        }
    }

    internal static class SettingsWindowDrag
    {
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>从自绘标题栏开始拖动窗口。</summary>
        public static void BeginDrag(Control source)
        {
            if (source == null) return;
            try
            {
                Form form = source.FindForm();
                if (form == null) return;
                ReleaseCapture();
                SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Write("窗口拖动失败: " + ex.Message);
            }
        }
    }

    /// <summary>给无法改源码的控件（如 HotkeyBox）加圆角裁剪，使其与设置窗口输入框风格一致。</summary>
    internal static class SettingsRoundClip
    {
        public static void Apply(Control control, float radius)
        {
            if (control == null) return;
            control.Resize += delegate { Update(control, radius); };
            Update(control, radius);
        }

        private static void Update(Control control, float radius)
        {
            if (control == null || control.Width <= 0 || control.Height <= 0) return;
            try
            {
                using (GraphicsPath path = ImageUtil.RoundedRect(
                    new RectangleF(0, 0, control.Width, control.Height), radius * SettingsDpi.Scale(control)))
                {
                    Region old = control.Region;
                    control.Region = new Region(path);
                    if (old != null)
                    {
                        try { old.Dispose(); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}
