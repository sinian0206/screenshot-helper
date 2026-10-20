using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using XiaoJie;

internal static class PortableRecordingTests
{
    private static readonly BindingFlags InternalMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly ApplicationAssembly = typeof(AppConfig).Assembly;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            string output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            Application.EnableVisualStyles();
            typeof(Toast).GetField("_ctx", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, new QuietToastContext());
            string direct = Path.Combine(output, "portable-writer.mp4");
            TestWriter(direct);
            string session = TestSession(output);
            var manifest = new Dictionary<string, object>
            {
                { "assembly", ApplicationAssembly.Location },
                { "version", ApplicationAssembly.GetName().Version.ToString() },
                { "writer", direct },
                { "session", session },
                { "audioMode", "none" },
                { "syntheticCapture", true }
            };
            File.WriteAllText(Path.Combine(output, "runner-manifest.json"),
                new JavaScriptSerializer().Serialize(manifest), new UTF8Encoding(false));
            Console.WriteLine("[PASS] 便携程序集反射 MP4 编码：5 帧，10 fps，321 × 181 原始位图。");
            Console.WriteLine("[PASS] 便携 RecordingSession 注入合成捕获，500 ms 自动收尾，无桌面/音频捕获。");
            Console.WriteLine("程序集: " + ApplicationAssembly.Location);
            Console.WriteLine("版本: " + ApplicationAssembly.GetName().Version);
            return 0;
        }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Console.WriteLine("[FAIL] " + error);
            return 1;
        }
        finally { Environment.SetEnvironmentVariable("XJ_RECORD_AUTO_STOP_MS", null); }
    }

    private sealed class QuietToastContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object state) { }
    }

    private static object Invoke(object instance, string method, params object[] arguments)
    {
        return instance.GetType().GetMethod(method, InternalMembers).Invoke(instance, arguments);
    }

    private static void TestWriter(string path)
    {
        Type writerType = ApplicationAssembly.GetType("XiaoJie.Mp4RecorderWriter", true);
        object writer = Activator.CreateInstance(writerType, InternalMembers, null,
            new object[] { path, 321, 181, 10, 80, "none" }, null);
        try
        {
            Invoke(writer, "SetPaused", false);
            for (int frame = 0; frame < 5; ++frame)
                using (Bitmap image = Fixture(321, 181)) Invoke(writer, "AddFrame", image);
            Invoke(writer, "Close");
        }
        finally { ((IDisposable)writer).Dispose(); }
        if (!File.Exists(path) || new FileInfo(path).Length < 1000)
            throw new InvalidOperationException("便携 MP4 编码没有产生可检查的文件。");
    }

    private static string TestSession(string output)
    {
        AppConfig config = new AppConfig
        {
            SaveDir = output,
            RecordFormat = "mp4",
            RecordAudioMode = "none",
            RecordFps = 10,
            RecordQuality = 80,
            RecordIncludeCursor = false,
            FirstRunDone = true
        };
        int captured = 0;
        Func<Rectangle, Bitmap> synthetic = delegate(Rectangle region)
        {
            ++captured;
            return Fixture(region.Width, region.Height);
        };
        Environment.SetEnvironmentVariable("XJ_RECORD_AUTO_STOP_MS", "500");
        Type sessionType = ApplicationAssembly.GetType("XiaoJie.RecordingSession", true);
        object session = Activator.CreateInstance(sessionType, InternalMembers, null,
            new object[] { new Rectangle(-32000, -32000, 320, 180), config, synthetic }, null);
        string path;
        try { path = (string)Invoke(session, "Execute"); }
        finally { ((IDisposable)session).Dispose(); }
        if (captured < 1 || string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException("便携录制会话未使用合成捕获成功保存 MP4。");
        string result = Path.Combine(output, "portable-session.mp4");
        File.Copy(path, result, true);
        return result;
    }

    private static Bitmap Fixture(int width, int height)
    {
        Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.FromArgb(30, 40, 200));
            using (Brush top = new SolidBrush(Color.FromArgb(200, 40, 20)))
                graphics.FillRectangle(top, 0, 0, width, height / 2);
        }
        return image;
    }
}
