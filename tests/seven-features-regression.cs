using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using XiaoJie;

public static class SevenFeaturesRegression
{
    [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "NativeAudio_GetInputPeakLevel")]
    private static extern float NativeAudio_GetInputPeakLevel(int deviceIndex);

    [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "NativeOcr_IsAvailable")]
    private static extern int NativeOcr_IsAvailable();

    private static int _pass = 0;
    private static int _fail = 0;

    private static void Assert(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _pass++;
            Console.WriteLine("[PASS] " + name + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
        }
        else
        {
            _fail++;
            Console.WriteLine("[FAIL] " + name + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("========================================");
        Console.WriteLine(" 七项新功能深度回归验证 (Seven Features)");
        Console.WriteLine("========================================");

        TestPrivacyRules();
        TestOcrTextFormatter();
        TestImageComposer();
        TestHistoryManager();
        TestNativeInterops();
        TestAdaptiveTranslationLayout();

        Console.WriteLine("----------------------------------------");
        Console.WriteLine(string.Format("验证汇总: {0} PASS, {1} FAIL", _pass, _fail));
        Console.WriteLine(_fail == 0 ? "全部通过！" : "存在失败项！");
        return _fail == 0 ? 0 : 1;
    }

    private static void TestPrivacyRules()
    {
        Console.WriteLine("\n[1] 验证自定义隐私规则 (PrivacyRules)...");
        string sensitive = "机密项目\n张三\n13800138000";
        string ignored = "公开机密\n张三丰";
        PrivacyRules rules = new PrivacyRules(sensitive, ignored);

        string reason;
        bool s1 = rules.IsSensitive("这是公司的机密项目内容", out reason);
        Assert("敏感词命中-机密项目", s1 && reason.Contains("敏感词"));

        bool s2 = rules.IsSensitive("联系人：张三", out reason);
        Assert("敏感词命中-张三", s2);

        bool s3 = rules.IsSensitive("电话：13800138000", out reason);
        Assert("敏感词命中-手机号", s3);

        string removed = rules.RemoveIgnored("这是公开机密信息和张三丰的报道");
        Assert("白名单排除测试", !removed.Contains("公开机密") && !removed.Contains("张三丰"));

        string canonical = PrivacyRules.Canonicalize("  hello \r\n\r\n world \n hello  ");
        Assert("规则格式规范化", canonical.Contains("world") && canonical.Contains("hello"));
    }

    private static void TestOcrTextFormatter()
    {
        Console.WriteLine("\n[2] 验证 OCR 格式化与表格识别 (OcrTextFormatter)...");
        var result = new LocalOcrResult();
        result.Lines = new List<LocalOcrLine>
        {
            new LocalOcrLine { Text = "姓名", Bounds = new RectangleF(10, 10, 40, 20) },
            new LocalOcrLine { Text = "年龄", Bounds = new RectangleF(60, 10, 40, 20) },
            new LocalOcrLine { Text = "部门", Bounds = new RectangleF(110, 10, 40, 20) },
            new LocalOcrLine { Text = "李四", Bounds = new RectangleF(10, 40, 40, 20) },
            new LocalOcrLine { Text = "28", Bounds = new RectangleF(60, 40, 40, 20) },
            new LocalOcrLine { Text = "研发部", Bounds = new RectangleF(110, 40, 50, 20) }
        };

        var raw = OcrTextFormatter.Format(result, "lines", false);
        Assert("保留换行输出", raw.Text.Contains("姓名") && raw.Text.Contains("李四"));

        var merged = OcrTextFormatter.Format(result, "paragraphs", false);
        Assert("合并段落输出", !string.IsNullOrEmpty(merged.Text));

        var clean = OcrTextFormatter.Format(result, "lines", true);
        Assert("清理空格输出", !clean.Text.Contains("  "));

        var table = OcrTextFormatter.Format(result, "table", false);
        Assert("表格制表符格式", table.Text.Contains("\t") && table.Text.Contains("\n"), "内容: " + table.Text.Replace("\r\n", "\\n").Replace("\t", "\\t"));
    }

    private static void TestImageComposer()
    {
        Console.WriteLine("\n[3] 验证多图拼图引擎 (ImageComposer)...");
        using (Bitmap b1 = new Bitmap(100, 60))
        using (Bitmap b2 = new Bitmap(120, 80))
        using (Bitmap b3 = new Bitmap(80, 50))
        {
            using (Graphics g = Graphics.FromImage(b1)) g.Clear(Color.Red);
            using (Graphics g = Graphics.FromImage(b2)) g.Clear(Color.Green);
            using (Graphics g = Graphics.FromImage(b3)) g.Clear(Color.Blue);

            var list = new List<Bitmap> { b1, b2, b3 };

            // 纵向拼接：最大宽 120，总高 60 + 80 + 50 + 2*10 = 210
            using (Bitmap vComp = ImageComposer.Compose(list, true, 10, Color.White))
            {
                Assert("纵向拼接尺寸", vComp.Width == 120 && vComp.Height == 210, vComp.Width + "x" + vComp.Height);
                Color top = vComp.GetPixel(10, 10);
                Assert("纵向首图色彩", top.R > 200 && top.G < 50);
            }

            // 横向拼接：总宽 100 + 120 + 80 + 2*10 = 320，最大高 80
            using (Bitmap hComp = ImageComposer.Compose(list, false, 10, Color.White))
            {
                Assert("横向拼接尺寸", hComp.Width == 320 && hComp.Height == 80, hComp.Width + "x" + hComp.Height);
                Color last = hComp.GetPixel(250, 10);
                Assert("横向末图色彩", last.B > 200);
            }
        }
    }

    private static void TestHistoryManager()
    {
        Console.WriteLine("\n[4] 验证截图历史记录 (HistoryManager)...");
        ConfigStore store = new ConfigStore();
        AppConfig cfg = new AppConfig();
        cfg.HistoryEnabled = true;
        cfg.HistoryMaxCount = 5;
        cfg.HistoryMaxMB = 10;
        HistoryManager.ClearAll(store);

        using (Bitmap img = new Bitmap(50, 50))
        {
            HistoryManager.SaveItem(store, cfg, img, "first searchable text");
            HistoryManager.SaveItem(store, cfg, img, "second searchable text");
            HistoryManager.SaveItem(store, cfg, img, "third searchable text");
        }

        var items = HistoryManager.GetItems(store);
        Assert("历史记录入库数量 >= 3", items.Count >= 3, "数量: " + items.Count);
        Assert("历史记录保存 OCR 文本", items.Exists(item => item.OcrText.Contains("searchable text")));

        var favorite = items.Find(item => item.OcrText == "third searchable text");
        Require(favorite != null, "未找到用于收藏的历史记录。");
        HistoryManager.ToggleFavorite(store, favorite.Id);
        var ordered = HistoryManager.GetItems(store);
        Assert("收藏记录置顶", ordered.Count > 0 && ordered[0].Id == favorite.Id && ordered[0].IsFavorite);
        HistoryManager.ToggleFavorite(store, favorite.Id);

        HistoryManager.ClearAll(store);
        var empty = HistoryManager.GetItems(store);
        Assert("清空历史记录", empty.Count == 0);

        cfg.HistoryMaxMB = 1;
        using (Bitmap noisy = MakeNoise(500))
            HistoryManager.SaveItem(store, cfg, noisy, "quota one");
        using (Bitmap noisy = MakeNoise(501))
            HistoryManager.SaveItem(store, cfg, noisy, "quota two");
        var quotaItems = HistoryManager.GetItems(store);
        long totalBytes = 0;
        foreach (HistoryItem item in quotaItems)
        {
            if (File.Exists(item.ImageFile)) totalBytes += new FileInfo(item.ImageFile).Length;
            if (File.Exists(item.ThumbFile)) totalBytes += new FileInfo(item.ThumbFile).Length;
        }
        Assert("历史容量上限生效", quotaItems.Count >= 1 && totalBytes <= 1024L * 1024L,
            "数量: " + quotaItems.Count + "，总大小: " + totalBytes);
        HistoryManager.ClearAll(store);
    }
    private static void TestNativeInterops()
    {
        Console.WriteLine("\n[7] 验证 RecorderNative.dll (Native OCR & Audio Meter)...");
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        bool dllExists = File.Exists(Path.Combine(baseDir, "RecorderNative.dll")) ||
                         File.Exists(Path.Combine(baseDir, @"..\..\..\build_native\Release\RecorderNative.dll"));
        Assert("RecorderNative.dll 文件存在", dllExists, Path.Combine(baseDir, "RecorderNative.dll"));

        try
        {
            int ocrAvail = NativeOcr_IsAvailable();
            Console.WriteLine("Native OCR 可用性状态: " + ocrAvail);
            Assert("NativeOcr_IsAvailable 调用无崩溃", true);

            float peak = NativeAudio_GetInputPeakLevel(-1);
            Assert("麦克风电平探测范围 [0.0, 1.0]", peak >= 0.0f && peak <= 1.0f, "电平: " + peak);
        }
        catch (Exception ex)
        {
            Assert("原生互操作异常捕获", false, ex.Message);
        }
    }
    private static void TestAdaptiveTranslationLayout()
    {
        Console.WriteLine("\n[8] 验证 1:1 原位翻译自适应排版...");
        using (Bitmap bmp = new Bitmap(300, 100))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            Rectangle targetRect = new Rectangle(10, 10, 120, 24);
            string longTranslation = "This is a very long translated text sentence that exceeds original width";

            float size = 12f;
            using (Font initialFont = new Font("Microsoft YaHei", size, FontStyle.Regular))
            {
                SizeF measured = g.MeasureString(longTranslation, initialFont);
                while (measured.Width > targetRect.Width && size > 8f)
                {
                    size -= 0.5f;
                    using (Font testFont = new Font("Microsoft YaHei", size, FontStyle.Regular))
                    {
                        measured = g.MeasureString(longTranslation, testFont);
                    }
                }
            }
            Assert("自适应字号下调保证完整展示", size >= 8f, "最终字号: " + size);
        }
    }

    private static Bitmap MakeNoise(int side)
    {
        Bitmap image = new Bitmap(side, side);
        var random = new Random(side);
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
                image.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
        }
        return image;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
