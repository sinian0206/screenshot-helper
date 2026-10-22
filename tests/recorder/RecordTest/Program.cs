// recorder 模块独立验证程序：
//   * 在真实桌面上调用 ScreenRecorder.RecordRegion 录 2~3 秒（XJ_RECORD_AUTO_STOP_MS 自动停止）；
//   * 打印录制结果路径/大小/耗时，供 AVI 解析脚本与 Lead 验证；
//   * 不参与主项目构建（主项目只用 src\ScreenRecorder.cs / src\AviWriter.cs）。
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using XiaoJie;

namespace RecordTest
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 光标绘制离屏探针：抓光标周围 220x220 → 保存 before → 用 RecorderCursor.Stamp 画光标 → 保存 after
            if (args.Length > 0 && args[0] == "--cursor-probe")
            {
                string probeDir = args.Length > 1 ? args[1] : baseDir;
                Directory.CreateDirectory(probeDir);
                Log.Init(Path.Combine(probeDir, "cursor_probe.log"));
                Point cursor = Cursor.Position;
                Rectangle probeRegion = new Rectangle(Math.Max(0, cursor.X - 110), Math.Max(0, cursor.Y - 110), 220, 220);
                using (Bitmap bmp = ImageUtil.Capture(probeRegion))
                {
                    bmp.Save(Path.Combine(probeDir, "cursor_probe_before.png"), System.Drawing.Imaging.ImageFormat.Png);
                    RecorderCursor.Stamp(bmp, probeRegion);
                    bmp.Save(Path.Combine(probeDir, "cursor_probe_after.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.WriteLine("CURSOR_POS=" + cursor);
                Console.WriteLine("PROBE_REGION=" + probeRegion);
                Console.WriteLine("PROBE_DIR=" + probeDir);
                return 0;
            }

            // 日志：验证结束后检查是否有异常/警告
            Log.Init(Path.Combine(baseDir, "recordtest.log"));
            Log.Write("recordtest 启动，参数: " + string.Join(" ", args));

            AppConfig cfg = new AppConfig();
            string outDir = args.Length > 0 ? args[0] : Path.Combine(baseDir, "recordings");
            try { Directory.CreateDirectory(outDir); } catch { }
            cfg.SaveDir = outDir;
            cfg.RecordFps = args.Length > 1 ? ParseInt(args[1], 15) : 15;
            cfg.RecordQuality = args.Length > 2 ? ParseInt(args[2], 85) : 85;
            cfg.RecordIncludeCursor = args.Length > 3 ? (args[3] != "0") : true;

            Rectangle region;
            if (args.Length >= 8)
            {
                region = new Rectangle(ParseInt(args[4], 0), ParseInt(args[5], 0),
                    ParseInt(args[6], 800), ParseInt(args[7], 450));
            }
            else
            {
                // 默认：主屏右侧中间区域（避开控制台窗口与左下角 Toast）
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                int w = Math.Min(800, work.Width / 2);
                int h = Math.Min(450, work.Height / 2);
                region = new Rectangle(work.Right - w - 120, work.Top + 80, w, h);
            }

            Console.WriteLine("AUTO_STOP_MS=" + (Environment.GetEnvironmentVariable("XJ_RECORD_AUTO_STOP_MS") ?? "(未设置，需手动停止)"));
            Console.WriteLine("CONFIG=fps=" + cfg.RecordFps + " quality=" + cfg.RecordQuality + " cursor=" + cfg.RecordIncludeCursor);
            Console.WriteLine("REGION=" + region);
            Console.WriteLine("OUT_DIR=" + outDir);

            DateTime start = DateTime.Now;
            string path = ScreenRecorder.RecordRegion(region, cfg);
            double seconds = (DateTime.Now - start).TotalSeconds;

            Console.WriteLine("ELAPSED_SEC=" + seconds.ToString("0.00"));
            Console.WriteLine("RESULT=" + (path ?? "null"));
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                Console.WriteLine("FILE=" + path);
                Console.WriteLine("SIZE=" + new FileInfo(path).Length);
                return 0;
            }
            Console.WriteLine("录制未返回有效文件");
            return 2;
        }

        private static int ParseInt(string text, int defaultValue)
        {
            int value;
            return int.TryParse(text, out value) ? value : defaultValue;
        }
    }
}
