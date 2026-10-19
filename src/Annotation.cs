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
    /// 马赛克像素化底图（全屏截图的低分辨率版本放大而来），按需懒加载。
    /// 绘制时用遮罩路径裁剪，从而实现“只马赛克笔刷覆盖区域”。
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
            int w = Math.Max(1, _source.Width);
            int h = Math.Max(1, _source.Height);
            int bw = Math.Max(1, w / _block);
            int bh = Math.Max(1, h / _block);
            Bitmap small = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(_source, new Rectangle(0, 0, bw, bh));
            }
            _pixelated = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(_pixelated))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.DrawImage(small, new Rectangle(0, 0, w, h));
            }
            small.Dispose();
        }

        private void DisposePixelated()
        {
            if (_pixelated != null) { _pixelated.Dispose(); _pixelated = null; }
        }

        /// <summary>在 g 上按 mask（屏幕坐标）绘制像素化画面。</summary>
        public void DrawMasked(Graphics g, GraphicsPath mask)
        {
            if (mask == null || mask.PointCount == 0) return;
            Bitmap px = Pixelated;
            GraphicsState state = g.Save();
            try
            {
                g.SetClip(mask, CombineMode.Intersect);
                InterpolationMode oldInterp = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(px, new Rectangle(0, 0, _source.Width, _source.Height));
                g.InterpolationMode = oldInterp;
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
        public GraphicsPath Mask;

        public override AnnotationType Type { get { return AnnotationType.Mosaic; } }

        public override void Draw(Graphics g, MosaicCanvas mosaic)
        {
            if (Mask == null || mosaic == null) return;
            SetupHighQuality(g);
            mosaic.DrawMasked(g, Mask);
        }

        public override RectangleF GetBounds()
        {
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
                g.DrawString(Text, font, brush, Location, fmt);
            }
        }

        public override RectangleF GetBounds()
        {
            using (Font font = new Font(UiTheme.UiFontName, FontPx, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                SizeF size = TextRenderer.MeasureText(Text ?? "", font);
                return new RectangleF(Location.X, Location.Y, Math.Max(1f, size.Width), Math.Max(1f, size.Height));
            }
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

