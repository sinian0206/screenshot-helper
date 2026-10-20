using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using XiaoJie;

internal static class EditorRegression {
    private static int failures;
    private static string outputDirectory;

    [STAThread]
    private static int Main(string[] args) {
        outputDirectory = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
        Directory.CreateDirectory(outputDirectory);
        Application.EnableVisualStyles();
        Run("打码像素不保留源图信息", TestMosaicIndependence);
        Run("打码覆盖透明像素且不修改选区外", TestMosaicOpacity);
        Run("交叉打码笔刷不留下原图空洞", TestCrossingMosaic);
        Run("文字边界与透明绘制使用同一布局", TestTextBounds);
        Run("小钉图关闭按钮完整可见", TestSmallPinClose);
        Run("钉图关闭按钮不覆盖图像内容", TestPinContent);
        Run("钉图缩放后关闭按钮仍在屏幕内", TestPinZoom);
        Run("钉图拖动结束释放鼠标且离开重置悬停", TestPinPointerState);
        Run("框选松开前绘制实时边框", TestLiveSelection);
        Run("文字编辑透明预览且可以拖动", TestTextMovement);
        Run("已有文字再次编辑后保留", TestTextReedit);
        Run("长译文绘制不越过原始区域", TestLongTranslationBounds);
        Run("多行译文绘制不覆盖相邻区域", TestMultilineTranslationBounds);
        Run("译文溢出可报告且内容修改后更新", TestTranslationOverflowStatus);
        Run("识别期间鼠标无法移动选区", TestBusySelection);
        Run("识别期间鼠标无法添加标注", TestBusyDrawing);
        Run("识别期间键盘无法移动选区", TestBusyKeyboard);
        Console.WriteLine("结果: " + (failures == 0 ? "全部通过" : failures + " 项失败"));
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action action) {
        try { action(); Console.WriteLine("[PASS] " + name); }
        catch (Exception exception) {
            failures++;
            Exception error = exception is TargetInvocationException ? exception.InnerException : exception;
            Console.WriteLine("[FAIL] " + name + ": " + error.Message);
        }
    }

    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Bitmap Solid(int width, int height, Color color) {
        Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(color);
        return image;
    }

    private static void TestMosaicIndependence() {
        using (Bitmap first = Solid(180, 100, Color.White))
        using (Bitmap second = Solid(180, 100, Color.Black))
        using (Graphics text = Graphics.FromImage(first))
        using (Font font = new Font("Arial", 24, FontStyle.Bold, GraphicsUnit.Pixel))
        using (GraphicsPath mask = MosaicAnnotation.BuildRectMask(new RectangleF(20, 20, 140, 60), 0))
        using (MosaicCanvas firstCanvas = new MosaicCanvas(first, 12))
        using (MosaicCanvas secondCanvas = new MosaicCanvas(second, 12))
        using (Bitmap firstResult = (Bitmap)first.Clone())
        using (Bitmap secondResult = (Bitmap)second.Clone()) {
            text.DrawString("13800138000", font, Brushes.Black, 22, 22);
            using (Graphics graphics = Graphics.FromImage(firstResult)) firstCanvas.DrawMasked(graphics, mask);
            using (Graphics graphics = Graphics.FromImage(secondResult)) secondCanvas.DrawMasked(graphics, mask);
            int differences = 0;
            for (int y = 21; y < 79; y++)
                for (int x = 21; x < 159; x++)
                    if (firstResult.GetPixel(x, y) != secondResult.GetPixel(x, y)) differences++;
            firstResult.Save(Path.Combine(outputDirectory, "mosaic.png"));
            Require(differences == 0, "相同遮罩的输出仍依赖源图，差异像素: " + differences);
        }
    }

    private static void TestMosaicOpacity() {
        using (Bitmap source = Solid(120, 80, Color.Transparent))
        using (Bitmap output = Solid(120, 80, Color.Magenta))
        using (MosaicCanvas canvas = new MosaicCanvas(source, 24))
        using (GraphicsPath mask = MosaicAnnotation.BuildRectMask(new RectangleF(20, 20, 80, 40), 0)) {
            using (Graphics graphics = Graphics.FromImage(output)) {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                canvas.DrawMasked(graphics, mask);
            }
            Require(output.GetPixel(40, 40).A == 255, "遮罩内像素应完全不透明");
            Require(output.GetPixel(10, 10).ToArgb() == Color.Magenta.ToArgb(), "选区外像素发生变化");
        }
    }

    private static void TestTextBounds() {
        TextAnnotation annotation = new TextAnnotation {
            Text = "Hi 中文\n第二行", Location = new PointF(20, 20), FontPx = 26, Color = Color.Black
        };
        using (Bitmap image = Solid(320, 150, Color.Magenta))
        using (Graphics graphics = Graphics.FromImage(image))
        using (Font font = new Font(UiTheme.UiFontName, annotation.FontPx, FontStyle.Regular, GraphicsUnit.Pixel))
        using (StringFormat format = new StringFormat(StringFormat.GenericTypographic)) {
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            SizeF measured = graphics.MeasureString(annotation.Text, font, new PointF(0, 0), format);
            RectangleF actual = annotation.GetBounds();
            Require(Math.Abs(actual.Width - measured.Width) < 1.5f && Math.Abs(actual.Height - measured.Height) < 1.5f,
                "测量和绘制不一致: " + actual.Size + " / " + measured);
            annotation.Draw(graphics, null);
            Require(image.GetPixel(300, 100).ToArgb() == Color.Magenta.ToArgb(), "文字绘制产生了背景填充");
            image.Save(Path.Combine(outputDirectory, "text-transparent.png"));
        }
    }

    private static void TestCrossingMosaic() {
        PointF[] points = { new PointF(20, 20), new PointF(160, 100), new PointF(20, 100), new PointF(160, 20) };
        using (Bitmap source = Solid(180, 120, Color.White))
        using (Bitmap output = (Bitmap)source.Clone())
        using (MosaicCanvas canvas = new MosaicCanvas(source, 24))
        using (GraphicsPath mask = MosaicAnnotation.BuildStrokeMask(points, 30)) {
            using (Graphics graphics = Graphics.FromImage(output)) canvas.DrawMasked(graphics, mask);
            for (int segment = 1; segment < points.Length; segment++) {
                for (int step = 0; step <= 100; step++) {
                    int x = (int)Math.Round(points[segment - 1].X + (points[segment].X - points[segment - 1].X) * step / 100f);
                    int y = (int)Math.Round(points[segment - 1].Y + (points[segment].Y - points[segment - 1].Y) * step / 100f);
                    Require(output.GetPixel(x, y).R < 200, "笔刷轨迹有未遮挡像素: " + x + "," + y);
                }
            }
        }
    }

    private static void TestSmallPinClose() {
        using (PinForm form = new PinForm(Solid(8, 8, Color.Cyan), new Rectangle(0, 0, 8, 8), false)) {
            Rectangle close = (Rectangle)Call(form, "CloseButtonRect");
            Require(form.ClientRectangle.Contains(close), "关闭按钮超出窗口: " + close + " / " + form.ClientRectangle);
            Mouse(form, "OnMouseDown", new Point(close.X + close.Width / 2, close.Y + close.Height / 2), MouseButtons.Left);
            Require(form.IsDisposed, "点击关闭按钮后窗口未关闭");
        }
    }

    private static void TestPinContent() {
        using (PinForm form = new PinForm(Solid(120, 60, Color.Magenta), new Rectangle(0, 0, 120, 60), false))
        using (Bitmap rendered = Render(form)) {
            int pixels = 0;
            for (int y = 0; y < rendered.Height; y++)
                for (int x = 0; x < rendered.Width; x++)
                    if (rendered.GetPixel(x, y).ToArgb() == Color.Magenta.ToArgb()) pixels++;
            rendered.Save(Path.Combine(outputDirectory, "pin.png"));
            Require(pixels == 120 * 60, "原图被边框/关闭按钮遮挡或缩放，完整像素数: " + pixels);
        }
    }

    private static void TestPinZoom() {
        using (PinForm form = new PinForm(Solid(40, 20, Color.Cyan), new Rectangle(0, 0, 40, 20), false)) {
            Rectangle close = (Rectangle)Call(form, "CloseButtonRect");
            Rectangle workArea = Screen.FromRectangle(form.Bounds).WorkingArea;
            close.Offset(form.Location);
            Require(workArea.Contains(close), "屏幕边缘钉图关闭按钮被裁剪");
            Call(form, "SetZoom", 4f, new Point(workArea.Left, workArea.Top));
            close = (Rectangle)Call(form, "CloseButtonRect");
            Require(form.ClientRectangle.Contains(close), "放大后关闭按钮超出客户区");
            close.Offset(form.Location);
            Require(workArea.Contains(close), "缩放后的关闭按钮移出屏幕");
        }
    }

    private static void TestPinPointerState() {
        using (PinForm form = new PinForm(Solid(120, 80, Color.Cyan), new Rectangle(80, 80, 120, 80), false)) {
            Rectangle close = (Rectangle)Call(form, "CloseButtonRect");
            Mouse(form, "OnMouseMove", new Point(close.X + 2, close.Y + 2), MouseButtons.None);
            Require((bool)Field(form, "_closeHover"), "进入关闭按钮未高亮");
            Call(form, "OnMouseLeave", EventArgs.Empty);
            Require(!(bool)Field(form, "_closeHover"), "离开窗口后关闭按钮仍高亮");
            Mouse(form, "OnMouseDown", new Point(10, form.Height - 10), MouseButtons.Left);
            Mouse(form, "OnMouseUp", new Point(20, form.Height - 10), MouseButtons.Left);
            Require(!form.Capture && !(bool)Field(form, "_dragging"), "拖动结束未释放鼠标状态");
        }
    }

    private static OverlayForm NewOverlay() {
        AppConfig config = new AppConfig { FirstRunDone = true, AutoCopy = false, AutoSave = false, ShowDimensions = false, ShowColorValue = false };
        OverlayForm form = new OverlayForm(Solid(800, 600, Color.FromArgb(240, 240, 240)),
            new Rectangle(0, 0, 800, 600), config, new ConfigStore(), null);
        form.SuppressActivation = true;
        return form;
    }

    private static void Select(OverlayForm form) {
        Mouse(form, "OnMouseDown", new Point(50, 50), MouseButtons.Left);
        Mouse(form, "OnMouseMove", new Point(650, 450), MouseButtons.Left);
        Mouse(form, "OnMouseUp", new Point(650, 450), MouseButtons.Left);
    }

    private static void TestLiveSelection() {
        using (OverlayForm form = NewOverlay()) {
            Mouse(form, "OnMouseDown", new Point(100, 100), MouseButtons.Left);
            Mouse(form, "OnMouseMove", new Point(400, 300), MouseButtons.Left);
            using (Bitmap bitmap = Render(form)) {
                Color boundary = bitmap.GetPixel(400, 220);
                Require(boundary.G > boundary.R + 50 && boundary.G > boundary.B + 30, "松开前右侧边框未跟随指针");
                Require(bitmap.GetPixel(200, 200).R > bitmap.GetPixel(450, 350).R, "实时选区未清除遮罩");
                bitmap.Save(Path.Combine(outputDirectory, "live-selection.png"));
            }
        }
    }

    private static TextAnnotation AddText(OverlayForm form) {
        Call(form, "BeginTextEdit", new Point(180, 180));
        TextBox input = (TextBox)Field(form, "_textInput");
        input.Text = "编辑示例";
        Call(form, "CommitText");
        return ((List<Annotation>)Field(form, "_annotations"))[0] as TextAnnotation;
    }

    private static void TestTextMovement() {
        using (OverlayForm form = NewOverlay()) {
            Select(form);
            TextAnnotation text = AddText(form);
            Point from = new Point((int)text.Location.X + 6, (int)text.Location.Y + 6);
            Mouse(form, "OnMouseDown", from, MouseButtons.Left);
            Mouse(form, "OnMouseMove", new Point(from.X + 80, from.Y + 30), MouseButtons.Left);
            Mouse(form, "OnMouseUp", new Point(from.X + 80, from.Y + 30), MouseButtons.Left);
            Require(text.Location == new PointF(260, 210), "文字未按拖动偏移移动: " + text.Location);
            Call(form, "BeginEditExistingText", text);
            using (Bitmap bitmap = Render(form)) {
                Require(bitmap.GetPixel(490, 210).R == 240, "文字预览额外填充了不透明背景");
                bitmap.Save(Path.Combine(outputDirectory, "text-edit.png"));
            }
        }
    }

    private static void TestTextReedit() {
        using (OverlayForm form = NewOverlay()) {
            Select(form);
            TextAnnotation text = AddText(form);
            Call(form, "BeginEditExistingText", text);
            ((TextBox)Field(form, "_textInput")).Text = "修改后的文字";
            Call(form, "CommitText");
            List<Annotation> annotations = (List<Annotation>)Field(form, "_annotations");
            Require(annotations.Count == 1 && ((TextAnnotation)annotations[0]).Text == "修改后的文字", "已有文字提交后从标注列表消失");
        }
    }

    private static void TestLongTranslationBounds() {
        TestTranslationBounds(new string('W', 150), new RectangleF(40, 40, 70, 16), "translation-long.png");
    }

    private static void TestMultilineTranslationBounds() {
        TestTranslationBounds("第一行\n第二行\n第三行\n第四行\n第五行", new RectangleF(40, 40, 80, 18), "translation-multiline.png");
    }

    private static void TestTranslationOverflowStatus() {
        TranslationOverlayAnnotation annotation = new TranslationOverlayAnnotation();
        TranslationBlock block = new TranslationBlock { Rect = new RectangleF(20, 20, 180, 32), Translation = "OK", FontPx = 16 };
        annotation.Blocks.Add(block);
        Require(!(bool)Call(annotation, "HasOverflow"), "有足够空间的短译文被标记溢出");
        block.Translation = new string('W', 200);
        Require((bool)Call(annotation, "HasOverflow"), "长译文未报告溢出，编辑器无法提供完整文本");
        block.Translation = "OK";
        Require(!(bool)Call(annotation, "HasOverflow"), "译文改变后仍使用旧溢出状态");
    }

    private static void TestBusySelection() {
        using (OverlayForm form = NewOverlay()) {
            Select(form);
            Rectangle before = (Rectangle)Field(form, "_selection");
            form.GetType().GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, true);
            Mouse(form, "OnMouseDown", new Point(180, 180), MouseButtons.Left);
            Mouse(form, "OnMouseMove", new Point(230, 210), MouseButtons.Left);
            Mouse(form, "OnMouseUp", new Point(230, 210), MouseButtons.Left);
            Require((Rectangle)Field(form, "_selection") == before, "识别期间鼠标改变选区，旧 OCR 将映射到新坐标");
        }
    }

    private static void TestBusyDrawing() {
        using (OverlayForm form = NewOverlay()) {
            Select(form);
            Call(form, "SetTool", EditorTool.Rectangle);
            form.GetType().GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, true);
            Mouse(form, "OnMouseDown", new Point(180, 180), MouseButtons.Left);
            Mouse(form, "OnMouseMove", new Point(260, 240), MouseButtons.Left);
            Mouse(form, "OnMouseUp", new Point(260, 240), MouseButtons.Left);
            Require(((List<Annotation>)Field(form, "_annotations")).Count == 0, "识别期间仍可添加矩形标注");
        }
    }

    private static void TestBusyKeyboard() {
        using (OverlayForm form = NewOverlay()) {
            Select(form);
            Rectangle before = (Rectangle)Field(form, "_selection");
            form.GetType().GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, true);
            Message message = Message.Create(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
            Call(form, "ProcessCmdKey", message, Keys.Right);
            Require((Rectangle)Field(form, "_selection") == before, "识别期间方向键改变选区");
        }
    }

    private static void TestTranslationBounds(string text, RectangleF rect, string fileName) {
        TranslationOverlayAnnotation annotation = new TranslationOverlayAnnotation();
        annotation.Blocks.Add(new TranslationBlock { Rect = rect, Translation = text, Background = Color.White, Foreground = Color.Black, FontPx = 16 });
        using (Bitmap bitmap = Solid(240, 160, Color.Magenta))
        using (Graphics graphics = Graphics.FromImage(bitmap)) {
            annotation.Draw(graphics, null);
            bitmap.Save(Path.Combine(outputDirectory, fileName));
            int overflow = 0;
            int ink = 0;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++) {
                    Color color = bitmap.GetPixel(x, y);
                    if (!rect.Contains(x, y) && color.ToArgb() != Color.Magenta.ToArgb()) overflow++;
                    if (rect.Contains(x, y) && color.R < 180 && color.G < 180 && color.B < 180) ink++;
                }
            Require(overflow == 0, "译文画到原区域之外，像素数: " + overflow);
            Require(ink > 0, "长译文未绘制任何内容或省略标记");
        }
    }

    private static object Field(object target, string name) {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }

    private static object Call(object target, string name, params object[] args) {
        return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    }

    private static void Mouse(Form form, string method, Point point, MouseButtons button) {
        Call(form, method, new MouseEventArgs(button, 1, point.X, point.Y, 0));
    }

    private static Bitmap Render(Form form) {
        Bitmap bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            Call(form, "OnPaint", new PaintEventArgs(graphics, new Rectangle(Point.Empty, bitmap.Size)));
        return bitmap;
    }
}
