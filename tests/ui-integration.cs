using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using XiaoJie;

internal static class UiIntegration
{
    private static readonly List<string> Report = new List<string>();
    private static string testOut;
    private static int failures;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        testOut = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(testOut);
        LocalOcr.DefaultEnginePath = Path.GetFullPath(args[0]);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        typeof(Toast).GetField("_ctx", BindingFlags.Static | BindingFlags.NonPublic)
            .SetValue(null, new SilentToastContext());
        Report.Add("UI 逻辑集成测试：只使用生成的虚构截图和 localhost mock。");
        Report.Add("程序集: " + typeof(OverlayForm).Assembly.Location);
        Report.Add("版本: " + typeof(OverlayForm).Assembly.GetName().Version);
        Report.Add("OCR: " + LocalOcr.DefaultEnginePath);
        Run("无 Key 本地 OCR 面板入口", TestOcrPanel);
        Run("智能打码入口与合成像素", TestSmartMosaic);
        Run("原位翻译重排编号及溢出完整面板", TestTranslation);
        Run("已手动遮挡的手机号不进入翻译请求", () => TestExistingRedaction(false));
        Run("已手动遮挡的手机号不进入隐私分类请求", () => TestExistingRedaction(true));
        Run("设置三 Tab 短屏滚动及控件持久化", TestSettings);
        Report.Add("结果: " + (failures == 0 ? "全部通过" : failures + " 项失败"));
        File.WriteAllLines(Path.Combine(testOut, "report.txt"), Report, new UTF8Encoding(false));
        foreach (string line in Report) Console.WriteLine(line);
        CloseOwnForms();
        return failures == 0 ? 0 : 1;
    }

    private sealed class SilentToastContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object state) { }
    }

    private static void Run(string name, Action test)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            test();
            Report.Add("[PASS] " + name + " (" + elapsed.ElapsedMilliseconds + " ms)");
        }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            failures++;
            Report.Add("[FAIL] " + name + ": " + error.Message);
        }
        finally { CloseOwnForms(); }
        File.WriteAllLines(Path.Combine(testOut, "report.txt"), Report, new UTF8Encoding(false));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Bitmap Fixture()
    {
        Bitmap image = new Bitmap(960, 600, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        using (Font font = new Font("Microsoft YaHei UI", 28, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.DrawString("LOCAL OCR SAMPLE", font, Brushes.Black, 40, 45);
            graphics.DrawString("Phone: 13800138000", font, Brushes.Black, 40, 145);
            graphics.DrawString("Email: demo@example.test", font, Brushes.Black, 40, 245);
            graphics.DrawString("Project Alpha", font, Brushes.Black, 40, 345);
        }
        return image;
    }

    private static AppConfig Config()
    {
        return new AppConfig
        {
            ApiKey = "", BaseUrl = "http://127.0.0.1:1/v1", FirstRunDone = true,
            AutoCopy = false, AutoSave = false, ShowColorValue = false,
            ShowDimensions = false, SemanticPrivacy = false, VisionPrivacy = false
        };
    }

    private static OverlayForm Overlay(AppConfig config)
    {
        Bitmap fixture = Fixture();
        fixture.Save(Path.Combine(testOut, "fixture.png"), ImageFormat.Png);
        OverlayForm form = new OverlayForm(fixture, new Rectangle(0, 0, 960, 600), config, null, null);
        form.SuppressActivation = true;
        form.ShowInTaskbar = false;
        form.Location = new Point(-32000, -32000);
        form.Show();
        Pump();
        Mouse(form, "OnMouseDown", new Point(10, 10));
        Mouse(form, "OnMouseMove", new Point(930, 550));
        Mouse(form, "OnMouseUp", new Point(930, 550));
        Check((bool)Get(form, "_hasSelection"), "输入事件未建立截图选区");
        return form;
    }

    private static void TestOcrPanel()
    {
        using (OverlayForm form = Overlay(Config()))
        {
            Call(form, "StartAi", AiPanelMode.Ocr);
            WaitUntil(() => Get(form, "_aiPanel") != null, 1000, "未创建 OCR 面板");
            AiResultPanel panel = (AiResultPanel)Get(form, "_aiPanel");
            TextBox box = (TextBox)Get(panel, "_ocrBox");
            WaitUntil(() => box.Text.Contains("13800138000"), 7000, "本地 OCR 面板未收到手机号；错误=" + ErrorText(panel));
            Check(box.Text.Contains("LOCAL") && box.Text.Contains("example.test"), "OCR 面板内容缺行: " + box.Text);
            Check(!((DeepSeekClient)Get(form, "_client")).HasApiKey, "OCR 用例意外配置了 Key");
            Render(panel, "ocr-panel.png");
            File.WriteAllText(Path.Combine(testOut, "ocr-text.txt"), box.Text, new UTF8Encoding(false));
            Call(form, "CloseAiPanel");
            Check(panel.IsDisposed && Get(form, "_aiPanel") == null, "关闭入口未释放 OCR 面板");
        }
    }

    private static void TestSmartMosaic()
    {
        using (OverlayForm form = Overlay(Config()))
        {
            Call(form, "StartSmartMosaic");
            WaitUntil(() => !(bool)Get(form, "_busy"), 7000, "智能打码未完成");
            List<Annotation> annotations = (List<Annotation>)Get(form, "_annotations");
            MosaicAnnotation mosaic = annotations.OfType<MosaicAnnotation>().SingleOrDefault();
            Check(mosaic != null && mosaic.Rects != null && mosaic.Rects.Count >= 2, "手机号和邮箱未同时产生打码区域");
            Rectangle selection = (Rectangle)Get(form, "_selection");
            Check(mosaic.Rects.Any(rect => rect.Contains(200, 165)), "手机号文字位置未被遮挡");
            Check(mosaic.Rects.Any(rect => rect.Contains(220, 265)), "邮箱文字位置未被遮挡");
            using (Bitmap composed = (Bitmap)Call(form, "ComposeSelection"))
            {
                Bitmap tiles = ((MosaicCanvas)Get(form, "_mosaic")).Pixelated;
                int checkedPixels = 0;
                foreach (RectangleF rect in mosaic.Rects)
                {
                    Rectangle interior = Rectangle.Ceiling(rect);
                    interior.Inflate(-2, -2);
                    interior.Intersect(selection);
                    for (int y = interior.Top; y < interior.Bottom; y++)
                        for (int x = interior.Left; x < interior.Right; x++)
                        {
                            Check(composed.GetPixel(x - selection.X, y - selection.Y).ToArgb() == tiles.GetPixel(x, y).ToArgb(),
                                "合成图片的打码区域仍混入原始像素: " + x + "," + y);
                            checkedPixels++;
                        }
                }
                Check(checkedPixels > 1000, "打码像素验证区域不足");
                Check(composed.GetPixel(800, 420).ToArgb() == Color.White.ToArgb(), "打码修改了范围外的空白区域");
                composed.Save(Path.Combine(testOut, "smart-mosaic.png"), ImageFormat.Png);
                Report.Add("[INFO] 实际遮挡 " + mosaic.Rects.Count + " 处，逐像素验证 " + checkedPixels + " 个像素。");
            }
        }
    }

    private static void TestTranslation()
    {
        using (MockApi mock = new MockApi())
        {
            AppConfig config = Config();
            config.ApiKey = "ui-local-test-key";
            config.BaseUrl = mock.Url;
            config.TranslateTarget = "English";
            using (OverlayForm form = Overlay(config))
            {
                Call(form, "StartTranslateInPlace");
                WaitUntil(() => !(bool)Get(form, "_busy"), 8000, "原位翻译未完成；mock=" + mock.Error);
                List<Annotation> annotations = (List<Annotation>)Get(form, "_annotations");
                TranslationOverlayAnnotation translation = annotations.OfType<TranslationOverlayAnnotation>().SingleOrDefault();
                Check(translation != null && translation.Blocks.Count >= 4, "翻译入口没有添加四行原位标注；mock=" + mock.Error);
                Dictionary<int, string> input = mock.InputLines;
                for (int index = 0; index < input.Count; index++)
                {
                    TranslationBlock block = translation.Blocks.Single(item => item.Original == input[index]);
                    Check(block.Translation == mock.Translation(index), "重排后的响应覆盖到错误的原文行: " + index);
                }
                Check(translation.Blocks.Single(block => block.Original.Contains("13800138000")).Rect.Contains(200, 165),
                    "翻译手机号行的本地坐标偏离原始文字");
                Check(translation.Blocks.Single(block => block.Original.Contains("example.test")).Rect.Contains(220, 265),
                    "翻译邮箱行的本地坐标偏离原始文字");
                Check(mock.ReturnedIds.SequenceEqual(input.Keys.OrderByDescending(id => id)), "mock 未实际重排 id");
                Check(mock.RequestCount == 1 && mock.RequestPath == "/v1/chat/completions", "翻译请求数量或 /v1 路径错误");
                Check(!mock.RequestBody.Contains("data:image") && !mock.RequestBody.Contains("image_url"), "本地 OCR 翻译仍上传截图");
                string translated = translation.Blocks.Single(block =>
                    block.Original == input[input.Keys.OrderBy(id => id).First()]).Translation;
                Check(input.Values.All(value => translation.Blocks.Any(block => block.Original == value)),
                    "翻译对象缺少原文");
                Check(translated.Contains(mock.Translation(0)), "翻译对象截断了长译文");
                using (Bitmap composed = (Bitmap)Call(form, "ComposeSelection"))
                    composed.Save(Path.Combine(testOut, "translate-overlay.png"), ImageFormat.Png);
                PumpFor(300);
                Check(mock.RequestCount == 1, "原位翻译重复请求了翻译服务");
                Report.Add("[INFO] mock 返回逆序 id: " + string.Join(",", mock.ReturnedIds) + "；完整译文 " + translated.Length + " 字符；HTTP 请求 1 次。");
            }
        }
    }

    private static void TestSettings()
    {
        AppConfig config = Config();
        using (SettingsForm form = new SettingsForm(config, null))
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Pump();
            form.ClientSize = new Size(760, 420);
            Call(form, "ApplyViewport");
            Control tabs = (Control)Get(form, "_tabs");
            string[] pages = { "_pageLocal", "_pageAi", "_pageShot" };
            string[] names = { "local", "api", "shot" };
            for (int index = 0; index < pages.Length; index++)
            {
                tabs.GetType().GetProperty("SelectedIndex").SetValue(tabs, index, null);
                Pump();
                Panel page = (Panel)Get(form, pages[index]);
                Check(page.Visible, "Tab 切换没有显示页面: " + index);
                Check(page.AutoScroll && page.AutoScrollMinSize.Height > page.ClientSize.Height,
                    "短屏页面缺少滚动范围: " + index + " / " + page.AutoScrollMinSize + " / " + page.ClientSize);
                page.AutoScrollPosition = new Point(0, page.AutoScrollMinSize.Height);
                Pump();
                Check(page.AutoScrollPosition.Y < 0, "页面无法滚动到底部: " + index);
                Render(form, "settings-" + names[index] + ".png");
            }
            NumericUpDown block = (NumericUpDown)Get(form, "_mosaicNum");
            block.Value = 40;
            object semantic = Get(form, "_semanticPrivacySwitch");
            object vision = Get(form, "_visionPrivacySwitch");
            semantic.GetType().GetProperty("Checked").SetValue(semantic, false, null);
            vision.GetType().GetProperty("Checked").SetValue(vision, true, null);
            Call(form, "SaveFromControls");
            AppConfig working = (AppConfig)Get(form, "_working");
            Check(working.MosaicBlock == 40 && !working.SemanticPrivacy && working.VisionPrivacy,
                "设置控件未写入待保存配置");
            // OnSaveClick also edits autostart; exercise its mapping and serializer without that side effect.
            ConfigStore store = new ConfigStore();
            Check(Path.GetDirectoryName(store.ConfigPath) == AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'),
                "配置文件未隔离到测试输出目录");
            store.Save(working);
            var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(store.ConfigPath));
            Check(Convert.ToDouble(values["MosaicBlock"]) == 40 && !(bool)values["SemanticPrivacy"] && (bool)values["VisionPrivacy"],
                "JSON 未持久化马赛克大小和两个检测开关");
            Report.Add("[INFO] 设置验证使用 SaveFromControls + ConfigStore.Save；未调用含自启动注册表写入的 OnSaveClick，未读取真实设置。");
        }
    }

    private static void TestExistingRedaction(bool privacy)
    {
        using (MockApi mock = new MockApi())
        {
            AppConfig config = Config();
            config.ApiKey = "ui-local-test-key";
            config.BaseUrl = mock.Url;
            config.SemanticPrivacy = privacy;
            using (OverlayForm form = Overlay(config))
            {
                Call(form, "AddAnnotation", new MosaicAnnotation {
                    Rects = new List<RectangleF> { new RectangleF(30, 140, 530, 56) }
                });
                Call(form, privacy ? "StartSmartMosaic" : "StartTranslateInPlace");
                WaitUntil(() => !(bool)Get(form, "_busy"), 7000, "已遮挡画面的识别未完成");
                WaitUntil(() => mock.RequestCount >= 1, 7000, "未收到预期的 localhost 文本请求: " + mock.Error);
                Check(mock.RequestCount == 1, "未收到预期的 localhost 文本请求: " + mock.Error);
                Check(!mock.RequestBody.Contains("13800138000"), "入口忽略已存在的手动打码，向服务端发送了原始手机号");
            }
        }
    }

    private static object Get(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new MissingFieldException(target.GetType().Name, name);
        return field.GetValue(target);
    }

    private static object Call(object target, string name, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null) throw new MissingMethodException(target.GetType().Name, name);
        return method.Invoke(target, arguments);
    }

    private static void Mouse(Form form, string method, Point point)
    {
        Call(form, method, new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
    }

    private static void WaitUntil(Func<bool> condition, int milliseconds, string message)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(10); }
        Check(condition(), message);
    }

    private static void Pump() { Application.DoEvents(); }

    private static void PumpFor(int milliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(10); }
    }

    private static void Render(Control control, string name)
    {
        using (Bitmap bitmap = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppArgb))
        {
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(testOut, name), ImageFormat.Png);
        }
    }

    private static string ErrorText(AiResultPanel panel)
    {
        Control view = (Control)Get(panel, "_ocrError");
        return string.Join(" / ", view.Controls.Cast<Control>().Select(control => control.Text));
    }

    private static void CloseOwnForms()
    {
        foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
        {
            try { form.Close(); form.Dispose(); } catch { }
        }
        Pump();
    }

    private sealed class MockApi : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly Task server;
        internal readonly Dictionary<int, string> InputLines = new Dictionary<int, string>();
        internal readonly List<int> ReturnedIds = new List<int>();
        internal readonly string Url;
        internal volatile int RequestCount;
        internal string RequestBody = "";
        internal string RequestPath = "";
        internal string Error = "";

        internal MockApi()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = "http://127.0.0.1:" + port + "/v1";
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            listener.Start();
            server = Task.Run(async () =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
                        RequestPath = context.Request.Url.AbsolutePath;
                        using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                            RequestBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                        Interlocked.Increment(ref RequestCount);
                        JavaScriptSerializer serializer = new JavaScriptSerializer();
                        var root = serializer.Deserialize<Dictionary<string, object>>(RequestBody);
                        var messages = (ArrayList)root["messages"];
                        var user = (Dictionary<string, object>)messages[messages.Count - 1];
                        var input = serializer.Deserialize<Dictionary<string, object>>((string)user["content"]);
                        var lines = ((ArrayList)input["lines"]).Cast<Dictionary<string, object>>().Reverse().ToArray();
                        List<object> output = new List<object>();
                        foreach (var line in lines)
                        {
                            int id = (int)line["id"];
                            InputLines[id] = (string)line["text"];
                            ReturnedIds.Add(id);
                            output.Add(new { id = id, translation = Translation(id), sensitive = false });
                        }
                        string content = serializer.Serialize(new { lines = output });
                        byte[] body = Encoding.UTF8.GetBytes(serializer.Serialize(new {
                            choices = new[] { new { message = new { content = content }, finish_reason = "stop" } }
                        }));
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = body.Length;
                        using (Stream response = context.Response.OutputStream)
                            await response.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
                    }
                }
                catch (HttpListenerException) { }
                catch (ObjectDisposedException) { }
                catch (Exception error) { Error = error.ToString(); }
            });
        }

        internal string Translation(int id)
        {
            return id == 0 ? "译文起始 " + string.Join(" ", Enumerable.Repeat("long translation", 40)) + " 译文结束" : "translated line " + id;
        }

        public void Dispose()
        {
            listener.Close();
            try { server.Wait(1000); } catch (AggregateException) { }
        }
    }
}
