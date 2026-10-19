path = r'D:\Agents\微信截图助手\src\SelfTest.cs'
src = open(path, encoding='utf-8').read()

old = '''                        using (Bitmap snap = new Bitmap(overlay.Width, overlay.Height))
                        {
                            overlay.DrawToBitmap(snap, new Rectangle(0, 0, overlay.Width, overlay.Height));
                            string snapPath = Path.Combine(uiDir, "editor_annotated.png");
                            snap.Save(snapPath, ImageFormat.Png);
                            check("编辑器标注渲染", HasWarmColor(snap), snapPath);
                        }

                        Rectangle saveBtn = toolbar.NonSeparatorBounds(12);
                        ClickControl(toolbar, saveBtn);
                        Pump(6);
'''
new = '''                        using (Bitmap snap = new Bitmap(overlay.Width, overlay.Height))
                        {
                            overlay.DrawToBitmap(snap, new Rectangle(0, 0, overlay.Width, overlay.Height));
                            string snapPath = Path.Combine(uiDir, "editor_annotated.png");
                            snap.Save(snapPath, ImageFormat.Png);
                            check("编辑器标注渲染", HasWarmColor(snap), snapPath);
                        }

                        // 撤销：点撤销按钮后标注应消失
                        ClickControl(toolbar, toolbar.NonSeparatorBounds(11)); // 撤销
                        using (Bitmap undone = new Bitmap(overlay.Width, overlay.Height))
                        {
                            overlay.DrawToBitmap(undone, new Rectangle(0, 0, overlay.Width, overlay.Height));
                            undone.Save(Path.Combine(uiDir, "editor_after_undo.png"), ImageFormat.Png);
                            check("撤销功能", !HasWarmColor(undone), "撤销后不应有红色标注");
                        }

                        // 文字工具：点击文字工具 → 点击画布 → 文本框出现 → 输入 → Enter 提交
                        ClickControl(toolbar, toolbar.NonSeparatorBounds(6)); // 文字
                        PostMouse(overlay.Handle, 0x0201, 640, 420);
                        Pump(2);
                        PostMouse(overlay.Handle, 0x0202, 640, 420);
                        Pump(2);
                        TextBox textBox = FindTextBox(overlay);
                        bool textOk = false;
                        if (textBox != null)
                        {
                            foreach (char ch in "Hi") PostChar(textBox.Handle, ch);
                            Pump(2);
                            PostKeyDown(textBox.Handle, 0x0D); // Enter
                            Pump(3);
                            textOk = FindTextBox(overlay) == null;
                        }
                        check("文字工具交互", textOk, textOk ? "输入并提交成功" : "未找到文本框或提交失败");

                        // 重新画一个矩形，确保最终保存的图片包含标注
                        ClickControl(toolbar, toolbar.NonSeparatorBounds(0));
                        PostMouse(overlay.Handle, 0x0201, 320, 300);
                        Pump();
                        for (int i = 1; i <= 5; i++)
                        {
                            PostMouse(overlay.Handle, 0x0200, 320 + 260 * i / 5, 300 + 160 * i / 5);
                            Pump();
                        }
                        PostMouse(overlay.Handle, 0x0202, 580, 460);
                        Pump();

                        Rectangle saveBtn = toolbar.NonSeparatorBounds(12);
                        ClickControl(toolbar, saveBtn);
                        Pump(6);
'''
assert old in src
src = src.replace(old, new, 1)

helpers_anchor = '''        private static AiResultPanel FindAiPanel(Control parent)'''
helper_code = '''        private static TextBox FindTextBox(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                TextBox box = control as TextBox;
                if (box != null) return box;
            }
            return null;
        }

        private static void PostChar(IntPtr hwnd, char ch)
        {
            Native.PostMessage(hwnd, 0x0102, new IntPtr(ch), IntPtr.Zero);
        }

        private static void PostKeyDown(IntPtr hwnd, int vk)
        {
            Native.PostMessage(hwnd, 0x0100, new IntPtr(vk), IntPtr.Zero);
            Native.PostMessage(hwnd, 0x0101, new IntPtr(vk), IntPtr.Zero);
        }

        private static AiResultPanel FindAiPanel(Control parent)'''
assert helpers_anchor in src
src = src.replace(helpers_anchor, helper_code, 1)
open(path, 'w', encoding='utf-8').write(src)
print('patched undo/text tests')
