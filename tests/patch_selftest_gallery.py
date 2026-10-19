path = r'D:\Agents\微信截图助手\src\SelfTest.cs'
src = open(path, encoding='utf-8').read()

# 1) 调用点传入 mockUrl
src = src.replace('RunUiTests(report, Path.GetDirectoryName(outPath), check);',
                  'RunUiTests(report, Path.GetDirectoryName(outPath), check, mockUrl);', 1)
# 2) 修改签名
src = src.replace('private static void RunUiTests(StringBuilder report, string reportDir, Action<string, bool, string> check)',
                  'private static void RunUiTests(StringBuilder report, string reportDir, Action<string, bool, string> check, string mockUrl)', 1)

# 3) 在 RunUiTests 的工具栏图片测试之后插入标注集渲染；在 UI 测试末尾（保存流程 check 之后）插入 AI 面板集成测试
anchor1 = '''                // 2) 编辑器离屏交互：框选 → 选择矩形工具 → 画矩形 → 保存
'''
gallery = '''                // 2) 全部标注类型渲染（离屏）
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
                        list.Add(new MosaicAnnotation { Mask = MosaicAnnotation.BuildRectMask(new RectangleF(620, 300, 180, 90)) });
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

'''
assert anchor1 in src
src = src.replace(anchor1, gallery + anchor1, 1)

anchor2 = '''                    check("保存后编辑器关闭", overlay.IsDisposed, overlay.IsDisposed ? "已关闭" : "仍打开");
'''
ai_test = '''                    check("保存后编辑器关闭", overlay.IsDisposed, overlay.IsDisposed ? "已关闭" : "仍打开");

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
                                ClickControl(aiToolbar, aiToolbar.NonSeparatorBounds(9)); // OCR
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
'''
assert anchor2 in src
src = src.replace(anchor2, ai_test, 1)

# 4) 增加辅助方法（在 RunSync 之前插入）
anchor3 = '        private static T RunSync<T>(Task<T> task)'
helpers = '''        private static AiResultPanel FindAiPanel(Control parent)
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

        private static T RunSync<T>(Task<T> task)'''
assert anchor3 in src
src = src.replace(anchor3, helpers, 1)

open(path, 'w', encoding='utf-8').write(src)
print('patched gallery + AI panel test')
