using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading;
using XiaoJie;

internal static class LocalOcrTests
{
    private static int passed;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Run(string name, Action action)
    {
        action();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static Bitmap MakeImage(int width, int height, Action<Graphics> paint)
    {
        var image = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            paint(graphics);
        }
        return image;
    }

    private static void Draw(Graphics g, string text, float x, float y, float size)
    {
        using (var font = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel))
            g.DrawString(text, font, Brushes.Black, x, y, StringFormat.GenericTypographic);
    }

    private static void CheckBounds(LocalOcrResult result, Bitmap image)
    {
        Assert(result.Lines.Count > 0, "识别结果为空");
        foreach (LocalOcrLine line in result.Lines)
        {
            Assert(line.Bounds.Width > 0 && line.Bounds.Height > 0, "包围框为空");
            Assert(line.Bounds.Left >= 0 && line.Bounds.Top >= 0 &&
                line.Bounds.Right <= image.Width && line.Bounds.Bottom <= image.Height,
                "包围框超过输入图像边界");
            Assert(line.Confidence >= 0 && line.Confidence <= 1, "置信度不在 [0, 1]");
        }
    }

    private static int Main(string[] args)
    {
        try
        {
            string engine = args.Length > 0 ? args[0] : null;
            string evidence = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "evidence");
            Directory.CreateDirectory(evidence);
            using (var ocr = new LocalOcr(engine))
            {
                Run("中英混排和真实坐标", () =>
                {
                    using (Bitmap image = MakeImage(1100, 180, g =>
                    {
                        Draw(g, "截图助手 本地文字识别", 32, 22, 30);
                        Draw(g, "Hello Windows 123456", 32, 95, 28);
                    }))
                    {
                        image.Save(Path.Combine(evidence, "mixed.png"));
                        var result = ocr.RecognizeAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                        File.WriteAllText(Path.Combine(evidence, "mixed.txt"), result.Text);
                        CheckBounds(result, image);
                        Assert(result.Text.Contains("截图助手") && result.Text.Contains("本地文字识别"), "中文识别不匹配");
                        Assert(result.Text.Contains("Hello Windows 123456"), "英文识别不匹配");
                        Assert(result.Lines.Any(l => l.Text.Contains("Hello") && l.Bounds.Top > 75), "英文坐标未映射到原图");
                    }
                });
                Run("双列各自返回文字框", () =>
                {
                    using (Bitmap image = MakeImage(1000, 220, g =>
                    {
                        Draw(g, "姓名：张三", 28, 30, 24);
                        Draw(g, "邮箱：test@example.com", 510, 30, 24);
                        Draw(g, "订单号：A20261019", 28, 115, 24);
                        Draw(g, "金额：128.50元", 510, 115, 24);
                    }))
                    {
                        image.Save(Path.Combine(evidence, "columns.png"));
                        var result = ocr.RecognizeAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                        File.WriteAllText(Path.Combine(evidence, "columns.txt"), result.Text);
                        CheckBounds(result, image);
                        Assert(result.Text.Contains("张三") && result.Text.Contains("test@example.com") &&
                            result.Text.Contains("A20261019") && result.Text.Contains("128.50"), "多列识别内容缺失");
                        Assert(result.Lines.Any(l => l.Text.Contains("张三") && l.Bounds.Right < 450), "左列被扩展到右列");
                        Assert(result.Lines.Any(l => l.Text.Contains("test@") && l.Bounds.Left > 450), "右列坐标错误");
                    }
                });
                Run("13px 小字", () =>
                {
                    using (Bitmap image = MakeImage(800, 110, g =>
                    {
                        Draw(g, "本地识别 订单编号 ABC-20261019", 24, 28, 13);
                        Draw(g, "Small text Windows 1234567890", 24, 65, 13);
                    }))
                    {
                        image.Save(Path.Combine(evidence, "small.png"));
                        var result = ocr.RecognizeAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                        File.WriteAllText(Path.Combine(evidence, "small.txt"), result.Text);
                        CheckBounds(result, image);
                        Assert(result.Text.Contains("订单编号") && result.Text.Contains("ABC-20261019"), "13px 中文/标识符识别缺失");
                        Assert(result.Text.Contains("1234567890"), "13px 数字识别缺失");
                    }
                });
                Run("深色背景浅色文字", () =>
                {
                    using (Bitmap image = MakeImage(1000, 130, g =>
                    {
                        g.Clear(Color.FromArgb(34, 38, 44));
                        using (var font = new Font("Microsoft YaHei UI", 22, FontStyle.Regular, GraphicsUnit.Pixel))
                            g.DrawString("深色背景 文字识别 Dark mode 20261019", font, Brushes.White, 25, 35,
                                StringFormat.GenericTypographic);
                    }))
                    {
                        image.Save(Path.Combine(evidence, "dark.png"));
                        var result = ocr.RecognizeAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                        File.WriteAllText(Path.Combine(evidence, "dark.txt"), result.Text);
                        CheckBounds(result, image);
                        Assert(result.Text.Contains("深色背景") && result.Text.Replace(" ", "").Contains("Darkmode20261019"),
                            "深色背景识别失败");
                    }
                });
                Run("空白返回空结果", () =>
                {
                    using (Bitmap image = MakeImage(320, 120, g => { }))
                    {
                        var result = ocr.RecognizeAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                        Assert(result.Lines.Count == 0 && result.Text == "", "空白图不应产生内容");
                    }
                });
                Run("长图小字保持原始像素识别", () =>
                {
                    using (Bitmap image = MakeImage(560, 18000, g =>
                    {
                        Draw(g, "长截图顶部 TOP-123456", 24, 34, 13);
                        Draw(g, "切片边界 SEAM-246810", 24, 2968, 13);
                        Draw(g, "长截图中部 MIDDLE-654321", 24, 9010, 13);
                        Draw(g, "长截图底部 BOTTOM-987654", 24, 17930, 13);
                    }))
                    {
                        image.Save(Path.Combine(evidence, "tall.png"));
                        var result = ocr.RecognizeAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                        File.WriteAllText(Path.Combine(evidence, "tall.txt"), result.Text);
                        CheckBounds(result, image);
                        Assert(result.Text.Contains("TOP-123456") && result.Text.Contains("MIDDLE-654321") &&
                            result.Text.Contains("BOTTOM-987654"), "长图缩小后丢失小字");
                        Assert(result.Lines.Count(l => l.Text.Contains("SEAM-246810")) == 1,
                            "切片边界文字丢失或重复");
                        Assert(result.Lines.Any(l => l.Text.Contains("BOTTOM-") && l.Bounds.Top > 17800),
                            "长图底部坐标未映射到原图");
                    }
                });
                Run("已取消的调用不启动引擎", () =>
                {
                    using (Bitmap image = MakeImage(64, 64, g => { }))
                    using (var cancel = new CancellationTokenSource())
                    {
                        cancel.Cancel();
                        bool cancelled = false;
                        try { ocr.RecognizeAsync(image, cancel.Token).GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { cancelled = true; }
                        Assert(cancelled, "取消没有生效");
                    }
                });
                Run("运行中取消并回收进程", () =>
                {
                    using (Bitmap image = MakeImage(3200, 1800, g =>
                    {
                        for (int y = 12; y < 1750; y += 35)
                            Draw(g, "截图助手本地识别 OCR cancellation 1234567890", 25, y, 22);
                    }))
                    using (var cancel = new CancellationTokenSource())
                    {
                        var watch = Stopwatch.StartNew();
                        var work = ocr.RecognizeAsync(image, cancel.Token);
                        Thread.Sleep(100);
                        var processField = typeof(LocalOcr).GetField("activeProcess",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        var activeProcess = (Process)processField.GetValue(ocr);
                        int processId = activeProcess == null ? -1 : activeProcess.Id;
                        cancel.Cancel();
                        bool cancelled = false;
                        try { work.GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { cancelled = true; }
                        Assert(cancelled, "运行中的识别未取消");
                        Assert(watch.ElapsedMilliseconds < 5000, "取消后的调用未及时结束");
                        if (processId > 0)
                        {
                            bool exited;
                            try { using (Process child = Process.GetProcessById(processId)) exited = child.HasExited; }
                            catch (ArgumentException) { exited = true; }
                            Assert(exited, "取消后有当前调用的引擎进程残留");
                        }
                        // 下一次识别仍可运行，证明取消没有留下占用状态。
                        using (Bitmap blank = MakeImage(64, 64, g => { }))
                            Assert(ocr.RecognizeAsync(blank, CancellationToken.None).GetAwaiter().GetResult().Lines.Count == 0,
                                "取消后无法再次识别");
                    }
                });
                Run("同一实例并发请求立即拒绝", () =>
                {
                    using (Bitmap image = MakeImage(2500, 1500, g =>
                    {
                        for (int y = 12; y < 1450; y += 35)
                            Draw(g, "并发测试 Screenshot OCR 1234567890", 25, y, 22);
                    }))
                    using (var cancel = new CancellationTokenSource())
                    {
                        var first = ocr.RecognizeAsync(image, cancel.Token);
                        bool rejected = false;
                        try { ocr.RecognizeAsync(image, CancellationToken.None).GetAwaiter().GetResult(); }
                        catch (InvalidOperationException) { rejected = true; }
                        finally
                        {
                            cancel.Cancel();
                            try { first.GetAwaiter().GetResult(); }
                            catch (OperationCanceledException) { }
                        }
                        Assert(rejected, "并发调用进入了等待队列");
                    }
                });
            }
            Run("释放后拒绝调用", () =>
            {
                var ocr = new LocalOcr(engine);
                ocr.Dispose();
                using (Bitmap image = MakeImage(64, 64, g => { }))
                {
                    bool disposed = false;
                    try { ocr.RecognizeAsync(image, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (ObjectDisposedException) { disposed = true; }
                    Assert(disposed, "释放后仍接受调用");
                }
            });
            Console.WriteLine("RESULT " + passed + " passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error);
            return 1;
        }
    }
}
