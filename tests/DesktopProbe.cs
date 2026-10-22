using System;
using System.Drawing;
using System.Windows.Forms;

namespace XiaoJie
{
    internal static class DesktopProbe
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var cfg = new AppConfig { ApiKey = "", FirstRunDone = true, SemanticPrivacy = false };
            var store = new ConfigStore();
            if (args.Length > 0 && args[0] == "settings")
            {
                using (var settings = new SettingsForm(cfg, null, false)) settings.ShowDialog();
                return;
            }
            Rectangle desktop = DpiUtil.VirtualScreen;
            var screenshot = new Bitmap(desktop.Width, desktop.Height);
            using (Graphics g = Graphics.FromImage(screenshot))
            using (Font title = new Font("Microsoft YaHei UI", 30, GraphicsUnit.Pixel))
            using (Font body = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel))
            {
                g.Clear(Color.FromArgb(244, 246, 248));
                g.FillRectangle(Brushes.White, 120, 100, 940, 600);
                g.DrawString("截图助手 · 本地识别测试", title, Brushes.Black, 165, 145);
                g.DrawString("本地识别 hello screenshot", body, Brushes.Black, 165, 225);
                g.DrawString("手机号：13800138000", body, Brushes.Black, 165, 295);
                g.DrawString("姓名：张三", body, Brushes.Black, 165, 365);
                g.DrawString("邮箱：demo@example.com", body, Brushes.Black, 165, 435);
                g.DrawString("普通内容：测试数据", body, Brushes.Black, 165, 505);
            }
            using (var overlay = new OverlayForm(screenshot, desktop, cfg, store, null))
                Application.Run(overlay);
        }
    }
}
