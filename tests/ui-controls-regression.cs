using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using XiaoJie;

public static class UiControlsRegression
{
    private static int _pass;
    private static int _fail;
    private static string _output;

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("ui-controls-evidence");
        Directory.CreateDirectory(_output);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        TestToolbarClicks();
        TestToolbarStates();
        TestSettingsControls();
        Console.WriteLine("SUMMARY " + _pass + " PASS / " + _fail + " FAIL");
        return _fail == 0 ? 0 : 1;
    }

    private static void TestToolbarClicks()
    {
        Console.WriteLine("[1] toolbar click matrix");
        using (EditorToolbar toolbar = new EditorToolbar(1f))
        {
            int toolEvents = 0;
            int contextEvents = 0;
            int actionEvents = 0;
            toolbar.ToolClicked += delegate { toolEvents++; };
            toolbar.ToolContextRequested += delegate { contextEvents++; };
            toolbar.ActionClicked += delegate { actionEvents++; };
            EditorTool[] tools =
            {
                EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Emoji, EditorTool.Arrow,
                EditorTool.Pen, EditorTool.Mosaic, EditorTool.Text, EditorTool.Number,
                EditorTool.Loupe, EditorTool.Select
            };
            foreach (EditorTool tool in tools)
            {
                toolEvents = 0;
                contextEvents = 0;
                Rectangle bounds = toolbar.ToolBounds(tool);
                Require(!bounds.IsEmpty, "tool bounds: " + tool);
                Click(toolbar, Center(bounds), MouseButtons.Left);
                Assert("tool click " + tool, toolEvents == 1 && toolbar.ActiveTool == tool);
                Click(toolbar, Center(bounds), MouseButtons.Left);
                Assert("tool toggle " + tool, toolEvents == 2 && toolbar.ActiveTool == EditorTool.None);
                Click(toolbar, Center(bounds), MouseButtons.Right);
                Assert("tool context " + tool, contextEvents == 1 && toolbar.ContextTool == tool);
            }

            ToolbarAction[] actions =
            {
                ToolbarAction.Translate, ToolbarAction.Ocr, ToolbarAction.LongShot,
                ToolbarAction.Undo, ToolbarAction.Redo, ToolbarAction.Save,
                ToolbarAction.Pin, ToolbarAction.Close, ToolbarAction.Confirm
            };
            toolbar.CanUndo = true;
            toolbar.CanRedo = true;
            foreach (ToolbarAction action in actions)
            {
                actionEvents = 0;
                Rectangle bounds = toolbar.ActionBounds(action);
                Require(!bounds.IsEmpty, "action bounds: " + action);
                Click(toolbar, Center(bounds), MouseButtons.Left);
                Assert("action click " + action, actionEvents == 1 && toolbar.LastAction == action);
            }

            using (EditorToolbar disabled = new EditorToolbar(1f))
            {
                int events = 0;
                disabled.ActionClicked += delegate { events++; };
                Click(disabled, Center(disabled.ActionBounds(ToolbarAction.Undo)), MouseButtons.Left);
                Click(disabled, Center(disabled.ActionBounds(ToolbarAction.Redo)), MouseButtons.Left);
                Assert("disabled undo/redo ignore clicks", events == 0);
            }
        }
    }

    private static void TestToolbarStates()
    {
        Console.WriteLine("[2] toolbar render states");
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            using (EditorToolbar toolbar = new EditorToolbar(scale))
            {
                toolbar.ActiveTool = EditorTool.Text;
                toolbar.CanUndo = false;
                toolbar.CanRedo = true;
                Rectangle bounds = new Rectangle(0, 0, toolbar.Width, toolbar.Height);
                Require(bounds.Width > 0 && bounds.Height > 0, "toolbar size");

                int lastRight = 0;
                int lastBottom = 0;
                foreach (Rectangle item in ItemBounds(toolbar))
                {
                    if (item.IsEmpty) continue;
                    Require(bounds.Contains(item), "item outside toolbar at " + scale);
                    Require(item.Right > lastRight || item.Top >= lastBottom, "item overlap at " + scale);
                    lastRight = item.Right;
                    lastBottom = item.Bottom;
                }

                using (Bitmap normal = Capture(toolbar))
                {
                    Save(normal, "toolbar-" + ScaleName(scale) + "-normal.png");
                    Assert("render normal " + scale, ColorCount(normal) >= 8);
                }

                SetField(toolbar, "_hoverIndex", 0);
                using (Bitmap hover = Capture(toolbar))
                    Save(hover, "toolbar-" + ScaleName(scale) + "-hover.png");

                Console.WriteLine("  scale=" + scale + " size=" + toolbar.Width + "x" + toolbar.Height +
                    " items=" + CountItems(toolbar));
            }
        }
    }

    private static void TestSettingsControls()
    {
        Console.WriteLine("[3] settings controls");
        using (SettingsTabs tabs = new SettingsTabs())
        {
            tabs.Size = new Size(440, 40);
            tabs.SetItems("本地识别", "接口", "截图");
            int events = 0;
            tabs.SelectedIndexChanged += delegate { events++; };
            Click(tabs, new Point(tabs.Width / 2, tabs.Height / 2), MouseButtons.Left);
            Assert("settings tab click", events == 1 && tabs.SelectedIndex == 1);
        }

        using (SettingsSwitch toggle = new SettingsSwitch())
        {
            int events = 0;
            toggle.CheckedChanged += delegate { events++; };
            Activate(toggle);
            Assert("settings switch click", events == 1 && toggle.Checked);
        }
    }

    private static List<Rectangle> ItemBounds(EditorToolbar toolbar)
    {
        FieldInfo field = typeof(EditorToolbar).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic);
        var bounds = new List<Rectangle>();
        foreach (object item in (System.Collections.IEnumerable)field.GetValue(toolbar))
            bounds.Add((Rectangle)item.GetType().GetField("Bounds").GetValue(item));
        return bounds;
    }

    private static int CountItems(EditorToolbar toolbar)
    {
        FieldInfo field = typeof(EditorToolbar).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic);
        int count = 0;
        foreach (object item in (System.Collections.IEnumerable)field.GetValue(toolbar))
            if (!(bool)item.GetType().GetField("IsSeparator").GetValue(item)) count++;
        return count;
    }

    private static Bitmap Capture(Control control)
    {
        Bitmap image = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppArgb);
        control.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
        return image;
    }

    private static int ColorCount(Bitmap image)
    {
        var colors = new HashSet<int>();
        int stepX = Math.Max(1, image.Width / 40);
        int stepY = Math.Max(1, image.Height / 12);
        for (int x = 0; x < image.Width; x += stepX)
            for (int y = 0; y < image.Height; y += stepY)
                colors.Add(image.GetPixel(x, y).ToArgb());
        return colors.Count;
    }

    private static void Click(Control control, Point point, MouseButtons button)
    {
        MethodInfo method = control.GetType().GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Invoke(control, new object[] { new MouseEventArgs(button, 1, point.X, point.Y, 0) });
    }

    private static void Activate(Control control)
    {
        MethodInfo method = control.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Invoke(control, new object[] { EventArgs.Empty });
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }

    private static Point Center(Rectangle bounds)
    {
        return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    }

    private static string ScaleName(float scale)
    {
        return scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_');
    }

    private static void Save(Bitmap image, string name)
    {
        image.Save(Path.Combine(_output, name), ImageFormat.Png);
    }

    private static void Assert(string name, bool condition)
    {
        if (condition)
        {
            _pass++;
            Console.WriteLine("PASS " + name);
        }
        else
        {
            _fail++;
            Console.WriteLine("FAIL " + name);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
