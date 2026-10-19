p = r'D:\Agents\微信截图助手\tools\ui_probe.ps1'
s = open(p, encoding='utf-8').read()
s = s.replace("""    public const uint LEFTDOWN = 0x02, LEFTUP = 0x04;""",
"""    public const uint LEFTDOWN = 0x02, LEFTUP = 0x04, RIGHTDOWN = 0x08, RIGHTUP = 0x10, WHEEL = 0x0800;""")
s = s.replace("""    public static void Esc() { keybd_event(0x1B, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(0x1B, 0, 2, UIntPtr.Zero); }""",
"""    public static void Esc() { keybd_event(0x1B, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(0x1B, 0, 2, UIntPtr.Zero); }
    public static void RightClick(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(80); mouse_event(RIGHTDOWN, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(50); mouse_event(RIGHTUP, 0, 0, 0, UIntPtr.Zero); }
    public static void KeyTap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); keybd_event(vk, 0, 2, UIntPtr.Zero); }
    public static void Wheel(int delta) { mouse_event(WHEEL, 0, 0, (uint)delta, UIntPtr.Zero); }""")
open(p, 'w', encoding='utf-8').write(s)
print('probe helpers added')
