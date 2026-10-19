using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 内置自检（开发/QA 用）。运行：微信截图助手.exe --selftest [--out 报告文件] [--mock-api 端口]
    /// 输出纯文本报告到文件（WinExe 无控制台），退出码 0 = 全部通过。
    /// 负责队友：Lead + QA。
    /// </summary>
    internal static class SelfTest
    {
        public static int Run(string[] args)
        {
            StringBuilder report = new StringBuilder();
            int failures = 0;
            string outPath = Program.GetArgValue(args, "--out", "");
            if (string.IsNullOrEmpty(outPath))
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest");
                Directory.CreateDirectory(dir);
                outPath = Path.Combine(dir, "selftest-report.txt");
            }

            Action<string, bool, string> check = delegate (string name, bool ok, string detail)
            {
                report.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
                if (!ok) failures++;
            };

            try
            {
                report.AppendLine("微信截图助手自检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                report.AppendLine("版本: " + AppInfo.Version);
                report.AppendLine("虚拟屏幕: " + DpiUtil.VirtualScreen);
                report.AppendLine("系统缩放: " + DpiUtil.SystemScale);
                report.AppendLine();

                ConfigStore store = new ConfigStore();
                AppConfig cfg = store.Load();
                check("配置读取", cfg != null, "配置路径: " + store.ConfigPath);

                // 屏幕捕获
                Bitmap shot = ImageUtil.Capture(DpiUtil.VirtualScreen);
                check("屏幕捕获", shot != null && shot.Width > 0 && shot.Height > 0, shot == null ? "" : shot.Width + "x" + shot.Height);
                if (shot != null)
                {
                    string png = Path.Combine(Path.GetDirectoryName(outPath), "capture.png");
                    ImageUtil.Save(shot, png, "png");
                    check("PNG 保存", File.Exists(png), png);
                    string dataUrl = ImageUtil.ToDataUrl(shot);
                    check("图片 Base64 编码", dataUrl.StartsWith("data:image/png;base64,"), "长度 " + dataUrl.Length);
                    shot.Dispose();
                }

                // 热键解析
                HotkeySpec spec;
                bool hk = HotkeyUtil.TryParse(cfg.Hotkey, out spec);
                check("热键解析 " + cfg.Hotkey, hk && spec.IsValid, hk ? HotkeyUtil.Format(spec) : "解析失败");

                // 圆角绘制
                using (Bitmap bmp = new Bitmap(200, 100))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        ImageUtil.FillRounded(g, Color.FromArgb(7, 193, 96), new RectangleF(1, 1, 198, 98), 12);
                    }
                    string p = Path.Combine(Path.GetDirectoryName(outPath), "rounded.png");
                    ImageUtil.Save(bmp, p, "png");
                    check("圆角绘制", File.Exists(p), p);
                }

                // JSON 序列化
                try
                {
                    System.Web.Script.Serialization.JavaScriptSerializer ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                    string json = ser.Serialize(new { a = 1, b = "中文" });
                    check("JSON 序列化", json.Contains("中文"), json);
                }
                catch (Exception ex)
                {
                    check("JSON 序列化", false, ex.Message);
                }

                // DeepSeek 客户端离线校验
                try
                {
                    DeepSeekClient client = new DeepSeekClient(cfg);
                    check("API 地址拼装", client.Endpoint.StartsWith("http"), client.Endpoint);
                    bool keyOk = client.HasApiKey;
                    report.AppendLine("[INFO] 已配置 API Key: " + keyOk);
                }
                catch (Exception ex)
                {
                    check("DeepSeek 客户端", false, ex.Message);
                }

                // 外部模块自检（存在则调用）
                try
                {
                    string st = LongShotStitcher.SelfTest();
                    check("长截图拼接算法", !string.IsNullOrEmpty(st) && !st.StartsWith("FAIL"), st);
                }
                catch (Exception ex)
                {
                    check("长截图拼接算法", false, ex.Message);
                }

                report.AppendLine();
                report.AppendLine(failures == 0 ? "结果: 全部通过" : "结果: " + failures + " 项失败");
            }
            catch (Exception ex)
            {
                report.AppendLine("[FATAL] " + ex);
                failures++;
            }

            try
            {
                File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return failures == 0 ? 0 : 1;
        }
    }
}
