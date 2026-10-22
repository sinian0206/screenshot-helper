using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
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
        private bool _resizing;
        private Point _dragStartScreen;
        private Point _startLocation;
        private float _resizeStartZoom;
        private ContextMenuStrip _menu;
        private bool _closeHover;
        private float _scale;
        private bool _rescaling;
        private Bitmap _zoomedImage;
        private Rectangle _zoomedImageBounds;
        private bool _zoomCacheUnavailable;

        public PinForm(Bitmap image, Rectangle screenBounds, bool alwaysOnTop)
        {
            if (image == null) throw new ArgumentNullException("image");
            _image = image;
            _topMost = alwaysOnTop;
            Point target = screenBounds.IsEmpty ? Cursor.Position :
                new Point(screenBounds.X + screenBounds.Width / 2, screenBounds.Y + screenBounds.Height / 2);
            _scale = DpiUtil.ScaleAtPoint(target);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = alwaysOnTop;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = Color.FromArgb(24, 24, 24);
            Text = "钉在桌面";
            AccessibleName = "钉图";
            AccessibleDescription = "拖动移动，滚轮缩放，右上角关闭或按 Esc 关闭";
            SetStyle(ControlStyles.ResizeRedraw, true);
            Rectangle workingArea = Screen.FromPoint(target).WorkingArea;
            int maxW = Math.Max(120, workingArea.Width - 20 - BorderWidth * 2);
            int maxH = Math.Max(120, workingArea.Height - 20 - HeaderHeight - BorderWidth * 2);
            if (screenBounds.Width <= 0) screenBounds = new Rectangle(120, 120, Math.Min(_image.Width, maxW), Math.Min(_image.Height, maxH));
            _zoom = 1f;
            Size size = new Size(Math.Min(_image.Width, maxW), Math.Min(_image.Height, maxH));
            if (size.Width < _image.Width || size.Height < _image.Height)
                _zoom = Math.Min((float)size.Width / _image.Width, (float)size.Height / _image.Height);
            ClientSize = SizeForZoom(_zoom);
            Location = new Point(screenBounds.X - ImageBounds().X, screenBounds.Y - ImageBounds().Y);
            KeepCloseButtonVisible();
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
            ToolStripMenuItem scaleMenu = new ToolStripMenuItem("缩放比例");
            foreach (int percent in new[] { 50, 75, 100, 125, 150, 200 })
            {
                int selected = percent;
                ToolStripMenuItem item = new ToolStripMenuItem(selected + "%");
                item.Click += delegate { SetZoomFromCenter(selected / 100f); };
                scaleMenu.DropDownItems.Add(item);
            }
            _menu.Items.Add(scaleMenu);
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
                        MessageBox.Show(this, "保存失败：" + ex.Message, "截图助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Rectangle imageBounds = ImageBounds();
            if (_zoom == 1f) g.DrawImageUnscaled(_image, imageBounds.Location);
            else
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                DrawZoomedImage(g, imageBounds);
            }
            using (Pen outline = new Pen(Color.FromArgb(20, 20, 20)))
                g.DrawRectangle(outline, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (Pen border = new Pen(UiTheme.Green, Math.Max(1f, _scale)))
                g.DrawRectangle(border, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);

            // Keep controls outside the image so copying and inspecting use the same content.
            Rectangle closeRect = CloseButtonRect();
            Rectangle titleRect = new Rectangle(BorderWidth + 8, BorderWidth,
                Math.Max(1, closeRect.Left - BorderWidth - 12), HeaderHeight);
            using (Font font = new Font(UiTheme.UiFontName, 11f * _scale, FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "钉图 · " + Math.Round(_zoom * 100) + "%", font, titleRect, Color.White,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(closeRect.X, closeRect.Y, closeRect.Width, closeRect.Height), closeRect.Width / 2f))
            using (SolidBrush bg = new SolidBrush(_closeHover ? UiTheme.ActionRed : Color.FromArgb(64, 64, 64)))
                g.FillPath(bg, path);
            using (Pen pen = new Pen(Color.White, Math.Max(1.6f, 2f * _scale)))
            {
                float px = closeRect.X + closeRect.Width * 0.3f;
                float py = closeRect.Y + closeRect.Height * 0.3f;
                float ex = closeRect.Right - closeRect.Width * 0.3f;
                float ey = closeRect.Bottom - closeRect.Height * 0.3f;
                g.DrawLine(pen, px, py, ex, ey);
                g.DrawLine(pen, ex, py, px, ey);
            }
            DrawResizeGrip(g);
        }

        private Rectangle ResizeGripRect()
        {
            int size = Math.Max(14, (int)Math.Round(18 * _scale));
            return new Rectangle(ClientSize.Width - size - BorderWidth, ClientSize.Height - size - BorderWidth, size, size);
        }

        private void DrawResizeGrip(Graphics graphics)
        {
            Rectangle grip = ResizeGripRect();
            using (Pen pen = new Pen(Color.FromArgb(220, 255, 255, 255), Math.Max(1f, _scale)))
            {
                for (int i = 0; i < 3; i++)
                {
                    int inset = (int)Math.Round((i + 1) * 4 * _scale);
                    graphics.DrawLine(pen, grip.Right - inset, grip.Bottom - 2,
                        grip.Right - 2, grip.Bottom - inset);
                }
            }
        }

        private Rectangle CloseButtonRect()
        {
            int size = (int)Math.Round(22 * _scale);
            return new Rectangle(ClientSize.Width - size - BorderWidth - (int)Math.Round(4 * _scale),
                BorderWidth + (HeaderHeight - size) / 2, size, size);
        }

        private int BorderWidth { get { return Math.Max(2, (int)Math.Round(2 * _scale)); } }
        private int HeaderHeight { get { return Math.Max(28, (int)Math.Round(30 * _scale)); } }
        private int GripFooterHeight { get { return Math.Max(18, (int)Math.Round(20 * _scale)); } }

        private Size SizeForZoom(float zoom)
        {
            return new Size(Math.Max((int)Math.Round(120 * _scale), (int)Math.Round(_image.Width * zoom)) + BorderWidth * 2,
                Math.Max(1, (int)Math.Round(_image.Height * zoom)) + HeaderHeight + GripFooterHeight + BorderWidth * 2);
        }

        private Rectangle ImageBounds()
        {
            int width = Math.Max(1, (int)Math.Round(_image.Width * _zoom));
            int height = Math.Max(1, (int)Math.Round(_image.Height * _zoom));
            return new Rectangle((ClientSize.Width - width) / 2, BorderWidth + HeaderHeight, width, height);
        }

        private void KeepCloseButtonVisible()
        {
            Rectangle workArea = Screen.FromRectangle(Bounds).WorkingArea;
            Rectangle close = CloseButtonRect();
            int margin = Math.Max(4, BorderWidth);
            int x = Math.Max(workArea.Left + margin - close.Left,
                Math.Min(workArea.Right - margin - close.Right, Left));
            int y = Math.Max(workArea.Top + margin,
                Math.Min(workArea.Bottom - margin - HeaderHeight - BorderWidth, Top));
            Location = new Point(x, y);
        }

        private void UpdateMonitorScale(float monitorScale)
        {
            if (_rescaling || _image == null || monitorScale < 0.75f || monitorScale > 5f ||
                Math.Abs(monitorScale - _scale) < 0.01f) return;
            _rescaling = true;
            try
            {
                Rectangle before = ImageBounds();
                Point imageOrigin = new Point(Left + before.X, Top + before.Y);
                Point oldLocation = Location;
                // DPI 只改变边框和关闭控件；钉图的 zoom 和像素尺寸保持一致。
                _scale = monitorScale;
                ClientSize = SizeForZoom(_zoom);
                Rectangle after = ImageBounds();
                Location = new Point(imageOrigin.X - after.X, imageOrigin.Y - after.Y);
                KeepCloseButtonVisible();
                if (_dragging) _startLocation.Offset(Left - oldLocation.X, Top - oldLocation.Y);
                Invalidate();
            }
            finally { _rescaling = false; }
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (_image != null && !_rescaling && IsHandleCreated)
                UpdateMonitorScale(DpiUtil.ScaleAtPoint(new Point(Left + Width / 2, Top + HeaderHeight / 2)));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0)
            {
                // 不接受系统按 DPI 放大的截图；图片位置由物理像素契约维持。
                UpdateMonitorScale((m.WParam.ToInt64() & 0xffff) / 96f);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && CloseButtonRect().Contains(e.Location))
            {
                Close();
                return;
            }
            if (e.Button == MouseButtons.Left && ResizeGripRect().Contains(e.Location))
            {
                _resizing = true;
                Capture = true;
                _dragStartScreen = Cursor.Position;
                _resizeStartZoom = _zoom;
                Activate();
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                Capture = true;
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
            if (_resizing)
            {
                Point resizeNow = Cursor.Position;
                int dx = resizeNow.X - _dragStartScreen.X;
                int dy = resizeNow.Y - _dragStartScreen.Y;
                Rectangle image = ImageBounds();
                float widthZoom = (_image.Width <= 0) ? _resizeStartZoom : (image.Width + dx) / (float)_image.Width;
                float heightZoom = (_image.Height <= 0) ? _resizeStartZoom : (image.Height + dy) / (float)_image.Height;
                float zoom = Math.Max(0.1f, Math.Min(4f, Math.Max(widthZoom, heightZoom)));
                SetZoom(zoom, new Point(Left + image.Left, Top + image.Top));
                Cursor = Cursors.SizeNWSE;
                return;
            }
            bool hover = CloseButtonRect().Contains(e.Location);
            if (hover != _closeHover)
            {
                _closeHover = hover;
                Cursor = hover ? Cursors.Hand : Cursors.Default;
                Invalidate(CloseButtonRect());
            }
            if (ResizeGripRect().Contains(e.Location)) Cursor = Cursors.SizeNWSE;
            if (!_dragging) return;
            Point now = Cursor.Position;
            Location = new Point(_startLocation.X + now.X - _dragStartScreen.X, _startLocation.Y + now.Y - _dragStartScreen.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            _resizing = false;
            Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) _dragging = false;
            if (!Capture) _resizing = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_closeHover) { _closeHover = false; Cursor = Cursors.Default; Invalidate(CloseButtonRect()); }
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
            Rectangle oldImage = ImageBounds();
            float localX = (anchorScreen.X - Left - oldImage.X) / oldZoom;
            float localY = (anchorScreen.Y - Top - oldImage.Y) / oldZoom;
            _zoom = Math.Max(0.1f, Math.Min(4f, zoom));
            if (_zoom != oldZoom) DisposeZoomedImage();
            ClientSize = SizeForZoom(_zoom);
            Rectangle newImage = ImageBounds();
            Location = new Point(anchorScreen.X - newImage.X - (int)Math.Round(localX * _zoom),
                anchorScreen.Y - newImage.Y - (int)Math.Round(localY * _zoom));
            KeepCloseButtonVisible();
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void DrawZoomedImage(Graphics graphics, Rectangle imageBounds)
        {
            if (_zoomCacheUnavailable)
            {
                graphics.DrawImage(_image, imageBounds);
                return;
            }
            if (_zoomedImage == null || _zoomedImage.Size != ClientSize || _zoomedImageBounds != imageBounds)
            {
                DisposeZoomedImage();
                // The source image is immutable. Preserve the same interpolation,
                // but reuse its result during moves, hover, and expose repainting.
                Bitmap scaled = null;
                try
                {
                    scaled = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics cache = Graphics.FromImage(scaled))
                    {
                        cache.Clear(BackColor);
                        cache.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        cache.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        cache.DrawImage(_image, imageBounds);
                    }
                    _zoomedImage = scaled;
                    _zoomedImageBounds = imageBounds;
                }
                catch (OutOfMemoryException)
                {
                    if (scaled != null) scaled.Dispose();
                    _zoomCacheUnavailable = true;
                }
                catch (ExternalException)
                {
                    if (scaled != null) scaled.Dispose();
                    _zoomCacheUnavailable = true;
                }
                catch
                {
                    if (scaled != null) scaled.Dispose();
                    throw;
                }
            }
            if (_zoomedImage != null) graphics.DrawImageUnscaled(_zoomedImage, Point.Empty);
            else graphics.DrawImage(_image, imageBounds);
        }

        private void DisposeZoomedImage()
        {
            _zoomCacheUnavailable = false;
            if (_zoomedImage == null) return;
            _zoomedImage.Dispose();
            _zoomedImage = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeZoomedImage();
                if (_menu != null) { _menu.Font.Dispose(); _menu.Dispose(); _menu = null; }
                if (_image != null) _image.Dispose();
            }
            base.Dispose(disposing);
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
                        MessageBox.Show(this, "保存失败：" + ex.Message, "截图助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
