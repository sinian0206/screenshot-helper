using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using XiaoJie;

internal static class OcrIconTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static LocalOcrResult Recognize(LocalOcr ocr, Bitmap image, string name, string output)
    {
        var watch = Stopwatch.StartNew();
        LocalOcrResult result = ocr.RecognizeAsync(image, CancellationToken.None).GetAwaiter().GetResult();
        string report = string.Join(Environment.NewLine, result.Lines.Select(line => string.Format(
            CultureInfo.InvariantCulture, "{0}\t{1:0.000}\t{2:0},{3:0},{4:0},{5:0}", line.Text,
            line.Confidence, line.Bounds.X, line.Bounds.Y, line.Bounds.Width, line.Bounds.Height)));
        Console.WriteLine(name + " elapsed_ms=" + watch.ElapsedMilliseconds);
        Console.WriteLine(report);
        File.WriteAllText(Path.Combine(output, name + ".txt"), report);
        return result;
    }

    private static Bitmap MakeShortText(bool withBody)
    {
        var image = new Bitmap(480, 330, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(image))
        using (var font = new Font("Microsoft YaHei UI", 26, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            string[] samples = { "G", "3", "日", "电", "gro" };
            for (int index = 0; index < samples.Length; index++)
            {
                graphics.DrawString(samples[index], font, Brushes.Black, 40, 20 + index * 60,
                    StringFormat.GenericTypographic);
                if (withBody)
                    graphics.DrawString("合法正文 Text", font, Brushes.Black, 120, 20 + index * 60,
                        StringFormat.GenericTypographic);
            }
        }
        return image;
    }

    private static void CheckUserResult(LocalOcrResult actual, float scale)
    {
        Assert(actual.Text.Contains("输出内容") && actual.Text.Contains("创建文件或站点") &&
            actual.Text.Contains("子智能体") && actual.Text.Contains("网页搜索") &&
            actual.Text.Contains("查看全部") && actual.Text.Contains("yyapi.cloud/v1"), "正文丢失");
        Assert(!actual.Lines.Any(line => line.Bounds.Left / scale < 72 && line.Bounds.Top / scale > 210 &&
            line.Bounds.Height / scale < 21), "图标或卡片边缘被识别成独立文字");
        Assert(actual.Lines.Any(line => line.Text.Replace(" ", "") == "3完成" &&
            line.Bounds.Left / scale > 100 && line.Bounds.Right / scale >= 150),
            "表情图标仍并入 3完成 文字框，或文字右端被截断");
    }

    private static int Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(args[2]);
            using (var ocr = new LocalOcr(args[0]))
            using (var image = new Bitmap(args[1]))
            using (Bitmap shortText = MakeShortText(false))
            using (Bitmap shortLabels = MakeShortText(true))
            {
                LocalOcrResult actual = Recognize(ocr, image, "user-icons", args[2]);
                CheckUserResult(actual, 1);
                using (var scaled = new Bitmap(image.Width * 2, image.Height * 2))
                {
                    using (Graphics graphics = Graphics.FromImage(scaled))
                        graphics.DrawImage(image, new Rectangle(0, 0, scaled.Width, scaled.Height));
                    CheckUserResult(Recognize(ocr, scaled, "user-icons-2x", args[2]), 2);
                }
                shortText.Save(Path.Combine(args[2], "short-text.png"));
                LocalOcrResult shortActual = Recognize(ocr, shortText, "short-text", args[2]);
                shortLabels.Save(Path.Combine(args[2], "short-labels.png"));
                LocalOcrResult labelActual = Recognize(ocr, shortLabels, "short-labels", args[2]);
                foreach (string expected in new[] { "G", "3", "日", "电", "gro" })
                {
                    Assert(shortActual.Lines.Any(line => line.Text.Trim() == expected), "合法短文字丢失：" + expected);
                    Assert(labelActual.Lines.Any(line => line.Text.Trim() == expected), "同行正文左侧的合法短文字丢失：" + expected);
                }
            }
            Console.WriteLine("PASS 用户图标误识别与合法短文字保留");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error.Message);
            return 1;
        }
    }
}
