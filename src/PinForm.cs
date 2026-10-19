using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 钉在桌面的贴图窗口：拖动移动、滚轮缩放、右键菜单、双击/Esc 关闭。
    /// </summary>
    public sealed class PinForm : Form
    {
        private readonly Bitmap _image;
        private readonly bool _topMost;
        private float _zoom = 1f;
        private bool _dragging;
        private Point _dragStartScreen;
        private Point _startLocation;
        private ContextMenuStrip _menu;

        public PinForm(Bitmap image, Rectangle screenBounds, bool alwaysOnTop)
        {
            _image = image;
            _topMost = alwaysOnTop;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = alwaysOnTop;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = Color.FromArgb(24, 24, 24);
            Text = "钉在桌面";
            int maxW = Math.Max(120, DpiUtil.VirtualScreen.Width - 20);
            int maxH = Math.Max(120, DpiUtil.VirtualScreen.Height - 20);
            if (screenBounds.Width <= 0) screenBounds = new Rectangle(120, 120, Math.Min(_image.Width, maxW), Math.Min(_image.Height, maxH));
            _zoom = 1f;
            Size size = new Size(Math.Min(_image.Width, maxW), Math.Min(_image.Height, maxH));
            if (size.Width < _image.Width || size.Height < _image.Height)
                _zoom = Math.Min((float)size.Width / _image.Width, (float)size.Height / _image.Height);
            Bounds = new Rectangle(screenBounds.X, screenBounds.Y,
                Math.Max(20, (int)Math.Round(_image.Width * _zoom)),
                Math.Max(20, (int)Math.Round(_image.Height * _zoom)));
            BuildMenu();
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Font = new Font(UiTheme.UiFontName, 9f);
            ToolStripMenuItem copy = new ToolStripMenuItem("复制图片");
            copy.Click += delegate
            {
                if (ClipUtil.CopyImage(_image)) Toast.Show("已复制图片");
                else Toast.Show("复制失败，请重试");
            };
            _menu.Items.Add(copy);
            ToolStripMenuItem save = new ToolStripMenuItem("另存为…");
            save.Click += delegate { SaveAs(); };
            _menu.Items.Add(save);
            _menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem top = new ToolStripMenuItem("窗口置顶");
            top.Checked = _topMost;
            top.Click += delegate
            {
                TopMost = !TopMost;
                Native.SetWindowPos(Handle, TopMost ? Native.HWND_TOPMOST : IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                top.Checked = TopMost;
            };
            _menu.Items.Add(top);
            ToolStripMenuItem reset = new ToolStripMenuItem("恢复 100% 大小");
            reset.Click += delegate { SetZoomFromCenter(1f); };
            _menu.Items.Add(reset);
            _menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem close = new ToolStripMenuItem("关闭");
            close.Click += delegate { Close(); };
            _menu.Items.Add(close);
        }

        private void SaveAs()
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "保存钉图";
                dialog.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg";
                dialog.FileName = "钉图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                try { dialog.InitialDirectory = new AppConfig().EnsureSaveDir(); } catch { }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        string fmt = Path.GetExtension(dialog.FileName).ToLowerInvariant() == ".jpg" ? "jpg" : "png";
                        ImageUtil.Save(_image, dialog.FileName, fmt);
                        Toast.Show("已保存到 " + dialog.FileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "保存失败：" + ex.Message, "微信截图助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_image, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            using (Pen pen = new Pen(Color.FromArgb(160, 255, 255, 255)))
                g.DrawRectangle(pen, 0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragStartScreen = Cursor.Position;
                _startLocation = Location;
                Activate();
            }
            else if (e.Button == MouseButtons.Right)
            {
                _menu.Show(Cursor.Position);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            Point now = Cursor.Position;
            Location = new Point(_startLocation.X + now.X - _dragStartScreen.X, _startLocation.Y + now.Y - _dragStartScreen.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left) Close();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float factor = e.Delta > 0 ? 1.12f : 1f / 1.12f;
            float newZoom = Math.Max(0.15f, Math.Min(4f, _zoom * factor));
            SetZoom(newZoom, Cursor.Position);
        }

        private void SetZoomFromCenter(float zoom)
        {
            Point center = new Point(Left + Width / 2, Top + Height / 2);
            SetZoom(zoom, center);
        }

        private void SetZoom(float zoom, Point anchorScreen)
        {
            if (_image.Width <= 0 || _image.Height <= 0) return;
            float oldZoom = _zoom;
            if (oldZoom <= 0) oldZoom = 1f;
            float localX = (anchorScreen.X - Left) / oldZoom;
            float localY = (anchorScreen.Y - Top) / oldZoom;
            _zoom = Math.Max(0.1f, Math.Min(4f, zoom));
            int w = Math.Max(20, (int)Math.Round(_image.Width * _zoom));
            int h = Math.Max(20, (int)Math.Round(_image.Height * _zoom));
            int x = anchorScreen.X - (int)Math.Round(localX * _zoom);
            int y = anchorScreen.Y - (int)Math.Round(localY * _zoom);
            Bounds = new Rectangle(x, y, w, h);
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_menu != null) { _menu.Dispose(); _menu = null; }
            if (_image != null) { try { _image.Dispose(); } catch { } }
            base.OnFormClosed(e);
        }
    }

    /// <summary>
    /// 长截图结果窗口：预览（适应窗口）、复制、保存、钉在桌面、关闭。
    /// </summary>
    public sealed class LongShotResultForm : Form
    {
        private readonly Bitmap _image;
        private readonly Panel _bar;
        private float _previewScale = 1f;

        public LongShotResultForm(Bitmap image)
        {
            _image = image;
            Text = "长截图结果  " + image.Width + " × " + image.Height;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            BackColor = Color.FromArgb(245, 245, 245);
            DoubleBuffered = true;
            Font = new Font(UiTheme.UiFontName, 9f);
            Rectangle vs = DpiUtil.VirtualScreen;
            int barH = 52;
            int w = Math.Min(image.Width + 40, (int)(vs.Width * 0.9));
            int h = Math.Min(image.Height + barH + 40, (int)(vs.Height * 0.92));
            Size = new Size(Math.Max(480, w), Math.Max(260, h));
            MinimumSize = new Size(480, 220);

            _bar = new Panel();
            _bar.Dock = DockStyle.Top;
            _bar.Height = barH;
            _bar.BackColor = Color.White;
            _bar.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Color.FromArgb(226, 226, 226)))
                    e.Graphics.DrawLine(pen, 0, _bar.Height - 1, _bar.Width, _bar.Height - 1);
            };
            Controls.Add(_bar);

            Button copy = MakeButton("复制图片", 96);
            copy.Click += delegate
            {
                if (ClipUtil.CopyImage(_image)) Toast.Show("已复制长截图，可直接粘贴发送");
                else Toast.Show("复制失败，请重试");
            };
            Button save = MakeButton("保存…", 78);
            save.Click += delegate { SaveAs(); };
            Button pin = MakeButton("钉在桌面", 96);
            pin.Click += delegate
            {
                Bitmap clone = (Bitmap)_image.Clone();
                PinForm form = new PinForm(clone, new Rectangle(80, 80, _image.Width, _image.Height), true);
                form.Show();
            };
            Button close = MakeButton("关闭", 70);
            close.Click += delegate { Close(); };

            copy.Location = new Point(12, 10);
            save.Location = new Point(116, 10);
            pin.Location = new Point(202, 10);
            close.Location = new Point(306, 10);
            _bar.Controls.Add(copy);
            _bar.Controls.Add(save);
            _bar.Controls.Add(pin);
            _bar.Controls.Add(close);

            KeyPreview = true;
        }

        private Button MakeButton(string text, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 32;
            button.FlatStyle = FlatStyle.System;
            return button;
        }

        private void SaveAs()
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "保存长截图";
                dialog.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg";
                dialog.FileName = "长截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                string dir = AppConfig.DefaultSaveDir();
                try { Directory.CreateDirectory(dir); dialog.InitialDirectory = dir; } catch { }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        string fmt = Path.GetExtension(dialog.FileName).ToLowerInvariant() == ".jpg" ? "jpg" : "png";
                        ImageUtil.Save(_image, dialog.FileName, fmt);
                        Toast.Show("已保存到 " + dialog.FileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "保存失败：" + ex.Message, "微信截图助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Rectangle area = new Rectangle(0, _bar.Height, ClientSize.Width, ClientSize.Height - _bar.Height);
            if (area.Width <= 0 || area.Height <= 0) return;
            _previewScale = Math.Min(1f, Math.Min((float)area.Width / _image.Width, (float)area.Height / _image.Height));
            int w = Math.Max(1, (int)Math.Round(_image.Width * _previewScale));
            int h = Math.Max(1, (int)Math.Round(_image.Height * _previewScale));
            int x = area.X + (area.Width - w) / 2;
            int y = area.Y + (area.Height - h) / 2;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_image, new Rectangle(x, y, w, h));
            using (Pen pen = new Pen(Color.FromArgb(200, 200, 200)))
                g.DrawRectangle(pen, x, y, w, h);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_image != null) { try { _image.Dispose(); } catch { } }
            base.OnFormClosed(e);
        }
    }
}
