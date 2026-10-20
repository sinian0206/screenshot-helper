using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace XiaoJie
{
    internal static class OcrPipelineRegression
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                using (Bitmap image = MakeFixture())
                {
                    var cfg = new AppConfig { ApiKey = "", BaseUrl = "http://127.0.0.1:1/v1", TimeoutSec = 10 };
                    var client = new DeepSeekClient(cfg);
                    string text = client.OcrAsync(image, null, null, CancellationToken.None).GetAwaiter().GetResult();
                    Check(text.Contains("13800138000"), "无 Key、无网络可提取手机号");
                    Check(text.Contains("hello"), "本地中英混排提取");
                    var privacy = client.AnalyzePrivacyAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                    Check(privacy.Lines.Any(line => line.Sensitive && line.Text.Contains("13800138000")), "无 Key 本地隐私检测");
                    Check(privacy.Lines.All(line => line.HasRect), "隐私结果保留 OCR 坐标");
                    Check(privacy.Lines.Any(line => line.Text.Contains("13800138000") && line.Y > 0.30f && line.Y < 0.65f), "敏感行坐标对应真实内容");
                }
                TestTranslation(false);
                TestTranslation(true);
                TestPrivacyFallback();
                TestRequestTimeout();
                TestVisualRegions();
                TestInvalidVisualRegions();
                TestPrivacyRules();
                var tiny = new ImageTextBlock { Text = "13800138000", Sensitive = true, HasLocalCoordinates = true, X = .1f, Y = .8f, W = .5f, H = .0015f };
                Check(tiny.HasRect, "长截图18px文字框仍是有效本地坐标");
                int textCount, faceCount;
                var masks = PrivacyDetector.BuildMaskRects(null, new PrivacyAnalysis { HasLocalCoordinates = true, Lines = new List<ImageTextBlock> { tiny } },
                    new Rectangle(0, 0, 600, 12000), .18f, out textCount, out faceCount);
                Check(masks.Count == 1 && masks[0].Y > 9500 && masks[0].Height >= 18, "长截图隐私文字不因归一化高度小而漏码");
                var faceMasks = PrivacyDetector.BuildMaskRects(null, new PrivacyAnalysis { Faces = new List<FaceBox> {
                    new FaceBox { X = .1f, Y = .8f, W = 40f / 600, H = 40f / 30000 } } },
                    new Rectangle(0, 0, 600, 30000), .18f, out textCount, out faceCount);
                Check(faceMasks.Count == 1 && faceMasks[0].Height >= 40, "长截图小头像区域不因比例过小而漏码");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FAIL] " + ex.Message);
                return 1;
            }
        }

        private static void TestPrivacyRules()
        {
            string reason;
            Check(PrivacyDetector.LooksSensitive("电话：+86 138-0013-8000", null, out reason), "分组手机号本地召回");
            Check(PrivacyDetector.LooksSensitive("手机：１３８００１３８０００", null, out reason), "全角数字手机号本地召回");
            Check(PrivacyDetector.LooksSensitive("身份证：110101 19900101 123X", null, out reason), "分组且末尾 X 的证件号召回");
            Check(PrivacyDetector.LooksSensitive("姓名 张三", null, out reason), "无冒号的姓名字段召回");
            Check(!PrivacyDetector.LooksSensitive("软件版本号是2026年10月", null, out reason), "普通版本号文案不误判地址");
        }

        private static void TestTranslation(bool invalid)
        {
            using (var server = new MockApi(invalid ? "duplicate" : "reverse"))
            using (Bitmap image = MakeFixture())
            {
                var client = new DeepSeekClient(new AppConfig { ApiKey = "test-key", BaseUrl = server.Url, TextModel = "text-model" });
                try
                {
                    var blocks = client.TranslateInPlaceAsync(image, "English", CancellationToken.None).GetAwaiter().GetResult();
                    Check(!invalid, "完整编号响应可用");
                    Check(blocks.All(block => block.Translation == "translated:" + block.Text), "响应乱序时译文仍关联原始行");
                    Check(blocks[1].Y > blocks[0].Y && blocks.All(block => block.HasLocalCoordinates), "翻译保留原始 OCR 坐标");
                    Check(!server.Request.Contains("image_url") && !server.Request.Contains("base64"), "翻译请求仅发送文字");
                    Check(server.Path == "/v1/chat/completions", "网关请求路径包含 /v1");
                }
                catch (AiException)
                {
                    if (!invalid) throw;
                    Check(true, "重复编号响应失败时不返回部分覆盖结果");
                }
            }
        }

        private static void TestPrivacyFallback()
        {
            using (var server = new MockApi("malformed"))
            using (Bitmap image = MakeFixture())
            {
                var client = new DeepSeekClient(new AppConfig { ApiKey = "test-key", BaseUrl = server.Url, SemanticPrivacy = true });
                var result = client.AnalyzePrivacyAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                Check(result.Lines.Any(line => line.Sensitive && line.Text.Contains("13800138000")), "联网分类失败仍保留本地规则命中");
                Check(result.Warning.Contains("未完成"), "联网失败明确报告检测未完成");
                Check(!server.Request.Contains("image_url"), "默认隐私分类不上传图片");
            }
        }

        private static void TestRequestTimeout()
        {
            using (var server = new MockApi("hang"))
            using (var cancellation = new CancellationTokenSource(3000))
            {
                var client = new DeepSeekClient(new AppConfig { ApiKey = "test-key", BaseUrl = server.Url, TimeoutSec = 1 });
                try
                {
                    client.TestConnectionAsync(cancellation.Token).GetAwaiter().GetResult();
                    Check(false, "超时请求应终止");
                }
                catch (AiException ex) { Check(ex.Message.Contains("超时"), "异步请求按配置超时且区别于用户取消"); }
            }
        }

        private static void TestVisualRegions()
        {
            using (var server = new MockApi("visual"))
            using (Bitmap image = MakeFixture())
            {
                var client = new DeepSeekClient(new AppConfig { ApiKey = "test-key", BaseUrl = server.Url, VisionModel = "vision-model", SemanticPrivacy = false, VisionPrivacy = true });
                var result = client.AnalyzePrivacyAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                Check(result.Faces.Count == 2, "启用视觉检测时同时保留头像和二维码区域");
                Check(server.Request.Contains("image_url"), "仅显式启用视觉检测时上传测试选区");
            }
        }

        private static void TestInvalidVisualRegions()
        {
            using (var server = new MockApi("visualmissing"))
            using (Bitmap image = MakeFixture())
            {
                var client = new DeepSeekClient(new AppConfig { ApiKey = "test-key", BaseUrl = server.Url, VisionModel = "vision-model", SemanticPrivacy = false, VisionPrivacy = true });
                var result = client.AnalyzePrivacyAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                Check(result.Faces.Count == 0 && result.Warning.Contains("未完成"), "视觉框缺少坐标时拒绝错误遮挡并报告未完成");
            }
        }

        private sealed class MockApi : IDisposable
        {
            private readonly HttpListener listener = new HttpListener();
            private readonly Task server;
            private readonly ManualResetEventSlim release = new ManualResetEventSlim();
            internal readonly string Url;
            internal string Request = "";
            internal string Path = "";

            internal MockApi(string mode)
            {
                var portFinder = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                portFinder.Start();
                int port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
                portFinder.Stop();
                Url = "http://127.0.0.1:" + port + "/v1";
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                listener.Start();
                server = Task.Run(async delegate
                {
                    var context = await listener.GetContextAsync();
                    Path = context.Request.Url.AbsolutePath;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) Request = reader.ReadToEnd();
                    if (mode == "hang") { release.Wait(); context.Response.Abort(); return; }
                    var serializer = new JavaScriptSerializer();
                    if (mode.StartsWith("visual", StringComparison.Ordinal))
                    {
                        string visual = mode == "visualmissing" ? "{\"faces\":[{\"w\":0.2,\"h\":0.2}],\"codes\":[]}" : "{\"faces\":[{\"x\":0.1,\"y\":0.1,\"w\":0.15,\"h\":0.2}],\"codes\":[{\"x\":0.6,\"y\":0.5,\"w\":0.2,\"h\":0.3}]}";
                        byte[] visualBytes = Encoding.UTF8.GetBytes(serializer.Serialize(new { choices = new[] { new { message = new { content = visual }, finish_reason = "stop" } } }));
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = visualBytes.Length;
                        using (var stream = context.Response.OutputStream) await stream.WriteAsync(visualBytes, 0, visualBytes.Length);
                        return;
                    }
                    var root = serializer.Deserialize<Dictionary<string, object>>(Request);
                    var messages = (ArrayList)root["messages"];
                    var user = (Dictionary<string, object>)messages[messages.Count - 1];
                    var input = serializer.Deserialize<Dictionary<string, object>>((string)user["content"]);
                    var lines = ((ArrayList)input["lines"]).Cast<Dictionary<string, object>>().Reverse().ToList();
                    var output = new List<object>();
                    foreach (var line in lines) output.Add(new { id = mode == "duplicate" ? 0 : (int)line["id"], translation = "translated:" + line["text"] });
                    string content = mode == "malformed" ? "{}" : serializer.Serialize(new { lines = output });
                    string response = serializer.Serialize(new { choices = new[] { new { message = new { content = content }, finish_reason = "stop" } } });
                    byte[] bytes = Encoding.UTF8.GetBytes(response);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    using (var stream = context.Response.OutputStream) await stream.WriteAsync(bytes, 0, bytes.Length);
                });
            }

            public void Dispose()
            {
                release.Set();
                listener.Close();
                try { server.GetAwaiter().GetResult(); }
                catch (HttpListenerException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private static Bitmap MakeFixture()
        {
            var image = new Bitmap(720, 230, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(image))
            using (Font font = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel))
            {
                g.Clear(Color.White);
                g.DrawString("本地文字识别 hello", font, Brushes.Black, 24, 24);
                g.DrawString("手机号：13800138000", font, Brushes.Black, 24, 94);
                g.DrawString("普通内容", font, Brushes.Black, 24, 164);
            }
            return image;
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("[PASS] " + name);
        }
    }
}
