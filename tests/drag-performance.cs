using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using XiaoJie;

internal static class DragPerformanceTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Measurements = new List<object>();
    private static string output;
    private static string baseline;
    private static bool record;
    private static int failures;

    [STAThread]
    private static int Main(string[] args)
    {
        output = args[0]; baseline = args[1]; record = bool.Parse(args[2]);
        Directory.CreateDirectory(output);
        if (record) Directory.CreateDirectory(baseline);
        Application.EnableVisualStyles();
        AppDomain.MonitoringIsEnabled = true;
        Run("1440p 选区拖拽与像素一致性", delegate { OverlayScenario(2560, 1440); });
        Run("4K 选区拖拽与像素一致性", delegate { OverlayScenario(3840, 2160); });
        Run("1440p 钉图缩放重绘与像素一致性", delegate { PinScenario(2560, 1440); });
        Run("选区边缘、取色与 DPI 差异重绘", SelectionEdges);
        Run("缓存失效、内存回退与资源释放", CacheLifecycle);
        File.WriteAllText(Path.Combine(output, "metrics.json"), new JavaScriptSerializer().Serialize(Measurements));
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action action)
    {
        try { action(); Console.WriteLine("[PASS] " + name); }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Console.WriteLine("[FAIL] " + name + ": " + error.Message); failures++;
        }
    }

    private static object Call(object target, string method, params object[] args)
    { return target.GetType().GetMethod(method, Hidden).Invoke(target, args); }
    private static void Set(object target, string field, object value)
    { target.GetType().GetField(field, Hidden).SetValue(target, value); }
    private static object Get(object target, string field)
    { return target.GetType().GetField(field, Hidden).GetValue(target); }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Mouse(OverlayForm form, string method, Point point)
    { Call(form, method, new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)); }

    private static Bitmap Fixture(int width, int height)
    {
        Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        using (Font font = new Font("Consolas", 19f, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.FromArgb(231, 235, 241));
            for (int y = 0; y < height; y += 43)
                for (int x = 0; x < width; x += 211)
                {
                    using (Brush brush = new SolidBrush(Color.FromArgb((x * 17 + y) % 256, (x + y * 11) % 256, (x * 7 + y * 3) % 256)))
                        graphics.FillRectangle(brush, x, y, 190, 31);
                    graphics.DrawString("capture 0123456789", font, Brushes.Black, x + 4, y + 5);
                }
        }
        return image;
    }

    private static void Paint(Form form, Bitmap image, Rectangle clip)
    {
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.SetClip(clip);
            using (PaintEventArgs paint = new PaintEventArgs(graphics, clip)) Call(form, "OnPaint", paint);
        }
    }

    private static void OverlayScenario(int width, int height)
    {
        using (OverlayForm form = new OverlayForm(Fixture(width, height), new Rectangle(0, 0, width, height),
            new AppConfig { AutoCopy = false, AutoSave = false, ShowColorValue = false, ShowDimensions = true,
                FirstRunDone = true, SemanticPrivacy = false, VisionPrivacy = false }, null, null))
        using (Bitmap frame = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            Rectangle invalidated = Rectangle.Empty;
            form.Invalidated += delegate(object sender, InvalidateEventArgs args)
            { invalidated = invalidated.IsEmpty ? args.InvalidRect : Rectangle.Union(invalidated, args.InvalidRect); };
            form.MinimumSize = new Size(width, height);
            form.ClientSize = new Size(width, height);
            IntPtr handle = form.Handle;
            form.Location = new Point(-32000, -32000);
            // A hidden fixture may exceed the host monitor's window tracking limit.
            // Only its offscreen drawing size is changed; no display mode is changed.
            typeof(Control).GetField("clientWidth", Hidden).SetValue(form, width);
            typeof(Control).GetField("clientHeight", Hidden).SetValue(form, height);
            Check(form.ClientSize == new Size(width, height), "离屏渲染范围不完整。");
            Mouse(form, "OnMouseDown", new Point(300, 180));
            Mouse(form, "OnMouseMove", new Point(width / 2, height / 2));
            Paint(form, frame, form.ClientRectangle);
            CompareOverlay(form, "overlay-selecting-" + width + ".png", frame);
            Mouse(form, "OnMouseUp", new Point(width / 2, height / 2));
            Paint(form, frame, form.ClientRectangle);
            CompareOverlay(form, "overlay-selected-" + width + ".png", frame);
            Mouse(form, "OnMouseDown", new Point(500, 350));
            double[] durations = new double[100];
            long memoryBefore = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            long maxArea = 0;
            for (int i = 0; i < durations.Length + 8; i++)
            {
                invalidated = Rectangle.Empty;
                Stopwatch watch = Stopwatch.StartNew();
                Mouse(form, "OnMouseMove", new Point(500 + i % 40, 350 + i % 25));
                Rectangle clip = invalidated.IsEmpty ? form.ClientRectangle : invalidated;
                Paint(form, frame, clip);
                watch.Stop();
                if (i >= 8) durations[i - 8] = watch.Elapsed.TotalMilliseconds;
                maxArea = Math.Max(maxArea, (long)clip.Width * clip.Height);
            }
            long memoryAfter = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            Report("overlay-" + width, durations, memoryAfter - memoryBefore, maxArea, (long)width * height);
            using (Bitmap full = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                Paint(form, full, form.ClientRectangle);
                Check(Equal(frame, full), "差异重绘留下残影或改变像素。");
            }
            if (!record) Check(maxArea < (long)width * height / 2, "选区小步移动仍触发超过半屏重绘。");
        }
    }

    private static void PinScenario(int width, int height)
    {
        using (PinForm form = new PinForm(Fixture(width, height), new Rectangle(200, 100, width / 2, height / 2), true))
        {
            Call(form, "SetZoomFromCenter", 0.5f);
            using (Bitmap image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppArgb))
            {
                Paint(form, image, form.ClientRectangle);
                ComparePin(form, "pin-half-" + width + ".png", image);
                double[] durations = new double[100];
                long memoryBefore = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                for (int i = 0; i < durations.Length + 8; i++)
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    Paint(form, image, form.ClientRectangle);
                    watch.Stop();
                    if (i >= 8) durations[i - 8] = watch.Elapsed.TotalMilliseconds;
                }
                Report("pin-" + width, durations, AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - memoryBefore,
                    (long)image.Width * image.Height, (long)image.Width * image.Height);
            }
        }
    }

    private static void Report(string name, double[] times, long managedGrowth, long dirtyArea, long screenArea)
    {
        Array.Sort(times);
        double median = times[times.Length / 2], p95 = times[(int)(times.Length * 0.95)];
        Console.WriteLine(name + " p50=" + median.ToString("F2") + "ms p95=" + p95.ToString("F2") +
            "ms allocatedManaged=" + managedGrowth + "B maxDirty=" + ((double)dirtyArea / screenArea).ToString("P1"));
        Measurements.Add(new { scenario = name, p50Ms = median, p95Ms = p95,
            allocatedManagedBytes = managedGrowth, maxDirtyPixels = dirtyArea, screenPixels = screenArea });
    }

    private static void SelectionEdges()
    {
        foreach (float scale in new float[] { 1f, 1.5f, 2f })
        using (OverlayForm form = new OverlayForm(Fixture(1280, 720), new Rectangle(-1280, -720, 1280, 720),
            new AppConfig { ShowColorValue = true, ShowDimensions = true }, null, null))
        using (Bitmap frame = new Bitmap(1280, 720, PixelFormat.Format32bppArgb))
        {
            Set(form, "_scale", scale);
            IntPtr handle = form.Handle;
            Rectangle dirty = Rectangle.Empty;
            form.Invalidated += delegate(object sender, InvalidateEventArgs args)
            { dirty = dirty.IsEmpty ? args.InvalidRect : Rectangle.Union(dirty, args.InvalidRect); };
            Mouse(form, "OnMouseDown", new Point(3, 3));
            Paint(form, frame, form.ClientRectangle);
            Point[] trace = { new Point(6, 6), new Point(50, 25), new Point(1278, 50),
                new Point(1278, 718), new Point(100, 718), new Point(700, 400) };
            foreach (Point point in trace)
            {
                dirty = Rectangle.Empty;
                Mouse(form, "OnMouseMove", point);
                Check(!dirty.IsEmpty, "选区发生变化但未请求重绘。");
                Paint(form, frame, dirty);
                CompareOverlay(form, "edge-" + scale + "-" + point.X + "-" + point.Y + ".png", frame);
            }
        }
        foreach (float scale in new float[] { 1f, 1.5f, 2f })
        using (OverlayForm form = new OverlayForm(Fixture(800, 600), new Rectangle(0, 0, 800, 600),
            new AppConfig { ShowColorValue = false, ShowDimensions = true }, null, null))
        using (Bitmap frame = new Bitmap(800, 600, PixelFormat.Format32bppArgb))
        {
            Set(form, "_scale", scale);
            IntPtr handle = form.Handle;
            Rectangle dirty = Rectangle.Empty;
            form.Invalidated += delegate(object sender, InvalidateEventArgs args)
            { dirty = dirty.IsEmpty ? args.InvalidRect : Rectangle.Union(dirty, args.InvalidRect); };
            Mouse(form, "OnMouseDown", new Point(780, 100));
            Paint(form, frame, form.ClientRectangle);
            foreach (Point point in new Point[] { new Point(795, 150), new Point(792, 150), new Point(798, 599), new Point(784, 599) })
            {
                dirty = Rectangle.Empty;
                Mouse(form, "OnMouseMove", point);
                Paint(form, frame, dirty);
                CompareOverlay(form, "right-edge-" + scale + "-" + point.X + ".png", frame);
            }
        }
        foreach (float scale in new float[] { 1f, 1.5f, 2f })
        using (OverlayForm form = new OverlayForm(Fixture(800, 600), new Rectangle(0, 0, 800, 600),
            new AppConfig { ShowColorValue = false, ShowDimensions = true }, null, null))
        using (Bitmap frame = new Bitmap(800, 600, PixelFormat.Format32bppArgb))
        {
            Set(form, "_scale", scale);
            IntPtr handle = form.Handle;
            Rectangle dirty = Rectangle.Empty;
            form.Invalidated += delegate(object sender, InvalidateEventArgs args)
            { dirty = dirty.IsEmpty ? args.InvalidRect : Rectangle.Union(dirty, args.InvalidRect); };
            Mouse(form, "OnMouseDown", new Point(300, 0));
            Paint(form, frame, form.ClientRectangle);
            foreach (Point point in new Point[] { new Point(350, 3), new Point(340, 4), new Point(355, 8), new Point(370, 5) })
            {
                dirty = Rectangle.Empty;
                Mouse(form, "OnMouseMove", point);
                Paint(form, frame, dirty);
                CompareOverlay(form, "top-edge-" + scale + "-" + point.X + ".png", frame);
            }
            Set(form, "_hasSelection", true);
            Set(form, "_selection", new Rectangle(300, 0, 100, 8));
            Set(form, "_startRect", new Rectangle(300, 0, 100, 8));
            Set(form, "_dragStart", new Point(320, 3));
            Set(form, "_drag", Enum.Parse(Get(form, "_drag").GetType(), "Moving"));
            Paint(form, frame, form.ClientRectangle);
            foreach (Point point in new Point[] { new Point(330, 6), new Point(340, 4), new Point(350, 7) })
            {
                dirty = Rectangle.Empty;
                Mouse(form, "OnMouseMove", point);
                Paint(form, frame, dirty);
                CompareOverlay(form, "top-move-" + scale + "-" + point.X + ".png", frame);
            }
        }
        foreach (float scale in new float[] { 1f, 1.5f, 2f })
        using (OverlayForm form = new OverlayForm(Fixture(800, 600), new Rectangle(0, 0, 800, 600),
            new AppConfig { ShowColorValue = false, ShowDimensions = true }, null, null))
        using (Bitmap frame = new Bitmap(800, 600, PixelFormat.Format32bppArgb))
        {
            Set(form, "_scale", scale);
            Set(form, "_hoverWindowRect", new Rectangle(0, 0, 800, 300));
            Set(form, "_hoverWindowTitle", "Window selection");
            IntPtr handle = form.Handle;
            Rectangle dirty = Rectangle.Empty;
            form.Invalidated += delegate(object sender, InvalidateEventArgs args)
            { dirty = dirty.IsEmpty ? args.InvalidRect : Rectangle.Union(dirty, args.InvalidRect); };
            Mouse(form, "OnMouseDown", new Point(100, 100));
            Paint(form, frame, form.ClientRectangle);
            dirty = Rectangle.Empty;
            Mouse(form, "OnMouseMove", new Point(152, 150));
            Paint(form, frame, dirty);
            CompareOverlay(form, "hover-select-" + scale + ".png", frame);
            Check((Rectangle)Get(form, "_candidateWindow") == new Rectangle(0, 0, 800, 300), "清除高亮改变了点击窗口的候选。");
        }
    }

    private static void CacheLifecycle()
    {
        OverlayForm overlay = new OverlayForm(Fixture(1000, 700), new Rectangle(0, 0, 1000, 700),
            new AppConfig { ShowColorValue = false }, null, null);
        using (Bitmap frame = new Bitmap(1000, 700, PixelFormat.Format32bppArgb))
        {
            Mouse(overlay, "OnMouseDown", new Point(120, 110));
            Mouse(overlay, "OnMouseMove", new Point(650, 450));
            Mouse(overlay, "OnMouseUp", new Point(650, 450));
            Paint(overlay, frame, overlay.ClientRectangle);
            Bitmap cached = (Bitmap)Get(overlay, "_maskedScreenshot");
            Check(cached != null, "未复用静态背景。");
            ((List<Annotation>)Get(overlay, "_annotations")).Add(new ShapeAnnotation
            { Rect = new RectangleF(180, 180, 120, 60), Color = Color.Blue, LineWidth = 4f });
            Paint(overlay, frame, overlay.ClientRectangle);
            CompareOverlay(overlay, "annotated.png", frame);
            Check(ReferenceEquals(cached, Get(overlay, "_maskedScreenshot")), "新增标注重建了静态背景。");
            Call(overlay, "DisposeCaptureBackground");
            Set(overlay, "_maskCacheUnavailable", true);
            Paint(overlay, frame, overlay.ClientRectangle);
            CompareOverlay(overlay, "allocation-fallback.png", frame);
            Set(overlay, "_hasSelection", false);
            Set(overlay, "_selection", Rectangle.Empty);
            Paint(overlay, frame, overlay.ClientRectangle);
            CompareOverlay(overlay, "record-cancel.png", frame);
        }
        overlay.Dispose();
        Check(Get(overlay, "_maskedScreenshot") == null, "截图关闭后仍持有背景缓存。");

        PinForm pin = new PinForm(Fixture(1000, 700), new Rectangle(200, 100, 500, 350), true);
        Call(pin, "SetZoomFromCenter", 0.5f);
        using (Bitmap frame = new Bitmap(pin.ClientSize.Width, pin.ClientSize.Height, PixelFormat.Format32bppArgb))
        {
            Paint(pin, frame, pin.ClientRectangle);
            Check(Get(pin, "_zoomedImage") != null, "钉图未缓存缩放结果。");
            Set(pin, "_closeHover", true);
            Paint(pin, frame, pin.ClientRectangle);
            ComparePin(pin, "pin-hover.png", frame);
            Call(pin, "DisposeZoomedImage");
            Set(pin, "_zoomCacheUnavailable", true);
            Paint(pin, frame, pin.ClientRectangle);
            ComparePin(pin, "pin-allocation-fallback.png", frame);
        }
        Call(pin, "SetZoomFromCenter", 0.75f);
        Check(Get(pin, "_zoomedImage") == null && !(bool)Get(pin, "_zoomCacheUnavailable"), "缩放变化未重置缓存。");
        using (Bitmap frame = new Bitmap(pin.ClientSize.Width, pin.ClientSize.Height, PixelFormat.Format32bppArgb))
        {
            Paint(pin, frame, pin.ClientRectangle);
            ComparePin(pin, "pin-zoom-change.png", frame);
            Call(pin, "UpdateMonitorScale", 1.5f);
        }
        using (Bitmap frame = new Bitmap(pin.ClientSize.Width, pin.ClientSize.Height, PixelFormat.Format32bppArgb))
        {
            Paint(pin, frame, pin.ClientRectangle);
            ComparePin(pin, "pin-dpi-change.png", frame);
        }
        pin.Dispose();
        Check(Get(pin, "_zoomedImage") == null, "钉图关闭后仍持有缩放缓存。");
    }

    private static void CompareOverlay(OverlayForm form, string name, Bitmap actual)
    {
        using (Bitmap expected = new Bitmap(actual.Width, actual.Height, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(expected))
        {
            LegacyOverlayPaint(form, graphics);
            Check(Equal(actual, expected), "渲染像素与原绘制路径不同: " + name);
            if (record) expected.Save(Path.Combine(baseline, name), ImageFormat.Png);
        }
    }

    private static void ComparePin(PinForm form, string name, Bitmap actual)
    {
        using (Bitmap expected = new Bitmap(actual.Width, actual.Height, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(expected))
        {
            LegacyPinPaint(form, graphics);
            Check(Equal(actual, expected), "钉图像素与原绘制路径不同: " + name);
            if (record) expected.Save(Path.Combine(baseline, name), ImageFormat.Png);
        }
    }

    private static void LegacyOverlayPaint(OverlayForm form, Graphics graphics)
    {
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImageUnscaled((Bitmap)Get(form, "_screenshot"), 0, 0);
        bool selected = (bool)Get(form, "_hasSelection");
        Rectangle selection = (Rectangle)Get(form, "_selection");
        AppConfig config = (AppConfig)Get(form, "_cfg");
        float scale = (float)Get(form, "_scale");
        string drag = Get(form, "_drag").ToString();
        if (!selected)
        {
            bool live = drag == "Selecting" && selection.Width > 0 && selection.Height > 0;
            Rectangle hole = live ? selection : (Rectangle)Get(form, "_hoverWindowRect");
            if (hole.Width > 0)
            {
                LegacyMask(graphics, hole, form.ClientSize);
                using (Pen pen = new Pen(UiTheme.Green, live ? Math.Max(1.5f, 2f * scale) : Math.Max(1f, 1.5f * scale)))
                {
                    if (!live) { pen.DashStyle = DashStyle.Dash; pen.DashPattern = new float[] { 4f, 3f }; }
                    graphics.DrawRectangle(pen, hole.X, hole.Y, hole.Width, hole.Height);
                }
                if (config.ShowDimensions) Call(form, "DrawSizeLabel", graphics, hole, !live);
            }
            else using (Brush brush = new SolidBrush(UiTheme.Mask)) graphics.FillRectangle(brush, form.ClientRectangle);
            if (config.ShowColorValue && (bool)Get(form, "_mouseInside")) Call(form, "DrawColorChip", graphics);
            return;
        }
        LegacyMask(graphics, selection, form.ClientSize);
        Call(form, "DrawAnnotations", graphics, selection);
        Call(form, "DrawTextPreview", graphics);
        Call(form, "DrawSelectionChrome", graphics);
        Call(form, "DrawObjectSelection", graphics);
        if (config.ShowDimensions) Call(form, "DrawSizeLabel", graphics, selection, false);
        if (config.ShowColorValue && (bool)Get(form, "_mouseInside") && drag == "Selecting") Call(form, "DrawColorChip", graphics);
        Call(form, "DrawPrivacyStatus", graphics);
        Call(form, "DrawEditorReview", graphics);
    }

    private static void LegacyMask(Graphics graphics, Rectangle hole, Size size)
    {
        Rectangle r = Rectangle.Intersect(hole, new Rectangle(Point.Empty, size));
        using (Brush brush = new SolidBrush(UiTheme.Mask))
        {
            if (r.Top > 0) graphics.FillRectangle(brush, 0, 0, size.Width, r.Top);
            if (r.Bottom < size.Height) graphics.FillRectangle(brush, 0, r.Bottom, size.Width, size.Height - r.Bottom);
            if (r.Left > 0) graphics.FillRectangle(brush, 0, r.Top, r.Left, Math.Max(0, r.Height));
            if (r.Right < size.Width) graphics.FillRectangle(brush, r.Right, r.Top, size.Width - r.Right, Math.Max(0, r.Height));
        }
    }

    private static void LegacyPinPaint(PinForm form, Graphics graphics)
    {
        float zoom = (float)Get(form, "_zoom"), scale = (float)Get(form, "_scale");
        Bitmap source = (Bitmap)Get(form, "_image");
        Rectangle image = (Rectangle)Call(form, "ImageBounds");
        graphics.Clear(form.BackColor);
        if (zoom == 1f) graphics.DrawImageUnscaled(source, image.Location);
        else
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, image);
        }
        using (Pen outline = new Pen(Color.FromArgb(20, 20, 20)))
            graphics.DrawRectangle(outline, 0, 0, form.ClientSize.Width - 1, form.ClientSize.Height - 1);
        using (Pen border = new Pen(UiTheme.Green, Math.Max(1f, scale)))
            graphics.DrawRectangle(border, 1, 1, form.ClientSize.Width - 3, form.ClientSize.Height - 3);
        int borderWidth = Math.Max(2, (int)Math.Round(2 * scale));
        int headerHeight = Math.Max(28, (int)Math.Round(30 * scale));
        Rectangle close = (Rectangle)Call(form, "CloseButtonRect");
        Rectangle title = new Rectangle(borderWidth + 8, borderWidth, Math.Max(1, close.Left - borderWidth - 12), headerHeight);
        using (Font font = new Font(UiTheme.UiFontName, 11f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            TextRenderer.DrawText(graphics, "钉图 · " + Math.Round(zoom * 100) + "%", font, title, Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(close.X, close.Y, close.Width, close.Height), close.Width / 2f))
        using (Brush brush = new SolidBrush((bool)Get(form, "_closeHover") ?
            (Color)typeof(UiTheme).GetField("ActionRed", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) : Color.FromArgb(64, 64, 64)))
            graphics.FillPath(brush, path);
        using (Pen pen = new Pen(Color.White, Math.Max(1.6f, 2f * scale)))
        {
            float left = close.X + close.Width * 0.3f, top = close.Y + close.Height * 0.3f;
            float right = close.Right - close.Width * 0.3f, bottom = close.Bottom - close.Height * 0.3f;
            graphics.DrawLine(pen, left, top, right, bottom);
            graphics.DrawLine(pen, right, top, left, bottom);
        }
    }

    private static bool Equal(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        Rectangle bounds = new Rectangle(Point.Empty, first.Size);
        BitmapData left = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData right = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] a = new byte[first.Width * 4], b = new byte[first.Width * 4];
            for (int y = 0; y < first.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(left.Scan0, y * left.Stride), a, 0, a.Length);
                Marshal.Copy(IntPtr.Add(right.Scan0, y * right.Stride), b, 0, b.Length);
                for (int x = 0; x < a.Length; x++) if (a[x] != b[x])
                {
                    Console.WriteLine("firstDifference=" + (x / 4) + "," + y + " channel=" + (x % 4) + " actual=" + a[x] + " expected=" + b[x]);
                    return false;
                }
            }
            return true;
        }
        finally { first.UnlockBits(left); second.UnlockBits(right); }
    }
}
