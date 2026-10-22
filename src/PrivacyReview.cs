using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace XiaoJie
{
    internal sealed class PrivacyCandidate
    {
        internal RectangleF Bounds;
        internal string Kind;
        internal bool Enabled = true;

        public override string ToString()
        {
            // 审阅列表只显示类别和位置，避免在截图外另行展示敏感原文。
            return PrivacyReviewSession.CategoryName(Kind) + " · " +
                (int)Math.Round(Bounds.X) + ", " + (int)Math.Round(Bounds.Y);
        }
    }

    /// <summary>Keeps review choices stable while local and network candidates arrive.</summary>
    internal sealed class PrivacyReviewSession
    {
        internal static readonly string[] Categories = { "phone", "identity", "email", "name", "address", "account", "secret", "visual", "other" };
        private readonly List<PrivacyCandidate> _candidates = new List<PrivacyCandidate>();
        private readonly HashSet<string> _disabledCategories = new HashSet<string>(StringComparer.Ordinal);

        internal int Count { get { return _candidates.Count; } }
        internal IList<PrivacyCandidate> Candidates { get { return _candidates.AsReadOnly(); } }

        internal void Merge(PrivacyAnalysis analysis, Rectangle selection)
        {
            if (analysis == null || selection.Width <= 0 || selection.Height <= 0) return;
            if (analysis.Lines != null)
            {
                foreach (ImageTextBlock line in analysis.Lines)
                {
                    if (line == null) continue;
                    string reason;
                    bool local = PrivacyDetector.LooksSensitive(line.Text, line.Kind, out reason);
                    if (!line.Sensitive && !local) continue;
                    var single = new PrivacyAnalysis { HasLocalCoordinates = analysis.HasLocalCoordinates };
                    single.Lines.Add(line);
                    int textCount, visualCount;
                    foreach (RectangleF rect in PrivacyDetector.BuildMaskRects(null, single, selection, .18f, out textCount, out visualCount))
                        Add(rect, NormalizeKind(line.Kind, reason));
                }
            }
            if (analysis.Faces != null)
            {
                var visual = new PrivacyAnalysis { HasLocalCoordinates = analysis.HasLocalCoordinates, Faces = analysis.Faces };
                int textCount, visualCount;
                foreach (RectangleF rect in PrivacyDetector.BuildMaskRects(null, visual, selection, .18f, out textCount, out visualCount))
                    Add(rect, "visual");
            }
        }

        private void Add(RectangleF rect, string kind)
        {
            if (_candidates.Exists(item => Math.Abs(item.Bounds.X - rect.X) < .5f && Math.Abs(item.Bounds.Y - rect.Y) < .5f &&
                Math.Abs(item.Bounds.Width - rect.Width) < .5f && Math.Abs(item.Bounds.Height - rect.Height) < .5f)) return;
            _candidates.Add(new PrivacyCandidate { Bounds = rect, Kind = kind });
        }

        internal void SetCandidateEnabled(int index, bool enabled)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException("index");
            _candidates[index].Enabled = enabled;
        }

        internal void SetCategoryEnabled(string kind, bool enabled)
        {
            if (Array.IndexOf(Categories, kind) < 0) throw new ArgumentException("Unknown privacy category.", "kind");
            if (enabled) _disabledCategories.Remove(kind);
            else _disabledCategories.Add(kind);
        }

        internal bool IsCategoryEnabled(string kind) { return !_disabledCategories.Contains(kind); }
        internal bool IsEffective(int index) { return _candidates[index].Enabled && IsCategoryEnabled(_candidates[index].Kind); }

        internal List<RectangleF> BuildRects()
        {
            var result = new List<RectangleF>();
            for (int index = 0; index < Count; index++) if (IsEffective(index)) result.Add(_candidates[index].Bounds);
            return result;
        }

        internal static string NormalizeKind(string kind, string reason)
        {
            string value = (kind ?? "").Trim().ToLowerInvariant();
            if (value == "phone") return "phone";
            if (value == "id" || value == "card" || value == "identity") return "identity";
            if (value == "email") return "email";
            if (value == "name") return "name";
            if (value == "address") return "address";
            if (value == "account" || value == "qq" || value == "wechat") return "account";
            if (value == "code" || value == "password" || value == "secret") return "secret";
            if (value == "visual") return "visual";
            switch (reason)
            {
                case "手机号": return "phone";
                case "身份证/银行卡": return "identity";
                case "邮箱": return "email";
                case "姓名": return "name";
                case "地址": return "address";
                case "账号": return "account";
                case "验证码": case "密钥或口令": return "secret";
                default: return "other";
            }
        }

        internal static string CategoryName(string kind)
        {
            switch (kind)
            {
                case "phone": return "手机号";
                case "identity": return "证件 / 银行卡";
                case "email": return "邮箱";
                case "name": return "姓名";
                case "address": return "地址";
                case "account": return "账号";
                case "secret": return "验证码 / 密钥";
                case "visual": return "头像 / 二维码";
                default: return "其他隐私";
            }
        }
    }

    internal sealed class PrivacyReviewPanel : Form
    {
        private readonly PrivacyReviewSession _session;
        private readonly CheckedListBox _categories;
        private readonly CheckedListBox _regions;
        private readonly Label _status;
        private readonly Panel _content;
        private bool _refreshing;
        private bool _layingOutContent;
        internal Action Changed;
        internal Action AddRegion;
        internal Action StopDetection;
        internal Action<int> Highlight;
        internal Action Undo;
        internal Action Redo;

        internal PrivacyReviewPanel(PrivacyReviewSession session)
        {
            _session = session;
            Text = "智能打码审阅";
            Name = "PrivacyReviewPanel";
            Font = UiTheme.Font(12f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(300, 496);
            BackColor = Color.White;
            ForeColor = UiTheme.TextDark;
            _status = new Label { Location = new Point(12, 10), Size = new Size(276, 40), Text = "勾选项已遮挡，可取消误判。" };
            Controls.Add(_status);
            Controls.Add(new Label { Location = new Point(12, 54), Size = new Size(276, 22), Text = "遮挡类别" });
            _categories = new CheckedListBox { Location = new Point(12, 78), Size = new Size(276, 148), BorderStyle = BorderStyle.FixedSingle,
                CheckOnClick = true, IntegralHeight = false, MultiColumn = true, ColumnWidth = 132 };
            _regions = new CheckedListBox { Location = new Point(12, 256), Size = new Size(276, 146), BorderStyle = BorderStyle.FixedSingle,
                CheckOnClick = true, IntegralHeight = false };
            Controls.Add(_categories);
            Controls.Add(new Label { Location = new Point(12, 231), Size = new Size(276, 22), Text = "候选区域（点击定位）" });
            Controls.Add(_regions);
            var add = new Button { Text = "补选区域", Location = new Point(12, 414), Size = new Size(132, 30) };
            var stop = new Button { Text = "停止联网补漏", Location = new Point(156, 414), Size = new Size(132, 30) };
            var done = new Button { Text = "完成审阅", Location = new Point(12, 452), Size = new Size(276, 32), BackColor = UiTheme.GreenLight };
            Controls.AddRange(new Control[] { add, stop, done });
            add.Click += delegate { if (AddRegion != null) AddRegion(); };
            stop.Click += delegate { if (StopDetection != null) StopDetection(); };
            done.Click += delegate { Close(); };
            _regions.SelectedIndexChanged += delegate { if (!_refreshing && Highlight != null) Highlight(_regions.SelectedIndex); };
            _categories.ItemCheck += delegate(object sender, ItemCheckEventArgs e)
            {
                if (_refreshing) return;
                _session.SetCategoryEnabled(PrivacyReviewSession.Categories[e.Index], e.NewValue == CheckState.Checked);
                if (Changed != null) Changed();
            };
            _regions.ItemCheck += delegate(object sender, ItemCheckEventArgs e)
            {
                if (_refreshing) return;
                _session.SetCandidateEnabled(e.Index, e.NewValue == CheckState.Checked);
                if (Changed != null) Changed();
            };
            _content = new Panel { Name = "ReviewContent", Dock = DockStyle.Fill, AutoScroll = true,
                AutoScrollMinSize = new Size(300, 496), BackColor = Color.White };
            var children = new Control[Controls.Count];
            Controls.CopyTo(children, 0);
            foreach (Control child in children) _content.Controls.Add(child);
            Controls.Add(_content);
            _content.Layout += delegate { UpdateContentExtent(); };
            UpdateContentExtent();
            RefreshCandidates();
        }

        private void UpdateContentExtent()
        {
            if (_layingOutContent || _content == null) return;
            _layingOutContent = true;
            try
            {
                Point offset = _content.AutoScrollPosition;
                int right = 0, bottom = 0;
                foreach (Control child in _content.Controls)
                {
                    right = Math.Max(right, child.Right - offset.X);
                    bottom = Math.Max(bottom, child.Bottom - offset.Y);
                }
                Size extent = new Size(right + 12, bottom + 12);
                if (_content.AutoScrollMinSize != extent) _content.AutoScrollMinSize = extent;
            }
            finally { _layingOutContent = false; }
        }

        internal void FitToWorkingArea(Rectangle working)
        {
            Size = new Size(Math.Min(Width, Math.Max(220, working.Width - 16)),
                Math.Min(Height, Math.Max(260, working.Height - 16)));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitToWorkingArea(Screen.FromControl(this).WorkingArea);
        }

        internal void RefreshCandidates()
        {
            _refreshing = true;
            try
            {
                int selected = _regions.SelectedIndex;
                _categories.Items.Clear();
                foreach (string kind in PrivacyReviewSession.Categories)
                    _categories.Items.Add(PrivacyReviewSession.CategoryName(kind), _session.IsCategoryEnabled(kind));
                _regions.Items.Clear();
                foreach (PrivacyCandidate candidate in _session.Candidates) _regions.Items.Add(candidate, candidate.Enabled);
                if (selected >= 0 && selected < _regions.Items.Count) _regions.SelectedIndex = selected;
                _status.Text = "已遮挡 " + _session.BuildRects().Count + " / " + _session.Count + " 处。\r\n类别开关会覆盖该类候选的勾选结果。";
            }
            finally { _refreshing = false; }
        }

        internal void RefreshStatus()
        {
            _status.Text = "已遮挡 " + _session.BuildRects().Count + " / " + _session.Count + " 处。\r\n勾选项已遮挡，可取消误判。";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape || keyData == Keys.Enter) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.Z) && Undo != null) { Undo(); return true; }
            if ((keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) && Redo != null) { Redo(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
