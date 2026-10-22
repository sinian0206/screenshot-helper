using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>Owns imported image copies and transfers the completed composition on request.</summary>
    internal sealed class ImageComposerForm : Form
    {
        private sealed class Entry
        {
            internal Bitmap Image;
            internal string Name;
            internal string SourcePath;
            public override string ToString() { return Name + "  (" + Image.Width + " × " + Image.Height + ")"; }
        }

        private readonly ListBox _list;
        private readonly PictureBox _preview;
        private readonly ComboBox _direction;
        private readonly ComboBox _background;
        private readonly NumericUpDown _spacing;
        private readonly Label _status;
        private readonly Button _edit;
        private readonly Button _copy;
        private readonly Button _save;
        private Bitmap _result;

        internal ImageComposerForm()
        {
            Text = "拼图工作台";
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = UiTheme.Font(14);
            BackColor = SettingsColors.PageBg;
            ForeColor = UiTheme.TextDark;
            ClientSize = new Size(920, 620);
            MinimumSize = new Size(720, 480);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(16) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            Controls.Add(layout);

            var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
            _direction = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, AccessibleName = "排列方向" };
            _direction.Items.AddRange(new object[] { "纵向排列", "横向排列" }); _direction.SelectedIndex = 0;
            _spacing = new NumericUpDown { Minimum = 0, Maximum = 200, Value = 12, Width = 65, AccessibleName = "图片间距" };
            _background = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, AccessibleName = "背景颜色" };
            _background.Items.AddRange(new object[] { "白色背景", "深色背景", "透明背景" }); _background.SelectedIndex = 0;
            settings.Controls.Add(_direction);
            settings.Controls.Add(new Label { Text = "间距", AutoSize = true, Padding = new Padding(4, 6, 0, 0) });
            settings.Controls.Add(_spacing); settings.Controls.Add(_background);
            layout.Controls.Add(settings, 0, 0); layout.SetColumnSpan(settings, 2);

            var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var imports = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            imports.Controls.Add(MakeButton("添加图片", AddFiles));
            imports.Controls.Add(MakeButton("粘贴图片", PasteImage));
            sidebar.Controls.Add(imports, 0, 0);
            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, ForeColor = UiTheme.TextDark, BackColor = Color.White, AccessibleName = "图片顺序" };
            sidebar.Controls.Add(_list, 0, 1);
            var order = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            order.Controls.Add(MakeButton("上移", () => MoveItem(-1), 67));
            order.Controls.Add(MakeButton("下移", () => MoveItem(1), 67));
            order.Controls.Add(MakeButton("移除", RemoveImage, 67));
            sidebar.Controls.Add(order, 0, 2);
            layout.Controls.Add(sidebar, 0, 1);
            _preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(243, 245, 247), BorderStyle = BorderStyle.FixedSingle, AccessibleName = "拼图预览" };
            layout.Controls.Add(_preview, 1, 1);

            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoScroll = true };
            _edit = MakeButton("继续编辑", () => { if (_result != null) { DialogResult = DialogResult.OK; Close(); } }, 100, true);
            _save = MakeButton("保存 PNG", SaveImage, 100);
            _copy = MakeButton("复制图片", CopyImage, 100);
            footer.Controls.Add(_edit); footer.Controls.Add(_save); footer.Controls.Add(_copy);
            _status = new Label { Text = "添加图片后显示预览", AutoSize = true, Padding = new Padding(8, 10, 0, 0), ForeColor = UiTheme.TextGray, BackColor = Color.Transparent };
            footer.Controls.Add(_status);
            layout.Controls.Add(footer, 0, 2); layout.SetColumnSpan(footer, 2);
            _direction.SelectedIndexChanged += (sender, args) => RefreshComposition();
            _background.SelectedIndexChanged += (sender, args) => RefreshComposition();
            _spacing.ValueChanged += (sender, args) => RefreshComposition();
            UpdateEnabled();
            AllowDrop = true;
            DragEnter += (sender, args) => { if (args.Data.GetDataPresent(DataFormats.FileDrop)) args.Effect = DragDropEffects.Copy; };
            DragDrop += (sender, args) => AddPaths((string[])args.Data.GetData(DataFormats.FileDrop));
        }

        internal Bitmap TakeResult()
        {
            Bitmap result = _result; _result = null; _preview.Image = null; return result;
        }

        internal void AddPaths(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                Bitmap image = null;
                try { image = ImageImport.LoadFile(path); AddImage(image, Path.GetFileName(path), Path.GetFullPath(path)); image = null; }
                catch (Exception error) { MessageBox.Show(this, Path.GetFileName(path) + "：" + error.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information); }
                finally { if (image != null) image.Dispose(); }
            }
        }

        private Button MakeButton(string text, Action action, int width = 108, bool primary = false)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = 34,
                AutoEllipsis = true,
                FlatStyle = FlatStyle.Flat,
                ForeColor = primary ? UiTheme.SignalText : UiTheme.TextDark,
                BackColor = primary ? UiTheme.Signal : Color.White,
                Font = UiTheme.Font(10.5f, primary ? FontStyle.Bold : FontStyle.Regular),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = primary ? UiTheme.Signal : SettingsColors.InputBorder;
            button.FlatAppearance.MouseOverBackColor = primary ? UiTheme.SignalHover : SettingsColors.HoverBg;
            button.Click += (sender, args) => action();
            return button;
        }

        private void AddFiles()
        {
            using (var dialog = new OpenFileDialog { Title = "添加图片", Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*", Multiselect = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(dialog.FileNames);
        }

        private void PasteImage()
        {
            Bitmap image = null;
            try { image = ImageImport.FromClipboard(); AddImage(image, "剪贴板图片", null); image = null; }
            catch (Exception error) { MessageBox.Show(this, error.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information); }
            finally { if (image != null) image.Dispose(); }
        }

        private void AddImage(Bitmap image, string name, string path)
        {
            long pixels = (long)image.Width * image.Height;
            foreach (Entry entry in _list.Items) pixels += (long)entry.Image.Width * entry.Image.Height;
            if (pixels > ImageImport.MaximumPixels || _list.Items.Count >= 32) throw new InvalidDataException("图片总量超过限制（最多 32 张、4000 万像素）。");
            _list.Items.Add(new Entry { Image = image, Name = name, SourcePath = path });
            _list.SelectedIndex = _list.Items.Count - 1; RefreshComposition();
        }

        private void MoveItem(int direction)
        {
            int index = _list.SelectedIndex; int target = index + direction;
            if (index < 0 || target < 0 || target >= _list.Items.Count) return;
            object item = _list.Items[index]; _list.Items.RemoveAt(index); _list.Items.Insert(target, item); _list.SelectedIndex = target; RefreshComposition();
        }

        private void RemoveImage()
        {
            int index = _list.SelectedIndex; if (index < 0) return;
            Entry entry = (Entry)_list.Items[index]; _list.Items.RemoveAt(index); entry.Image.Dispose();
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1); RefreshComposition();
        }

        private void RefreshComposition()
        {
            _preview.Image = null; if (_result != null) { _result.Dispose(); _result = null; }
            try
            {
                if (_list.Items.Count == 0) { _status.Text = "添加图片后显示预览"; return; }
                var images = new List<Bitmap>(); foreach (Entry entry in _list.Items) images.Add(entry.Image);
                Color background = _background.SelectedIndex == 2 ? Color.Transparent : _background.SelectedIndex == 1 ? UiTheme.DarkBg : Color.White;
                _result = ImageComposer.Compose(images, _direction.SelectedIndex == 0, (int)_spacing.Value, background);
                _preview.Image = _result; _status.Text = _result.Width + " × " + _result.Height + " px";
            }
            catch (Exception error) { _status.Text = error.Message; }
            finally { UpdateEnabled(); }
        }

        private void UpdateEnabled() { _edit.Enabled = _copy.Enabled = _save.Enabled = _result != null; }

        private void CopyImage()
        {
            if (_result == null) return;
            try { Clipboard.SetImage(_result); _status.Text = "图片已复制"; }
            catch (Exception error) { _status.Text = "复制失败：" + error.Message; }
        }

        private void SaveImage()
        {
            if (_result == null) return;
            using (var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "拼图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png", AddExtension = true, DefaultExt = "png" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (Entry entry in _list.Items)
                    if (entry.SourcePath != null && string.Equals(Path.GetFullPath(dialog.FileName), entry.SourcePath, StringComparison.OrdinalIgnoreCase))
                    { MessageBox.Show(this, "请选择其他文件名，以保留导入的原图。", AppInfo.Name); return; }
                try { _result.Save(dialog.FileName, ImageFormat.Png); _status.Text = "图片已保存"; }
                catch (Exception error) { _status.Text = "保存失败：" + error.Message; }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _preview.Image = null;
                if (_result != null) { _result.Dispose(); _result = null; }
                foreach (Entry entry in _list.Items) entry.Image.Dispose(); _list.Items.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
