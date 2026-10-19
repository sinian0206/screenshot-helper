p = r'D:\Agents\微信截图助手\src\OverlayForm.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('''                        _busy = false;
                        Dispose();
                        disposeImage.Dispose();''','''                        _busy = false;
                        disposeImage.Dispose();''', 1)
open(p, 'w', encoding='utf-8').write(s)

p2 = r'D:\Agents\微信截图助手\tools\ui_probe.ps1'
t = open(p2, encoding='utf-8').read()
t = t.replace("""    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);""",
"""    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
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
    }""")
open(p2, 'w', encoding='utf-8').write(t)

p3 = r'D:\Agents\微信截图助手\tests\real_ui_test3.ps1'
z = open(p3, encoding='utf-8').read()
old = '''    Start-Overlay $hidden (Join-Path $out '2_selection.png')
    [UiProbe]::Click(686, 798)    # 文字工具
    Start-Sleep -Milliseconds 300
    [UiProbe]::Click(650, 450)    # 输入位置
    Start-Sleep -Milliseconds 400
    foreach ($vk in 0x48, 0x45, 0x4C, 0x4C, 0x4F) { [UiProbe]::KeyTap([byte]$vk) }  # hello
    Start-Sleep -Milliseconds 300
    Save-ScreenPng (Join-Path $out '2_text_edit.png')
    [UiProbe]::KeyTap(0x0D)       # Enter 提交文字
    Start-Sleep -Milliseconds 400
    Save-ScreenPng (Join-Path $out '2_text_committed.png')
    [UiProbe]::Click(1106, 798)   # 完成
    Start-Sleep -Milliseconds 900'''
new = '''    Start-Overlay $hidden (Join-Path $out '2_selection.png')
    [UiProbe]::Click(686, 798)    # 文字工具
    Start-Sleep -Milliseconds 300
    [UiProbe]::Click(650, 450)    # 输入位置
    Start-Sleep -Milliseconds 500
    $overlay2 = [UiProbe]::FindByTitle('微信截图助手 - 截图')
    $edit = [IntPtr]::Zero
    if ($overlay2 -ne [IntPtr]::Zero) { $edit = [UiProbe]::FindEditControl($overlay2) }
    if ($edit -eq [IntPtr]::Zero) {
        $failures += '文字输入: 未找到文本框'
    } else {
        # 直接向文本控件投递字符，绕开中文输入法候选框
        [UiProbe]::PostChars($edit, 'hello')
        Start-Sleep -Milliseconds 200
        Save-ScreenPng (Join-Path $out '2_text_edit.png')
        [UiProbe]::PostKeyTo($edit, 0x0D)  # Enter 提交文字
        Start-Sleep -Milliseconds 400
    }
    Save-ScreenPng (Join-Path $out '2_text_committed.png')
    [UiProbe]::Click(1106, 798)   # 完成
    Start-Sleep -Milliseconds 900'''
assert old in z
z = z.replace(old, new, 1)
open(p3, 'w', encoding='utf-8').write(z)
print('patched')
