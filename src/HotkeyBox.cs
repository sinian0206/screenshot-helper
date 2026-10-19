using System;
using System.Drawing;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 热键录入控件：点击后按下组合键即可录制，例如 Alt+A、Ctrl+Shift+S、F1。
    /// HotkeyText 使用 HotkeyUtil.Format() 的规范格式（"Ctrl+Alt+A"）。
    /// 负责队友：ai-ui
    /// </summary>
    public sealed class HotkeyBox : Control
    {
        private string _hotkeyText = "";

        public HotkeyBox()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            Height = 28;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        /// <summary>规范化热键字符串，如 "Alt+A"。</summary>
        public string HotkeyText
        {
            get { return _hotkeyText; }
            set
            {
                _hotkeyText = value ?? "";
                Invalidate();
            }
        }

        public event EventHandler HotkeyChanged;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(BackColor);
            using (Pen pen = new Pen(Color.FromArgb(200, 200, 200)))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            string text = string.IsNullOrEmpty(_hotkeyText) ? "点击后按下组合键" : _hotkeyText;
            TextRenderer.DrawText(e.Graphics, text, Font,
                new Rectangle(8, 0, Width - 16, Height),
                string.IsNullOrEmpty(_hotkeyText) ? Color.FromArgb(150, 150, 150) : Color.FromArgb(40, 40, 40),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
