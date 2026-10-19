using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XiaoJie
{
    public static class AppInfo
    {
        public const string Name = "微信截图助手";
        public const string Version = "1.0.0";
        public const string FullName = "微信截图助手（DeepSeek 版）";
        public const string HiddenWindowTitle = "XiaoJie_WeChatShot_Hidden_v1";
        public const int WM_APP_TRIGGER_CAPTURE = 0x8001;   // 第二个实例请求截图
        public const int WM_APP_SHOW_TOAST = 0x8002;        // 第二个实例请求提示已在运行
        public const int WM_APP_OPEN_SETTINGS = 0x8003;     // 第二个实例请求打开设置
    }

    public static class UiTheme
    {
        public static readonly Color Green = Color.FromArgb(7, 193, 96);
        public static readonly Color GreenDark = Color.FromArgb(5, 168, 84);
        public static readonly Color GreenLight = Color.FromArgb(232, 248, 240);
        public static readonly Color Red = Color.FromArgb(250, 81, 81);
        public static readonly Color Orange = Color.FromArgb(255, 152, 0);
        public static readonly Color TextDark = Color.FromArgb(51, 51, 51);
        public static readonly Color TextGray = Color.FromArgb(128, 128, 128);
        public static readonly Color IconColor = Color.FromArgb(72, 72, 72);
        public static readonly Color Disabled = Color.FromArgb(188, 188, 188);
        public static readonly Color ToolbarBg = Color.White;
        public static readonly Color ToolbarBorder = Color.FromArgb(226, 226, 226);
        public static readonly Color HoverBg = Color.FromArgb(242, 242, 242);
        public static readonly Color PanelBg = Color.FromArgb(250, 250, 250);
        public static readonly Color DarkBg = Color.FromArgb(32, 32, 32);
        public static readonly Color Mask = Color.FromArgb(110, 0, 0, 0);

        private static string _uiFont = null;

        public static string UiFontName
        {
            get
            {
                if (_uiFont == null)
                {
                    _uiFont = "Microsoft YaHei UI";
                    try
                    {
                        using (InstalledFontCollection col = new InstalledFontCollection())
                        {
                            bool found = false;
                            foreach (FontFamily ff in col.Families)
                            {
                                if (string.Equals(ff.Name, _uiFont, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                            }
                            if (!found) _uiFont = "Microsoft YaHei";
                        }
                    }
                    catch { _uiFont = "Microsoft YaHei"; }
                }
                return _uiFont;
            }
        }

        public static Font Font(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font(UiFontName, size, style, GraphicsUnit.Pixel);
        }
    }

    internal static class Native
    {
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int WM_HOTKEY = 0x0312;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint GA_ROOT = 2;

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
            public Point ToPoint() { return new Point(X, Y); }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
    }

    public static class DpiUtil
    {
        /// <summary>虚拟屏幕（所有显示器并集）的物理像素矩形。</summary>
        public static Rectangle VirtualScreen
        {
            get
            {
                try
                {
                    int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
                    int y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
                    int w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
                    int h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
                    if (w <= 0 || h <= 0) return SystemInformation.VirtualScreen;
                    return new Rectangle(x, y, w, h);
                }
                catch
                {
                    return SystemInformation.VirtualScreen;
                }
            }
        }

        /// <summary>系统（主屏）DPI 缩放比例，96 DPI = 1.0。</summary>
        public static float SystemScale
        {
            get
            {
                try
                {
                    uint dpi = Native.GetDpiForSystem();
                    if (dpi >= 72 && dpi <= 480) return dpi / 96f;
                }
                catch { }
                try
                {
                    using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                        return g.DpiX / 96f;
                }
                catch { }
                return 1f;
            }
        }

        /// <summary>指定屏幕坐标处显示器的 DPI 缩放比例。</summary>
        public static float ScaleAtPoint(Point screenPoint)
        {
            try
            {
                Native.POINT pt = new Native.POINT(screenPoint.X, screenPoint.Y);
                IntPtr mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero)
                {
                    uint dx, dy;
                    if (Native.GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx >= 72 && dx <= 480)
                        return dx / 96f;
                }
            }
            catch { }
            return SystemScale;
        }

        public static int Scale(int value, float scale)
        {
            return (int)Math.Round(value * scale);
        }
    }

    /// <summary>屏幕捕获、图片保存、圆角/绘制辅助。</summary>
    public static class ImageUtil
    {
        /// <summary>抓取屏幕上指定物理像素矩形。</summary>
        public static Bitmap Capture(Rectangle bounds)
        {
            if (bounds.Width <= 0) bounds.Width = 1;
            if (bounds.Height <= 0) bounds.Height = 1;
            Bitmap bmp = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                    }
                    bmp.SetResolution(96, 96);
                    return bmp;
                }
                catch (Exception ex)
                {
                    if (bmp != null) { bmp.Dispose(); bmp = null; }
                    if (attempt == 2)
                    {
                        Log.Write("屏幕捕获失败: " + ex.Message);
                        bmp = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(bmp)) g.Clear(Color.Black);
                        return bmp;
                    }
                    Thread.Sleep(60);
                }
            }
            return bmp;
        }

        public static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;
            float max = Math.Min(rect.Width, rect.Height) / 2f;
            if (radius > max) radius = max;
            if (radius <= 0.5f)
            {
                path.AddRectangle(rect);
                return path;
            }
            float d = radius * 2f;
            path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Color color, RectangleF rect, float radius)
        {
            using (GraphicsPath path = RoundedRect(rect, radius))
            using (SolidBrush brush = new SolidBrush(color))
                g.FillPath(brush, path);
        }

        public static void DrawRounded(Graphics g, Color color, RectangleF rect, float radius, float width)
        {
            using (GraphicsPath path = RoundedRect(rect, radius))
            using (Pen pen = new Pen(color, width))
                g.DrawPath(pen, path);
        }

        public static Bitmap CloneRegion(Bitmap source, Rectangle rect)
        {
            rect.Intersect(new Rectangle(0, 0, source.Width, source.Height));
            if (rect.Width <= 0 || rect.Height <= 0)
                return new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            return source.Clone(rect, PixelFormat.Format32bppArgb);
        }

        /// <summary>保存图片（png / jpg）。</summary>
        public static void Save(Bitmap bitmap, string path, string format, int jpgQuality = 92)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool jpeg = string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase)
                        || ext == ".jpg" || ext == ".jpeg";
            if (jpeg)
            {
                ImageCodecInfo codec = GetEncoder(ImageFormat.Jpeg);
                if (codec != null)
                {
                    using (EncoderParameters eps = new EncoderParameters(1))
                    {
                        eps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(30, Math.Min(100, jpgQuality)));
                        bitmap.Save(path, codec, eps);
                        return;
                    }
                }
            }
            bitmap.Save(path, ImageFormat.Png);
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == format.Guid) return codec;
            }
            return null;
        }

        /// <summary>把图片转为 data:image/png;base64,...（超过 8192 像素边会等比缩小）。</summary>
        public static string ToDataUrl(Bitmap bitmap, int maxSide = 8192)
        {
            Bitmap work = bitmap;
            Bitmap scaled = null;
            try
            {
                int max = Math.Max(bitmap.Width, bitmap.Height);
                if (max > maxSide)
                {
                    float ratio = (float)maxSide / max;
                    int w = Math.Max(1, (int)Math.Round(bitmap.Width * ratio));
                    int h = Math.Max(1, (int)Math.Round(bitmap.Height * ratio));
                    scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.DrawImage(bitmap, new Rectangle(0, 0, w, h));
                    }
                    work = scaled;
                }
                using (MemoryStream ms = new MemoryStream())
                {
                    work.Save(ms, ImageFormat.Png);
                    string b64 = Convert.ToBase64String(ms.ToArray());
                    return "data:image/png;base64," + b64;
                }
            }
            finally
            {
                if (scaled != null) scaled.Dispose();
            }
        }

        /// <summary>把图片等比缩放，最长边不超过 maxSide（返回新图，调用方负责释放）。</summary>
        public static Bitmap ScaleToMaxSide(Bitmap source, int maxSide)
        {
            int max = Math.Max(source.Width, source.Height);
            if (max <= maxSide) return (Bitmap)source.Clone();
            float ratio = (float)maxSide / max;
            int w = Math.Max(1, (int)Math.Round(source.Width * ratio));
            int h = Math.Max(1, (int)Math.Round(source.Height * ratio));
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, w, h));
            }
            dst.SetResolution(96, 96);
            return dst;
        }
    }

    public static class ClipUtil
    {
        public static bool CopyImage(Bitmap bitmap)
        {
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    Clipboard.SetImage(bitmap);
                    return true;
                }
                catch (Exception ex)
                {
                    if (i == 3) { Log.Write("复制图片到剪贴板失败: " + ex.Message); return false; }
                    Thread.Sleep(60);
                }
            }
            return false;
        }

        public static bool CopyText(string text)
        {
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    Clipboard.SetText(text ?? "");
                    return true;
                }
                catch (Exception ex)
                {
                    if (i == 3) { Log.Write("复制文本到剪贴板失败: " + ex.Message); return false; }
                    Thread.Sleep(60);
                }
            }
            return false;
        }

        public static string TryGetText()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { return null; }
        }
    }

    public struct HotkeySpec
    {
        public Keys Key;
        public bool Alt;
        public bool Ctrl;
        public bool Shift;
        public bool Win;

        public bool IsValid { get { return Key != Keys.None; } }
    }

    public static class HotkeyUtil
    {
        public static bool TryParse(string text, out HotkeySpec spec)
        {
            spec = new HotkeySpec();
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            bool keyFound = false;
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;
                string up = p.ToUpperInvariant();
                if (i < parts.Length - 1 || IsModifierName(up))
                {
                    switch (up)
                    {
                        case "ALT": spec.Alt = true; continue;
                        case "CTRL":
                        case "CONTROL": spec.Ctrl = true; continue;
                        case "SHIFT": spec.Shift = true; continue;
                        case "WIN":
                        case "WINDOWS": spec.Win = true; continue;
                    }
                }
                Keys key;
                if (!TryParseKey(p, out key)) return false;
                spec.Key = key;
                keyFound = true;
            }
            if (!keyFound || spec.Key == Keys.None) return false;
            // 不允许仅由修饰键组成
            if (spec.Key == Keys.Menu || spec.Key == Keys.ControlKey || spec.Key == Keys.ShiftKey || spec.Key == Keys.LWin || spec.Key == Keys.RWin)
                return false;
            return true;
        }

        private static bool IsModifierName(string up)
        {
            return up == "ALT" || up == "CTRL" || up == "CONTROL" || up == "SHIFT" || up == "WIN" || up == "WINDOWS";
        }

        private static bool TryParseKey(string text, out Keys key)
        {
            key = Keys.None;
            if (text.Length == 1)
            {
                char c = char.ToUpperInvariant(text[0]);
                if (c >= 'A' && c <= 'Z') { key = (Keys)(int)c; return true; }
                if (c >= '0' && c <= '9') { key = (Keys)(int)c; return true; }
            }
            Keys parsed;
            if (Enum.TryParse<Keys>(text, true, out parsed))
            {
                if (parsed >= Keys.NumPad0 && parsed <= Keys.NumPad9) return false;
                if (parsed == Keys.None) return false;
                key = parsed;
                return true;
            }
            return false;
        }

        public static string Format(HotkeySpec spec)
        {
            if (!spec.IsValid) return "";
            StringBuilder sb = new StringBuilder();
            if (spec.Ctrl) sb.Append("Ctrl+");
            if (spec.Alt) sb.Append("Alt+");
            if (spec.Shift) sb.Append("Shift+");
            if (spec.Win) sb.Append("Win+");
            sb.Append(KeyName(spec.Key));
            return sb.ToString();
        }

        public static string KeyName(Keys key)
        {
            if (key >= Keys.A && key <= Keys.Z) return ((char)key).ToString();
            if (key >= Keys.D0 && key <= Keys.D9) return ((char)key).ToString();
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "Num" + (key - Keys.NumPad0);
            switch (key)
            {
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Space: return "Space";
                case Keys.Return: return "Enter";
                case Keys.Escape: return "Esc";
                case Keys.Tab: return "Tab";
                case Keys.Back: return "Backspace";
                case Keys.Delete: return "Delete";
                case Keys.Insert: return "Insert";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "PageUp";
                case Keys.PageDown: return "PageDown";
                case Keys.Up: return "Up";
                case Keys.Down: return "Down";
                case Keys.Left: return "Left";
                case Keys.Right: return "Right";
                case Keys.PrintScreen: return "PrintScreen";
                case Keys.Pause: return "Pause";
                case Keys.Scroll: return "ScrollLock";
                case Keys.CapsLock: return "CapsLock";
                case Keys.NumLock: return "NumLock";
                case Keys.LWin: return "Win";
                case Keys.RWin: return "Win";
            }
            return key.ToString();
        }

        public static uint Modifiers(HotkeySpec spec)
        {
            uint mod = 0;
            if (spec.Alt) mod |= Native.MOD_ALT;
            if (spec.Ctrl) mod |= Native.MOD_CONTROL;
            if (spec.Shift) mod |= Native.MOD_SHIFT;
            if (spec.Win) mod |= Native.MOD_WIN;
            return mod;
        }
    }

    /// <summary>开机自启（当前用户注册表 Run 项，无需管理员权限）。</summary>
    public static class Autostart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "XiaoJieWeChatShot";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object v = key.GetValue(ValueName);
                    return v != null && v.ToString().Trim().Length > 0;
                }
            }
            catch { return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        string exe = Application.ExecutablePath;
                        key.SetValue(ValueName, "\"" + exe + "\" --autostart");
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
                Log.Write("开机自启已" + (enabled ? "开启" : "关闭"));
            }
            catch (Exception ex)
            {
                Log.Write("设置开机自启失败: " + ex.Message);
            }
        }
    }

    public static class Log
    {
        private static readonly object Sync = new object();
        private static string _path = null;

        public static void Init(string path)
        {
            _path = path;
        }

        public static void Write(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(_path)) return;
                lock (Sync)
                {
                    string dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    if (File.Exists(_path) && new FileInfo(_path).Length > 1024 * 512)
                    {
                        string old = _path + ".old";
                        try { if (File.Exists(old)) File.Delete(old); File.Move(_path, old); } catch { }
                    }
                    File.AppendAllText(_path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    /// <summary>右下角轻量提示（不抢焦点、自动淡出）。</summary>
    public static class Toast
    {
        private static SynchronizationContext _ctx;
        private static ToastForm _current;

        public static void Init()
        {
            _ctx = SynchronizationContext.Current;
        }

        public static void Show(string text, int durationMs = 2200)
        {
            if (string.IsNullOrEmpty(text)) return;
            SynchronizationContext ctx = _ctx;
            if (ctx == null)
            {
                try { ShowCore(text, durationMs); } catch { }
                return;
            }
            ctx.Post(delegate
            {
                try { ShowCore(text, durationMs); } catch (Exception ex) { Log.Write("Toast 失败: " + ex.Message); }
            }, null);
        }

        private static void ShowCore(string text, int durationMs)
        {
            if (_current != null)
            {
                try { _current.Close(); } catch { }
                _current = null;
            }
            _current = new ToastForm(text, durationMs);
            _current.Show();
        }

        internal static void Clear(ToastForm form)
        {
            if (_current == form) _current = null;
        }
    }

    internal sealed class ToastForm : Form
    {
        private readonly string _text;
        private readonly System.Windows.Forms.Timer _fadeTimer;
        private readonly System.Windows.Forms.Timer _lifeTimer;
        private float _opacity;
        private bool _fadingOut;
        private readonly int _duration;

        public ToastForm(string text, int durationMs)
        {
            _text = text;
            _duration = Math.Max(800, durationMs);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(38, 38, 38);
            DoubleBuffered = true;
            Opacity = 0;
            _opacity = 0;

            float scale = DpiUtil.SystemScale;
            Font font = UiTheme.Font(14f * scale);
            Size textSize = TextRenderer.MeasureText(text, font, new Size((int)(460 * scale), 2000), TextFormatFlags.WordBreak);
            int padX = (int)(18 * scale), padY = (int)(12 * scale);
            int w = Math.Min((int)(520 * scale), textSize.Width + padX * 2 + (int)(26 * scale));
            int h = textSize.Height + padY * 2;
            Size = new Size(Math.Max((int)(180 * scale), w), Math.Max((int)(44 * scale), h));
            Font = font;

            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(work.Right - Width - (int)(18 * scale), work.Bottom - Height - (int)(18 * scale));

            using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(0, 0, Width, Height), 10 * scale))
            {
                Region = new Region(path);
            }

            _fadeTimer = new System.Windows.Forms.Timer();
            _fadeTimer.Interval = 15;
            _fadeTimer.Tick += OnFadeTick;
            _lifeTimer = new System.Windows.Forms.Timer();
            _lifeTimer.Interval = _duration;
            _lifeTimer.Tick += OnLifeTick;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            _fadeTimer.Start();
            _lifeTimer.Start();
        }

        private void OnFadeTick(object sender, EventArgs e)
        {
            if (!_fadingOut)
            {
                _opacity += 0.14f;
                if (_opacity >= 0.96f) { _opacity = 0.96f; _fadeTimer.Stop(); }
            }
            else
            {
                _opacity -= 0.12f;
                if (_opacity <= 0f) { _opacity = 0f; _fadeTimer.Stop(); Close(); return; }
            }
            Opacity = _opacity;
        }

        private void OnLifeTick(object sender, EventArgs e)
        {
            _lifeTimer.Stop();
            _fadingOut = true;
            _fadeTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(38, 38, 38))) g.FillRectangle(b, ClientRectangle);
            // 左侧绿色小圆点
            float scale = Height / 44f;
            using (SolidBrush dot = new SolidBrush(UiTheme.Green))
                g.FillEllipse(dot, 10 * scale, Height / 2f - 3.5f * scale, 7 * scale, 7 * scale);
            Rectangle textRect = new Rectangle((int)(24 * scale), 0, Width - (int)(34 * scale), Height);
            TextRenderer.DrawText(g, _text, Font, textRect, Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Toast.Clear(this);
            _fadeTimer.Dispose();
            _lifeTimer.Dispose();
            base.OnFormClosed(e);
        }
    }

    public static class WinUtil
    {
        /// <summary>尝试把微信窗口切到前台（用于“转发”后直接粘贴）。</summary>
        public static bool ActivateWeChat()
        {
            string[] names = new string[] { "Weixin", "WeChat" };
            foreach (string name in names)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(name);
                    foreach (Process p in procs)
                    {
                        if (p.MainWindowHandle != IntPtr.Zero && Native.IsWindowVisible(p.MainWindowHandle))
                        {
                            Native.ShowWindow(p.MainWindowHandle, Native.SW_RESTORE);
                            if (Native.SetForegroundWindow(p.MainWindowHandle)) return true;
                            // 前台窗口锁定时的兜底
                            uint fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint fgPid);
                            uint targetThread = Native.GetWindowThreadProcessId(p.MainWindowHandle, out uint targetPid);
                            if (fgThread != 0 && targetThread != 0 && fgThread != targetThread)
                            {
                                Native.AttachThreadInput(fgThread, targetThread, true);
                                bool ok = Native.SetForegroundWindow(p.MainWindowHandle);
                                Native.AttachThreadInput(fgThread, targetThread, false);
                                if (ok) return true;
                            }
                        }
                    }
                }
                catch { }
            }
            return false;
        }

        /// <summary>获取屏幕坐标处顶层窗口的矩形（用于截图时智能识别窗口）。</summary>
        public static bool TryGetWindowRectAt(Point screenPoint, out Rectangle bounds, out string title)
        {
            bounds = Rectangle.Empty;
            title = "";
            try
            {
                Native.POINT pt = new Native.POINT(screenPoint.X, screenPoint.Y);
                IntPtr hwnd = Native.WindowFromPoint(pt);
                if (hwnd == IntPtr.Zero) return false;
                hwnd = Native.GetAncestor(hwnd, Native.GA_ROOT);
                if (hwnd == IntPtr.Zero || !Native.IsWindowVisible(hwnd)) return false;
                Native.RECT rc;
                if (!Native.GetWindowRect(hwnd, out rc)) return false;
                Rectangle rect = rc.ToRectangle();
                if (rect.Width < 40 || rect.Height < 30) return false;
                // 排除桌面/任务栏/我们自己的窗口
                StringBuilder cls = new StringBuilder(256);
                Native.GetClassName(hwnd, cls, cls.Capacity);
                string className = cls.ToString();
                // 桌面 / 任务栏 / 开始菜单等系统窗口不参与智能识别
                if (className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd" ||
                    className == "Windows.UI.Core.CoreWindow" || className == "Shell_SecondaryTrayWnd" ||
                    className == "NotifyIconOverflowWindow")
                    return false;
                int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return false;
                Rectangle vs = DpiUtil.VirtualScreen;
                rect.Intersect(vs);
                if (rect.Width < 40 || rect.Height < 30) return false;
                StringBuilder sb = new StringBuilder(Native.GetWindowTextLength(hwnd) + 2);
                Native.GetWindowText(hwnd, sb, sb.Capacity);
                title = sb.ToString();
                bounds = rect;
                return true;
            }
            catch { return false; }
        }
    }
}


