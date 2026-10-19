# UI 自动化探针（开发/QA 用，Lead 维护）
# 提供：查找隐藏主窗口、发送热键消息、模拟鼠标拖拽/点击、发送 Esc、全屏截图
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class UiProbe
{
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    public static IntPtr FindByTitle(string title) {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            var sb = new System.Text.StringBuilder(512);
            GetWindowText(h, sb, 512);
            if (sb.ToString() == title) { result = h; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    public delegate bool EnumChildProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);
    public static IntPtr FindEditControl(IntPtr parent) {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
            var cls = new System.Text.StringBuilder(256);
            GetClassName(h, cls, 256);
            if (cls.ToString().IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static void PostChars(IntPtr h, string text) {
        foreach (char c in text) { PostMessage(h, 0x0102, new IntPtr(c), IntPtr.Zero); System.Threading.Thread.Sleep(20); }
    }
    public static void PostKeyTo(IntPtr h, int vk) {
        PostMessage(h, 0x0100, new IntPtr(vk), IntPtr.Zero); System.Threading.Thread.Sleep(30);
        PostMessage(h, 0x0101, new IntPtr(vk), IntPtr.Zero); System.Threading.Thread.Sleep(30);
    }
    public const uint LEFTDOWN = 0x02, LEFTUP = 0x04, RIGHTDOWN = 0x08, RIGHTUP = 0x10, MOUSEWHEEL = 0x0800;
    public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(60); mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero); }
    public static void Drag(int x1, int y1, int x2, int y2, int steps) {
        SetCursorPos(x1, y1); System.Threading.Thread.Sleep(80);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
        for (int i = 1; i <= steps; i++) { int x = x1 + (x2 - x1) * i / steps; int y = y1 + (y2 - y1) * i / steps; SetCursorPos(x, y); System.Threading.Thread.Sleep(18); }
        System.Threading.Thread.Sleep(60); mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
    public static void Esc() { keybd_event(0x1B, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(0x1B, 0, 2, UIntPtr.Zero); }
    public static void RightClick(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(80); mouse_event(RIGHTDOWN, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(50); mouse_event(RIGHTUP, 0, 0, 0, UIntPtr.Zero); }
    public static void KeyTap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); keybd_event(vk, 0, 2, UIntPtr.Zero); }
    public static void WheelEvent(int delta) { mouse_event(MOUSEWHEEL, 0, 0, (uint)delta, UIntPtr.Zero); }
}
"@
function Find-XJWindow { return [UiProbe]::FindByTitle('XiaoJie_WeChatShot_Hidden_v1') }
function Send-XJHotkey([IntPtr]$hwnd) { return [UiProbe]::PostMessage($hwnd, 0x0312, [IntPtr]0x5A11, [IntPtr]::Zero) }
function Save-ScreenPng([string]$path) {
    Add-Type -AssemblyName System.Drawing
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}


