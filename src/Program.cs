using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using System.Net;

namespace XiaoJie
{
    internal static class Program
    {
        public static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string a in args)
            {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static string GetArgValue(string[] args, string name, string def = "")
        {
            if (args == null) return def;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) return args[i + 1];
                    return def;
                }
            }
            return def;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { } // TLS1.3（Win11 支持）
            }
            catch { }
            ServicePointManager.DefaultConnectionLimit = 16;

            if (HasArg(args, "--selftest"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return SelfTest.Run(args);
            }

            if (HasArg(args, "--help") || HasArg(args, "/?"))
            {
                MessageBox.Show(
                    AppInfo.FullName + "\r\n\r\n" +
                    "用法：微信截图助手.exe [参数]\r\n\r\n" +
                    "  （无参数）  启动程序并驻留托盘\r\n" +
                    "  --capture   立即截图（托盘已在运行时触发已有实例）\r\n" +
                    "  --settings  打开设置\r\n" +
                    "  --autostart 开机自启时使用（正常启动）\r\n" +
                    "  --selftest  运行内置自检（开发用，写出 selftest-report.txt）\r\n" +
                    "  --version   显示版本号\r\n\r\n" +
                    "截图热键：Alt+A（可在设置中修改）",
                    AppInfo.FullName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            if (HasArg(args, "--version"))
            {
                MessageBox.Show(AppInfo.Name + " v" + AppInfo.Version + "（绿色免安装版）", AppInfo.FullName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            // 单实例：已存在时把请求转交给已有实例
            bool createdNew;
            Mutex mutex;
            try
            {
                mutex = new Mutex(true, "Local\\XiaoJie_WeChatShot_SingleInstance_v1", out createdNew);
            }
            catch
            {
                mutex = null;
                createdNew = true;
            }

            if (!createdNew)
            {
                IntPtr hwnd = Native.FindWindow(null, AppInfo.HiddenWindowTitle);
                if (hwnd != IntPtr.Zero)
                {
                    uint message = AppInfo.WM_APP_SHOW_TOAST;
                    if (HasArg(args, "--capture")) message = AppInfo.WM_APP_TRIGGER_CAPTURE;
                    else if (HasArg(args, "--settings")) message = AppInfo.WM_APP_OPEN_SETTINGS;
                    Native.PostMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero);
                }
                return 0;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                ConfigStore store = new ConfigStore();
                Log.Init(store.LogPath);
                Log.Write("===== 启动 " + AppInfo.Version + "，配置: " + store.ConfigPath + " =====");
                AppConfig cfg = store.Load();
                Toast.Init();

                bool firstRun = !cfg.FirstRunDone || !FileExistsSafe(store.ConfigPath);
                Application.Run(new TrayApp(cfg, store, args, firstRun));
                Log.Write("===== 正常退出 =====");
                return 0;
            }
            finally
            {
                if (mutex != null)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                    mutex.Dispose();
                }
            }
        }

        private static bool FileExistsSafe(string path)
        {
            try { return System.IO.File.Exists(path); } catch { return false; }
        }
    }
}
