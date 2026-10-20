using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace XiaoJie
{
    /// <summary>
    /// 本地文字行检测：不依赖 AI 的 bbox，用截图像素（行投影 + 墨迹统计）找出每一行文字的精确位置。
    /// AI 只负责“识别文字/翻译/判断敏感”，定位由本地完成，避免模型 bbox 漂移导致覆盖错位。
    /// </summary>
    public static class TextLineDetector
    {
        public sealed class TextLine
        {
            public RectangleF Rect;
            public float CenterY { get { return Rect.Top + Rect.Height / 2f; } }
            public override string ToString()
            {
                return "(" + Rect.X.ToString("0") + "," + Rect.Y.ToString("0") + "," +
                       Rect.Width.ToString("0") + "x" + Rect.Height.ToString("0") + ")";
            }
        }

        /// <summary>
        /// 在 area（屏幕坐标）内检测文字行，返回按从上到下排序的行矩形（屏幕坐标）。
        /// </summary>
        public static List<TextLine> Detect(Bitmap source, Rectangle area)
        {
            List<TextLine> lines = new List<TextLine>();
            if (source == null || area.Width < 8 || area.Height < 8) return lines;
            area.Intersect(new Rectangle(0, 0, source.Width, source.Height));
            if (area.Width < 8 || area.Height < 8) return lines;

            Color background = SampleBackground(source, area);
            int threshold = 56;
            int minRowInk = Math.Max(2, area.Width / 140);
            int stride;
            byte[] pixels;
            int width = area.Width;
            int height = area.Height;
            try
            {
                BitmapData data = source.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                stride = data.Stride;
                pixels = new byte[stride * height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                source.UnlockBits(data);
            }
            catch
            {
                return FallbackDetect(source, area);
            }

            int[] rowInk = new int[height];
            for (int y = 0; y < height; y++)
            {
                int offset = y * stride;
                int count = 0;
                for (int x = 0; x < width; x++)
                {
                    int i = offset + x * 4;
                    int b = pixels[i];
                    int g = pixels[i + 1];
                    int r = pixels[i + 2];
                    int d = Math.Abs(r - background.R) + Math.Abs(g - background.G) + Math.Abs(b - background.B);
                    if (d > threshold) count++;
                }
                rowInk[y] = count;
            }

            int minLineHeight = 8;
            int maxLineHeight = Math.Max(24, area.Height / 3);
            int gapTolerance = 2;
            int y0 = -1;
            for (int y = 0; y <= height; y++)
            {
                bool active = y < height && rowInk[y] >= minRowInk;
                if (active)
                {
                    if (y0 < 0) y0 = y;
                }
                else if (y0 >= 0)
                {
                    int y1 = y - 1;
                    // 允许行内小间隙：如果间隙 <= gapTolerance 且后面还有墨迹，继续延伸
                    int probe = y;
                    int skipped = 0;
                    while (probe < height && skipped <= gapTolerance && rowInk[probe] < minRowInk) { probe++; skipped++; }
                    if (probe < height && rowInk[probe] >= minRowInk && skipped <= gapTolerance)
                    {
                        continue;
                    }
                    int lineHeight = y1 - y0 + 1;
                    if (lineHeight >= minLineHeight && lineHeight <= maxLineHeight)
                    {
                        AddLine(source, area, pixels, stride, background, threshold, y0, y1, minLineHeight, lines);
                    }
                    y0 = -1;
                }
            }
            return lines;
        }

        private static void AddLine(Bitmap source, Rectangle area, byte[] pixels, int stride, Color background, int threshold,
            int y0, int y1, int minLineHeight, List<TextLine> lines)
        {
            int width = area.Width;
            int height = area.Height;
            int minColInk = Math.Max(1, (y1 - y0 + 1) / 5);
            int left = int.MaxValue, right = int.MinValue;
            for (int x = 0; x < width; x++)
            {
                int count = 0;
                for (int y = Math.Max(0, y0); y <= Math.Min(height - 1, y1); y++)
                {
                    int i = y * stride + x * 4;
                    int b = pixels[i];
                    int g = pixels[i + 1];
                    int r = pixels[i + 2];
                    int d = Math.Abs(r - background.R) + Math.Abs(g - background.G) + Math.Abs(b - background.B);
                    if (d > threshold) count++;
                    if (count >= minColInk) break;
                }
                if (count >= minColInk)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                }
            }
            if (left == int.MaxValue || right <= left) return;
            // 裁掉左右两端过长的空白（避免把旁边的图标/滚动条算成一行）
            int trimLeft = left, trimRight = right;
            while (trimLeft < trimRight - 2 && !ColumnHasInk(pixels, stride, trimLeft, y0, y1, background, threshold, height)) trimLeft++;
            while (trimRight > trimLeft + 2 && !ColumnHasInk(pixels, stride, trimRight, y0, y1, background, threshold, height)) trimRight--;
            RectangleF rect = new RectangleF(area.Left + trimLeft, area.Top + y0, Math.Max(2, trimRight - trimLeft + 1), y1 - y0 + 1);
            lines.Add(new TextLine { Rect = rect });
        }

        private static bool ColumnHasInk(byte[] pixels, int stride, int x, int y0, int y1, Color background, int threshold, int height)
        {
            int count = 0;
            int minColInk = Math.Max(1, (y1 - y0 + 1) / 5);
            for (int y = Math.Max(0, y0); y <= Math.Min(height - 1, y1); y++)
            {
                int i = y * stride + x * 4;
                int b = pixels[i];
                int g = pixels[i + 1];
                int r = pixels[i + 2];
                int d = Math.Abs(r - background.R) + Math.Abs(g - background.G) + Math.Abs(b - background.B);
                if (d > threshold) count++;
                if (count >= minColInk) return true;
            }
            return false;
        }

        private static Color SampleBackground(Bitmap source, Rectangle area)
        {
            List<int> rs = new List<int>(); List<int> gs = new List<int>(); List<int> bs = new List<int>();
            int step = Math.Max(1, area.Width / 32);
            for (int x = area.Left; x < area.Right; x += step)
            {
                Add(rs, gs, bs, source, x, area.Top);
                Add(rs, gs, bs, source, x, Math.Max(area.Top, area.Bottom - 1));
            }
            step = Math.Max(1, area.Height / 24);
            for (int y = area.Top; y < area.Bottom; y += step)
            {
                Add(rs, gs, bs, source, area.Left, y);
                Add(rs, gs, bs, source, Math.Max(area.Left, area.Right - 1), y);
            }
            if (rs.Count == 0) return Color.White;
            rs.Sort(); gs.Sort(); bs.Sort();
            int mid = rs.Count / 2;
            return Color.FromArgb(rs[mid], gs[mid], bs[mid]);
        }

        private static void Add(List<int> rs, List<int> gs, List<int> bs, Bitmap source, int x, int y)
        {
            if (x < 0 || y < 0 || x >= source.Width || y >= source.Height) return;
            Color c = source.GetPixel(x, y);
            rs.Add(c.R); gs.Add(c.G); bs.Add(c.B);
        }

        /// <summary>LockBits 失败时的兜底（较慢，仅小区域使用）。</summary>
        private static List<TextLine> FallbackDetect(Bitmap source, Rectangle area)
        {
            List<TextLine> lines = new List<TextLine>();
            try
            {
                Color bg = source.GetPixel(area.Left, area.Top);
                int threshold = 56;
                List<int> inkRows = new List<int>();
                for (int y = area.Top; y < area.Bottom; y++)
                {
                    int count = 0;
                    for (int x = area.Left; x < area.Right; x++)
                    {
                        Color c = source.GetPixel(x, y);
                        if (Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > threshold) count++;
                    }
                    if (count > Math.Max(2, area.Width / 140)) inkRows.Add(y);
                }
                int i2 = 0;
                while (i2 < inkRows.Count)
                {
                    int start = inkRows[i2];
                    int end = start;
                    while (i2 + 1 < inkRows.Count && inkRows[i2 + 1] - end <= 3) { i2++; end = inkRows[i2]; }
                    i2++;
                    if (end - start + 1 >= 8)
                        lines.Add(new TextLine { Rect = new RectangleF(area.Left, start, area.Width, end - start + 1) });
                }
            }
            catch { }
            return lines;
        }

        /// <summary>
        /// 把 AI 返回的文字行（可能 bbox 漂移）映射到本地检测到的文字行：优先按顺序/最近中心匹配。
        /// 返回与 blocks 等长的数组，元素为屏幕坐标矩形；无法匹配的为 RectangleF.Empty。
        /// </summary>
        public static RectangleF[] MapBlocks(List<TextLine> lines, IList<ImageTextBlock> blocks, Rectangle selection)
        {
            RectangleF[] result = new RectangleF[blocks == null ? 0 : blocks.Count];
            if (blocks == null) return result;
            if (lines == null || lines.Count == 0) return result;
            bool[] used = new bool[lines.Count];
            // 第一轮：按顺序等数匹配（最常见的截图文本场景）
            if (blocks.Count == lines.Count)
            {
                for (int i = 0; i < blocks.Count; i++)
                {
                    result[i] = lines[i].Rect;
                    used[i] = true;
                }
                return result;
            }
            // 第二轮：按模型 y 中心找最近的未使用行
            for (int i = 0; i < blocks.Count; i++)
            {
                ImageTextBlock block = blocks[i];
                if (block == null) continue;
                float targetY = selection.Top + block.Y * selection.Height + Math.Max(1f, block.H * selection.Height) / 2f;
                int best = -1;
                float bestDist = float.MaxValue;
                for (int j = 0; j < lines.Count; j++)
                {
                    if (used[j]) continue;
                    float dist = Math.Abs(lines[j].CenterY - targetY);
                    if (dist < bestDist) { bestDist = dist; best = j; }
                }
                float tolerance = Math.Max(18f, selection.Height * 0.04f);
                if (best >= 0 && bestDist <= tolerance)
                {
                    result[i] = lines[best].Rect;
                    used[best] = true;
                }
            }
            return result;
        }
    }
}
