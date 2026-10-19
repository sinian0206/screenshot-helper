# 1) TrayApp：只在首次运行时提示“配置完成”
p = r'D:\Agents\微信截图助手\src\TrayApp.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('''        private void ShowSettings(bool firstRun)
        {
            if (_settings != null && !_settings.IsDisposed)''',
'''        private void ShowSettings(bool firstRun)
        {
            bool wasFirstRun = firstRun;
            if (_settings != null && !_settings.IsDisposed)''', 1)
s = s.replace('''                else
                {
                    Toast.Show("配置完成！按 " + _cfg.Hotkey + " 开始截图", 3000);
                }''',
'''                else if (wasFirstRun)
                {
                    Toast.Show("配置完成！按 " + _cfg.Hotkey + " 开始截图", 3000);
                }''', 1)
open(p, 'w', encoding='utf-8').write(s)

# 2) 长截图结果窗口最小尺寸，避免高 DPI/小窗口下按钮被截断
p2 = r'D:\Agents\微信截图助手\src\PinForm.cs'
t = open(p2, encoding='utf-8').read()
t = t.replace('''            Size = new Size(Math.Max(420, w), Math.Max(260, h));
            MinimumSize = new Size(360, 220);''',
'''            Size = new Size(Math.Max(480, w), Math.Max(260, h));
            MinimumSize = new Size(480, 220);''', 1)
open(p2, 'w', encoding='utf-8').write(t)
print('patched')
