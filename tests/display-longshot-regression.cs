using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using XiaoJie;

internal static class DisplayLongShotRegression
{
    private static int _passed;
    private static int _failed;
    [STAThread]
    private static int Main()
    {
        Check("负坐标跨屏选区往返保持物理像素", delegate
        {
            Rectangle desktop = new Rectangle(-2560, -240, 6400, 2400);
            Rectangle screen = new Rectangle(-120, 30, 800, 1000);
            Rectangle image = DisplayGeometry.ScreenToImage(screen, desktop);
            Equal(new Rectangle(2440, 270, 800, 1000), image);
            Equal(screen, DisplayGeometry.ImageToScreen(image, desktop));
        });
        Check("150%/200% UI 缩放不缩放图像坐标", delegate
        {
            Equal(36, DisplayGeometry.ScaleUi(24, 1.5f));
            Equal(48, DisplayGeometry.ScaleUi(24, 2f));
            Equal(new Rectangle(500, 50, 400, 300), DisplayGeometry.ScreenToImage(
                new Rectangle(-1420, 50, 400, 300), new Rectangle(-1920, 0, 5760, 2160)));
        });
        Check("浮窗可放到负坐标副屏且不侵入选区", delegate
        {
            Rectangle selection = new Rectangle(0, 0, 1920, 1080);
            Rectangle[] work = { new Rectangle(0, 0, 1920, 1040), new Rectangle(-1920, 0, 1920, 1040) };
            Rectangle placed = DisplayGeometry.PlaceFloating(selection, new Size(600, 800), work, 10, true);
            Require(!placed.IsEmpty && work[1].Contains(placed), "未放入副屏工作区");
            Require(!placed.IntersectsWith(selection), "预览侵入选区");
        });
        Check("无外部空间时明确返回空矩形", delegate
        {
            Rectangle selection = new Rectangle(0, 0, 1920, 1080);
            Equal(Rectangle.Empty, DisplayGeometry.PlaceFloating(selection, new Size(600, 800),
                new[] { selection }, 10, true));
        });
        Check("浮窗超出小屏时限制可见边界", delegate
        {
            Rectangle work = new Rectangle(-1280, -200, 1280, 900);
            Rectangle placed = DisplayGeometry.PlaceFloating(new Rectangle(-1000, 0, 200, 200),
                new Size(1800, 1200), new[] { work }, 10, false);
            Require(work.Contains(placed), "窗口超出工作区");
        });
        Check("撤回末段及恢复均保持逐像素一致", delegate
        {
            using (LongShotJournal journal = new LongShotJournal(120))
            {
                for (int i = 0; i < 4; i++) using (Bitmap frame = Frame(i * 60)) Require(journal.AddFrame(frame), "帧被拒绝");
                using (Bitmap before = journal.Stitcher.TakeResult())
                {
                    Require(journal.Undo(), "无法撤回");
                    Equal(300, journal.Stitcher.ResultSize.Height);
                    Require(journal.Redo(), "无法恢复");
                    using (Bitmap after = journal.Stitcher.TakeResult()) Compare(before, after);
                }
            }
        });
        Check("撤回后重试替换分支并保留首帧", delegate
        {
            using (LongShotJournal journal = new LongShotJournal(120))
            {
                using (Bitmap frame = Frame(0)) Require(journal.AddFrame(frame), "首帧被拒绝");
                Require(!journal.Undo(), "首帧不应被撤回");
                using (Bitmap frame = Frame(60)) Require(journal.AddFrame(frame), "第二帧被拒绝");
                Require(journal.Undo(), "撤回失败");
                using (Bitmap frame = Frame(30)) Require(journal.AddFrame(frame), "重试帧被拒绝");
                Require(!journal.CanRedo, "旧分支未清理");
                Equal(210, journal.Stitcher.ResultSize.Height);
            }
        });
        Check("向上滚动的捕获段接缝及撤回恢复位置正确", delegate
        {
            using (LongShotJournal journal = new LongShotJournal(120))
            {
                for (int i = 3; i >= 0; i--) using (Bitmap frame = Frame(i * 60)) Require(journal.AddFrame(frame), "前插帧被拒绝");
                Rectangle[] parts = journal.SegmentBounds();
                Equal(new Rectangle(0, 180, 120, 180), parts[0]);
                Equal(new Rectangle(0, 0, 120, 60), parts[3]);
                using (Bitmap expected = journal.Stitcher.TakeResult())
                {
                    Require(journal.Undo() && journal.Redo(), "无法恢复前插段");
                    using (Bitmap restored = journal.Stitcher.TakeResult()) Compare(expected, restored);
                }
            }
        });
        Check("回退历史内存上限不会修改已有结果", delegate
        {
            using (LongShotJournal journal = new LongShotJournal(120, 1))
            using (Bitmap frame = Frame(0))
            {
                Require(!journal.AddFrame(frame), "超预算帧不应加入");
                Equal(0, journal.Count);
                Require(journal.HistoryFull, "未报告内存上限");
            }
        });
        Check("显式暂停丢弃待抓信号且滚动不能自动恢复", delegate
        {
            int captures = 0;
            using (LongShotSession session = new LongShotSession(new Rectangle(0, 0, 120, 180), delegate { captures++; return Frame(60); }))
            {
                LongShotStitcher stitcher = new LongShotStitcher(120);
                using (Bitmap frame = Frame(0)) stitcher.AddFrame(frame);
                Set(session, "_stitcher", stitcher);
                Call(session, "SignalScroll");
                Call(session, "ToggleManualPause");
                Call(session, "SignalScroll");
                Set(session, "_lastCaptureTick", unchecked(Environment.TickCount - 500));
                Set(session, "_lastScrollSignalTick", unchecked(Environment.TickCount - 160));
                Call(session, "OnTimerTick", null, EventArgs.Empty);
                Call(session, "FlushPendingFrame");
                Equal(0, captures);
                Call(session, "ToggleManualPause");
                Call(session, "SignalScroll");
                Set(session, "_lastScrollSignalTick", unchecked(Environment.TickCount - 160));
                Call(session, "OnTimerTick", null, EventArgs.Empty);
                Equal(1, captures);
            }
        });
        Check("错位帧拒绝后暂停且继续滚动不增加结果", delegate
        {
            int captures = 0;
            using (LongShotSession session = new LongShotSession(new Rectangle(0, 0, 120, 180), delegate
            {
                captures++;
                Bitmap foreign = new Bitmap(120, 180);
                using (Graphics g = Graphics.FromImage(foreign)) g.Clear(Color.White);
                return foreign;
            }))
            {
                LongShotStitcher stitcher = new LongShotStitcher(120);
                using (Bitmap frame = Frame(0)) stitcher.AddFrame(frame);
                Set(session, "_stitcher", stitcher);
                Call(session, "SignalScroll");
                Set(session, "_lastCaptureTick", unchecked(Environment.TickCount - 500));
                Set(session, "_lastScrollSignalTick", unchecked(Environment.TickCount - 160));
                Call(session, "OnTimerTick", null, EventArgs.Empty);
                Equal(1, captures);
                Call(session, "SignalScroll");
                Call(session, "OnTimerTick", null, EventArgs.Empty);
                Equal(1, captures);
                Equal(180, stitcher.ResultSize.Height);
            }
        });
        Check("抓帧期间点击暂停不提交迟到画面", delegate
        {
            LongShotSession session = null;
            using (session = new LongShotSession(new Rectangle(0, 0, 120, 180), delegate
            {
                Call(session, "ToggleManualPause");
                return Frame(60);
            }))
            {
                LongShotStitcher stitcher = new LongShotStitcher(120);
                using (Bitmap frame = Frame(0)) stitcher.AddFrame(frame);
                Set(session, "_stitcher", stitcher);
                Call(session, "SignalScroll");
                Set(session, "_lastCaptureTick", unchecked(Environment.TickCount - 500));
                Set(session, "_lastScrollSignalTick", unchecked(Environment.TickCount - 160));
                Call(session, "OnTimerTick", null, EventArgs.Empty);
                Equal(180, stitcher.ResultSize.Height);
            }
        });
        Check("钉图在150%/200%切换时保持图像像素与关闭控件可见", delegate
        {
            using (PinForm pin = new PinForm(Frame(0), new Rectangle(200, 200, 120, 180), true))
            {
                Rectangle original = (Rectangle)Call(pin, "ImageBounds");
                Call(pin, "UpdateMonitorScale", 1.5f);
                Rectangle scaled = (Rectangle)Call(pin, "ImageBounds");
                Equal(original.Size, scaled.Size);
                Rectangle close = (Rectangle)Call(pin, "CloseButtonRect");
                Require(pin.ClientRectangle.Contains(close), "150% 关闭控件超出窗口");
                Equal(33, close.Width);
                Call(pin, "UpdateMonitorScale", 2f);
                Equal(original.Size, ((Rectangle)Call(pin, "ImageBounds")).Size);
                close = (Rectangle)Call(pin, "CloseButtonRect");
                Require(pin.ClientRectangle.Contains(close), "200% 关闭控件超出窗口");
                Equal(44, close.Width);
            }
        });
        Check("预览在100%/150%/200%控件布局与撤回按钮可点击", delegate
        {
            string evidence = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "evidence");
            Directory.CreateDirectory(evidence);
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                int undo = 0;
                using (LongShotJournal journal = new LongShotJournal(120))
                {
                    for (int i = 0; i < 4; i++) using (Bitmap frame = Frame(i * 60)) journal.AddFrame(frame);
                    using (LongShotBar bar = new LongShotBar(scale, delegate { }, delegate { }, delegate { },
                        delegate { undo++; }, delegate { }, delegate { return journal.Stitcher.TakeResult(); }))
                    using (Bitmap preview = journal.Stitcher.CreatePreview(1000))
                    using (Bitmap render = new Bitmap(bar.Width, bar.Height))
                    {
                        bar.SetStatus(journal.Stitcher.ResultSize, preview, "已暂停；点击段检查接缝", true,
                            journal.SegmentSizes(), journal.SegmentBounds(), true, false);
                        bar.DrawToBitmap(render, bar.ClientRectangle);
                        Rectangle[] buttons = (Rectangle[])bar.GetType().GetField("_buttons", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bar);
                        for (int i = 0; i < buttons.Length; i++)
                        {
                            Require(bar.ClientRectangle.Contains(buttons[i]), "控件超出窗口");
                            for (int j = i + 1; j < buttons.Length; j++) Require(!buttons[i].IntersectsWith(buttons[j]), "控件重叠");
                        }
                        MethodInfo click = bar.GetType().GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic);
                        click.Invoke(bar, new object[] { new MouseEventArgs(MouseButtons.Left, 1, buttons[1].Left + 2, buttons[1].Top + 2, 0) });
                        Equal(1, undo);
                        render.Save(Path.Combine(evidence, "longshot-preview-" + (int)(scale * 100) + ".png"), ImageFormat.Png);
                    }
                }
            }
        });
        Check("捕获排除支持时重叠预览不会进入截图且不改变可见性", delegate
        {
            using (Form target = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Color.FromArgb(31, 80, 125),
                Bounds = new Rectangle(80, 100, 400, 260), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true })
            using (Form preview = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Color.Magenta,
                Bounds = new Rectangle(100, 120, 180, 100), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true })
            {
                target.Show(); target.BringToFront(); target.Refresh(); Application.DoEvents();
                System.Threading.Thread.Sleep(150);
                using (Bitmap baseline = ImageUtil.Capture(new Rectangle(110, 130, 20, 20)))
                    Equal(target.BackColor.ToArgb(), baseline.GetPixel(10, 10).ToArgb());
                if (!CaptureExclusion.TryExclude(preview))
                {
                    Console.WriteLine("  当前桌面不支持捕获排除；只验证选区外放置分支");
                    return;
                }
                preview.Show(); Application.DoEvents();
                System.Threading.Thread.Sleep(150);
                int visibleChanges = 0;
                preview.VisibleChanged += delegate { visibleChanges++; };
                for (int i = 0; i < 3; i++)
                    using (Bitmap captured = ImageUtil.Capture(new Rectangle(110, 130, 20, 20)))
                        Equal(target.BackColor.ToArgb(), captured.GetPixel(10, 10).ToArgb());
                Equal(0, visibleChanges);
                Require(preview.Visible, "捕获导致预览隐藏");
            }
        });
        Check("真实桌面会话预览及撤回恢复完成后输出原始像素", delegate
        {
            using (Bitmap first = Frame(0))
            using (Bitmap next = Frame(60))
            using (Form target = new Form { FormBorderStyle = FormBorderStyle.None, TopMost = true,
                Bounds = new Rectangle(500, 200, 120, 180), StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false, BackgroundImage = first, BackgroundImageLayout = ImageLayout.None })
            using (System.Windows.Forms.Timer driver = new System.Windows.Forms.Timer { Interval = 200 })
            using (LongShotSession session = new LongShotSession(target.Bounds, ImageUtil.Capture))
            {
                target.Show(); target.BringToFront(); target.Refresh(); Application.DoEvents();
                int stage = 0;
                driver.Tick += delegate
                {
                    stage++;
                    if (stage == 1) { target.BackgroundImage = next; target.Refresh(); Call(session, "SignalScroll"); }
                    if (stage == 4) { Call(session, "ToggleManualPause"); Call(session, "ChangeLastSegment", false); }
                    if (stage == 5) Call(session, "ChangeLastSegment", true);
                    if (stage == 6) { Set(session, "_finishRequested", true); driver.Stop(); }
                };
                driver.Start();
                using (Bitmap result = session.Execute())
                {
                    Equal(new Size(120, 240), result.Size);
                    for (int y = 0; y < 180; y++) for (int x = 0; x < 120; x++) Equal(first.GetPixel(x, y).ToArgb(), result.GetPixel(x, y).ToArgb());
                    for (int y = 180; y < 240; y++) for (int x = 0; x < 120; x++) Equal(next.GetPixel(x, y - 60).ToArgb(), result.GetPixel(x, y).ToArgb());
                    string evidence = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "evidence");
                    Directory.CreateDirectory(evidence);
                    result.Save(Path.Combine(evidence, "longshot-desktop-result.png"), ImageFormat.Png);
                }
            }
        });
        Console.WriteLine("显示与长截图：" + _passed + " PASS / " + _failed + " FAIL");
        return _failed == 0 ? 0 : 1;
    }

    private static void Set(object value, string name, object field) { value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, field); }
    private static object Call(object value, string name, params object[] args) { return value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, args); }
    private static void Check(string name, Action test) { try { test(); _passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { Require(object.Equals(expected, actual), "期望 " + expected + "，实际 " + actual); }
    private static Bitmap Frame(int top)
    {
        Bitmap image = new Bitmap(120, 180, PixelFormat.Format32bppArgb);
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
        {
            uint hash = unchecked((uint)(((top + y) * 120 + x) * 1103515245 + 12345));
            hash ^= hash >> 13;
            image.SetPixel(x, y, Color.FromArgb(255, (int)(hash & 255), (int)((hash >> 8) & 255), (int)((hash >> 16) & 255)));
        }
        return image;
    }
    private static void Compare(Bitmap expected, Bitmap actual)
    {
        Equal(expected.Size, actual.Size);
        for (int y = 0; y < expected.Height; y++) for (int x = 0; x < expected.Width; x++)
            Equal(expected.GetPixel(x, y).ToArgb(), actual.GetPixel(x, y).ToArgb());
    }
}
