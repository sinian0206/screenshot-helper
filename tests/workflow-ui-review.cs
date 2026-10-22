using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace XiaoJie
{
    internal static class WorkflowUiReview
    {
        private static int _passed, _failed;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [STAThread]
        private static int Main()
        {
            Check("独立快捷命令消息与操作身份一致", delegate
            {
                Equal(CaptureIntent.Ocr, CaptureCommand.FromArgs(new[] { "--ocr" }));
                Equal(CaptureIntent.Translate, CaptureCommand.FromArgs(new[] { "--translate" }));
                Equal(CaptureIntent.Privacy, CaptureCommand.FromArgs(new[] { "--redact" }));
                Equal((uint)AppInfo.WM_APP_TRIGGER_OCR, CaptureCommand.MessageForArgs(new[] { "--ocr" }));
                Equal((uint)AppInfo.WM_APP_TRIGGER_TRANSLATE, CaptureCommand.MessageForArgs(new[] { "--translate" }));
                Equal((uint)AppInfo.WM_APP_TRIGGER_PRIVACY, CaptureCommand.MessageForArgs(new[] { "--redact" }));
            });
            Check("窄屏100%/150%/200%工具栏换行后19个按钮均可点击", delegate
            {
                foreach (float scale in new[] { 1f, 1.5f, 2f })
                    using (EditorToolbar toolbar = new EditorToolbar(scale))
                    {
                        toolbar.MaximumLayoutWidth = 360;
                        toolbar.CanUndo = true; toolbar.CanRedo = true;
                        int clickCount = 0;
                        toolbar.ToolClicked += delegate { clickCount++; };
                        toolbar.ActionClicked += delegate { clickCount++; };
                        Require(toolbar.Width <= 360, "工具栏超出工作区宽度");
                        for (int i = 0; i < 19; i++)
                        {
                            Rectangle button = toolbar.NonSeparatorBounds(i);
                            Require(!button.IsEmpty && toolbar.ClientRectangle.Contains(button), "按钮裁切");
                            for (int j = i + 1; j < 19; j++) Require(!button.IntersectsWith(toolbar.NonSeparatorBounds(j)), "按钮重叠");
                            Call(toolbar, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, button.Left + button.Width / 2, button.Top + button.Height / 2, 0));
                        }
                        Equal(19, clickCount);
                    }
            });
            Check("审阅开关改变实际输出像素且撤销重做完整恢复", delegate
            {
                using (OverlayForm overlay = NewOverlay())
                {
                    var analysis = new PrivacyAnalysis { HasLocalCoordinates = true };
                    analysis.Lines.Add(new ImageTextBlock { Text = "手机13812345678", Kind = "phone", Sensitive = true, X = .1f, Y = .1f, W = .2f, H = .1f });
                    analysis.Lines.Add(new ImageTextBlock { Text = "姓名测试", Kind = "name", Sensitive = true, X = .1f, Y = .5f, W = .2f, H = .1f });
                    object[] args = { analysis, null };
                    Call(overlay, "MergePrivacyReviewStage", args);
                    var review = (PrivacyReviewSession)Get(overlay, "_privacyReview");
                    Equal(2, review.Count);
                    using (Bitmap both = (Bitmap)Call(overlay, "ComposeSelection"))
                    {
                        Require(both.GetPixel(70, 55).ToArgb() != Color.White.ToArgb(), "手机未被遮挡");
                        Require(both.GetPixel(70, 175).ToArgb() != Color.White.ToArgb(), "姓名未被遮挡");
                    }
                    var cancellation = new CancellationTokenSource();
                    Set(overlay, "_privacyCts", cancellation);
                    review.SetCategoryEnabled("phone", false);
                    Call(overlay, "ApplyPrivacyReviewChoices");
                    Require(cancellation.IsCancellationRequested, "修改审阅未取消后台补漏");
                    cancellation.Dispose();
                    using (Bitmap selected = (Bitmap)Call(overlay, "ComposeSelection"))
                    {
                        Equal(Color.White.ToArgb(), selected.GetPixel(70, 55).ToArgb());
                        Require(selected.GetPixel(70, 175).ToArgb() != Color.White.ToArgb(), "关闭手机类误清除姓名");
                        Call(overlay, "Undo");
                        using (Bitmap undone = (Bitmap)Call(overlay, "ComposeSelection")) Require(undone.GetPixel(70, 55).ToArgb() != Color.White.ToArgb(), "撤销未恢复手机遮挡");
                        Call(overlay, "Redo");
                        using (Bitmap redone = (Bitmap)Call(overlay, "ComposeSelection")) Compare(selected, redone);
                    }
                }
            });
            Check("选区快捷操作在框选前只排队不启动识别", delegate
            {
                using (OverlayForm overlay = NewOverlay())
                {
                    Set(overlay, "_hasSelection", false);
                    overlay.StartActionAfterSelection(ToolbarAction.Translate);
                    Equal(ToolbarAction.Translate, Get(overlay, "_selectionAction"));
                    Equal(false, Get(overlay, "_busy"));
                    overlay.StartPrivacyAfterSelection();
                    Equal(ToolbarAction.None, Get(overlay, "_selectionAction"));
                    Equal(true, Get(overlay, "_selectionPrivacy"));
                    Equal(false, Get(overlay, "_busy"));
                }
            });
            Check("本地零候选时关闭类别仍取消补漏且保留审阅选择", delegate
            {
                using (OverlayForm overlay = NewOverlay())
                {
                    object[] initial = { new PrivacyAnalysis { HasLocalCoordinates = true }, null };
                    Call(overlay, "MergePrivacyReviewStage", initial);
                    var session = (PrivacyReviewSession)Get(overlay, "_privacyReview");
                    session.SetCategoryEnabled("name", false);
                    using (var cancellation = new CancellationTokenSource())
                    {
                        Set(overlay, "_privacyCts", cancellation);
                        Call(overlay, "ApplyPrivacyReviewChoices");
                        Require(cancellation.IsCancellationRequested, "零候选审阅也须取消后台补漏");
                    }
                    var later = new PrivacyAnalysis { HasLocalCoordinates = true };
                    later.Lines.Add(new ImageTextBlock { Text = "测试姓名", Kind = "name", Sensitive = true, X = .1f, Y = .1f, W = .2f, H = .1f });
                    object[] stage = { later, null };
                    Call(overlay, "MergePrivacyReviewStage", stage);
                    Require(ReferenceEquals(session, Get(overlay, "_privacyReview")), "零候选阶段不应重置审阅");
                    Equal(0, session.BuildRects().Count);
                }
            });
            Check("切换到其他操作时结束旧审阅并保留输出遮挡", delegate
            {
                using (OverlayForm overlay = NewOverlay())
                {
                    var analysis = new PrivacyAnalysis { HasLocalCoordinates = true };
                    analysis.Lines.Add(new ImageTextBlock { Text = "手机13812345678", Kind = "phone", Sensitive = true,
                        X = .1f, Y = .1f, W = .2f, H = .1f });
                    object[] stage = { analysis, null };
                    Call(overlay, "MergePrivacyReviewStage", stage);
                    using (Bitmap before = (Bitmap)Call(overlay, "ComposeSelection"))
                    {
                        object[] action = { ToolbarAction.Translate, false };
                        Call(overlay, "BeforeEditorAction", action);
                        Require(Get(overlay, "_privacyReview") == null, "切换操作后旧审阅仍能改动编辑状态");
                        using (Bitmap after = (Bitmap)Call(overlay, "ComposeSelection")) Compare(before, after);
                    }
                }
            });
            Check("设置在560px窄视口时保存与取消按钮可见", delegate
            {
                using (SettingsForm settings = new SettingsForm(new AppConfig(), new ConfigStore(), false))
                {
                    settings.ClientSize = new Size(560, 500);
                    Call(settings, "ApplyViewport");
                    Panel footer = (Panel)Get(settings, "_bottomPanel");
                    foreach (string name in new[] { "_saveBtn", "_cancelBtn", "_resetBtn" })
                    {
                        Control button = (Control)Get(settings, name);
                        Require(footer.ClientRectangle.Contains(button.Bounds), name + " 超出底栏，位置=" + button.Bounds + " 底栏=" + footer.ClientRectangle);
                    }
                }
            });
            Check("设置与审阅在100%/150%/200%生成短屏渲染证据", delegate
            {
                string evidence = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "evidence");
                Directory.CreateDirectory(evidence);
                foreach (float scale in new[] { 1f, 1.5f, 2f })
                {
                    using (SettingsForm settings = new SettingsForm(new AppConfig(), new ConfigStore(), false))
                    {
                        ShowOffscreen(settings);
                        settings.Scale(new SizeF(scale, scale));
                        settings.ClientSize = new Size((int)(560 * scale), Math.Min(800, (int)(500 * scale)));
                        Call(settings, "ApplyViewport");
                        for (int page = 0; page < 3; page++)
                        {
                            Call(settings, "ShowPage", page);
                            settings.PerformLayout();
                            Application.DoEvents();
                            using (Bitmap rendered = new Bitmap(settings.Width, settings.Height))
                            {
                                settings.DrawToBitmap(rendered, settings.ClientRectangle);
                                Require(HasRenderedContent(rendered), "设置渲染缺少控件像素");
                                rendered.Save(Path.Combine(evidence, "settings-" + page + "-" + (int)(scale * 100) + ".png"), ImageFormat.Png);
                            }
                        }
                        Panel viewport = (Panel)Get(settings, "_pageShot");
                        foreach (string field in new[] { "_recordFormatCombo", "_recordAudioCombo", "_mosaicNum", "_ocrHotkeyBox", "_translateHotkeyBox", "_privacyHotkeyBox" })
                        {
                            Control control = (Control)Get(settings, field);
                            viewport.ScrollControlIntoView(control);
                            settings.PerformLayout();
                            Application.DoEvents();
                            Rectangle visible = viewport.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                            Require(viewport.ClientRectangle.Contains(visible), field + " 无法滚动到完整显示，位置=" + visible + " 视口=" + viewport.ClientRectangle);
                        }
                        using (Bitmap rendered = new Bitmap(settings.Width, settings.Height))
                        {
                            settings.DrawToBitmap(rendered, settings.ClientRectangle);
                            rendered.Save(Path.Combine(evidence, "settings-2-scrolled-" + (int)(scale * 100) + ".png"), ImageFormat.Png);
                        }
                    }
                    var session = new PrivacyReviewSession();
                    var analysis = new PrivacyAnalysis { HasLocalCoordinates = true };
                    analysis.Lines.Add(new ImageTextBlock { Text = "手机13812345678", Kind = "phone", Sensitive = true, X = .1f, Y = .1f, W = .2f, H = .1f });
                    session.Merge(analysis, new Rectangle(0, 0, 500, 300));
                    using (PrivacyReviewPanel panel = new PrivacyReviewPanel(session))
                    {
                        ShowOffscreen(panel);
                        panel.Scale(new SizeF(scale, scale));
                        panel.ClientSize = new Size((int)(300 * scale), Math.Min(750, (int)(496 * scale)));
                        panel.PerformLayout();
                        Application.DoEvents();
                        using (Bitmap rendered = new Bitmap(panel.Width, panel.Height))
                        {
                            panel.DrawToBitmap(rendered, new Rectangle(Point.Empty, panel.Size));
                            Require(HasRenderedContent(rendered), "审阅渲染缺少控件像素");
                            rendered.Save(Path.Combine(evidence, "privacy-review-" + (int)(scale * 100) + ".png"), ImageFormat.Png);
                        }
                        Panel content = (Panel)Get(panel, "_content");
                        Control done = null;
                        foreach (Control child in content.Controls) if (child.Text == "完成审阅") done = child;
                        Require(done != null, "找不到完成审阅按钮");
                        done.Focus();
                        content.ScrollControlIntoView(done);
                        panel.PerformLayout();
                        Application.DoEvents();
                        using (Bitmap rendered = new Bitmap(panel.Width, panel.Height))
                        {
                            panel.DrawToBitmap(rendered, new Rectangle(Point.Empty, panel.Size));
                            rendered.Save(Path.Combine(evidence, "privacy-review-scrolled-" + (int)(scale * 100) + ".png"), ImageFormat.Png);
                        }
                        Require(content.ClientRectangle.Contains(done.Bounds), "短屏无法滚动到完成审阅按钮，scale=" + scale + "，button=" + done.Bounds + "，viewport=" + content.ClientRectangle + "，scroll=" + content.AutoScrollPosition + "，min=" + content.AutoScrollMinSize + "，display=" + content.DisplayRectangle + "，max=" + content.VerticalScroll.Maximum);
                    }
                }
            });
            Console.WriteLine("工作流 UI 审查：" + _passed + " PASS / " + _failed + " FAIL");
            return _failed == 0 ? 0 : 1;
        }

        private static OverlayForm NewOverlay()
        {
            Bitmap source = new Bitmap(600, 400);
            using (Graphics g = Graphics.FromImage(source)) g.Clear(Color.White);
            var overlay = new OverlayForm(source, new Rectangle(0, 0, 600, 400), new AppConfig(), new ConfigStore(), delegate { });
            Set(overlay, "_hasSelection", true);
            Set(overlay, "_selection", new Rectangle(20, 20, 500, 300));
            Set(overlay, "_closing", true);
            return overlay;
        }
        private static void ShowOffscreen(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            IntPtr handle = form.Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
            form.Show();
            Application.DoEvents();
            form.Location = new Point(-32000, -32000);
        }
        private static bool HasRenderedContent(Bitmap image)
        {
            int dark = 0;
            for (int y = 45; y < image.Height - 10; y += 3) for (int x = 10; x < image.Width - 10; x += 3)
            {
                Color pixel = image.GetPixel(x, y);
                if (pixel.R < 180 && pixel.G < 180 && pixel.B < 180) dark++;
            }
            return dark > 50;
        }
        private static object Get(object target, string name) { return target.GetType().GetField(name, Flags).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
        private static object Call(object target, string name, params object[] args) { return target.GetType().GetMethod(name, Flags).Invoke(target, args); }
        private static void Check(string name, Action action) { try { action(); _passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); } }
        private static void Equal(object expected, object actual) { Require(object.Equals(expected, actual), "期望 " + expected + "，实际 " + actual); }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Compare(Bitmap expected, Bitmap actual)
        {
            Equal(expected.Size, actual.Size);
            for (int y = 0; y < expected.Height; y++) for (int x = 0; x < expected.Width; x++) Equal(expected.GetPixel(x, y).ToArgb(), actual.GetPixel(x, y).ToArgb());
        }
    }
}
