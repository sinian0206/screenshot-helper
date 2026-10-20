using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace XiaoJie
{
    /// <summary>Copies edit state without changing an annotation's document identity.</summary>
    internal static class AnnotationGeometry
    {
        internal static Annotation Copy(Annotation item)
        {
            ShapeAnnotation shape = item as ShapeAnnotation;
            if (shape != null) return new ShapeAnnotation { IsEllipse = shape.IsEllipse, Rect = shape.Rect, Color = shape.Color, LineWidth = shape.LineWidth };
            ArrowAnnotation arrow = item as ArrowAnnotation;
            if (arrow != null) return new ArrowAnnotation { Start = arrow.Start, End = arrow.End, Color = arrow.Color, LineWidth = arrow.LineWidth };
            PenAnnotation pen = item as PenAnnotation;
            if (pen != null) return new PenAnnotation { Points = new List<PointF>(pen.Points), Color = pen.Color, LineWidth = pen.LineWidth };
            MosaicAnnotation mosaic = item as MosaicAnnotation;
            if (mosaic != null) return new MosaicAnnotation {
                Rects = mosaic.Rects == null ? null : new List<RectangleF>(mosaic.Rects),
                Mask = mosaic.Mask == null ? null : (GraphicsPath)mosaic.Mask.Clone() };
            TextAnnotation text = item as TextAnnotation;
            if (text != null) return new TextAnnotation { Text = text.Text, Location = text.Location, Color = text.Color, FontPx = text.FontPx, LayoutSize = text.LayoutSize };
            EmojiAnnotation emoji = item as EmojiAnnotation;
            // Emoji bitmaps belong to EmojiAssets and are shared; only geometry is editable.
            if (emoji != null) return new EmojiAnnotation { Name = emoji.Name, Image = emoji.Image, Rect = emoji.Rect };
            NumberAnnotation number = item as NumberAnnotation;
            if (number != null) return new NumberAnnotation { Number = number.Number, Center = number.Center, Color = number.Color, Radius = number.Radius };
            TranslationOverlayAnnotation translation = item as TranslationOverlayAnnotation;
            if (translation != null)
            {
                TranslationOverlayAnnotation copy = new TranslationOverlayAnnotation { ShowOriginal = translation.ShowOriginal };
                foreach (TranslationBlock block in translation.Blocks)
                    copy.Blocks.Add(new TranslationBlock { Rect = block.Rect, SourceRect = block.SourceRect, Original = block.Original,
                        Translation = block.Translation, Background = block.Background, Foreground = block.Foreground, FontPx = block.FontPx });
                return copy;
            }
            LoupeAnnotation loupe = item as LoupeAnnotation;
            if (loupe != null)
            {
                return new LoupeAnnotation
                {
                    SourceRect = loupe.SourceRect,
                    DisplayRect = loupe.DisplayRect,
                    Zoom = loupe.Zoom,
                    BorderColor = loupe.BorderColor,
                    BorderWidth = loupe.BorderWidth
                };
            }
            throw new ArgumentException("Unsupported annotation.", "item");
        }

        internal static void ReleaseCopy(Annotation copy)
        {
            MosaicAnnotation mosaic = copy as MosaicAnnotation;
            if (mosaic != null && mosaic.Mask != null) { mosaic.Mask.Dispose(); mosaic.Mask = null; }
        }

        internal static void Restore(Annotation target, Annotation state)
        {
            ShapeAnnotation shape = target as ShapeAnnotation;
            if (shape != null) { ShapeAnnotation s = (ShapeAnnotation)state; shape.Rect = s.Rect; shape.IsEllipse = s.IsEllipse; shape.Color = s.Color; shape.LineWidth = s.LineWidth; return; }
            ArrowAnnotation arrow = target as ArrowAnnotation;
            if (arrow != null) { ArrowAnnotation s = (ArrowAnnotation)state; arrow.Start = s.Start; arrow.End = s.End; arrow.Color = s.Color; arrow.LineWidth = s.LineWidth; return; }
            PenAnnotation pen = target as PenAnnotation;
            if (pen != null) { PenAnnotation s = (PenAnnotation)state; pen.Points = new List<PointF>(s.Points); pen.Color = s.Color; pen.LineWidth = s.LineWidth; return; }
            MosaicAnnotation mosaic = target as MosaicAnnotation;
            if (mosaic != null)
            {
                MosaicAnnotation s = (MosaicAnnotation)state;
                if (mosaic.Mask != null) mosaic.Mask.Dispose();
                mosaic.Mask = s.Mask == null ? null : (GraphicsPath)s.Mask.Clone();
                mosaic.Rects = s.Rects == null ? null : new List<RectangleF>(s.Rects);
                return;
            }
            TextAnnotation text = target as TextAnnotation;
            if (text != null) { TextAnnotation s = (TextAnnotation)state; text.Text = s.Text; text.Location = s.Location; text.Color = s.Color; text.FontPx = s.FontPx; text.LayoutSize = s.LayoutSize; return; }
            EmojiAnnotation emoji = target as EmojiAnnotation;
            if (emoji != null) { EmojiAnnotation s = (EmojiAnnotation)state; emoji.Rect = s.Rect; return; }
            NumberAnnotation number = target as NumberAnnotation;
            if (number != null) { NumberAnnotation s = (NumberAnnotation)state; number.Center = s.Center; number.Radius = s.Radius; number.Color = s.Color; number.Number = s.Number; return; }
            TranslationOverlayAnnotation translation = target as TranslationOverlayAnnotation;
            if (translation != null)
            {
                TranslationOverlayAnnotation s = (TranslationOverlayAnnotation)state;
                translation.ShowOriginal = s.ShowOriginal;
                while (translation.Blocks.Count > s.Blocks.Count) translation.Blocks.RemoveAt(translation.Blocks.Count - 1);
                for (int index = 0; index < s.Blocks.Count; index++)
                {
                    TranslationBlock source = s.Blocks[index];
                    if (index == translation.Blocks.Count) translation.Blocks.Add(new TranslationBlock());
                    TranslationBlock block = translation.Blocks[index];
                    block.Rect = source.Rect; block.SourceRect = source.SourceRect; block.Original = source.Original;
                    block.Translation = source.Translation; block.Background = source.Background; block.Foreground = source.Foreground; block.FontPx = source.FontPx;
                }
            }
        }

        internal static RectangleF Frame(Annotation annotation, int blockIndex)
        {
            TranslationOverlayAnnotation translation = annotation as TranslationOverlayAnnotation;
            if (translation != null && blockIndex >= 0 && blockIndex < translation.Blocks.Count) return translation.Blocks[blockIndex].Rect;
            ShapeAnnotation shape = annotation as ShapeAnnotation;
            if (shape != null) return shape.Rect;
            ArrowAnnotation arrow = annotation as ArrowAnnotation;
            if (arrow != null) return new RectangleF(Math.Min(arrow.Start.X, arrow.End.X), Math.Min(arrow.Start.Y, arrow.End.Y),
                Math.Max(1f, Math.Abs(arrow.End.X - arrow.Start.X)), Math.Max(1f, Math.Abs(arrow.End.Y - arrow.Start.Y)));
            PenAnnotation pen = annotation as PenAnnotation;
            if (pen != null && pen.Points.Count > 0)
            {
                RectangleF bounds = pen.GetBounds();
                bounds.Inflate(-pen.LineWidth, -pen.LineWidth);
                bounds.Width = Math.Max(1f, bounds.Width); bounds.Height = Math.Max(1f, bounds.Height);
                return bounds;
            }
            return annotation.GetBounds();
        }

        internal static void Transform(Annotation target, Annotation initial, RectangleF from, RectangleF to, int blockIndex)
        {
            Restore(target, initial);
            float scaleX = to.Width / Math.Max(1f, from.Width);
            float scaleY = to.Height / Math.Max(1f, from.Height);
            Func<PointF, PointF> point = p => new PointF(to.X + (p.X - from.X) * scaleX, to.Y + (p.Y - from.Y) * scaleY);
            Func<RectangleF, RectangleF> rect = r => new RectangleF(point(r.Location), new SizeF(r.Width * scaleX, r.Height * scaleY));
            ShapeAnnotation shape = target as ShapeAnnotation;
            if (shape != null) { shape.Rect = rect(shape.Rect); return; }
            ArrowAnnotation arrow = target as ArrowAnnotation;
            if (arrow != null) { arrow.Start = point(arrow.Start); arrow.End = point(arrow.End); return; }
            PenAnnotation pen = target as PenAnnotation;
            if (pen != null) { for (int i = 0; i < pen.Points.Count; i++) pen.Points[i] = point(pen.Points[i]); return; }
            MosaicAnnotation mosaic = target as MosaicAnnotation;
            if (mosaic != null)
            {
                if (mosaic.Rects != null) for (int i = 0; i < mosaic.Rects.Count; i++) mosaic.Rects[i] = rect(mosaic.Rects[i]);
                if (mosaic.Mask != null)
                    using (Matrix matrix = new Matrix(scaleX, 0, 0, scaleY, to.X - from.X * scaleX, to.Y - from.Y * scaleY)) mosaic.Mask.Transform(matrix);
                return;
            }
            TextAnnotation text = target as TextAnnotation;
            if (text != null)
            {
                text.Location = point(text.Location);
                if (Math.Abs(scaleX - 1f) > .001f || Math.Abs(scaleY - 1f) > .001f)
                { text.FontPx = Math.Max(8f, Math.Min(200f, text.FontPx * scaleY)); text.LayoutSize = to.Size; }
                return;
            }
            EmojiAnnotation emoji = target as EmojiAnnotation;
            if (emoji != null) { emoji.Rect = rect(emoji.Rect); return; }
            NumberAnnotation number = target as NumberAnnotation;
            if (number != null) { number.Center = point(number.Center); number.Radius *= Math.Min(scaleX, scaleY); return; }
            TranslationOverlayAnnotation translation = target as TranslationOverlayAnnotation;
            if (translation != null)
            {
                for (int index = 0; index < translation.Blocks.Count; index++)
                {
                    if (blockIndex >= 0 && blockIndex != index) continue;
                    TranslationBlock block = translation.Blocks[index];
                    // Keep the original glyph area covered when the user moves the translation frame.
                    if (block.SourceRect.Width <= 0 || block.SourceRect.Height <= 0) block.SourceRect = block.Rect;
                    block.Rect = rect(block.Rect);
                }
            }
        }
    }

    internal sealed class AnnotationEditCommand : IDisposable
    {
        private readonly Action _undo;
        private readonly Action _redo;
        private readonly Annotation _before;
        private readonly Annotation _after;

        internal AnnotationEditCommand(Action undo, Action redo, Annotation before = null, Annotation after = null)
        { _undo = undo; _redo = redo; _before = before; _after = after; }

        internal void Undo() { _undo(); }
        internal void Redo() { _redo(); }
        public void Dispose() { AnnotationGeometry.ReleaseCopy(_before); AnnotationGeometry.ReleaseCopy(_after); }
    }
}
