p = r'D:\Agents\微信截图助手\tests\real_ui_test5.ps1'
s = open(p, encoding='utf-8').read()
# 在 TitleSearch 类中加入 ClientToScreen 辅助
s = s.replace('''    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public struct RECT { public int L, T, R, B; }''',
'''    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    public struct RECT { public int L, T, R, B; }
    public struct POINT { public int X, Y; }
    public static POINT ClientOrigin(IntPtr h) { POINT p; p.X = 0; p.Y = 0; ClientToScreen(h, ref p); return p; }''')
# 用客户区原点 + (60,26) 点击「复制图片」
s = s.replace('''        # 点击「复制图片」按钮（窗口内 (12,10,96x32) 中心约 (60,26)）
        [UiProbe]::Click($rr.L + 60, $rr.T + 26)''',
'''        # 点击「复制图片」按钮（客户区内 (12,10,96x32) 中心约 (60,26)）
        $clientOrigin = [TitleSearch]::ClientOrigin($resultHwnd)
        [UiProbe]::Click($clientOrigin.X + 60, $clientOrigin.Y + 26)''')
open(p, 'w', encoding='utf-8').write(s)
print('patched')
