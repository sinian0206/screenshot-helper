using System;
using System.Drawing;
using System.Text;

namespace XiaoJie
{
    internal static class OcrFormatsRegression
    {
        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                var source = new LocalOcrResult();
                Add(source, "  中 文  hello   world  2026  ", 10, 10, 300, 20);
                Add(source, "API  Key：  abc def", 10, 40, 300, 20);
                var formatted = OcrTextFormatter.Format(source, "clean", false);
                Check(formatted.Text == "中文 hello world 2026" + Environment.NewLine + "API Key： abc def", "清理空格应保留英文词边界。");
                Check(source.Lines[0].Text == "  中 文  hello   world  2026  ", "输出格式修改了原识别内容。");
                Check(OcrTextFormatter.Format(source, "lines", false).Text == source.Text, "无法恢复原始换行与空格。");
                Console.WriteLine("[PASS] 中英空格清理、原文可恢复");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("[FAIL] " + error.Message); return 1; }
        }

        private static void Add(LocalOcrResult result, string text, float x, float y, float width, float height)
        {
            result.Lines.Add(new LocalOcrLine { Text = text, Bounds = new RectangleF(x, y, width, height), Confidence = .98f });
        }

        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
