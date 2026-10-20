using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 热键录入控件：点击/聚焦后进入录制，按下组合键即可完成，例如 Alt+A、Ctrl+Shift+S、F1。
    /// 规则：
    ///   - 仅按下 Ctrl/Alt/Shift/Win 等修饰键不会结束录制；
    ///   - 主键要求至少带一个修饰键；F1~F24 允许单键；
    ///   - 普通字母/数字不带修饰键会被拒绝并继续等待；
    ///   - Esc 取消本次录制（保留原热键），Delete/Backspace 清空；
    ///   - 结果统一通过 HotkeyUtil.Format() 输出规范格式（如 "Ctrl+Alt+A"）。
    /// 负责队友：ai-ui
    /// </summary>
    public sealed class HotkeyBox : Control
    {
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_SYSCHAR = 0x0106;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private string _hotkeyText = "";
        private bool _recording;
        private bool _hover;
        private string _recordingHint = "";
        private int _hintTicks;
        private int _lastModifierState = -1;
        private Timer _modifierTimer;

        public HotkeyBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            BackColor = Color.White;
            Font = new Font(UiTheme.UiFontName, 9f);
            Height = 28;
            Cursor = Cursors.Hand;
            TabStop = true;
            DoubleBuffered = true;
        }

        /// <summary>规范化热键字符串，如 "Alt+A"。空字符串表示未设置。</summary>
        public string HotkeyText
        {
            get { return _hotkeyText; }
            set
            {
                string text = value ?? "";
                HotkeySpec spec;
                if (text.Length > 0 && HotkeyUtil.TryParse(text, out spec))
                    text = HotkeyUtil.Format(spec);
                if (_hotkeyText == text) return;
                _hotkeyText = text;
                Invalidate();
            }
        }

        /// <summary>用户成功录制到新热键（或清空）时触发。</summary>
        public event EventHandler HotkeyChanged;

        /// <summary>是否正在录制（供设置窗口判断按键提示等场景使用）。</summary>
        public bool IsRecording { get { return _recording; } }

        // ===================== 录制状态 =====================

        private void StartRecording()
        {
            if (_recording || !Enabled) return;
            _recording = true;
            _recordingHint = "";
            _hintTicks = 0;
            _lastModifierState = -1;
            if (_modifierTimer == null)
            {
                _modifierTimer = new Timer();
                _modifierTimer.Interval = 80;
                _modifierTimer.Tick += OnModifierTimerTick;
            }
            _modifierTimer.Start();
            Invalidate();
        }

        private void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            _recordingHint = "";
            _hintTicks = 0;
            if (_modifierTimer != null) _modifierTimer.Stop();
            Invalidate();
        }

        private void SetHotkeyFromUser(string text)
        {
            string value = text ?? "";
            if (_hotkeyText == value) return;
            _hotkeyText = value;
            Invalidate();
            EventHandler handler = HotkeyChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void ShowRejectHint(string hint)
        {
            _recordingHint = hint;
            _hintTicks = 14; // 约 1.1 秒
            Invalidate();
        }

        private void OnModifierTimerTick(object sender, EventArgs e)
        {
            if (!_recording)
            {
                if (_modifierTimer != null) _modifierTimer.Stop();
                return;
            }
            if (_hintTicks > 0)
            {
                _hintTicks--;
                if (_hintTicks == 0)
                {
                    _recordingHint = "";
                    Invalidate();
                }
            }
            int state = CurrentModifierState();
            if (state != _lastModifierState)
            {
                _lastModifierState = state;
                Invalidate();
            }
        }

        // ===================== 按键处理 =====================

        /// <summary>录制期间截获所有按键（包括 Tab/方向键/Alt 组合等）。</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!_recording) return base.ProcessCmdKey(ref msg, keyData);
            if (msg.Msg == WM_KEYDOWN || msg.Msg == WM_SYSKEYDOWN)
            {
                HandleRecordingKey(keyData);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (_recording) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_recording)
            {
                // 兜底：极少数情况下 ProcessCmdKey 未截获时仍能录制
                HandleRecordingKey(e.KeyData);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void WndProc(ref Message m)
        {
            // 录制时屏蔽 Alt 松开产生的系统菜单/提示音消息，避免“叮”声和菜单闪烁
            if (_recording && (m.Msg == WM_SYSKEYUP || m.Msg == WM_SYSCHAR))
                return;
            base.WndProc(ref m);
        }

        private void HandleRecordingKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.None) return;

            bool ctrl = (keyData & Keys.Control) != 0 || (Control.ModifierKeys & Keys.Control) != 0;
            bool alt = (keyData & Keys.Alt) != 0 || (Control.ModifierKeys & Keys.Alt) != 0;
            bool shift = (keyData & Keys.Shift) != 0 || (Control.ModifierKeys & Keys.Shift) != 0;
            bool win = IsWinKeyDown();

            if (key == Keys.Escape)
            {
                // 取消本次录制，保留原来的热键文本
                StopRecording();
                return;
            }
            if (key == Keys.Delete || key == Keys.Back)
            {
                SetHotkeyFromUser("");
                StopRecording();
                return;
            }
            if (IsModifierKey(key))
            {
                _recordingHint = "";
                Invalidate();
                return; // 仅按下修饰键不结束录制
            }

            if (!ctrl && !alt && !shift && !win)
            {
                bool functionKey = key >= Keys.F1 && key <= Keys.F24;
                if (!functionKey)
                {
                    ShowRejectHint("普通按键需配合 Ctrl / Alt / Shift；F1~F24 可单键");
                    return;
                }
            }

            HotkeySpec spec = new HotkeySpec();
            spec.Ctrl = ctrl;
            spec.Alt = alt;
            spec.Shift = shift;
            spec.Win = win;
            spec.Key = key;
            string text = HotkeyUtil.Format(spec);
            HotkeySpec check;
            if (string.IsNullOrEmpty(text) || !HotkeyUtil.TryParse(text, out check))
            {
                ShowRejectHint("该按键无法作为全局热键，请换一个组合");
                return;
            }
            SetHotkeyFromUser(text);
            StopRecording();
        }

        private static bool IsModifierKey(Keys key)
        {
            return key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu ||
                   key == Keys.LWin || key == Keys.RWin;
        }

        private static bool IsWinKeyDown()
        {
            try
            {
                return (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static int CurrentModifierState()
        {
            Keys mod = Control.ModifierKeys;
            int state = 0;
            if ((mod & Keys.Control) != 0) state |= 1;
            if ((mod & Keys.Alt) != 0) state |= 2;
            if ((mod & Keys.Shift) != 0) state |= 4;
            if (IsWinKeyDown()) state |= 8;
            return state;
        }

        private static string CurrentModifierPrefix()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if ((Control.ModifierKeys & Keys.Control) != 0) sb.Append("Ctrl+");
            if ((Control.ModifierKeys & Keys.Alt) != 0) sb.Append("Alt+");
            if ((Control.ModifierKeys & Keys.Shift) != 0) sb.Append("Shift+");
            if (IsWinKeyDown()) sb.Append("Win+");
            return sb.ToString();
        }

        // ===================== 焦点/鼠标 =====================

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            StartRecording();
        }

        protected override void OnLeave(EventArgs e)
        {
            StopRecording();
            base.OnLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (!Focused) Focus();
            if (!_recording) StartRecording();
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

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled) StopRecording();
            Invalidate();
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

        // ===================== 绘制 =====================

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            bool active = _recording || Focused;
            Color border = active ? UiTheme.Green : (_hover ? Color.FromArgb(170, 170, 170) : Color.FromArgb(204, 204, 204));
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (Pen pen = new Pen(border, active ? 2f : 1f))
            {
                float offset = active ? 1f : 0.5f;
                g.DrawRectangle(pen, offset, offset, Width - 1 - offset * 2, Height - 1 - offset * 2);
            }

            string text;
            Color color;
            if (!Enabled)
            {
                text = string.IsNullOrEmpty(_hotkeyText) ? "点击后按下组合键" : _hotkeyText;
                color = UiTheme.Disabled;
            }
            else if (_recording)
            {
                if (_recordingHint.Length > 0)
                {
                    text = _recordingHint;
                    color = UiTheme.ActionRed;
                }
                else
                {
                    string prefix = CurrentModifierPrefix();
                    text = prefix.Length > 0 ? prefix + "…" : "请按下组合键，Esc 取消";
                    color = UiTheme.ActionGreen;
                }
            }
            else if (string.IsNullOrEmpty(_hotkeyText))
            {
                text = "点击后按下组合键";
                color = UiTheme.TextMuted;
            }
            else
            {
                text = _hotkeyText;
                color = UiTheme.TextDark;
            }

            Rectangle textRect = new Rectangle(8, 1, Math.Max(1, Width - 16), Math.Max(1, Height - 2));
            TextRenderer.DrawText(g, text, Font, textRect, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopRecording();
                if (_modifierTimer != null)
                {
                    _modifierTimer.Tick -= OnModifierTimerTick;
                    _modifierTimer.Dispose();
                    _modifierTimer = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
