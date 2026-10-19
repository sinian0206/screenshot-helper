p = r'D:\Agents\微信截图助手\src\SelfTest.cs'
s = open(p, encoding='utf-8').read()
# 在 Run 的 Existing checks 之后、离屏 UI 测试之前插入 autostart 测试调用
anchor = '''                // ===== 离屏 UI 测试（不干扰用户桌面）====='''
insert = '''                // ===== 开机自启注册表测试（仅 --autostart-test 时，结束后恢复原状）=====
                if (Program.HasArg(args, "--autostart-test"))
                {
                    RunAutostartTest(check);
                }

                // ===== 离屏 UI 测试（不干扰用户桌面）====='''
assert anchor in s
s = s.replace(anchor, insert, 1)

method = r'''
        private static void RunAutostartTest(Action<string, bool, string> check)
        {
            bool original = false;
            try { original = Autostart.IsEnabled(); } catch { }
            try
            {
                Autostart.SetEnabled(true);
                bool enabled = Autostart.IsEnabled();
                string value = "";
                try
                {
                    using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", false))
                    {
                        if (key != null)
                        {
                            object v = key.GetValue("XiaoJieWeChatShot");
                            if (v != null) value = v.ToString();
                        }
                    }
                }
                catch { }
                Autostart.SetEnabled(false);
                bool disabled = Autostart.IsEnabled();
                bool ok = enabled && !disabled && value.IndexOf("微信截图助手.exe", StringComparison.OrdinalIgnoreCase) >= 0;
                check("开机自启注册表开关", ok,
                    "开启=" + enabled + "，关闭=" + disabled + "，Run 值=" + value);
            }
            catch (Exception ex)
            {
                check("开机自启注册表开关", false, ex.Message);
            }
            finally
            {
                try { Autostart.SetEnabled(original); } catch { }
            }
        }
'''
src = s.rstrip()
assert src.endswith('    }\n}')
src = src[:-len('    }\n}')] + method + '    }\n}\n'
open(p, 'w', encoding='utf-8').write(src)
print('patched autostart selftest')
