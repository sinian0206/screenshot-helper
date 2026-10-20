using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace XiaoJie
{
    internal static class PrivacySpeedRegression
    {
        private static int failures;

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            LocalOcr.DefaultEnginePath = args[0];
            Application.EnableVisualStyles();
            typeof(Toast).GetField("_ctx", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, new SilentToastContext());
            Run("慢联网前已显示本地规则遮挡，后台补漏合并且一次撤销", TestProgressive);
            Run("慢联网取消保留本地结果且不提交晚到响应", TestCancel);
            Run("撤销本地遮挡后晚到结果不重新打码", TestUndo);
            Run("选区改变后晚到结果不映射到新选区", TestSelectionChange);
            Run("慢联网超时保留本地遮挡并结束状态", TestTimeout);
            Run("本地规则已命中原文不进入联网语义请求", TestTextRedaction);
            Run("本地回调前取消不上传也不应用遮挡", TestEarlyCancel);
            Run("旧请求结束不干扰新请求的状态或遮挡", TestNewRequest);
            Run("视觉请求图片先遮挡本地已命中文字", TestVisualRedaction);
            Console.WriteLine("结果: " + (failures == 0 ? "全部通过" : failures + " 项失败"));
            return failures == 0 ? 0 : 1;
        }

        private sealed class SilentToastContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object state) { }
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("[PASS] " + name); }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                failures++;
                Console.WriteLine("[FAIL] " + name + ": " + error);
            }
            finally
            {
                foreach (Form form in Application.OpenForms.Cast<Form>().ToArray()) form.Dispose();
                Application.DoEvents();
            }
        }

        private static Bitmap Fixture()
        {
            Bitmap image = new Bitmap(720, 220);
            using (Graphics graphics = Graphics.FromImage(image))
            using (Font font = new Font("Microsoft YaHei UI", 26, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("Phone: 13800138000", font, Brushes.Black, 24, 26);
                graphics.DrawString("Project Alpha", font, Brushes.Black, 24, 106);
            }
            return image;
        }

        private static OverlayForm Open(SlowApi api, int timeoutSec = 10)
        {
            var cfg = new AppConfig { ApiKey = "fixture-key", BaseUrl = api.Url,
                TimeoutSec = timeoutSec, SemanticPrivacy = true, VisionPrivacy = false };
            var overlay = new OverlayForm(Fixture(), new Rectangle(0, 0, 720, 220), cfg, null, null);
            overlay.SuppressActivation = true;
            overlay.Location = new Point(-32000, -32000);
            overlay.Show();
            Set(overlay, "_selection", new Rectangle(0, 0, 720, 220));
            Set(overlay, "_hasSelection", true);
            Call(overlay, "StartSmartMosaic");
            return overlay;
        }

        private static IList Annotations(OverlayForm overlay) { return (IList)Get(overlay, "_annotations"); }
        private static int Masks(OverlayForm overlay)
        {
            return Annotations(overlay).Cast<Annotation>().OfType<MosaicAnnotation>().Sum(annotation => annotation.Rects.Count);
        }

        private static void LocalReady(OverlayForm overlay, SlowApi api)
        {
            Wait(() => api.Received.IsSet, 5000, "真实 OCR 后未发起 localhost 请求");
            Wait(() => Masks(overlay) > 0 && !(bool)Get(overlay, "_busy"), 1000,
                "本地规则结果仍等待慢联网，或编辑器仍 busy");
            Check(Get(overlay, "_privacyStatus") is string && ((string)Get(overlay, "_privacyStatus")).Contains("补漏"), "缺少联网阶段状态");
        }

        private static void TestProgressive()
        {
            using (var api = new SlowApi())
            using (OverlayForm overlay = Open(api))
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                LocalReady(overlay, api);
                long localMs = elapsed.ElapsedMilliseconds;
                Check(Masks(overlay) == 1, "本地规则应先只遮挡手机号");
                Pump(1500);
                Check(Masks(overlay) == 1, "服务器未响应前不应提交语义结果");
                api.Release.Set();
                Wait(() => Masks(overlay) >= 2, 3000, "语义补漏未生效");
                Check(Annotations(overlay).Count == 1, "同轮分阶段结果应合并成一个撤销项");
                Check(Masks(overlay) == 2, "本地命中重复添加");
                Console.WriteLine("[TIME] local=" + localMs + " ms, total=" + elapsed.ElapsedMilliseconds + " ms, mockHold=1500 ms");
                Call(overlay, "Undo");
                Check(Annotations(overlay).Count == 0, "一次撤销未移除本轮完整打码");
            }
        }

        private static void TestCancel()
        {
            using (var api = new SlowApi())
            using (OverlayForm overlay = Open(api))
            {
                LocalReady(overlay, api);
                var key = typeof(OverlayForm).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
                object[] parameters = { Message.Create(overlay.Handle, 0x100, IntPtr.Zero, IntPtr.Zero), Keys.Escape };
                key.Invoke(overlay, parameters);
                Check(!overlay.IsDisposed && Masks(overlay) == 1, "Esc 应取消补漏并保留本地结果");
                api.Release.Set();
                Pump(500);
                Check(Masks(overlay) == 1, "取消后晚到响应仍提交");
            }
        }

        private static void TestUndo()
        {
            using (var api = new SlowApi())
            using (OverlayForm overlay = Open(api))
            {
                LocalReady(overlay, api);
                Call(overlay, "Undo");
                api.Release.Set();
                Pump(500);
                Check(Annotations(overlay).Count == 0, "撤销后语义回调重新添加了蒙版");
            }
        }

        private static void TestSelectionChange()
        {
            using (var api = new SlowApi())
            using (OverlayForm overlay = Open(api))
            {
                LocalReady(overlay, api);
                Set(overlay, "_selection", new Rectangle(20, 20, 400, 160));
                api.Release.Set();
                Pump(500);
                Check(Masks(overlay) == 1, "旧坐标映射到新选区");
            }
        }

        private static void TestTimeout()
        {
            using (var api = new SlowApi())
            using (OverlayForm overlay = Open(api, 1))
            {
                LocalReady(overlay, api);
                Wait(() => string.IsNullOrEmpty((string)Get(overlay, "_privacyStatus")), 3000, "请求超时后阶段状态未结束");
                Check(Masks(overlay) == 1 && !(bool)Get(overlay, "_busy"), "超时丢失本地结果或编辑器仍 busy");
            }
        }

        private static void TestTextRedaction()
        {
            using (var api = new SlowApi())
            using (OverlayForm overlay = Open(api))
            {
                Wait(() => api.Received.IsSet, 5000, "未收到语义请求");
                Check(!api.Request.Contains("13800138000"), "本地规则已遮挡的手机号仍发送到语义服务");
                Check(api.Request.Contains("Project"), "未保留语义补漏的实际输入");
                api.Release.Set();
            }
        }

        private static void TestEarlyCancel()
        {
            using (var api = new SlowApi())
            using (OverlayForm overlay = Open(api))
            {
                Call(overlay, "CancelPrivacy", false);
                Pump(800);
                Check(!api.Received.IsSet && Masks(overlay) == 0, "本地回调前取消仍上传或添加了结果");
                Check(!(bool)Get(overlay, "_busy"), "提前取消未解除 busy");
            }
        }

        private static void TestNewRequest()
        {
            using (var first = new SlowApi())
            using (var second = new SlowApi())
            using (OverlayForm overlay = Open(first))
            {
                LocalReady(overlay, first);
                ((AppConfig)Get(overlay, "_cfg")).BaseUrl = second.Url;
                Call(overlay, "StartSmartMosaic");
                LocalReady(overlay, second);
                int requestId = (int)Get(overlay, "_privacyRequestId");
                first.Release.Set();
                Pump(350);
                Check((int)Get(overlay, "_privacyRequestId") == requestId &&
                    Get(overlay, "_privacyCts") != null && Masks(overlay) == 1, "旧请求完成清除了新请求或提交了旧结果");
                Check(!second.Request.Contains("13800138000"), "重试时已遮挡文字重新进入 OCR 请求");
                second.Release.Set();
                Wait(() => Masks(overlay) == 2, 3000, "新请求语义补漏未生效");
            }
        }

        private static void TestVisualRedaction()
        {
            using (var api = new SlowApi())
            using (Bitmap image = Fixture())
            {
                api.Release.Set();
                var client = new DeepSeekClient(new AppConfig { BaseUrl = api.Url, ApiKey = "fixture-key",
                    SemanticPrivacy = false, VisionPrivacy = true, VisionModel = "fixture-vision" });
                PrivacyAnalysis result = client.AnalyzePrivacyAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                Check(api.VisualImage != null, "显式视觉请求未上传样本图");
                ImageTextBlock phone = result.Lines.First(line => line.Text.Contains("13800138000"));
                Rectangle body = Rectangle.Round(new RectangleF(phone.X * image.Width, phone.Y * image.Height,
                    phone.W * image.Width, phone.H * image.Height));
                int pixels = 0;
                for (int y = body.Top; y < body.Bottom; y++)
                    for (int x = body.Left; x < body.Right; x++)
                    {
                        Color color = api.VisualImage.GetPixel(x, y);
                        Check(color.R < 8 && color.G < 8 && color.B < 8, "上传图本地命中区域仍保留文字像素");
                        pixels++;
                    }
                Check(pixels > 1000 && api.VisualImage.GetPixel(650, 180).ToArgb() == Color.White.ToArgb(),
                    "上传遮挡范围错误或图像背景改变");
            }
        }

        private static object Get(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        private static void Set(object value, string name, object data) { value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, data); }
        private static void Call(object value, string name, params object[] args) { value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, args); }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Wait(Func<bool> condition, int timeout, string message)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!condition() && elapsed.ElapsedMilliseconds < timeout) Pump(10);
            Check(condition(), message);
        }
        private static void Pump(int duration)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            do { Application.DoEvents(); Thread.Sleep(5); } while (elapsed.ElapsedMilliseconds < duration);
        }

        private sealed class SlowApi : IDisposable
        {
            private readonly HttpListener listener = new HttpListener();
            private readonly Task server;
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            internal readonly ManualResetEventSlim Received = new ManualResetEventSlim();
            internal readonly string Url;
            internal string Request = "";
            internal Bitmap VisualImage;

            internal SlowApi()
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
                    try
                    {
                        var context = await listener.GetContextAsync();
                        using (var reader = new StreamReader(context.Request.InputStream)) Request = reader.ReadToEnd();
                        Received.Set();
                        Release.Wait();
                        var serializer = new JavaScriptSerializer();
                        var root = serializer.Deserialize<Dictionary<string, object>>(Request);
                        var messages = (ArrayList)root["messages"];
                        var user = (Dictionary<string, object>)messages[messages.Count - 1];
                        string content;
                        if (user["content"] is ArrayList)
                        {
                            var image = ((ArrayList)user["content"]).Cast<Dictionary<string, object>>()
                                .First(part => (string)part["type"] == "image_url");
                            string url = (string)((Dictionary<string, object>)image["image_url"])["url"];
                            using (var stream = new MemoryStream(Convert.FromBase64String(url.Substring(url.IndexOf(',') + 1))))
                            using (var bitmap = new Bitmap(stream)) VisualImage = (Bitmap)bitmap.Clone();
                            content = "{\"faces\":[],\"codes\":[]}";
                        }
                        else
                        {
                            var input = serializer.Deserialize<Dictionary<string, object>>((string)user["content"]);
                            var lines = ((ArrayList)input["lines"]).Cast<Dictionary<string, object>>();
                            var output = lines.Select(line => new { id = (int)line["id"], sensitive = true }).ToArray();
                            content = serializer.Serialize(new { lines = output });
                        }
                        byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(new { choices = new[] { new { message = new { content = content }, finish_reason = "stop" } } }));
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        using (var stream = context.Response.OutputStream) await stream.WriteAsync(bytes, 0, bytes.Length);
                    }
                    catch (HttpListenerException) { }
                    catch (ObjectDisposedException) { }
                    catch (IOException) { }
                });
            }

            public void Dispose()
            {
                Release.Set();
                try { server.Wait(500); } catch (AggregateException) { }
                listener.Close();
                server.GetAwaiter().GetResult();
                if (VisualImage != null) VisualImage.Dispose();
            }
        }
    }
}
