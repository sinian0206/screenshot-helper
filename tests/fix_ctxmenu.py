p = r'D:\Agents\微信截图助手\src\OverlayForm.cs'
s = open(p, encoding='utf-8').read()
old = '''            menu.Closed += delegate
            {
                if (_contextMenu == menu) _contextMenu = null;
                menu.Dispose();
            };
            menu.Show(screenPoint);'''
new = '''            menu.Closed += delegate
            {
                if (_contextMenu == menu) _contextMenu = null;
                // 延迟释放：Closed 事件仍处在 ToolStrip.OnItemClicked 调用栈内，立即 Dispose 会抛 ObjectDisposedException
                try { BeginInvoke((Action)delegate { try { menu.Dispose(); } catch { } }); }
                catch { try { menu.Dispose(); } catch { } }
            };
            menu.Show(screenPoint);'''
assert old in s
s = s.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(s)
print('patched context menu disposal')
