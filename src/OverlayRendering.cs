using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace XiaoJie
{
    public sealed partial class OverlayForm
    {
        private Bitmap _maskedScreenshot;
        private bool _maskCacheUnavailable;

        private void DrawCaptureBackground(Graphics graphics)
        {
            if (_maskedScreenshot == null && !_maskCacheUnavailable)
            {
                // The frozen image and mask are immutable throughout this session.
                // Blend once rather than compositing the entire desktop on every move.
                Bitmap masked = null;
                try
                {
                    masked = new Bitmap(_screenshot.Width, _screenshot.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics background = Graphics.FromImage(masked))
                    using (Brush mask = new SolidBrush(UiTheme.Mask))
                    {
                        background.InterpolationMode = InterpolationMode.NearestNeighbor;
                        background.PixelOffsetMode = PixelOffsetMode.Half;
                        background.DrawImageUnscaled(_screenshot, 0, 0);
                        background.FillRectangle(mask, 0, 0, masked.Width, masked.Height);
                    }
                    _maskedScreenshot = masked;
                }
                catch (OutOfMemoryException)
                {
                    if (masked != null) masked.Dispose();
                    _maskCacheUnavailable = true;
                }
                catch (ExternalException)
                {
                    if (masked != null) masked.Dispose();
                    _maskCacheUnavailable = true;
                }
                catch
                {
                    if (masked != null) masked.Dispose();
                    throw;
                }
            }
            if (_maskedScreenshot != null) graphics.DrawImageUnscaled(_maskedScreenshot, 0, 0);
            else
            {
                // Caching is optional when a large virtual desktop exhausts GDI memory.
                graphics.DrawImageUnscaled(_screenshot, 0, 0);
                using (Brush mask = new SolidBrush(UiTheme.Mask)) graphics.FillRectangle(mask, ClientRectangle);
            }
        }

        private void DrawUnmaskedSelection(Graphics graphics, Rectangle selection)
        {
            Rectangle visible = Rectangle.Intersect(selection, ClientRectangle);
            if (visible.Width <= 0 || visible.Height <= 0) return;
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SetClip(visible, CombineMode.Intersect);
                graphics.DrawImageUnscaled(_screenshot, 0, 0);
            }
            finally { graphics.Restore(state); }
        }

        private void InvalidateSelectionChange(Rectangle previousSelection, Point previousMouse)
        {
            Rectangle dirty = Rectangle.Union(SelectionPaintBounds(previousSelection), SelectionPaintBounds(_selection));
            if (_cfg.ShowColorValue && _mouseInside && _drag == DragMode.Selecting)
            {
                dirty = Rectangle.Union(dirty, ColorChipPaintBounds(previousMouse));
                dirty = Rectangle.Union(dirty, ColorChipPaintBounds(_mousePos));
            }
            dirty = Rectangle.Intersect(dirty, ClientRectangle);
            if (!dirty.IsEmpty) Invalidate(dirty);
        }

        private Rectangle SelectionPaintBounds(Rectangle selection)
        {
            int padding = (int)Math.Ceiling(12 * _scale);
            int badgeHeight = (int)Math.Ceiling(64 * _scale);
            int badgeWidth = (int)Math.Ceiling(260 * _scale);
            int left = selection.Left - padding;
            int bottom = selection.Bottom + padding;
            if (_cfg.ShowDimensions)
            {
                // Size badges shift left when a narrow selection touches the right edge.
                left = Math.Min(left, Math.Max(0, ClientSize.Width - badgeWidth - padding));
                bottom = Math.Max(bottom, selection.Top + badgeHeight);
            }
            return Rectangle.FromLTRB(left, selection.Top - badgeHeight,
                Math.Max(selection.Right + padding, selection.Left + badgeWidth), bottom);
        }

        private Rectangle ColorChipPaintBounds(Point cursor)
        {
            // Include both sides because the chip flips near the desktop edge.
            int horizontal = (int)Math.Ceiling(180 * _scale);
            int vertical = (int)Math.Ceiling(90 * _scale);
            return new Rectangle(cursor.X - horizontal, cursor.Y - vertical, horizontal * 2, vertical * 2);
        }

        private void DisposeCaptureBackground()
        {
            if (_maskedScreenshot == null) return;
            _maskedScreenshot.Dispose();
            _maskedScreenshot = null;
        }
    }
}
