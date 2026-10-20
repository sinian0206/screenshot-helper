using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>倒计时不持有媒体资源；只有自然归零才允许创建录制会话。</summary>
    internal sealed class RecordingCountdownForm : Form
    {
        private readonly int _seconds;
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Timer _timer;
        private readonly Label _remaining;
        private readonly Font _font;
        internal bool Completed { get; private set; }

        internal RecordingCountdownForm(Rectangle region, int seconds, float scale)
        {
            _seconds = Math.Max(1, Math.Min(10, seconds));
            scale = Math.Max(0.75f, Math.Min(4f, scale));
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size((int)(240 * scale), (int)(110 * scale));
            Text = "录制倒计时"; ShowInTaskbar = false; TopMost = true;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            BackColor = Color.White; ForeColor = UiTheme.TextDark;
            _font = UiTheme.Font(16 * scale);
            Font = _font;
            _remaining = new Label { TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top,
                Height = (int)(65 * scale), ForeColor = UiTheme.TextDark, BackColor = Color.White };
            Controls.Add(_remaining);
            Button cancel = new Button { Text = "取消", Bounds = new Rectangle((int)(80 * scale), (int)(70 * scale), (int)(80 * scale), (int)(30 * scale)),
                DialogResult = DialogResult.Cancel, BackColor = Color.White, ForeColor = UiTheme.TextDark, FlatStyle = FlatStyle.Flat };
            Controls.Add(cancel); CancelButton = cancel;
            StartPosition = FormStartPosition.Manual;
            Rectangle work = Screen.FromPoint(new Point(region.Left + region.Width / 2, region.Top + region.Height / 2)).WorkingArea;
            Location = new Point(Math.Max(work.Left, Math.Min(work.Right - Width, region.Left + region.Width / 2 - Width / 2)),
                Math.Max(work.Top, Math.Min(work.Bottom - Height, region.Top + region.Height / 2 - Height / 2)));
            _timer = new Timer { Interval = 50 };
            _timer.Tick += TickCountdown;
            UpdateRemaining();
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); _clock.Start(); _timer.Start(); }
        private void TickCountdown(object sender, EventArgs e)
        {
            if (_clock.Elapsed.TotalSeconds >= _seconds)
            {
                _timer.Stop(); Completed = true; DialogResult = DialogResult.OK; Close();
            }
            else UpdateRemaining();
        }
        private void UpdateRemaining() { _remaining.Text = "将在 " + Math.Max(1, _seconds - (int)_clock.Elapsed.TotalSeconds) + " 秒后开始录制"; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _font.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
