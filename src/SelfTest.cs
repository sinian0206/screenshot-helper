using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 内置自检（开发/QA 用）。运行：截图助手.exe --selftest [--out 报告文件] [--mock-api 端口]
    /// 输出纯文本报告到文件（WinExe 无控制台），退出码 0 = 全部通过。
    /// 负责队友：Lead + QA。
    /// </summary>
    internal static class SelfTest
    {
        private static readonly HashSet<IntPtr> LeftButtonTargets = new HashSet<IntPtr>();

        public static int Run(string[] args)
        {
            LeftButtonTargets.Clear();
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
                report.AppendLine("截图助手自检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
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

                // ===== DeepSeek API mock 测试（仅当传入 --mock-url 时）=====
                string mockUrl = Program.GetArgValue(args, "--mock-url", "");
                if (!string.IsNullOrEmpty(mockUrl))
                {
                    RunApiTests(report, check, mockUrl, Program.GetArgValue(args, "--mock-key", "mock-key"));
                }

                // ===== 开机自启注册表测试（仅 --autostart-test 时，结束后恢复原状）=====
                if (Program.HasArg(args, "--autostart-test"))
                {
                    RunAutostartTest(check);
                }

                // ===== 真实 AI 测试（仅 --test-ai 时，使用当前配置里的真实 API）=====
                if (Program.HasArg(args, "--test-ai"))
                {
                    RunAiRealTests(report, Path.GetDirectoryName(outPath), check, cfg);
                }

                // ===== 离屏 UI 测试（不干扰用户桌面）=====
                RunUiTests(report, Path.GetDirectoryName(outPath), check, mockUrl);

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

        // ============================ 离屏 UI 自动化测试 ============================

        private static void RunUiTests(StringBuilder report, string reportDir, Action<string, bool, string> check, string mockUrl)
        {
            try
            {
                string uiDir = Path.Combine(reportDir, "ui");
                Directory.CreateDirectory(uiDir);

                // 1) 工具栏图标一览（直接渲染，便于人工检查）
                try
                {
                    using (Bitmap sheet = new Bitmap(20 * 58 + 20, 110))
                    {
                        using (Graphics g = Graphics.FromImage(sheet))
                        {
                            g.Clear(Color.White);
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            EditorToolbar toolbar = new EditorToolbar(1f);
                            toolbar.BringToFront();
                            // 用 EditorToolbar 自己的绘制逻辑生成整条工具栏
                            using (Bitmap tb = new Bitmap(toolbar.Width, toolbar.Height))
                            {
                                toolbar.DrawToBitmap(tb, new Rectangle(0, 0, toolbar.Width, toolbar.Height));
                                g.DrawImage(tb, 10, 8);
                            }
                            toolbar.Dispose();
                        }
                        string path = Path.Combine(uiDir, "toolbar_icons.png");
                        sheet.Save(path, ImageFormat.Png);
                        check("工具栏图标渲染", new FileInfo(path).Length > 1000, path);
                    }
                }
                catch (Exception ex)
                {
                    check("工具栏图标渲染", false, ex.Message);
                }

                // 2) 全部标注类型渲染（离屏）
                try
                {
                    using (Bitmap baseImg = BuildTestScreenshot(900, 620))
                    using (Bitmap canvas = (Bitmap)baseImg.Clone())
                    using (MosaicCanvas mosaic = new MosaicCanvas(baseImg, 12))
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        List<Annotation> list = new List<Annotation>();
                        list.Add(new ShapeAnnotation { Rect = new RectangleF(40, 60, 200, 110), Color = UiTheme.Red, LineWidth = 3 });
                        list.Add(new ShapeAnnotation { IsEllipse = true, Rect = new RectangleF(280, 60, 190, 110), Color = Color.FromArgb(30, 144, 255), LineWidth = 3 });
                        list.Add(new ArrowAnnotation { Start = new PointF(520, 80), End = new PointF(760, 220), Color = UiTheme.Green, LineWidth = 3.5f });
                        PenAnnotation pen = new PenAnnotation { Color = Color.FromArgb(255, 152, 0), LineWidth = 5 };
                        for (int i = 0; i < 12; i++) pen.Points.Add(new PointF(80 + i * 22, 330 + (float)Math.Sin(i * 0.6) * 40));
                        list.Add(pen);
                        list.Add(new MosaicAnnotation { Mask = MosaicAnnotation.BuildRectMask(new RectangleF(90, 150, 430, 130)) });
                        list.Add(new TextAnnotation { Text = "测试文字 Hello 123", Location = new PointF(80, 430), Color = Color.FromArgb(51, 51, 51), FontPx = 26 });
                        list.Add(new NumberAnnotation { Number = 1, Center = new PointF(480, 450), Color = UiTheme.Red, Radius = 15 });
                        list.Add(new NumberAnnotation { Number = 2, Center = new PointF(530, 450), Color = UiTheme.Red, Radius = 15 });
                        IReadOnlyList<EmojiItem> emojis = EmojiAssets.Items;
                        if (emojis != null && emojis.Count > 0 && emojis[0].Image != null)
                        {
                            list.Add(new EmojiAnnotation { Name = emojis[0].Name, Image = emojis[0].Image,
                                Rect = new RectangleF(700, 400, 64, 64) });
                        }
                        foreach (Annotation a in list) a.Draw(g, mosaic);
                        string galleryPath = Path.Combine(uiDir, "annotation_gallery.png");
                        canvas.Save(galleryPath, ImageFormat.Png);
                        check("标注类型渲染", HasWarmColor(canvas) && new FileInfo(galleryPath).Length > 2000, galleryPath);
                    }
                }
                catch (Exception ex)
                {
                    check("标注类型渲染", false, ex.Message);
                }

                // 2) 编辑器离屏交互：框选 → 选择矩形工具 → 画矩形 → 保存
                AppConfig cfg = new AppConfig();
                cfg.FirstRunDone = true;
                cfg.AutoCopy = false;
                cfg.AutoSave = false;
                cfg.SaveDir = Path.Combine(uiDir, "shots");
                cfg.SaveFormat = "png";
                cfg.ShowColorValue = true;
                cfg.ShowDimensions = true;
                Directory.CreateDirectory(cfg.SaveDir);
                ConfigStore store = new ConfigStore();
                Bitmap screenshot = BuildTestScreenshot(1200, 800);
                OverlayForm overlay = null;
                try
                {
                    overlay = new OverlayForm(screenshot, new Rectangle(0, 0, 1200, 800), cfg, store, null);
                    overlay.SuppressActivation = true;
                    overlay.Bounds = new Rectangle(-32000, -32000, 1200, 800);
                    overlay.Show();
                    Pump();

                    PostMouse(overlay.Handle, 0x0201, 100, 100);
                    Pump();
                    for (int i = 1; i <= 8; i++)
                    {
                        PostMouse(overlay.Handle, 0x0200, 100 + 600 * i / 8, 100 + 400 * i / 8);
                        Pump();
                    }
                    PostMouse(overlay.Handle, 0x0202, 700, 500);
                    Pump();

                    EditorToolbar toolbar = FindToolbar(overlay);
                    report.AppendLine("[INFO] 编辑器框选后状态: " + DescribeEditor(overlay, toolbar));
                    check("离屏框选建立", toolbar != null && toolbar.Visible, toolbar == null ? "未找到工具栏" : "选区已建立");
                    if (toolbar != null)
                    {
                        Rectangle rectBtn = toolbar.ToolBounds(EditorTool.Rectangle);
                        ClickControl(toolbar, rectBtn);
                        report.AppendLine("[INFO] 首次矩形工具点击后状态: " + DescribeEditor(overlay, toolbar));
                        PostMouse(overlay.Handle, 0x0201, 300, 300);
                        Pump();
                        for (int i = 1; i <= 6; i++)
                        {
                            PostMouse(overlay.Handle, 0x0200, 300 + 300 * i / 6, 300 + 180 * i / 6);
                            Pump();
                        }
                        PostMouse(overlay.Handle, 0x0202, 600, 480);
                        Pump();
                        report.AppendLine("[INFO] 首次绘制后状态: " + DescribeEditor(overlay, toolbar));

                        using (Bitmap snap = new Bitmap(overlay.Width, overlay.Height))
                        {
                            overlay.DrawToBitmap(snap, new Rectangle(0, 0, overlay.Width, overlay.Height));
                            string snapPath = Path.Combine(uiDir, "editor_annotated.png");
                            snap.Save(snapPath, ImageFormat.Png);
                            check("编辑器标注渲染", HasWarmColor(snap, new Rectangle(100, 100, 600, 400)), snapPath);
                        }

                        // 撤销：点撤销按钮后标注应消失
                        ClickControl(toolbar, toolbar.ActionBounds(ToolbarAction.Undo));
                        using (Bitmap undone = new Bitmap(overlay.Width, overlay.Height))
                        {
                            overlay.DrawToBitmap(undone, new Rectangle(0, 0, overlay.Width, overlay.Height));
                            undone.Save(Path.Combine(uiDir, "editor_after_undo.png"), ImageFormat.Png);
                            // 新控制台手柄本身使用橙色；检查内部内容区，避免把选区控制件误判为标注。
                            check("撤销功能", !HasWarmColor(undone, new Rectangle(130, 130, 540, 340)), "撤销后选区内容区不应有红色标注");
                        }

                        // 文字工具：点击文字工具 → 点击画布 → 文本框出现 → 输入 → Enter 提交
                        ClickControl(toolbar, toolbar.ToolBounds(EditorTool.Text));
                        report.AppendLine("[INFO] 文字工具点击后状态: " + DescribeEditor(overlay, toolbar));
                        PostMouse(overlay.Handle, 0x0201, 640, 420);
                        Pump(2);
                        PostMouse(overlay.Handle, 0x0202, 640, 420);
                        Pump(2);
                        TextBox textBox = FindTextBox(overlay);
                        bool textOk = false;
                        if (textBox != null)
                        {
                            foreach (char ch in "Hi") PostChar(textBox.Handle, ch);
                            Pump(2);
                            PostKeyDown(textBox.Handle, 0x0D); // Enter
                            Pump(3);
                            textOk = FindTextBox(overlay) == null;
                        }
                        check("文字工具交互", textOk, textOk ? "输入并提交成功" : "未找到文本框或提交失败");

                        // 重新画一个矩形，确保最终保存的图片包含标注
                        ClickControl(toolbar, toolbar.ToolBounds(EditorTool.Rectangle));
                        report.AppendLine("[INFO] 最终矩形工具点击后状态: " + DescribeEditor(overlay, toolbar));
                        PostMouse(overlay.Handle, 0x0201, 320, 300);
                        Pump();
                        for (int i = 1; i <= 5; i++)
                        {
                            PostMouse(overlay.Handle, 0x0200, 320 + 260 * i / 5, 300 + 160 * i / 5);
                            Pump();
                        }
                        PostMouse(overlay.Handle, 0x0202, 580, 460);
                        Pump();
                        report.AppendLine("[INFO] 最终绘制后状态: " + DescribeEditor(overlay, toolbar));

                        Rectangle saveBtn = toolbar.ActionBounds(ToolbarAction.Save);
                        ClickControl(toolbar, saveBtn);
                        Pump(6);
                    }

                    string[] saved = Directory.GetFiles(cfg.SaveDir, "截图_*.png");
                    check("保存流程", saved.Length > 0, saved.Length > 0 ? saved[0] : "没有生成文件");
                    if (saved.Length > 0)
                    {
                        using (Bitmap bmp = (Bitmap)Image.FromFile(saved[0]))
                        {
                            check("保存尺寸 600x400", bmp.Width == 600 && bmp.Height == 400, bmp.Width + "x" + bmp.Height);
                            check("保存图包含标注", HasWarmColor(bmp), saved[0]);
                        }
                    }
                    check("保存后编辑器关闭", overlay.IsDisposed, overlay.IsDisposed ? "已关闭" : "仍打开");

                    // 3) AI 面板集成（仅 mock 模式）：框选 → 点 OCR 按钮 → 等待流式结果 → 截图
                    if (!string.IsNullOrEmpty(mockUrl))
                    {
                        AppConfig aiCfg = cfg.Clone();
                        aiCfg.ApiKey = "mock-key";
                        aiCfg.BaseUrl = mockUrl;
                        aiCfg.ShowUsage = true;
                        Bitmap aiShot = BuildTestScreenshot(1100, 700);
                        OverlayForm aiOverlay = null;
                        try
                        {
                            aiOverlay = new OverlayForm(aiShot, new Rectangle(0, 0, 1100, 700), aiCfg, store, null);
                            aiOverlay.SuppressActivation = true;
                            aiOverlay.Bounds = new Rectangle(-33000, -33000, 1100, 700);
                            aiOverlay.Show();
                            Pump();
                            PostMouse(aiOverlay.Handle, 0x0201, 80, 80);
                            Pump();
                            for (int i = 1; i <= 8; i++)
                            {
                                PostMouse(aiOverlay.Handle, 0x0200, 80 + 800 * i / 8, 80 + 500 * i / 8);
                                Pump();
                            }
                            PostMouse(aiOverlay.Handle, 0x0202, 880, 580);
                            Pump();
                            EditorToolbar aiToolbar = FindToolbar(aiOverlay);
                            if (aiToolbar != null)
                            {
                                ClickControl(aiToolbar, aiToolbar.ActionBounds(ToolbarAction.Ocr));
                                string panelText = "";
                                bool panelFound = false;
                                for (int i = 0; i < 100; i++)
                                {
                                    Pump(1);
                                    AiResultPanel panel = FindAiPanel(aiOverlay);
                                    if (panel != null)
                                    {
                                        panelFound = true;
                                        panelText = CollectText(panel);
                                        if (panelText.Contains("识别文字")) break;
                                    }
                                    Thread.Sleep(60);
                                }
                                using (Bitmap aiSnap = new Bitmap(aiOverlay.Width, aiOverlay.Height))
                                {
                                    aiOverlay.DrawToBitmap(aiSnap, new Rectangle(0, 0, aiOverlay.Width, aiOverlay.Height));
                                    string aiPath = Path.Combine(uiDir, "ai_panel_ocr.png");
                                    aiSnap.Save(aiPath, ImageFormat.Png);
                                    check("AI 面板集成(OCR)", panelFound && panelText.Contains("第一行识别文字"),
                                        panelFound ? "面板文本=" + Shorten(panelText) : "未找到 AI 面板：" + aiPath);
                                }
                            }
                            else
                            {
                                check("AI 面板集成(OCR)", false, "未找到工具栏");
                            }
                        }
                        finally
                        {
                            if (aiOverlay != null)
                            {
                                try { if (!aiOverlay.IsDisposed) aiOverlay.Close(); } catch { }
                                try { aiOverlay.Dispose(); } catch { }
                            }
                        }
                    }
                }
                finally
                {
                    if (overlay != null)
                    {
                        try { if (!overlay.IsDisposed) overlay.Close(); } catch { }
                        try { overlay.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                check("离屏 UI 测试", false, ex.Message + "\n" + ex.StackTrace);
            }
        }

        private static string DescribeEditor(OverlayForm overlay, EditorToolbar toolbar)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            Type type = typeof(OverlayForm);
            object tool = type.GetField("_tool", flags).GetValue(overlay);
            object selection = type.GetField("_selection", flags).GetValue(overlay);
            object busy = type.GetField("_busy", flags).GetValue(overlay);
            System.Collections.ICollection annotations = (System.Collections.ICollection)type.GetField("_annotations", flags).GetValue(overlay);
            return "tool=" + tool + "，toolbar=" + (toolbar == null ? "null" : toolbar.ActiveTool.ToString()) +
                "，selection=" + selection + "，busy=" + busy + "，annotations=" + annotations.Count +
                "，toolbarDisposed=" + (toolbar == null || toolbar.IsDisposed);
        }

        private static EditorToolbar FindToolbar(OverlayForm overlay)
        {
            foreach (Control control in overlay.Controls)
            {
                EditorToolbar toolbar = control as EditorToolbar;
                if (toolbar != null) return toolbar;
            }
            return null;
        }

        private static void Pump(int rounds = 3)
        {
            for (int i = 0; i < rounds; i++)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }
        }

        private static void PostMouse(IntPtr hwnd, int message, int x, int y)
        {
            int lParam = ((y & 0xFFFF) << 16) | (x & 0xFFFF);
            if (message == 0x0201) LeftButtonTargets.Add(hwnd);
            if (message == 0x0202) LeftButtonTargets.Remove(hwnd);
            // 拖动消息携带真实鼠标的 MK_LBUTTON 状态，不能把按住左键报告为悬停。
            int keyState = LeftButtonTargets.Contains(hwnd) ? 1 : 0;
            Native.PostMessage(hwnd, (uint)message, new IntPtr(keyState), new IntPtr(lParam));
        }

        private static void ClickControl(Control control, Rectangle localBounds)
        {
            IntPtr hwnd = control.Handle;
            int x = localBounds.X + localBounds.Width / 2;
            int y = localBounds.Y + localBounds.Height / 2;
            PostMouse(hwnd, 0x0201, x, y);
            Pump(2);
            if (!control.IsDisposed) PostMouse(hwnd, 0x0202, x, y);
            Pump(2);
        }

        private static bool HasWarmColor(Bitmap bitmap)
        {
            return HasWarmColor(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }

        private static bool HasWarmColor(Bitmap bitmap, Rectangle area)
        {
            area = Rectangle.Intersect(area, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            if (area.Width <= 0 || area.Height <= 0) return false;
            int stepX = Math.Max(1, area.Width / 200);
            int stepY = Math.Max(1, area.Height / 200);
            for (int y = area.Top; y < area.Bottom; y += stepY)
            {
                for (int x = area.Left; x < area.Right; x += stepX)
                {
                    Color c = bitmap.GetPixel(x, y);
                    if (c.R > 190 && c.G < 130 && c.B < 130) return true;
                }
            }
            return false;
        }

        private static Bitmap BuildTestScreenshot(int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    new Rectangle(0, 0, width, height), Color.FromArgb(245, 247, 250), Color.FromArgb(210, 220, 235), 45f))
                    g.FillRectangle(bg, 0, 0, width, height);
                using (SolidBrush card = new SolidBrush(Color.White))
                    g.FillRectangle(card, 60, 60, width - 120, height - 160);
                using (Pen border = new Pen(Color.FromArgb(220, 224, 230)))
                    g.DrawRectangle(border, 60, 60, width - 120, height - 160);
                using (Font title = new Font(UiTheme.UiFontName, 28, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush dark = new SolidBrush(Color.FromArgb(45, 45, 55)))
                    g.DrawString("截图助手 · 离屏渲染测试", title, dark, 100, 100);
                using (Font body = new Font(UiTheme.UiFontName, 18, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush gray = new SolidBrush(Color.FromArgb(90, 100, 110)))
                {
                    g.DrawString("用于验证标注、工具栏与保存流程，不会显示在真实桌面上。", body, gray, 100, 160);
                    g.DrawString("第二行测试文字：ABCDEFG 1234567890", body, gray, 100, 195);
                    g.DrawString("第三行测试文字：截图 / API / OCR / 翻译", body, gray, 100, 230);
                }
                Random random = new Random(42);
                int areaW = Math.Max(40, width - 140);
                int areaH = Math.Max(30, height - 140);
                for (int i = 0; i < 18; i++)
                {
                    int w = 30 + random.Next(Math.Max(20, Math.Min(160, areaW / 2)));
                    int h = 14 + random.Next(Math.Max(10, Math.Min(60, areaH / 4)));
                    int x = 70 + random.Next(Math.Max(10, areaW - w - 10));
                    int y = 70 + random.Next(Math.Max(10, areaH - h - 10));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(120 + random.Next(100), 140 + random.Next(80), 160 + random.Next(80))))
                        g.FillRectangle(b, x, y, w, h);
                }
            }
            bmp.SetResolution(96, 96);
            return bmp;
        }

        // ============================ DeepSeek API mock 集成测试 ============================

        private static void RunApiTests(StringBuilder report, Action<string, bool, string> check, string baseUrl, string apiKey)
        {
            report.AppendLine();
            report.AppendLine("DeepSeek API mock 测试: " + baseUrl);
            AppConfig cfg = new AppConfig();
            cfg.BaseUrl = baseUrl;
            cfg.ApiKey = apiKey;
            cfg.VisionModel = "deepseek-flash";
            cfg.TextModel = "deepseek-flash";
            cfg.TimeoutSec = 30;
            DeepSeekClient client = new DeepSeekClient(cfg);
            Bitmap image = BuildTestScreenshot(420, 220);
            try
            {
                using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                {
                    AiUsage usage = RunSync(client.TestConnectionAsync(cts.Token));
                    check("API 测试连接", usage != null, usage == null ? "" : usage.ToString());

                    StringBuilder deltas = new StringBuilder();
                    string ocr = RunSync(client.OcrAsync(image, delegate (string piece) { deltas.Append(piece); }, null, cts.Token));
                    check("API 文字提取(流式)", !string.IsNullOrEmpty(ocr) && deltas.Length > 0,
                        "结果=" + Shorten(ocr) + "；流式增量长度=" + deltas.Length);

                    StringBuilder originalDeltas = new StringBuilder();
                    StringBuilder translationDeltas = new StringBuilder();
                    string[] translated = RunSync(client.TranslateImageAsync(image, "英文",
                        delegate (string piece) { originalDeltas.Append(piece); },
                        delegate (string piece) { translationDeltas.Append(piece); }, null, cts.Token));
                    bool translateOk = translated != null && translated.Length == 2 &&
                                       !string.IsNullOrEmpty(translated[0]) && !string.IsNullOrEmpty(translated[1]) &&
                                       translationDeltas.Length > 0;
                    check("API 图片翻译(流式)", translateOk,
                        translateOk ? "原文=" + Shorten(translated[0]) + "；译文=" + Shorten(translated[1]) :
                        "translated=" + (translated == null ? "null" : string.Join(" | ", translated)));

                    List<PrivacyRegion> regions = RunSync(client.DetectPrivacyAsync(image, cts.Token));
                    check("API 一键打码检测", regions != null && regions.Count >= 2,
                        regions == null ? "null" : "区域数=" + regions.Count + "，首个=" + (regions.Count > 0 ? regions[0].ToString() : ""));

                    AppConfig badCfg = cfg.Clone();
                    badCfg.ApiKey = "wrong-key";
                    DeepSeekClient badClient = new DeepSeekClient(badCfg);
                    bool gotError = false;
                    string errorMessage = "";
                    try
                    {
                        RunSync(badClient.TestConnectionAsync(cts.Token));
                    }
                    catch (AiException ex)
                    {
                        gotError = true;
                        errorMessage = ex.Message;
                    }
                    check("API 401 错误提示", gotError && errorMessage.Contains("API Key"), Shorten(errorMessage));
                }
            }
            catch (Exception ex)
            {
                check("API mock 测试", false, ex.Message);
            }
            finally
            {
                image.Dispose();
            }
        }

        private static TextBox FindTextBox(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                TextBox box = control as TextBox;
                if (box != null) return box;
                TextBox nested = FindTextBox(control);
                if (nested != null) return nested;
            }
            return null;
        }

        private static void PostChar(IntPtr hwnd, char ch)
        {
            Native.PostMessage(hwnd, 0x0102, new IntPtr(ch), IntPtr.Zero);
        }

        private static void PostKeyDown(IntPtr hwnd, int vk)
        {
            Native.PostMessage(hwnd, 0x0100, new IntPtr(vk), IntPtr.Zero);
            Native.PostMessage(hwnd, 0x0101, new IntPtr(vk), IntPtr.Zero);
        }

        private static AiResultPanel FindAiPanel(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                AiResultPanel panel = control as AiResultPanel;
                if (panel != null) return panel;
            }
            return null;
        }

        private static string CollectText(Control parent)
        {
            StringBuilder sb = new StringBuilder();
            CollectTextRecursive(parent, sb);
            return sb.ToString();
        }

        private static void CollectTextRecursive(Control parent, StringBuilder sb)
        {
            foreach (Control control in parent.Controls)
            {
                TextBox box = control as TextBox;
                if (box != null && !string.IsNullOrEmpty(box.Text)) sb.AppendLine(box.Text);
                Label label = control as Label;
                if (label != null && !string.IsNullOrEmpty(label.Text)) sb.AppendLine(label.Text);
                CollectTextRecursive(control, sb);
            }
        }

        private static T RunSync<T>(Task<T> task)
        {
            return task.GetAwaiter().GetResult();
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string oneLine = text.Replace("\r", " ").Replace("\n", " ");
            return oneLine.Length <= 60 ? oneLine : oneLine.Substring(0, 60) + "…";
        }

        private static void RunAutostartTest(Action<string, bool, string> check)
        {
            bool original = false;
            try { original = Autostart.IsEnabled(); } catch { }
            try
            {
                Autostart.SetEnabled(true);
                bool enabled = Autostart.IsEnabled();
                string value = "";
                try
                {
                    using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", false))
                    {
                        if (key != null)
                        {
                            object v = key.GetValue("XiaoJieWeChatShot");
                            if (v != null) value = v.ToString();
                        }
                    }
                }
                catch { }
                Autostart.SetEnabled(false);
                bool disabled = Autostart.IsEnabled();
                bool ok = enabled && !disabled && value.IndexOf("截图助手.exe", StringComparison.OrdinalIgnoreCase) >= 0;
                check("开机自启注册表开关", ok,
                    "开启=" + enabled + "，关闭=" + disabled + "，Run 值=" + value);
            }
            catch (Exception ex)
            {
                check("开机自启注册表开关", false, ex.Message);
            }
            finally
            {
                try { Autostart.SetEnabled(original); } catch { }
            }
        }

        // ============================ 真实 AI 集成测试 ============================

        private static void RunAiRealTests(StringBuilder report, string reportDir, Action<string, bool, string> check, AppConfig cfg)
        {
            report.AppendLine();
            report.AppendLine("真实 AI 测试（BaseUrl=" + cfg.BaseUrl + "，模型=" + cfg.VisionModel + "）");
            string aiDir = Path.Combine(reportDir, "ai");
            Directory.CreateDirectory(aiDir);
            Bitmap image = BuildAiSampleImage();
            DeepSeekClient client = new DeepSeekClient(cfg);
            try
            {
                using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(180)))
                {
                    // 1) 真实 OCR（流式）
                    StringBuilder deltas = new StringBuilder();
                    string ocr = RunSync(client.OcrAsync(image, delegate (string piece) { deltas.Append(piece); }, null, cts.Token));
                    bool ocrOk = !string.IsNullOrEmpty(ocr) && deltas.Length > 0 && ocr.Contains("会议");
                    check("真实 AI 文字提取", ocrOk, "结果=" + Shorten(ocr) + "，流式增量=" + deltas.Length);
                    if (ocrOk) File.WriteAllText(Path.Combine(aiDir, "ocr.txt"), ocr, new UTF8Encoding(false));

                    // 2) 真实原位翻译
                    List<ImageTextBlock> blocks = RunSync(client.TranslateInPlaceAsync(image, cfg.TranslateTarget, cts.Token));
                    int translated = 0;
                    foreach (ImageTextBlock block in blocks)
                    {
                        if (block.HasRect && !string.IsNullOrEmpty(block.Translation) &&
                            NormalizeForCompare(block.Text) != NormalizeForCompare(block.Translation)) translated++;
                    }
                    bool translateOk = blocks.Count >= 3 && translated >= 2;
                    check("真实 AI 原位翻译", translateOk,
                        "行数=" + blocks.Count + "，实际变化=" + translated + "，目标语言=" + cfg.TranslateTarget +
                        "，示例=" + (blocks.Count > 0 ? Shorten(blocks[0].Text) + " => " + Shorten(blocks[0].Translation) : ""));
                    report.AppendLine("  [AI] 图片尺寸=" + image.Width + "x" + image.Height + "，前 3 个 block 坐标：");
                    for (int i = 0; i < Math.Min(3, blocks.Count); i++)
                    {
                        ImageTextBlock b = blocks[i];
                        report.AppendLine("    #" + i + " text=" + Shorten(b.Text) + " rect=(" +
                            (b.X * image.Width).ToString("0") + "," + (b.Y * image.Height).ToString("0") + "," +
                            (b.W * image.Width).ToString("0") + "," + (b.H * image.Height).ToString("0") + ")");
                    }
                    if (blocks.Count > 0)
                    {
                        using (Bitmap canvas = (Bitmap)image.Clone())
                        {
                            TranslationOverlayAnnotation overlay = new TranslationOverlayAnnotation();
                            List<TextLineDetector.TextLine> detectedLines = TextLineDetector.Detect(image, new Rectangle(0, 0, image.Width, image.Height));
                            RectangleF[] mappedLines = TextLineDetector.MapBlocks(detectedLines, blocks, new Rectangle(0, 0, image.Width, image.Height));
                            for (int bi = 0; bi < blocks.Count; bi++)
                            {
                                ImageTextBlock block = blocks[bi];
                                if (NormalizeForCompare(block.Text) == NormalizeForCompare(block.Translation)) continue;
                                RectangleF rect;
                                if (bi < mappedLines.Length && mappedLines[bi] != RectangleF.Empty) rect = mappedLines[bi];
                                else if (block.HasRect)
                                {
                                    rect = new RectangleF(block.X * image.Width, block.Y * image.Height,
                                        Math.Max(1f, block.W * image.Width), Math.Max(1f, block.H * image.Height));
                                    rect = PrivacyDetector.InkRect.Refine(image, rect, new Rectangle(0, 0, image.Width, image.Height), 0.8f);
                                }
                                else continue;
                                rect.Inflate(2f, 1f);
                                overlay.Blocks.Add(new TranslationBlock
                                {
                                    Rect = rect, Original = block.Text, Translation = block.Translation,
                                    Background = Color.White, Foreground = Color.FromArgb(20, 20, 20),
                                    FontPx = Math.Max(9f, Math.Min(36f, rect.Height * 0.82f))
                                });
                            }
                            using (Graphics g = Graphics.FromImage(canvas)) overlay.Draw(g, null);
                            string path = Path.Combine(aiDir, "translate_inplace.png");
                            canvas.Save(path, ImageFormat.Png);
                            check("原位翻译渲染图", translated > 0, path);
                        }
                    }

                    // 3) 真实隐私分析（行 + 人脸框）
                    PrivacyAnalysis analysis = RunSync(client.AnalyzePrivacyAsync(image, cts.Token));
                    int lineCount, faceCount;
                    List<RectangleF> rects = PrivacyDetector.BuildMaskRects(image, analysis, new Rectangle(0, 0, image.Width, image.Height), 0.18f, out lineCount, out faceCount);
                    bool privacyOk = lineCount >= 2;
                    check("真实 AI 隐私检测", privacyOk,
                        "文字命中=" + lineCount + "，人脸=" + faceCount + "，行数=" + (analysis.Lines == null ? 0 : analysis.Lines.Count));
                    report.AppendLine("  [AI] 隐私行=" + (analysis.Lines == null ? 0 : analysis.Lines.Count) +
                        "，人脸=" + (analysis.Faces == null ? 0 : analysis.Faces.Count) + "，打码矩形=" + rects.Count);
                    for (int fi = 0; analysis.Faces != null && fi < analysis.Faces.Count; fi++)
                    {
                        FaceBox fb = analysis.Faces[fi];
                        report.AppendLine("    face rect=(" + (fb.X * image.Width).ToString("0") + "," + (fb.Y * image.Height).ToString("0") +
                            "," + (fb.W * image.Width).ToString("0") + "x" + (fb.H * image.Height).ToString("0") + ")");
                    }
                    image.Save(Path.Combine(aiDir, "ai_sample.png"), ImageFormat.Png);
                    if (analysis.Faces != null && analysis.Faces.Count > 0)
                    {
                        using (Bitmap canvas = (Bitmap)image.Clone())
                        using (MosaicCanvas mosaic = new MosaicCanvas(image, Math.Max(22, (int)cfg.MosaicBlock)))
                        using (Graphics g = Graphics.FromImage(canvas))
                        {
                            FaceBox fb = analysis.Faces[0];
                            RectangleF fr = new RectangleF(fb.X * image.Width, fb.Y * image.Height, fb.W * image.Width, fb.H * image.Height);
                            using (GraphicsPath p2 = new GraphicsPath())
                            {
                                p2.AddRectangle(fr);
                                new MosaicAnnotation { Mask = p2 }.Draw(g, mosaic);
                            }
                            canvas.Save(Path.Combine(aiDir, "privacy_face_only.png"), ImageFormat.Png);
                        }
                    }
                    if (rects.Count > 0)
                    {
                        using (Bitmap canvas = (Bitmap)image.Clone())
                        using (MosaicCanvas mosaic = new MosaicCanvas(image, Math.Max(22, (int)cfg.MosaicBlock)))
                        using (Graphics g = Graphics.FromImage(canvas))
                        {
                            MosaicAnnotation annotation = new MosaicAnnotation { Rects = new List<RectangleF>(rects) };
                            annotation.Draw(g, mosaic);
                            string png = Path.Combine(aiDir, "privacy_masked.png");
                            canvas.Save(png, ImageFormat.Png);
                            check("智能打码渲染图", true, png);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                check("真实 AI 测试", false, ex.Message + "\n" + ex.StackTrace);
            }
            finally
            {
                image.Dispose();
            }
        }

        private static string NormalizeForCompare(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (".,，。:：;；!！?？\"'“”‘’".IndexOf(c) >= 0) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static Bitmap BuildAiSampleImage()
        {
            Bitmap bmp = new Bitmap(960, 540, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(248, 249, 251));
                using (SolidBrush card = new SolidBrush(Color.White)) g.FillRectangle(card, 30, 30, 900, 480);
                using (Pen border = new Pen(Color.FromArgb(225, 228, 233))) g.DrawRectangle(border, 30, 30, 900, 480);
                using (SolidBrush avatar = new SolidBrush(Color.FromArgb(90, 160, 220))) g.FillEllipse(avatar, 60, 60, 80, 80);
                using (Font title = new Font(UiTheme.UiFontName, 26, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush dark = new SolidBrush(Color.FromArgb(32, 32, 36)))
                    g.DrawString("张伟  产品经理", title, dark, 165, 66);
                using (Font body = new Font(UiTheme.UiFontName, 20, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush gray = new SolidBrush(Color.FromArgb(60, 64, 70)))
                {
                    g.DrawString("会议通知：明天下午 3 点在三楼会议室开会", body, gray, 60, 170);
                    g.DrawString("联系人：张伟  手机：13812345678", body, gray, 60, 215);
                    g.DrawString("身份证号：110101199003071234", body, gray, 60, 260);
                    g.DrawString("地址：北京市朝阳区建国路 88 号 SOHO 现代城 A 座 1203", body, gray, 60, 305);
                    g.DrawString("邮箱：zhangwei@example.com", body, gray, 60, 350);
                    g.DrawString("English: Hello OCR, this is a sample screenshot.", body, gray, 60, 405);
                }
            }
            return bmp;
        }
    }
}
