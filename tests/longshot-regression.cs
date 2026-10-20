using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using XiaoJie;

internal static class LongShotRegression
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        Check("单击选区不会启动抓帧", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Mouse(0x0201, 30, 30);
                f.Tick();
                Equal(0, f.Captures);
            }
        });
        Check("选区外滚轮不会启动抓帧", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Mouse(0x020A, 1000, 1000);
                f.Tick();
                Equal(0, f.Captures);
            }
        });
        Check("方向键释放不会启动抓帧", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Keyboard(0x0101, 0x28);
                f.Tick();
                Equal(0, f.Captures);
            }
        });
        Check("一次滚动信号只抓取一帧", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Signal();
                f.Tick();
                f.Tick();
                Equal(1, f.Captures);
                Equal(2, f.Stitcher.AppendedFrames);
            }
        });
        Check("首次滚动等待目标重绘", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Signal();
                f.Tick(false);
                Equal(0, f.Captures);
                f.Tick();
                Equal(1, f.Captures);
            }
        });
        Check("连续滚动合并并按时捕获", delegate
        {
            using (Fixture f = new Fixture())
            {
                for (int i = 0; i < 20; ++i) f.Signal();
                Set(f.Session, "_pendingSinceTick", unchecked(Environment.TickCount - 250));
                f.Tick(false);
                f.Tick(false);
                Equal(1, f.Captures);
            }
        });
        Check("静止按住鼠标不会延长捕获", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Signal();
                Set(f.Session, "_leftDown", true);
                Set(f.Session, "_lastScrollSignalTick", unchecked(Environment.TickCount - 2000));
                f.Tick(false);
                Equal(0, f.Captures);
            }
        });
        Check("完成时无待处理滚动不会补抓", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Signal();
                f.Tick();
                Set(f.Session, "_lastCaptureTick", unchecked(Environment.TickCount - 500));
                Call(f.Session, "FlushPendingFrame");
                Equal(1, f.Captures);
            }
        });
        Check("完成时提交未处理的最后滚动", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Signal();
                Set(f.Session, "_lastScrollSignalTick", unchecked(Environment.TickCount - 160));
                Set(f.Session, "_finishRequested", true);
                Call(f.Session, "FlushPendingFrame");
                Equal(1, f.Captures);
                Equal(240, f.Stitcher.ResultSize.Height);
            }
        });
        Check("取消优先于最后滚动冲刷", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Signal();
                Set(f.Session, "_cancelRequested", true);
                Call(f.Session, "FlushPendingFrame");
                Equal(0, f.Captures);
            }
        });
        Check("单帧结果直接返回已捕获画面", delegate
        {
            using (Fixture f = new Fixture())
            using (Bitmap result = (Bitmap)Call(f.Session, "BuildResult"))
            {
                Equal(0, f.Captures);
                Equal(180, result.Height);
                Equal(Pixel(40, 60).ToArgb(), result.GetPixel(40, 60).ToArgb());
            }
            Toast.HideCurrent();
        });
        Check("取消后定时器不再抓帧", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Signal();
                Set(f.Session, "_cancelRequested", true);
                f.Tick();
                Equal(0, f.Captures);
            }
        });
        Check("消息泵重入不会嵌套抓帧", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.OnCapture = delegate { if (f.Captures == 1) f.Tick(); };
                f.Signal();
                f.Tick();
                Equal(1, f.Captures);
                Equal(2, f.Stitcher.AppendedFrames);
            }
        });
        Check("抓帧期间的新滚动留给下一帧", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.OnCapture = delegate { if (f.Captures == 1) f.Signal(); };
                f.Signal();
                f.Tick();
                f.Tick();
                f.Tick();
                Equal(2, f.Captures);
                Equal(300, f.Stitcher.ResultSize.Height);
            }
        });
        Check("抓帧期间取消不会提交结果", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.OnCapture = delegate { Set(f.Session, "_cancelRequested", true); };
                f.Signal();
                f.Tick();
                Equal(1, f.Stitcher.AppendedFrames);
            }
        });
        Check("选区内滚轮仍可触发抓帧", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Mouse(0x020A, 30, 30);
                f.Tick();
                Equal(1, f.Captures);
                Equal(240, f.Stitcher.ResultSize.Height);
            }
        });
        Check("其他窗口位于选区时忽略其滚轮", delegate
        {
            using (Fixture f = new Fixture())
            {
                Set(f.Session, "_targetWindow", new IntPtr(-1));
                f.Mouse(0x020A, 30, 30);
                f.Tick();
                Equal(0, f.Captures);
            }
        });
        Check("其他前台窗口方向键不会启动捕获", delegate
        {
            using (Fixture f = new Fixture())
            {
                Set(f.Session, "_targetWindow", new IntPtr(-1));
                f.Keyboard(0x0100, 0x28);
                f.Tick();
                Equal(0, f.Captures);
            }
        });
        Check("选区拖动仍可触发抓帧", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Mouse(0x0201, 30, 30);
                f.Mouse(0x0200, 30, 45);
                f.Tick();
                Equal(1, f.Captures);
            }
        });
        Check("暂停后无信号保持结果尺寸", delegate
        {
            using (Fixture f = new Fixture())
            {
                f.Signal();
                f.Tick();
                Set(f.Session, "_lastScrollSignalTick", unchecked(Environment.TickCount - 2000));
                f.Tick(false);
                for (int i = 0; i < 8; ++i) f.Tick(false);
                Equal(1, f.Captures);
                Equal(240, f.Stitcher.ResultSize.Height);
                f.Signal();
                f.Tick();
                Equal(2, f.Captures);
                Equal(300, f.Stitcher.ResultSize.Height);
            }
        });
        Check("拼接器既有边界和重绘滞后验证", delegate
        {
            string result = LongShotStitcher.SelfTest();
            if (result.StartsWith("FAIL", StringComparison.Ordinal)) throw new Exception(result);
            Console.WriteLine("  " + result);
        });
        Toast.HideCurrent();
        Console.WriteLine("结果: " + _passed + " PASS, " + _failed + " FAIL");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, Action test)
    {
        try { test(); ++_passed; Console.WriteLine("[PASS] " + name); }
        catch (Exception ex)
        {
            ++_failed;
            Exception cause = ex is TargetInvocationException ? ex.InnerException : ex;
            Console.WriteLine("[FAIL] " + name + ": " + cause.Message);
        }
    }

    private static void Equal(int expected, int actual)
    {
        if (expected != actual) throw new Exception("期望 " + expected + "，实际 " + actual);
    }

    private static object Call(object target, string name, params object[] args)
    {
        return target.GetType().GetMethod(name, PrivateInstance).Invoke(target, args);
    }

    private static void Set(object target, string name, object value)
    {
        target.GetType().GetField(name, PrivateInstance).SetValue(target, value);
    }

    private static Color Pixel(int x, int y)
    {
        uint hash = unchecked((uint)((y * 120 + x) * 1103515245 + 12345));
        hash ^= hash >> 13;
        return Color.FromArgb(255, (int)(hash & 255), (int)((hash >> 8) & 255), (int)((hash >> 16) & 255));
    }

    private static Bitmap Frame(int top)
    {
        Bitmap frame = new Bitmap(120, 180);
        for (int y = 0; y < frame.Height; ++y)
            for (int x = 0; x < frame.Width; ++x) frame.SetPixel(x, y, Pixel(x, top + y));
        return frame;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly LongShotSession Session;
        public readonly LongShotStitcher Stitcher;
        public int Captures;
        public Action OnCapture;

        public Fixture()
        {
            Session = new LongShotSession(new Rectangle(10, 10, 120, 180), delegate
            {
                ++Captures;
                if (OnCapture != null) OnCapture();
                return Frame(Captures * 60);
            });
            Stitcher = new LongShotStitcher(120);
            using (Bitmap first = Frame(0)) Stitcher.AddFrame(first);
            Set(Session, "_stitcher", Stitcher);
            Set(Session, "_lastCaptureTick", unchecked(Environment.TickCount - 500));
        }

        public void Signal() { Call(Session, "SignalScroll"); }

        public void Tick(bool matureSignal = true)
        {
            Set(Session, "_lastCaptureTick", unchecked(Environment.TickCount - 500));
            if (matureSignal) Set(Session, "_lastScrollSignalTick", unchecked(Environment.TickCount - 160));
            Call(Session, "OnTimerTick", null, EventArgs.Empty);
        }

        public void Mouse(int message, int x, int y)
        {
            Type dataType = typeof(LongShotSession).GetNestedType("MSLLHOOKSTRUCT", BindingFlags.NonPublic);
            Type pointType = typeof(LongShotSession).GetNestedType("POINT", BindingFlags.NonPublic);
            object point = Activator.CreateInstance(pointType);
            pointType.GetField("X").SetValue(point, x);
            pointType.GetField("Y").SetValue(point, y);
            object data = Activator.CreateInstance(dataType);
            dataType.GetField("pt").SetValue(data, point);
            InvokeHook("MouseHookProc", message, data, dataType);
        }

        public void Keyboard(int message, uint key)
        {
            Type dataType = typeof(LongShotSession).GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic);
            object data = Activator.CreateInstance(dataType);
            dataType.GetField("vkCode").SetValue(data, key);
            InvokeHook("KeyboardHookProc", message, data, dataType);
        }

        private void InvokeHook(string method, int message, object data, Type dataType)
        {
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(dataType));
            try
            {
                Marshal.StructureToPtr(data, pointer, false);
                Call(Session, method, 0, (IntPtr)message, pointer);
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        public void Dispose() { Session.Dispose(); }
    }
}
