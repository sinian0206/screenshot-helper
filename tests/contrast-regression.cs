using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>Checks colors painted by real UI controls without capture, network, or settings writes.</summary>
    internal static class ContrastRegression
    {
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly List<string> Pairs = new List<string>();
        private static string _evidence;
        private static int _failed, _pairs, _scenes;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string report = args.Length > 0 ? args[0] : "acceptance";
            _evidence = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "evidence-" + report);
            Directory.CreateDirectory(_evidence);
            Pairs.Add("scene\tforeground\tbackground\tcontrast\tresult");
            Scene("settings", TestSettings);
            Scene("button-states", TestButtonStates);
            Scene("hotkey-states", TestHotkeys);
            Scene("result-panel", TestAiPanel);
            Scene("recorder-bar", TestRecorder);
            Scene("recording-ready", TestRecordingReady);
            Scene("longshot-preview", TestLongShot);
            Scene("privacy-review", TestPrivacy);
            Scene("translation-editor", TestTranslation);
            Scene("text-input-hint", TestTextInput);
            Scene("selection-badge-backgrounds", TestSelectionBadges);
            Scene("annotation-color-preservation", TestAnnotationColors);
            Scene("dynamic-translation-colors", TestDynamicText);
            File.WriteAllLines(Path.Combine(_evidence, "pairs.tsv"), Pairs);
            Console.WriteLine("文字对比度回归：" + _scenes + " 个场景 / " + _pairs + " 个实际文字色对 / " + _failed + " FAIL");
            return _failed == 0 ? 0 : 1;
        }

        private static void TestSettings()
        {
            using (SettingsForm form = new SettingsForm(new AppConfig(), new ConfigStore(), true))
            {
                ShowOffscreen(form);
                for (int page = 0; page < 3; page++)
                {
                    Call(form, "ShowPage", page);
                    Application.DoEvents();
                    Save(form, "settings-" + page);
                    AuditControls(form, "settings-" + page);
                    SettingsTabs tabs = (SettingsTabs)Get(form, "_tabs");
                    using (Bitmap rendered = Render(tabs))
                    {
                        int segment = (tabs.Width - 8) / 3;
                        for (int i = 0; i < 3; i++)
                            Painted("settings-" + page + "/tab-" + i, rendered,
                                new Rectangle(4 + segment * i + 18, 9, segment - 36, tabs.Height - 18));
                    }
                    Set(tabs, "_hoverIndex", (page + 1) % 3);
                    using (Bitmap rendered = Render(tabs))
                    {
                        int segment = (tabs.Width - 8) / 3, hovered = (page + 1) % 3;
                        Painted("settings-" + page + "/hover-tab", rendered,
                            new Rectangle(4 + segment * hovered + 18, 9, segment - 36, tabs.Height - 18));
                    }
                    Set(tabs, "_hoverIndex", -1);
                }
                // Empty fields exercise visible placeholder text, rather than unused theme constants.
                Call(form, "ShowPage", 1);
                foreach (string name in new[] { "_visionField", "_translatePromptField" })
                {
                    SettingsField field = (SettingsField)Get(form, name);
                    if (string.IsNullOrEmpty(field.Placeholder)) continue;
                    field.Inner.Text = "";
                    field.Inner.Visible = false;
                    using (Bitmap image = Render(field))
                        Painted("settings/placeholder/" + name, image, new Rectangle(14, 4, field.Width - 28, field.Height - 8));
                }
            }
        }

        private static void TestHotkeys()
        {
            using (SettingsForm form = new SettingsForm(new AppConfig(), new ConfigStore(), false))
            {
                ShowOffscreen(form);
                Call(form, "ShowPage", 2);
                HotkeyBox box = (HotkeyBox)Get(form, "_ocrHotkeyBox");
                foreach (string state in new[] { "empty", "hover", "recording", "rejected", "disabled" })
                {
                    box.HotkeyText = "";
                    box.Enabled = state != "disabled";
                    Set(box, "_hover", state == "hover");
                    Set(box, "_recording", state == "recording" || state == "rejected");
                    Set(box, "_recordingHint", state == "rejected" ? "请同时按住修饰键" : "");
                    using (Bitmap image = Render(box))
                    {
                        image.Save(Path.Combine(_evidence, "hotkey-" + state + ".png"));
                        Painted("hotkey/" + state, image, new Rectangle(9, 4, box.Width - 18, box.Height - 8));
                    }
                }
            }
        }

        private static void TestButtonStates()
        {
            using (SettingsForm form = new SettingsForm(new AppConfig(), new ConfigStore(), false))
            {
                ShowOffscreen(form);
                foreach (string name in new[] { "_saveBtn", "_cancelBtn", "_resetBtn" })
                {
                    SettingsButton button = (SettingsButton)Get(form, name);
                    foreach (string state in new[] { "normal", "hover", "pressed", "disabled" })
                    {
                        button.Enabled = state != "disabled";
                        Set(button, "_hover", state == "hover" || state == "pressed");
                        Set(button, "_pressed", state == "pressed");
                        using (Bitmap image = Render(button))
                        {
                            image.Save(Path.Combine(_evidence, "button-" + name + "-" + state + ".png"));
                            Painted(name + "/" + state, image, Rectangle.Inflate(button.ClientRectangle, -6, -6));
                        }
                    }
                }
            }
        }

        private static void TestAiPanel()
        {
            using (Form host = Host())
            using (AiResultPanel panel = new AiResultPanel(new Bitmap(10, 10), AiPanelMode.Translate, new AppConfig(), new DeepSeekClient(new AppConfig()), 1f))
            {
                host.ClientSize = panel.Size;
                host.Controls.Add(panel);
                panel.ShowCompletedTranslation("原文内容：本地识别示例", "Translation: local text example");
                ShowOffscreen(host);
                Save(panel, "result-panel");
                AuditControls(panel, "result-panel");
                foreach (string name in new[] { "_copyOriginalBtn", "_copyTranslationBtn", "_copyAllBtn", "_closeBtn" })
                {
                    Button button = (Button)Get(panel, name);
                    if (button == null || !button.Visible) continue;
                    Ratio("result-panel/hover/" + name, button.ForeColor, button.FlatAppearance.MouseOverBackColor);
                    Ratio("result-panel/pressed/" + name, button.ForeColor, button.FlatAppearance.MouseDownBackColor);
                }
            }
        }

        private static void TestRecorder()
        {
            using (RecorderBar bar = new RecorderBar(1f, 15, @"D:\截图\录制示例.mp4", delegate { }, delegate { }, delegate { return true; }))
            {
                ShowOffscreen(bar);
                bar.SetState(125, 1875, 4 * 1024 * 1024);
                foreach (string state in new[] { "normal", "hover", "paused", "stopping" })
                {
                    Set(bar, "_hoverStop", state == "hover");
                    Set(bar, "_hoverPause", state == "hover");
                    if (state == "paused") bar.SetPaused(true);
                    if (state == "stopping") bar.SetStopping();
                    using (Bitmap image = Render(bar))
                    {
                        image.Save(Path.Combine(_evidence, "recorder-" + state + ".png"));
                        Painted("recorder/" + state + "/time", image, (Rectangle)Get(bar, "_timeRect"));
                        Painted("recorder/" + state + "/stop", image, Rectangle.Inflate((Rectangle)Get(bar, "_stopRect"), -7, -6));
                        Painted("recorder/" + state + "/pause", image, Rectangle.Inflate((Rectangle)Get(bar, "_pauseRect"), -6, -6));
                    }
                }
            }
        }

        private static void TestRecordingReady()
        {
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            using (RecordReadyForm form = new RecordReadyForm(new Rectangle(100, 100, 640, 480), new AppConfig(), scale))
            {
                ShowOffscreen(form);
                string name = "recording-ready-" + scale.ToString("0.0", CultureInfo.InvariantCulture);
                Save(form, name);
                AuditControls(form, name);
                foreach (string field in new[] { "_audio", "_format" })
                {
                    ComboBox choice = (ComboBox)Get(form, field);
                    Ratio(name + "/" + field, choice.ForeColor, choice.BackColor);
                    using (Bitmap image = Render(choice))
                        Require(HasTextPixels(image), name + "/" + field + " 未渲染文字");
                }
                foreach (string field in new[] { "_start", "_cancel" })
                {
                    Button button = (Button)Get(form, field);
                    Ratio(name + "/hover/" + field, button.ForeColor, button.FlatAppearance.MouseOverBackColor);
                    Ratio(name + "/pressed/" + field, button.ForeColor, button.FlatAppearance.MouseDownBackColor);
                }
            }
        }

        private static void TestLongShot()
        {
            using (Bitmap fixture = Fixture())
            using (LongShotBar bar = new LongShotBar(1f, delegate { }, delegate { }, delegate { }, delegate { }, delegate { }, () => (Bitmap)fixture.Clone()))
            {
                ShowOffscreen(bar);
                bar.SetStatus(new Size(420, 1400), fixture, "已暂停捕获，可检查接缝后继续。", true,
                    new[] { new Size(420, 700), new Size(420, 700) },
                    new[] { new Rectangle(0, 0, 420, 700), new Rectangle(0, 700, 420, 700) }, false, true);
                foreach (string state in new[] { "normal", "hover", "selected" })
                {
                    Set(bar, "_hover", state == "hover" ? 3 : -1);
                    Set(bar, "_selectedSegment", state == "selected" ? 0 : -1);
                    using (Bitmap image = Render(bar))
                    {
                        image.Save(Path.Combine(_evidence, "longshot-" + state + ".png"));
                        Painted("longshot/" + state + "/progress", image, new Rectangle(16, 43, 400, 22));
                        Painted("longshot/" + state + "/status", image, new Rectangle(16, 73, 400, 34));
                        Painted("longshot/" + state + "/hint", image, new Rectangle(16, bar.Height - 119, 428, 23));
                        Rectangle[] buttons = (Rectangle[])Get(bar, "_buttons");
                        for (int i = 0; i < buttons.Length; i++)
                            Painted("longshot/" + state + "/button-" + i, image, Rectangle.Inflate(buttons[i], -5, -5));
                        if (state == "selected")
                        {
                            Rectangle list = (Rectangle)Get(bar, "_listRect");
                            Painted("longshot/selected-row", image, new Rectangle(list.X + 2, list.Y + 29, list.Width - 4, 23));
                        }
                    }
                }
            }
        }

        private static void TestPrivacy()
        {
            PrivacyReviewSession session = new PrivacyReviewSession();
            PrivacyAnalysis analysis = new PrivacyAnalysis { HasLocalCoordinates = true };
            analysis.Lines.Add(new ImageTextBlock { Text = "电话示例", Kind = "phone", Sensitive = true, X = .1f, Y = .1f, W = .2f, H = .1f });
            session.Merge(analysis, new Rectangle(0, 0, 500, 300));
            using (PrivacyReviewPanel panel = new PrivacyReviewPanel(session))
            {
                ShowOffscreen(panel);
                Save(panel, "privacy-review");
                AuditControls(panel, "privacy-review");
                CheckedListBox regions = (CheckedListBox)Get(panel, "_regions");
                regions.SelectedIndex = 0;
                regions.Focus();
                Save(panel, "privacy-review-selected");
                using (Bitmap selected = Render(regions))
                    Painted("privacy-review/selected-row", selected, new Rectangle(22, 4, regions.Width - 27, regions.ItemHeight - 2), true);
            }
        }

        private static void TestTextInput()
        {
            using (OverlayForm overlay = new OverlayForm(Fixture(), new Rectangle(0, 0, 640, 480), new AppConfig(), new ConfigStore(), delegate { }))
            {
                Set(overlay, "_closing", true);
                Set(overlay, "_hasSelection", true);
                Set(overlay, "_selection", new Rectangle(20, 20, 500, 300));
                ShowOffscreen(overlay);
                Call(overlay, "BeginTextEdit", new Point(50, 80));
                Panel bar = (Panel)Get(overlay, "_textBar");
                TextBox input = (TextBox)Get(overlay, "_textInput");
                input.Text = "标注文字示例";
                Save(bar, "text-input");
                Label hint = (Label)Get(overlay, "_textHint");
                Ratio("text-input/fixed-hint", hint.ForeColor, EffectiveBackground(hint));
                using (Bitmap image = Render(hint)) Require(HasTextPixels(image), "文字输入提示未渲染");
                Size hintText = TextRenderer.MeasureText(hint.Text, hint.Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                Require(hint.ClientSize.Width >= hintText.Width, "文字输入提示宽度不足，完整单行需要 " + hintText.Width + "px，实际 " + hint.ClientSize.Width + "px");
                // Annotation colors are user content and deliberately outside the fixed UI palette audit.
            }
        }

        private static void TestSelectionBadges()
        {
            using (OverlayForm overlay = new OverlayForm(Fixture(), new Rectangle(0, 0, 640, 480), new AppConfig(), new ConfigStore(), delegate { }))
            {
                Set(overlay, "_scale", 1f);
                Rectangle selection = new Rectangle(50, 110, 320, 200);
                Bitmap reference = null;
                try
                {
                    foreach (string background in new[] { "white", "gray", "black", "texture" })
                    {
                        using (Bitmap canvas = new Bitmap(640, 480))
                        {
                            using (Graphics graphics = Graphics.FromImage(canvas))
                            {
                                graphics.Clear(background == "white" ? Color.White : background == "gray" ? Color.Gray : Color.Black);
                                if (background == "texture")
                                    for (int y = 0; y < canvas.Height; y += 4) for (int x = 0; x < canvas.Width; x += 4)
                                        graphics.FillRectangle((x + y) % 8 == 0 ? Brushes.Yellow : Brushes.DarkBlue, x, y, 4, 4);
                                Call(overlay, "DrawSizeLabel", graphics, selection, false);
                            }
                            Rectangle badge = ColorBounds(canvas, UiTheme.InfoBadgeBackground);
                            Require(badge.Width >= 50 && badge.Height >= 16, "尺寸标签缺少实色背景：" + background);
                            Painted("selection/" + background, canvas, badge);
                            Rectangle interior = Rectangle.Inflate(badge, -2, -2);
                            Require(interior.Width > 20 && interior.Height > 8, "尺寸标签有效背景区域不足");
                            using (Bitmap crop = canvas.Clone(interior, PixelFormat.Format32bppArgb))
                            {
                                if (reference == null) reference = (Bitmap)crop.Clone();
                                else Compare(reference, crop, "尺寸标签受到下层" + background + "背景影响");
                            }
                            using (Bitmap evidence = canvas.Clone(new Rectangle(30, 55, 380, 85), PixelFormat.Format32bppArgb))
                                evidence.Save(Path.Combine(_evidence, "selection-badge-on-" + background + ".png"));
                        }
                    }
                }
                finally { if (reference != null) reference.Dispose(); }
            }
        }

        private static void TestAnnotationColors()
        {
            foreach (Color chosen in new[] { Color.White, Color.Yellow })
            {
                using (OverlayForm overlay = new OverlayForm(Fixture(), new Rectangle(0, 0, 640, 480), new AppConfig(), new ConfigStore(), delegate { }))
                {
                    Set(overlay, "_closing", true);
                    Set(overlay, "_hasSelection", true);
                    Set(overlay, "_selection", new Rectangle(20, 20, 500, 300));
                    Set(overlay, "_color", chosen);
                    ShowOffscreen(overlay);
                    Call(overlay, "BeginTextEdit", new Point(50, 80));
                    TextBox input = (TextBox)Get(overlay, "_textInput");
                    input.Text = "保留所选颜色";
                    Ratio("text-input/chosen-" + Hex(chosen), input.ForeColor, input.BackColor);
                    Require(input.ForeColor.ToArgb() != chosen.ToArgb(), "输入框使用了不可读的标注颜色");
                    Save((Panel)Get(overlay, "_textBar"), "text-input-chosen-" + Hex(chosen).Substring(1));
                    Call(overlay, "CommitText");
                    List<Annotation> annotations = (List<Annotation>)Get(overlay, "_annotations");
                    Require(annotations.Count == 1 && annotations[0] is TextAnnotation, "未提交文字标注");
                    TextAnnotation annotation = (TextAnnotation)annotations[0];
                    Require(annotation.Color.ToArgb() == chosen.ToArgb(), "提交后改变了所选的标注颜色");
                    Require(annotation.Text == "保留所选颜色", "提交后文字不一致");
                }
            }
        }

        private static Rectangle ColorBounds(Bitmap image, Color color)
        {
            int left = image.Width, top = image.Height, right = -1, bottom = -1;
            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                if (image.GetPixel(x, y).ToArgb() == color.ToArgb())
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
            return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }

        private static void Compare(Bitmap expected, Bitmap actual, string message)
        {
            Require(expected.Size == actual.Size, message + "：尺寸改变");
            for (int y = 0; y < expected.Height; y++) for (int x = 0; x < expected.Width; x++)
                Require(expected.GetPixel(x, y).ToArgb() == actual.GetPixel(x, y).ToArgb(), message + "：像素不一致");
        }

        private static void TestTranslation()
        {
            using (OverlayForm overlay = new OverlayForm(Fixture(), new Rectangle(0, 0, 640, 480), new AppConfig(), new ConfigStore(), delegate { }))
            {
                Set(overlay, "_closing", true);
                ShowOffscreen(overlay);
                TranslationOverlayAnnotation annotation = new TranslationOverlayAnnotation();
                annotation.Blocks.Add(new TranslationBlock { Rect = new RectangleF(50, 80, 350, 40), Original = "原文示例", Translation = "Translated example" });
                Call(overlay, "BeginTranslationEdit", annotation, 0);
                Panel input = (Panel)Get(overlay, "_translationBar");
                Require(input != null && input.Controls.Count >= 4, "译文输入条未创建真实子控件");
                Save(input, "translation-input");
                AuditControls(input, "translation-input");
            }
        }

        private static void TestDynamicText()
        {
            MethodInfo method = typeof(OverlayForm).GetMethod("ContrastColor", BindingFlags.Static | BindingFlags.NonPublic);
            Require(method != null, "缺少原位译文前景色选择方法");
            foreach (Color background in new[] { Color.White, Color.Black, Color.Gray, Color.FromArgb(117, 117, 117), Color.FromArgb(140, 178, 142), Color.FromArgb(0, 160, 90) })
            {
                Color foreground = (Color)method.Invoke(null, new object[] { background });
                Ratio("translation/computed/" + Hex(background), foreground, background);
            }
        }

        private static void AuditControls(Control root, string path)
        {
            foreach (Control control in root.Controls)
            {
                if (!control.Visible) continue;
                string name = path + "/" + control.GetType().Name + "/" + Short(control.Text);
                bool basic = control is Label || control is TextBox || control is Button || control is CheckedListBox || control is NumericUpDown;
                if (basic && (control.Text.Length > 0 || control is CheckedListBox))
                {
                    Color back = EffectiveBackground(control);
                    Ratio(name, control.ForeColor, back);
                    using (Bitmap rendered = Render(control)) Require(HasTextPixels(rendered), name + " 未渲染实际文字像素");
                }
                SettingsButton custom = control as SettingsButton;
                if (custom != null && custom.Text.Length > 0)
                    using (Bitmap image = Render(custom)) Painted(name, image, Rectangle.Inflate(custom.ClientRectangle, -6, -6));
                AuditControls(control, name);
            }
        }

        // The modal fill and strongest repeated glyph pixels are read from the actual painted ROI.
        // Crops omit control borders, icons, screenshot content, and checkbox glyphs.
        private static void Painted(string name, Bitmap bitmap, Rectangle rect, bool systemSelection = false)
        {
            rect.Intersect(new Rectangle(Point.Empty, bitmap.Size));
            Require(rect.Width > 0 && rect.Height > 0, name + " 文字区域为空");
            Dictionary<int, int> counts = new Dictionary<int, int>();
            for (int y = rect.Top; y < rect.Bottom; y++) for (int x = rect.Left; x < rect.Right; x++)
            {
                int argb = bitmap.GetPixel(x, y).ToArgb(), count;
                counts.TryGetValue(argb, out count); counts[argb] = count + 1;
            }
            int modal = 0, largest = 0;
            foreach (KeyValuePair<int, int> pair in counts) if (pair.Value > largest) { largest = pair.Value; modal = pair.Key; }
            Color back = Color.FromArgb(modal), foreground = back;
            double best = 1;
            foreach (KeyValuePair<int, int> pair in counts)
            {
                if (pair.Value < 3) continue;
                Color candidate = Color.FromArgb(pair.Key);
                double ratio = Contrast(candidate, back);
                if (ratio > best) { best = ratio; foreground = candidate; }
            }
            Require(best > 1.05, name + " 未找到实际文字像素");
            if (systemSelection && foreground.ToArgb() == SystemColors.HighlightText.ToArgb() && back.ToArgb() == SystemColors.Highlight.ToArgb())
            {
                Pairs.Add(name + "\t" + Hex(foreground) + "\t" + Hex(back) + "\t" + best.ToString("0.000000", CultureInfo.InvariantCulture) + "\tINFO_OS_PALETTE");
                Console.WriteLine("INFO " + name + " 原生 Windows 选中态 " + Hex(foreground) + "/" + Hex(back) + " = " + best.ToString("0.000000", CultureInfo.InvariantCulture) + ":1；应用自绘阈值仍为4.5:1");
                return;
            }
            Ratio(name, foreground, back);
        }

        private static void Ratio(string name, Color foreground, Color background)
        {
            double value = Contrast(foreground, background);
            bool pass = value >= 4.5;
            _pairs++;
            Pairs.Add(name + "\t" + Hex(foreground) + "\t" + Hex(background) + "\t" + value.ToString("0.000", CultureInfo.InvariantCulture) + "\t" + (pass ? "PASS" : "FAIL"));
            if (!pass)
            {
                _failed++;
                Console.WriteLine("FAIL " + name + " " + Hex(foreground) + "/" + Hex(background) + " = " + value.ToString("0.00", CultureInfo.InvariantCulture) + ":1 < 4.5:1");
            }
        }

        private static double Contrast(Color a, Color b)
        {
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
        }
        private static double Luminance(Color color) { return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B); }
        private static double Linear(byte value) { double s = value / 255.0; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        private static Color EffectiveBackground(Control control)
        {
            for (Control node = control; node != null; node = node.Parent) if (node.BackColor.A == 255) return node.BackColor;
            return Color.White;
        }
        private static string Hex(Color color) { return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2"); }
        private static string Short(string text) { return text.Replace("\r", "").Replace("\n", " ").Substring(0, Math.Min(25, text.Replace("\r", "").Replace("\n", " ").Length)); }
        private static void Scene(string name, Action test)
        {
            int before = _failed;
            try { test(); _scenes++; Console.WriteLine((before == _failed ? "PASS " : "CHECK ") + name); }
            catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
        }
        private static object Get(object target, string name) { return target.GetType().GetField(name, Flags).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
        private static object Call(object target, string name, params object[] args) { return target.GetType().GetMethod(name, Flags).Invoke(target, args); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static Form Host() { return new Form { AutoScaleMode = AutoScaleMode.None, FormBorderStyle = FormBorderStyle.None, BackColor = Color.White }; }
        private static void ShowOffscreen(Form form)
        {
            OverlayForm overlay = form as OverlayForm;
            if (overlay != null) overlay.SuppressActivation = true;
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
            form.ShowInTaskbar = false; form.Opacity = 0;
            IntPtr handle = form.Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
            form.Show(); Application.DoEvents(); form.Location = new Point(-32000, -32000);
        }
        private static Bitmap Render(Control control)
        {
            control.CreateControl(); control.PerformLayout();
            Bitmap image = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
            using (Graphics g = Graphics.FromImage(image)) g.Clear(EffectiveBackground(control));
            control.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            return image;
        }
        private static void Save(Control control, string name)
        {
            using (Bitmap image = Render(control)) { Require(HasTextPixels(image), name + " 画面为空"); image.Save(Path.Combine(_evidence, name + ".png"), ImageFormat.Png); }
        }
        private static bool HasTextPixels(Bitmap image)
        {
            Color background = image.GetPixel(Math.Max(0, image.Width / 2), Math.Max(0, image.Height - 2));
            int different = 0;
            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
            { Color p = image.GetPixel(x, y); if (Contrast(p, background) > 1.1 && ++different > 5) return true; }
            return false;
        }
        private static Bitmap Fixture()
        {
            Bitmap fixture = new Bitmap(640, 480);
            using (Graphics g = Graphics.FromImage(fixture)) { g.Clear(Color.White); using (Font font = UiTheme.Font(16)) g.DrawString("屏幕示例内容", font, Brushes.Black, 20, 20); }
            return fixture;
        }
    }
}
