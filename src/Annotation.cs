using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace XiaoJie
{
    public enum AnnotationType
    {
        Rectangle,
        Ellipse,
        Arrow,
        Pen,
        Mosaic,
        Text,
        Emoji,
        Number
    }

    /// <summary>
    /// Opaque mosaic tiles that contain no screenshot information, created on demand.
    /// The mask limits redaction to the brush stroke or selected rectangles.
    /// </summary>
    public sealed class MosaicCanvas : IDisposable
    {
        private readonly Bitmap _source;
        private Bitmap _pixelated;
        private int _block = 12;

        public MosaicCanvas(Bitmap source, float block)
        {
            _source = source;
            _block = Math.Max(2, (int)Math.Round(block));
        }

        public Bitmap Source { get { return _source; } }

        public int Block
        {
            get { return _block; }
            set
            {
                int b = Math.Max(2, value);
                if (b != _block) { _block = b; DisposePixelated(); }
            }
        }

        public Bitmap Pixelated
        {
            get
            {
                if (_pixelated == null) Build();
                return _pixelated;
            }
        }

        private void Build()
        {
            int width = _source.Width;
            int height = _source.Height;
            Bitmap pixelated = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                // Source-independent, opaque tiles prevent text silhouettes surviving redaction.
                // Tile coordinates make repeated strokes stable without retaining screenshot colors.
                using (Graphics graphics = Graphics.FromImage(pixelated))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    for (int y = 0, row = 0; y < height; y += _block, row++)
                    {
                        for (int x = 0, column = 0; x < width; x += _block, column++)
                        {
                            uint tile = unchecked((uint)(column * 73856093) ^ (uint)(row * 19349663));
                            int shade = 76 + (int)(tile % 6) * 12;
                            using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, shade, shade, shade)))
                                graphics.FillRectangle(brush, x, y, _block, _block);
                        }
                    }
                }
                _pixelated = pixelated;
            }
            catch
            {
                pixelated.Dispose();
                throw;
            }
        }
        private void DisposePixelated()
        {
            if (_pixelated != null) { _pixelated.Dispose(); _pixelated = null; }
        }

        /// <summary>Draws opaque redaction tiles through a mask in screenshot coordinates.</summary>
        public void DrawMasked(Graphics g, GraphicsPath mask)
        {
            if (mask == null || mask.PointCount == 0) return;
            Bitmap px = Pixelated;
            GraphicsState state = g.Save();
            try
            {
                g.SetClip(mask, CombineMode.Intersect);
                g.DrawImageUnscaled(px, 0, 0);
            }
            finally
            {
                g.Restore(state);
            }
        }

        public void Dispose()
        {
            DisposePixelated();
        }
    }

    public abstract class Annotation
    {
        public abstract AnnotationType Type { get; }

        /// <summary>绘制（g 已按选区裁剪）。</summary>
        public abstract void Draw(Graphics g, MosaicCanvas mosaic);

        public abstract RectangleF GetBounds();

        protected static void SetupHighQuality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
        }
    }

    public sealed class ShapeAnnotation : Annotation
    {
        public bool IsEllipse;
        public RectangleF Rect;
        public Color Color = UiTheme.Red;
        public float LineWidth = 3f;

        public override AnnotationType Type
        {
            get { return IsEllipse ? AnnotationType.Ellipse : AnnotationType.Rectangle; }
        }

        public override void Draw(Graphics g, MosaicCanvas mosaic)
        {
            if (Rect.Width < 1f && Rect.Height < 1f) return;
            SetupHighQuality(g);
            using (Pen pen = new Pen(Color, LineWidth))
            {
                pen.LineJoin = LineJoin.Round;
                if (IsEllipse) g.DrawEllipse(pen, Rect);
                else g.DrawRectangle(pen, Rect.X, Rect.Y, Rect.Width, Rect.Height);
            }
        }

        public override RectangleF GetBounds()
        {
            float pad = LineWidth;
            return new RectangleF(Rect.X - pad, Rect.Y - pad, Rect.Width + pad * 2, Rect.Height + pad * 2);
        }
    }

    public sealed class ArrowAnnotation : Annotation
    {
        public PointF Start;
        public PointF End;
        public Color Color = UiTheme.Red;
        public float LineWidth = 3f;

        public override AnnotationType Type { get { return AnnotationType.Arrow; } }

        public override void Draw(Graphics g, MosaicCanvas mosaic)
        {
            float dx = End.X - Start.X;
            float dy = End.Y - Start.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 2f) return;
            SetupHighQuality(g);
            float head = Math.Min(len * 0.55f, Math.Max(11f, LineWidth * 4.6f));
            float ux = dx / len, uy = dy / len;
            PointF basePt = new PointF(End.X - ux * head, End.Y - uy * head);
            using (Pen pen = new Pen(Color, LineWidth))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLine(pen, Start, basePt);
            }
            float halfW = head * 0.42f;
            PointF p1 = new PointF(basePt.X - uy * halfW, basePt.Y + ux * halfW);
            PointF p2 = new PointF(basePt.X + uy * halfW, basePt.Y - ux * halfW);
            using (SolidBrush brush = new SolidBrush(Color))
            {
                g.FillPolygon(brush, new PointF[] { End, p1, p2 });
            }
        }

        public override RectangleF GetBounds()
        {
            float pad = LineWidth * 2f + 6f;
            float x = Math.Min(Start.X, End.X) - pad;
            float y = Math.Min(Start.Y, End.Y) - pad;
            float w = Math.Abs(End.X - Start.X) + pad * 2;
            float h = Math.Abs(End.Y - Start.Y) + pad * 2;
            return new RectangleF(x, y, w, h);
        }
    }

    public sealed class PenAnnotation : Annotation
    {
        public List<PointF> Points = new List<PointF>();
        public Color Color = UiTheme.Red;
        public float LineWidth = 4f;

        public override AnnotationType Type { get { return AnnotationType.Pen; } }

        public override void Draw(Graphics g, MosaicCanvas mosaic)
        {
            if (Points.Count == 0) return;
            SetupHighQuality(g);
            using (Pen pen = new Pen(Color, LineWidth))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                if (Points.Count == 1)
                {
                    using (SolidBrush b = new SolidBrush(Color))
                        g.FillEllipse(b, Points[0].X - LineWidth / 2f, Points[0].Y - LineWidth / 2f, LineWidth, LineWidth);
                }
                else if (Points.Count == 2)
                {
                    g.DrawLine(pen, Points[0], Points[1]);
                }
                else
                {
                    g.DrawCurve(pen, Points.ToArray(), 0.35f);
                }
            }
        }

        public override RectangleF GetBounds()
        {
            if (Points.Count == 0) return RectangleF.Empty;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (PointF p in Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            float pad = LineWidth;
            return new RectangleF(minX - pad, minY - pad, maxX - minX + pad * 2, maxY - minY + pad * 2);
        }
    }

    /// <summary>马赛克标注：保存一个屏幕坐标的遮罩路径（笔刷轨迹或矩形）。</summary>
    public sealed class MosaicAnnotation : Annotation
    {
        /// <summary>单个路径遮罩（笔刷轨迹 / 单个矩形）。</summary>
        public GraphicsPath Mask;

        /// <summary>多个矩形遮罩（智能打码用；逐个绘制，避免复杂路径裁剪差异）。</summary>
        public List<RectangleF> Rects;

        public override AnnotationType Type { get { return AnnotationType.Mosaic; } }

        public override void Draw(Graphics g, MosaicCanvas mosaic)
        {
            if (mosaic == null) return;
            SetupHighQuality(g);
            if (Rects != null && Rects.Count > 0)
            {
                foreach (RectangleF rect in Rects)
                {
                    if (rect.Width < 1f || rect.Height < 1f) continue;
                    using (GraphicsPath path = new GraphicsPath())
                    {
                        path.AddRectangle(rect);
                        mosaic.DrawMasked(g, path);
                    }
                }
                return;
            }
            if (Mask == null) return;
            mosaic.DrawMasked(g, Mask);
        }

        public override RectangleF GetBounds()
        {
            if (Rects != null && Rects.Count > 0)
            {
                float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
                foreach (RectangleF rect in Rects)
                {
                    l = Math.Min(l, rect.Left); t = Math.Min(t, rect.Top);
                    r = Math.Max(r, rect.Right); b = Math.Max(b, rect.Bottom);
                }
                return new RectangleF(l, t, r - l, b - t);
            }
            if (Mask == null) return RectangleF.Empty;
            return Mask.GetBounds();
        }

        /// <summary>由笔刷轨迹生成遮罩路径。</summary>
        public static GraphicsPath BuildStrokeMask(IList<PointF> points, float brushWidth)
        {
            GraphicsPath path = new GraphicsPath();
            if (points == null || points.Count == 0) return path;
            float w = Math.Max(4f, brushWidth);
            if (points.Count == 1)
            {
                path.AddEllipse(points[0].X - w / 2f, points[0].Y - w / 2f, w, w);
                return path;
            }
            PointF[] pts = new PointF[points.Count];
            for (int i = 0; i < points.Count; i++) pts[i] = points[i];
            path.AddLines(pts);
            try
            {
                using (Pen pen = new Pen(Color.Black, w))
                {
                    pen.LineJoin = LineJoin.Round;
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    path.Widen(pen);
                }
            }
            catch
            {
                // Widen 对退化路径可能失败，退化为矩形包围盒
                RectangleF b = path.GetBounds();
                path.Reset();
                path.AddRectangle(RectangleF.Inflate(b, w / 2f, w / 2f));
            }
            return path;
        }

        public static GraphicsPath BuildRectMask(RectangleF rect, float pad = 2f)
        {
            GraphicsPath path = new GraphicsPath();
            RectangleF r = rect;
            r.Inflate(pad, pad);
            path.AddRectangle(r);
            return path;
        }
    }

    public sealed class TextAnnotation : Annotation
    {
        public string Text = "";
        public PointF Location;
        public Color Color = UiTheme.Red;
        public float FontPx = 18f;
        /// <summary>Explicit layout size after resizing; empty uses natural text metrics.</summary>
        public SizeF LayoutSize;
        private string _measuredText;
        private float _measuredFontPx;
        private SizeF _measuredSize;

        public override AnnotationType Type { get { return AnnotationType.Text; } }

        public override void Draw(Graphics g, MosaicCanvas mosaic)
        {
            if (string.IsNullOrEmpty(Text)) return;
            SetupHighQuality(g);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (Font font = new Font(UiTheme.UiFontName, FontPx, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(Color))
            using (StringFormat fmt = new StringFormat(StringFormat.GenericTypographic))
            {
                fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                if (LayoutSize.Width > 0 && LayoutSize.Height > 0)
                {
                    fmt.Trimming = StringTrimming.EllipsisCharacter;
                    g.DrawString(Text, font, brush, new RectangleF(Location, LayoutSize), fmt);
                }
                else g.DrawString(Text, font, brush, Location, fmt);
            }
        }

        public override RectangleF GetBounds()
        {
            if (LayoutSize.Width > 0 && LayoutSize.Height > 0) return new RectangleF(Location, LayoutSize);
            string text = Text ?? "";
            if (_measuredText != text || _measuredFontPx != FontPx)
            {
                // Hit testing uses the same GDI+ layout as drawing, including trailing spaces.
                using (Bitmap surface = new Bitmap(1, 1))
                using (Graphics graphics = Graphics.FromImage(surface))
                using (Font font = new Font(UiTheme.UiFontName, FontPx, FontStyle.Regular, GraphicsUnit.Pixel))
                using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                {
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    _measuredSize = graphics.MeasureString(text.Length == 0 ? " " : text, font, new PointF(0, 0), format);
                }
                _measuredText = text;
                _measuredFontPx = FontPx;
            }
            return new RectangleF(Location, new SizeF(Math.Max(1f, _measuredSize.Width), Math.Max(1f, _measuredSize.Height)));
        }
    }

    public sealed class EmojiAnnotation : Annotation
    {
        public string Name = "";
        public Bitmap Image;
        public RectangleF Rect;

        public override AnnotationType Type { get { return AnnotationType.Emoji; } }

        public override void Draw(Graphics g, MosaicCanvas mosaic)
        {
            if (Image == null) return;
            SetupHighQuality(g);
            InterpolationMode old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Image, Rect);
            g.InterpolationMode = old;
        }

        public override RectangleF GetBounds()
        {
            return Rect;
        }
    }

    /// <summary>原位翻译中的一行/一块译文，覆盖在原文字区域上。</summary>
    public sealed class TranslationBlock
    {
        public RectangleF Rect;
        /// <summary>The original glyph area remains covered after a manual frame adjustment.</summary>
        public RectangleF SourceRect;
        public string Original = "";
        public string Translation = "";
        public Color Background = Color.White;
        public Color Foreground = Color.Black;
        public float FontPx = 14f;
    }

    /// <summary>原位翻译结果（一次性覆盖多行，单次撤销）。</summary>
    public sealed class TranslationOverlayAnnotation : Annotation
    {
        public List<TranslationBlock> Blocks = new List<TranslationBlock>();
        public bool ShowOriginal;

        public override AnnotationType Type { get { return AnnotationType.Text; } }

        public override void Draw(Graphics g, MosaicCanvas mosaic)
        {
            if (ShowOriginal || Blocks == null || Blocks.Count == 0) return;
            SetupHighQuality(g);
            // Cover original glyphs before drawing any translated block; their destination
            // frames can overlap only after an explicit user adjustment.
            foreach (TranslationBlock block in Blocks)
            {
                if (block == null || block.SourceRect.Width <= 0 || block.SourceRect.Height <= 0) continue;
                using (SolidBrush background = new SolidBrush(block.Background)) g.FillRectangle(background, block.SourceRect);
            }
            foreach (TranslationBlock block in Blocks)
            {
                if (block == null || block.Rect.Width < 4f || block.Rect.Height < 4f) continue;
                RectangleF rect = block.Rect;
                using (GraphicsPath path = ImageUtil.RoundedRect(rect, Math.Min(4f, rect.Height * 0.18f)))
                using (SolidBrush bg = new SolidBrush(block.Background))
                    g.FillPath(bg, path);
                string text = block.Translation ?? "";
                if (text.Length == 0) continue;
                DrawFittedText(g, block);
            }
        }

        private const float MinimumTranslationFontPx = 9f;

        private sealed class TextLayout
        {
            internal string Text;
            internal RectangleF Rect;
            internal float PreferredPx;
            internal float FontPx;
            internal bool SingleLine;
            internal bool Overflow;
        }

        private readonly Dictionary<TranslationBlock, TextLayout> _layouts = new Dictionary<TranslationBlock, TextLayout>();

        /// <summary>Reports overflow so the editor can expose the complete translation separately.</summary>
        internal bool HasOverflow()
        {
            if (Blocks == null) return false;
            using (Bitmap surface = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(surface))
            {
                foreach (TranslationBlock block in Blocks)
                {
                    if (block == null || string.IsNullOrEmpty(block.Translation)) continue;
                    if (GetLayout(graphics, block).Overflow) return true;
                }
            }
            return false;
        }

        private TextLayout GetLayout(Graphics graphics, TranslationBlock block)
        {
            TextLayout layout;
            string text = block.Translation ?? "";
            if (_layouts.TryGetValue(block, out layout) && layout.Text == text &&
                layout.Rect == block.Rect && layout.PreferredPx == block.FontPx) return layout;

            layout = new TextLayout { Text = text, Rect = block.Rect, PreferredPx = block.FontPx };
            RectangleF inner = TextArea(block.Rect);
            float preferred = Math.Max(MinimumTranslationFontPx, Math.Min(36f, block.FontPx));
            for (int mode = 0; mode < 2; mode++)
            {
                bool singleLine = mode == 0;
                int steps = (int)Math.Ceiling((preferred - MinimumTranslationFontPx) / 0.5f);
                for (int step = 0; step <= steps; step++)
                {
                    float size = Math.Max(MinimumTranslationFontPx, preferred - step * 0.5f);
                    using (Font font = new Font(UiTheme.UiFontName, size, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (StringFormat format = new StringFormat())
                    {
                        format.FormatFlags = singleLine ? StringFormatFlags.NoWrap : 0;
                        format.Trimming = StringTrimming.None;
                        int characters;
                        int lines;
                        SizeF measured = graphics.MeasureString(text, font,
                            new SizeF(singleLine ? 1000000f : Math.Max(1f, inner.Width), 1000000f),
                            format, out characters, out lines);
                        if (characters != text.Length || (singleLine && lines > 1) ||
                            measured.Width > inner.Width || measured.Height > inner.Height) continue;
                        layout.FontPx = size;
                        layout.SingleLine = singleLine;
                        _layouts[block] = layout;
                        return layout;
                    }
                }
            }
            layout.FontPx = MinimumTranslationFontPx;
            layout.SingleLine = true;
            layout.Overflow = true;
            _layouts[block] = layout;
            return layout;
        }

        private static RectangleF TextArea(RectangleF rect)
        {
            float padding = Math.Max(1f, Math.Min(3f, rect.Height * 0.08f));
            return new RectangleF(rect.X + padding, rect.Y + padding,
                Math.Max(0f, rect.Width - padding * 2), Math.Max(0f, rect.Height - padding * 2));
        }

        private void DrawFittedText(Graphics graphics, TranslationBlock block)
        {
            TextLayout layout = GetLayout(graphics, block);
            RectangleF inner = TextArea(block.Rect);
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SetClip(block.Rect, CombineMode.Intersect);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float fontSize = Math.Max(8f, layout.FontPx);
                using (Font font = new Font(UiTheme.UiFontName, fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush brush = new SolidBrush(block.Foreground))
                using (StringFormat format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Near;
                    format.LineAlignment = StringAlignment.Center;
                    format.FormatFlags = layout.SingleLine ? StringFormatFlags.NoWrap : 0;
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    graphics.DrawString(layout.Text, font, brush, inner, format);
                }
            }
            finally
            {
                graphics.Restore(state);
            }
        }
        public override RectangleF GetBounds()
        {
            if (Blocks == null || Blocks.Count == 0) return RectangleF.Empty;
            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            foreach (TranslationBlock block in Blocks)
            {
                if (block == null) continue;
                l = Math.Min(l, block.Rect.Left);
                t = Math.Min(t, block.Rect.Top);
                r = Math.Max(r, block.Rect.Right);
                b = Math.Max(b, block.Rect.Bottom);
            }
            if (l == float.MaxValue) return RectangleF.Empty;
            return new RectangleF(l, t, r - l, b - t);
        }
    }

    public sealed class NumberAnnotation : Annotation
    {
        public int Number = 1;
        public PointF Center;
        public Color Color = UiTheme.Red;
        public float Radius = 13f;

        public override AnnotationType Type { get { return AnnotationType.Number; } }

        public override void Draw(Graphics g, MosaicCanvas mosaic)
        {
            SetupHighQuality(g);
            using (SolidBrush brush = new SolidBrush(Color))
                g.FillEllipse(brush, Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);
            using (Font font = new Font(UiTheme.UiFontName, Radius * 1.2f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush white = new SolidBrush(Color.White))
            using (StringFormat fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                g.DrawString(Number.ToString(), font, white,
                    new RectangleF(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2), fmt);
            }
        }

        public override RectangleF GetBounds()
        {
            return new RectangleF(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);
        }
    }

}
