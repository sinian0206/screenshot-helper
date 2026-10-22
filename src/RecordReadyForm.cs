using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>录制准备窗口。所有参数只应用于本次会话，不写回全局配置。</summary>
    internal sealed class RecordReadyForm : Form
    {
        [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "NativeAudio_GetInputPeakLevel")]
        private static extern float NativeAudio_GetInputPeakLevel(int deviceIndex);

        private static readonly string[] AudioModes = { "none", "system", "microphone", "mixed" };
        private readonly AppConfig _config;
        private readonly ComboBox _audio;
        private readonly ComboBox _format;
        private readonly ProgressBar _levelBar;
        private readonly CheckBox _countdownCheck;
        private readonly Timer _meterTimer;
        private readonly Button _start;
        private readonly Button _cancel;
        private readonly Label _formatHint;
        private readonly Font _uiFont;
        private readonly Rectangle _region;
        private readonly float _scale;
        private RecorderBorder _border;
        private bool _updating;

        internal AppConfig SelectedConfig { get; private set; }

        internal RecordReadyForm(Rectangle region, AppConfig config)
            : this(region, config, DpiUtil.ScaleAtPoint(new Point(region.Left + region.Width / 2, region.Top + region.Height / 2))) { }

        internal RecordReadyForm(Rectangle region, AppConfig config, float scale)
        {
            _region = region;
            _config = (config ?? new AppConfig()).Clone();
            Point center = new Point(region.Left + region.Width / 2, region.Top + region.Height / 2);
            if (scale <= 0.5f || scale > 4f) scale = 1f;
            _scale = scale;
            _uiFont = UiTheme.Font(13f * scale);
            Font = _uiFont;
            AutoScaleMode = AutoScaleMode.None;
            Text = "录制准备与测试";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.White;
            ForeColor = UiTheme.TextDark;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(Px(430, scale), Px(270, scale));

            Label regionLabel = NewLabel(string.Format("选区 {0} × {1} 像素  ·  {2} fps",
                region.Width, region.Height, Math.Max(5, Math.Min(60, _config.RecordFps))),
                20, 14, 390, 24, scale);
            Controls.Add(regionLabel);

            Controls.Add(NewLabel("声音", 20, 48, 65, 26, scale));
            _audio = NewChoice(91, 46, 315, scale, new[] { "不录声音", "系统声音", "麦克风", "系统声音和麦克风" });
            _audio.AccessibleName = "本次录制声音";
            int audioIndex = Array.IndexOf(AudioModes, (_config.RecordAudioMode ?? "none").ToLowerInvariant());
            _audio.SelectedIndex = Math.Max(0, audioIndex);
            Controls.Add(_audio);

            Controls.Add(NewLabel("音量", 20, 84, 65, 22, scale));
            _levelBar = new ProgressBar
            {
                Bounds = new Rectangle(Px(91, scale), Px(84, scale), Px(315, scale), Px(14, scale)),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous
            };
            Controls.Add(_levelBar);

            Controls.Add(NewLabel("格式", 20, 114, 65, 26, scale));
            _format = NewChoice(91, 112, 315, scale, new[] { "MP4（H.264 / AAC）", "AVI（MJPEG，仅画面）" });
            _format.AccessibleName = "本次录制格式";
            _format.SelectedIndex = string.Equals(_config.RecordFormat, "avi", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            Controls.Add(_format);

            _countdownCheck = new CheckBox
            {
                Text = "开始前 3 秒倒计时",
                Bounds = new Rectangle(Px(91, scale), Px(148, scale), Px(200, scale), Px(24, scale)),
                Checked = true,
                BackColor = Color.White,
                ForeColor = UiTheme.TextDark
            };
            Controls.Add(_countdownCheck);

            _formatHint = NewLabel("参数仅用于本次录制；点击开始后才捕获画面和声音。", 20, 180, 390, 30, scale);
            _formatHint.ForeColor = UiTheme.TextGray;
            Controls.Add(_formatHint);

            _cancel = NewButton("重新选择", 184, 220, 104, 34, scale, false);
            _cancel.DialogResult = DialogResult.Cancel;
            _cancel.AccessibleName = "取消准备并重新选择录制范围";
            _start = NewButton("开始录制", 302, 220, 104, 34, scale, true);
            _start.AccessibleName = "开始录制";
            _start.Click += StartRecording;
            Controls.Add(_cancel);
            Controls.Add(_start);
            AcceptButton = _start;
            CancelButton = _cancel;
            _audio.SelectedIndexChanged += OptionsChanged;
            _format.SelectedIndexChanged += OptionsChanged;
            NormalizeFormat();

            _meterTimer = new Timer { Interval = 60 };
            _meterTimer.Tick += delegate
            {
                if (_audio.SelectedIndex == 2 || _audio.SelectedIndex == 3) // 麦克风或混合
                {
                    try
                    {
                        float level = NativeAudio_GetInputPeakLevel(-1);
                        int val = Math.Max(0, Math.Min(100, (int)(level * 100f)));
                        _levelBar.Value = val;
                    }
                    catch { _levelBar.Value = 0; }
                }
                else
                {
                    _levelBar.Value = 0;
                }
            };
            _meterTimer.Start();

            Rectangle outside = DisplayGeometry.PlaceFloating(region, Size, DisplayGeometry.WorkingAreas(), Px(12, scale), true);
            if (!outside.IsEmpty) Location = outside.Location;
            else
            {
                Rectangle work = Screen.FromPoint(center).WorkingArea;
                Location = new Point(Math.Max(work.Left, Math.Min(work.Right - Width, center.X - Width / 2)),
                    Math.Max(work.Top, Math.Min(work.Bottom - Height, center.Y - Height / 2)));
            }
        }

        private static int Px(int value, float scale) { return (int)Math.Round(value * scale); }

        private static Label NewLabel(string text, int x, int y, int width, int height, float scale)
        {
            return new Label { Text = text, Bounds = new Rectangle(Px(x, scale), Px(y, scale), Px(width, scale), Px(height, scale)),
                ForeColor = UiTheme.TextDark, BackColor = Color.White, UseMnemonic = false };
        }

        private static ComboBox NewChoice(int x, int y, int width, float scale, string[] choices)
        {
            ComboBox combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList,
                Bounds = new Rectangle(Px(x, scale), Px(y, scale), Px(width, scale), Px(30, scale)),
                ForeColor = UiTheme.TextDark, BackColor = Color.White, IntegralHeight = true };
            combo.Items.AddRange(choices);
            return combo;
        }

        private static Button NewButton(string text, int x, int y, int width, int height, float scale, bool primary)
        {
            Button button = new Button { Text = text, FlatStyle = FlatStyle.Flat,
                Bounds = new Rectangle(Px(x, scale), Px(y, scale), Px(width, scale), Px(height, scale)),
                ForeColor = primary ? Color.White : UiTheme.TextDark,
                BackColor = primary ? UiTheme.ActionGreen : Color.White,
                UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = primary ? UiTheme.ActionGreen : UiTheme.TextMuted;
            button.FlatAppearance.MouseOverBackColor = primary ? UiTheme.ActionGreenHover : UiTheme.HoverBg;
            button.FlatAppearance.MouseDownBackColor = primary ? UiTheme.ActionGreenPressed : UiTheme.DisabledBackground;
            return button;
        }

        private void OptionsChanged(object sender, EventArgs e) { NormalizeFormat(); }

        private void NormalizeFormat()
        {
            if (_updating) return;
            _updating = true;
            try
            {
                if (_audio.SelectedIndex > 0 && _format.SelectedIndex == 1)
                {
                    _format.SelectedIndex = 0;
                    _formatHint.Text = "AVI 不支持音频，已切换为 MP4；声音选项已保留。";
                }
                else
                    _formatHint.Text = "参数仅用于本次录制；点击开始后才捕获画面和声音。";
            }
            finally { _updating = false; }
        }

        private void StartRecording(object sender, EventArgs e)
        {
            NormalizeFormat();
            SelectedConfig = _config.Clone();
            SelectedConfig.RecordAudioMode = AudioModes[Math.Max(0, Math.Min(AudioModes.Length - 1, _audio.SelectedIndex))];
            SelectedConfig.RecordFormat = _format.SelectedIndex == 1 ? "avi" : "mp4";

            _meterTimer.Stop();

            if (_countdownCheck.Checked)
            {
                using (var cd = new RecordingCountdownForm(_region, 3, _scale))
                {
                    if (cd.ShowDialog(this) != DialogResult.OK)
                    {
                        _meterTimer.Start();
                        return;
                    }
                }
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            int thickness = Math.Max(2, Px(2, _scale));
            _border = new RecorderBorder(Rectangle.Inflate(_region, thickness, thickness), _region, thickness);
            _border.Show(this);
            base.OnShown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_border != null) { _border.Dispose(); _border = null; }
                if (_meterTimer != null) _meterTimer.Dispose();
                if (_uiFont != null) _uiFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
