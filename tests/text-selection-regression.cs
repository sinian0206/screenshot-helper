using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using XiaoJie;

internal static class TextSelectionRegression
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
        {
            if (args.Length == 0) return null;
            string path = System.IO.Path.Combine(System.IO.Path.GetFullPath(args[0]), "截图助手.exe");
            return System.IO.File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { return Run(args); }
        catch (Exception error)
        {
            Console.WriteLine("[FAIL] " + error.GetType().FullName + ": " + error.Message);
            Console.WriteLine(error.StackTrace ?? "");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("需要传入包含 OCR 组件的应用目录。");
        string appDir = System.IO.Path.GetFullPath(args[0]);
        SetDllDirectory(appDir);
        LocalOcr.DefaultEnginePath = System.IO.Path.Combine(appDir, "ocr", "RapidOCR-json.exe");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using (Bitmap source = new Bitmap(800, 600))
        using (Graphics graphics = Graphics.FromImage(source))
        using (Font font = new Font("Arial", 32f, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.DrawString("SELECT-ME-20261022", font, Brushes.Black, new PointF(160, 180));
            AppConfig config = new AppConfig { FirstRunDone = true, AutoCopy = false, AutoSave = false,
                ShowDimensions = false, ShowColorValue = false };
            using (OverlayForm form = new OverlayForm(source, new Rectangle(0, 0, 800, 600), config, new ConfigStore(), null))
            {
                form.SuppressActivation = true;
                Mouse(form, "OnMouseDown", new Point(50, 50), MouseButtons.Left);
                Mouse(form, "OnMouseMove", new Point(750, 500), MouseButtons.Left);
                Mouse(form, "OnMouseUp", new Point(750, 500), MouseButtons.Left);
                Call(form, "SetTool", EditorTool.TextSelect);
                Mouse(form, "OnMouseDown", new Point(120, 150), MouseButtons.Left);
                Mouse(form, "OnMouseMove", new Point(700, 250), MouseButtons.Left);
                Mouse(form, "OnMouseUp", new Point(700, 250), MouseButtons.Left);
                DateTime deadline = DateTime.UtcNow.AddSeconds(20);
                while (Field(form, "_textCopyCts") != null && DateTime.UtcNow < deadline)
                {
                    Application.DoEvents();
                    Thread.Sleep(40);
                }
                Require(Field(form, "_textCopyCts") == null, "选择文字识别未在 20 秒内结束");
                string copied = ClipUtil.TryGetText() ?? "";
                Require(copied.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) >= 0,
                    "选择文字未复制识别结果: " + copied);
                Console.WriteLine("[PASS] 截图内拖选文字并复制: " + copied);
            }
        }
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object Field(object target, string name)
    {
        return target.GetType().GetField(name, PrivateInstance).GetValue(target);
    }

    private static void Call(object target, string name, params object[] args)
    {
        target.GetType().GetMethod(name, PrivateInstance).Invoke(target, args);
    }

    private static void Mouse(Form form, string method, Point point, MouseButtons button)
    {
        Call(form, method, new MouseEventArgs(button, 1, point.X, point.Y, 0));
    }
}
