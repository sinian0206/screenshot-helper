using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace XiaoJie
{
    internal static class ImageComposer
    {
        /// <summary>Composes native-size images in order. Input images remain caller-owned.</summary>
        internal static Bitmap Compose(IList<Bitmap> images, bool vertical, int spacing, Color background)
        {
            Size size = Measure(images, vertical, spacing);
            Bitmap result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.Clear(background);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    int position = 0;
                    foreach (Bitmap image in images)
                    {
                        graphics.DrawImageUnscaled(image, vertical ? 0 : position, vertical ? position : 0);
                        position += (vertical ? image.Height : image.Width) + spacing;
                    }
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        internal static Size Measure(IList<Bitmap> images, bool vertical, int spacing)
        {
            if (images == null || images.Count == 0) throw new InvalidDataException("请至少添加一张图片。");
            if (spacing < 0) throw new InvalidDataException("图片间距不能为负数。");
            long along = (long)spacing * (images.Count - 1);
            int across = 0;
            foreach (Bitmap image in images)
            {
                if (image == null) throw new InvalidDataException("拼图中包含不可用的图片。");
                ImageImport.ValidateSize(image.Width, image.Height);
                along += vertical ? image.Height : image.Width;
                across = Math.Max(across, vertical ? image.Width : image.Height);
            }
            if (along <= 0 || along > int.MaxValue || along * across > ImageImport.MaximumPixels)
                throw new InvalidDataException("拼图尺寸超过限制（最多 4000 万像素），请减少图片或间距。");
            return vertical ? new Size(across, (int)along) : new Size((int)along, across);
        }
    }
}
