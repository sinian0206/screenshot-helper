path = r'D:\Agents\微信截图助手\src\OverlayForm.cs'
src = open(path, encoding='utf-8').read()
old = '''            Controls.Add(panel);
            panel.BringToFront();
            _aiPanel = panel;
            panel.Start();'''
new = '''            Controls.Add(panel);
            panel.BringToFront();
            _aiPanel = panel;
            _toolbar.Visible = false;
            panel.Start();'''
assert old in src
src = src.replace(old, new, 1)
old2 = '''            catch { }
            try { Activate(); Focus(); } catch { }
            Invalidate();
        }

        private void PromptConfigureApiKey()'''
new2 = '''            catch { }
            if (_hasSelection)
            {
                _toolbar.Visible = true;
                LayoutToolbar();
            }
            try { Activate(); Focus(); } catch { }
            Invalidate();
        }

        private void PromptConfigureApiKey()'''
assert old2 in src
src = src.replace(old2, new2, 1)
open(path, 'w', encoding='utf-8').write(src)
print('patched')
