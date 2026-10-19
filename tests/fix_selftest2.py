path = r'D:\Agents\微信截图助手\src\SelfTest.cs'
src = open(path, encoding='utf-8').read()
old = '''        private static void ClickControl(Control control, Rectangle localBounds)
        {
            int x = localBounds.X + localBounds.Width / 2;
            int y = localBounds.Y + localBounds.Height / 2;
            PostMouse(control.Handle, 0x0201, x, y);
            Pump(2);
            PostMouse(control.Handle, 0x0202, x, y);
            Pump(2);
        }'''
new = '''        private static void ClickControl(Control control, Rectangle localBounds)
        {
            IntPtr hwnd = control.Handle;
            int x = localBounds.X + localBounds.Width / 2;
            int y = localBounds.Y + localBounds.Height / 2;
            PostMouse(hwnd, 0x0201, x, y);
            Pump(2);
            if (!control.IsDisposed) PostMouse(hwnd, 0x0202, x, y);
            Pump(2);
        }'''
assert old in src
src = src.replace(old, new)
open(path, 'w', encoding='utf-8').write(src)
print('patched')
