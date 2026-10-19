p = r'D:\Agents\微信截图助手\tools\scroll_target.ps1'
s = open(p, encoding='utf-8').read()
if 'ConsoleHider' not in s:
    s = s.replace("Add-Type -AssemblyName System.Windows.Forms",
"""Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ConsoleHider {
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    public static void Hide() { IntPtr h = GetConsoleWindow(); if (h != IntPtr.Zero) ShowWindow(h, 0); }
}
"@
[ConsoleHider]::Hide()
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TopMostHelper {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static void MakeTopMost(IntPtr h) { SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010); }
}
"@""", 1)
    s = s.replace("[System.Windows.Forms.Application]::Run($form)",
"""[System.Windows.Forms.Application]::Run($form)""", 1)
    # 在 Run 之前强制置顶
    s = s.replace("[System.Windows.Forms.Application]::Run($form)",
"""$form.Handle | Out-Null
[TopMostHelper]::MakeTopMost($form.Handle)
[System.Windows.Forms.Application]::Run($form)""", 1)
open(p, 'w', encoding='utf-8').write(s)

p2 = r'D:\Agents\微信截图助手\tests\real_ui_test5.ps1'
t = open(p2, encoding='utf-8').read()
t = t.replace("-PassThru -WindowStyle Hidden -RedirectStandardError $targetErr -RedirectStandardOutput $targetOut",
              "-PassThru -WindowStyle Normal -RedirectStandardError $targetErr -RedirectStandardOutput $targetOut")
open(p2, 'w', encoding='utf-8').write(t)
print('patched')
