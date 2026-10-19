import io, re, sys
path = r'D:\Agents\微信截图助手\src\SelfTest.cs'
src = open(path, encoding='utf-8').read()

call_code = '''
                // ===== 离屏 UI 测试（不干扰用户桌面）=====
                RunUiTests(report, Path.GetDirectoryName(outPath), check);

                report.AppendLine();
                report.AppendLine(failures == 0 ? "结果: 全部通过" : "结果: " + failures + " 项失败");
'''

old_tail = '''
                report.AppendLine();
                report.AppendLine(failures == 0 ? "结果: 全部通过" : "结果: " + failures + " 项失败");
'''
assert old_tail in src, "tail not found"
src = src.replace(old_tail, call_code, 1)

methods = r'''
        // ============================ 离屏 UI 自动化测试 ============================

        private static void RunUiTests(StringBuilder report, string reportDir, Action<string, bool, string> check)
        {
            try
            {
                string uiDir = Path.Combine(reportDir, "ui");
                Directory.CreateDirectory(uiDir);

                // 1) 工具栏图标一览（直接渲染，便于人工检查）
                try
                {
                    Rectangle[] bounds = new Rectangle[17];
                    using (Bitmap sheet = new Bitmap(17 * 58 + 20, 110))
                    {
                        using (Graphics g = Graphics.FromImage(sheet))
                        {
                            g.Clear(Color.White);
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            EditorToolbar toolbar = new EditorToolbar(1f);
                            toolbar.BringToFront();
                            for (int i = 0; i < 17; i++) bounds[i] = toolbar.NonSeparatorBounds(i);
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
                    check("离屏框选建立", toolbar != null && toolbar.Visible, toolbar == null ? "未找到工具栏" : "选区已建立");
                    if (toolbar != null)
                    {
                        Rectangle rectBtn = toolbar.NonSeparatorBounds(0);
                        ClickControl(toolbar, rectBtn);
                        PostMouse(overlay.Handle, 0x0201, 300, 300);
                        Pump();
                        for (int i = 1; i <= 6; i++)
                        {
                            PostMouse(overlay.Handle, 0x0200, 300 + 300 * i / 6, 300 + 180 * i / 6);
                            Pump();
                        }
                        PostMouse(overlay.Handle, 0x0202, 600, 480);
                        Pump();

                        using (Bitmap snap = new Bitmap(overlay.Width, overlay.Height))
                        {
                            overlay.DrawToBitmap(snap, new Rectangle(0, 0, overlay.Width, overlay.Height));
                            string snapPath = Path.Combine(uiDir, "editor_annotated.png");
                            snap.Save(snapPath, ImageFormat.Png);
                            check("编辑器标注渲染", HasWarmColor(snap), snapPath);
                        }

                        Rectangle saveBtn = toolbar.NonSeparatorBounds(12);
                        ClickControl(toolbar, saveBtn);
                        Pump(6);
                    }

                    string[] saved = Directory.GetFiles(cfg.SaveDir, "微信截图_*.png");
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
            Native.PostMessage(hwnd, (uint)message, IntPtr.Zero, new IntPtr(lParam));
        }

        private static void ClickControl(Control control, Rectangle localBounds)
        {
            int x = localBounds.X + localBounds.Width / 2;
            int y = localBounds.Y + localBounds.Height / 2;
            PostMouse(control.Handle, 0x0201, x, y);
            Pump(2);
            PostMouse(control.Handle, 0x0202, x, y);
            Pump(2);
        }

        private static bool HasWarmColor(Bitmap bitmap)
        {
            int stepX = Math.Max(1, bitmap.Width / 200);
            int stepY = Math.Max(1, bitmap.Height / 200);
            for (int y = 0; y < bitmap.Height; y += stepY)
            {
                for (int x = 0; x < bitmap.Width; x += stepX)
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
                    g.DrawString("微信截图助手 · 离屏渲染测试", title, dark, 100, 100);
                using (Font body = new Font(UiTheme.UiFontName, 18, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush gray = new SolidBrush(Color.FromArgb(90, 100, 110)))
                {
                    g.DrawString("用于验证标注、工具栏与保存流程，不会显示在真实桌面上。", body, gray, 100, 160);
                    g.DrawString("第二行测试文字：ABCDEFG 1234567890", body, gray, 100, 195);
                    g.DrawString("第三行测试文字：微信截图 / DeepSeek / OCR / 翻译", body, gray, 100, 230);
                }
                Random random = new Random(42);
                for (int i = 0; i < 18; i++)
                {
                    int x = 100 + random.Next(width - 300);
                    int y = 280 + random.Next(height - 420);
                    int w = 40 + random.Next(160);
                    int h = 18 + random.Next(60);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(120 + random.Next(100), 140 + random.Next(80), 160 + random.Next(80))))
                        g.FillRectangle(b, x, y, w, h);
                }
            }
            bmp.SetResolution(96, 96);
            return bmp;
        }
'''
insert_at = src.rstrip().rfind('}')
src = src[:insert_at] + methods + '\n' + src[insert_at:]
# 补充 using
src = src.replace('using System.Threading;', 'using System.Threading;\nusing System.Drawing.Drawing2D;\nusing System.Drawing.Imaging;')
open(path, 'w', encoding='utf-8').write(src)
print("SelfTest patched, len =", len(src))
