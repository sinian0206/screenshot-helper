using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>Decodes independent image copies without keeping source files open.</summary>
    internal static class ImageImport
    {
        internal const long MaximumPixels = 40000000;

        internal static Bitmap LoadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择图片文件。", "path");
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image image = Image.FromStream(stream, true, true)) return CloneImage(image);
            }
            catch (InvalidDataException) { throw; }
            catch (FileNotFoundException) { throw new InvalidDataException("图片文件不存在。"); }
            catch (UnauthorizedAccessException) { throw new InvalidDataException("无法读取图片文件，请检查文件权限。"); }
            catch (Exception error) when (error is ArgumentException || error is ExternalException || error is OutOfMemoryException || error is IOException)
            { throw new InvalidDataException("图片无法读取，文件可能损坏、格式不受支持或尺寸过大。", error); }
        }

        internal static Bitmap CloneImage(Image image)
        {
            if (image == null) throw new InvalidDataException("没有可用的图片。");
            ValidateSize(image.Width, image.Height);
            Bitmap copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(copy))
                {
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    graphics.DrawImageUnscaled(image, 0, 0);
                }
                return copy;
            }
            catch { copy.Dispose(); throw; }
        }

        internal static Bitmap FromClipboard()
        {
            try
            {
                using (Image image = Clipboard.GetImage()) return CloneImage(image);
            }
            catch (ExternalException error) { throw new InvalidDataException("剪贴板暂时不可用，请稍后重试。", error); }
        }

        internal static void ValidateSize(int width, int height)
        {
            if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
                throw new InvalidDataException("图片尺寸超过限制（最多 4000 万像素）。");
        }
    }
}
