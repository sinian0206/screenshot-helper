using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>Checks dropdown lifetime through the real WinForms modal message filter.</summary>
    internal static class SettingsMenuRegression
    {
        private static int _failures;
        private static int _scenes;
        private static int _assertions;
        private static readonly List<string> Errors = new List<string>();
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static Form _host;
        private static SettingsComboBox _combo;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs args)
            {
                _failures++;
                Errors.Add(args.Exception.ToString());
            };
            using (_host = new Form())
            using (Timer watchdog = new Timer())
            {
                _host.StartPosition = FormStartPosition.Manual;
                _host.Location = new Point(-30000, -30000);
                _host.ShowInTaskbar = false;
                _host.Size = new Size(400, 240);
                _combo = NewCombo(_host);
                watchdog.Interval = 15000;
                watchdog.Tick += delegate
                {
                    _failures++;
                    Errors.Add("Settings menu message loop exceeded 15 seconds.");
                    Application.Exit();
                };
                watchdog.Start();
                _host.Shown += delegate
                {
                    _host.BeginInvoke((Action)delegate
                    {
                        Scene("outside-click-and-modal-reopen", TestOutsideClick);
                        Scene("escape-close", TestEscape);
                        Scene("select-item", TestSelectItem);
                        Scene("editable-select-item", TestEditableItem);
                        Scene("toggle-and-reuse", TestToggle);
                        Scene("updated-items", TestUpdatedItems);
                        Scene("owner-dispose-with-open-menu", TestOwnerDispose);
                        Scene("close-settings-from-selection-event", TestSelectionClose);
                        Scene("actual-settings-close-reopen", TestActualSettings);
                        Scene("modal-settings-explicit-dispose", TestModalLifetime);
                        Scene("tray-style-modal-entry", TestTrayEntry);
                        Scene("100-menu-lifetime-cycles", TestRepeatedCycles);
                        _host.Close();
                    });
                };
                Application.Run(_host);
                watchdog.Stop();
            }
            foreach (string error in Errors) Console.WriteLine(error);
            Console.WriteLine("settings-menu regression: " + _scenes + " scenes / " + _assertions + " assertions / " + _failures + " FAIL");
            return _failures == 0 ? 0 : 1;
        }

        private static void Scene(string name, Action test)
        {
            _scenes++;
            try
            {
                test();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                _failures++;
                TargetInvocationException invocation = exception as TargetInvocationException;
                Errors.Add("FAIL " + name + ": " + (invocation != null && invocation.InnerException != null ? invocation.InnerException : exception));
            }
        }

        private static void TestOutsideClick()
        {
            for (int cycle = 0; cycle < 8; cycle++)
            {
                ContextMenuStrip menu = Open(_combo);
                Click(_host, new Point(250, 150));
                Check(!menu.Visible, "Queued outside click must close the dropdown.");
                Check(!menu.IsDisposed, "Closed dropdown must remain owned until its control is disposed.");
                OpenDialog(_host);
            }
        }

        private static void TestEscape()
        {
            ContextMenuStrip menu = Open(_combo);
            Check(PostMessage(menu.Handle, 0x0100, new IntPtr((int)Keys.Escape), IntPtr.Zero), "Queue Escape key.");
            Check(PostMessage(menu.Handle, 0x0101, new IntPtr((int)Keys.Escape), IntPtr.Zero), "Queue Escape release.");
            Application.DoEvents();
            Check(!menu.Visible && !menu.IsDisposed, "Escape closes without releasing the owned menu.");
            OpenDialog(_host);
        }

        private static void TestSelectItem()
        {
            _combo.SelectedIndex = 0;
            ContextMenuStrip menu = Open(_combo);
            ClickItem(menu, 1);
            Check(_combo.SelectedIndex == 1 && _combo.Text == "JPEG", "Mouse selection updates the chosen value.");
            Check(!menu.Visible && !menu.IsDisposed, "Selection closes without synchronous menu disposal.");
            OpenDialog(_host);
        }

        private static void TestEditableItem()
        {
            using (SettingsComboBox combo = NewCombo(_host))
            {
                combo.ComboStyle = SettingsComboStyle.DropDown;
                ContextMenuStrip menu = Open(combo);
                ClickItem(menu, 2);
                Check(combo.SelectedIndex == 2 && combo.Text == "BMP", "Editable mode updates both index and text.");
            }
            OpenDialog(_host);
        }

        private static void TestToggle()
        {
            ContextMenuStrip menu = Open(_combo);
            Call(_combo, "OpenDropDown");
            Application.DoEvents();
            Check(!menu.Visible && !menu.IsDisposed, "Second dropdown action toggles the existing menu closed.");
            ContextMenuStrip reopened = Open(_combo);
            Check(ReferenceEquals(menu, reopened), "Reopening reuses the same owned menu.");
            reopened.Close();
        }

        private static void TestUpdatedItems()
        {
            _combo.SetItems("MP4");
            ContextMenuStrip menu = Open(_combo);
            Check(menu.Items.Count == 1 && menu.Items[0].Text == "MP4", "Reused menu reflects the current item list.");
            ClickItem(menu, 0);
            Check(_combo.Text == "MP4", "Updated selection remains available.");
            _combo.SetItems("PNG", "JPEG", "BMP");
        }

        private static void TestOwnerDispose()
        {
            SettingsComboBox combo = NewCombo(_host);
            ContextMenuStrip menu = Open(combo);
            combo.Dispose();
            Application.DoEvents();
            Check(menu.IsDisposed, "Disposing the owner releases its menu.");
            OpenDialog(_host);
        }

        private static void TestSelectionClose()
        {
            using (Form child = new Form())
            {
                child.ShowInTaskbar = false;
                child.StartPosition = FormStartPosition.Manual;
                child.Location = new Point(-29000, -29000);
                SettingsComboBox combo = NewCombo(child);
                combo.SelectedIndexChanged += delegate { child.Close(); };
                child.Show(_host);
                ContextMenuStrip menu = Open(combo);
                ClickItem(menu, 1);
                Check(child.IsDisposed && menu.IsDisposed, "Closing a modeless owner from selection releases its menu.");
            }
            OpenDialog(_host);
        }

        private static void TestActualSettings()
        {
            for (int cycle = 0; cycle < 4; cycle++)
            {
                using (SettingsForm settings = new SettingsForm(new AppConfig(), new ConfigStore(), false))
                {
                    settings.ShowInTaskbar = false;
                    settings.StartPosition = FormStartPosition.Manual;
                    settings.Location = new Point(-28000, -28000);
                    settings.Shown += delegate
                    {
                        settings.BeginInvoke((Action)delegate
                        {
                            try
                            {
                                Call(settings, "ShowPage", 2);
                                SettingsComboBox combo = (SettingsComboBox)typeof(SettingsForm).GetField("_formatCombo", PrivateInstance).GetValue(settings);
                                ContextMenuStrip menu = Open(combo);
                                Click(settings, new Point(3, 3));
                                Check(!menu.Visible, "Actual settings dropdown closes through the modal filter.");
                                OpenDialog(settings);
                            }
                            finally { settings.Close(); }
                        });
                    };
                    settings.ShowDialog(_host);
                }
            }
        }

        private static void TestTrayEntry()
        {
            using (ContextMenuStrip trayMenu = new ContextMenuStrip())
            {
                bool opened = false;
                ToolStripMenuItem item = new ToolStripMenuItem("设置");
                item.Click += delegate
                {
                    OpenDialog(_host);
                    opened = true;
                };
                trayMenu.Items.Add(item);
                trayMenu.Show(_host, new Point(0, 0));
                Application.DoEvents();
                ClickItem(trayMenu, 0);
                Check(opened && !trayMenu.IsDisposed, "Tray-style Click can enter and leave a modal dialog.");
            }
        }

        private static void TestModalLifetime()
        {
            SettingsForm settings = new SettingsForm(new AppConfig(), new ConfigStore(), false);
            ContextMenuStrip menu = null;
            try
            {
                settings.ShowInTaskbar = false;
                settings.StartPosition = FormStartPosition.Manual;
                settings.Location = new Point(-28000, -28000);
                settings.Shown += delegate
                {
                    settings.BeginInvoke((Action)delegate
                    {
                        try
                        {
                            Call(settings, "ShowPage", 2);
                            SettingsComboBox combo = (SettingsComboBox)typeof(SettingsForm).GetField("_formatCombo", PrivateInstance).GetValue(settings);
                            menu = Open(combo);
                            Click(settings, new Point(3, 3));
                        }
                        finally { settings.Close(); }
                    });
                };
                settings.ShowDialog(_host);
                Check(!settings.IsDisposed, "Closing a modal form does not dispose its managed controls.");
                Check(menu != null && !menu.IsDisposed, "Owned dropdown remains allocated until modal form disposal.");
                Check(typeof(SettingsForm).GetField("_titleFont", PrivateInstance).GetValue(settings) != null,
                    "Modal close does not run the settings font cleanup.");
            }
            finally { settings.Dispose(); }
            Check(settings.IsDisposed && menu != null && menu.IsDisposed,
                "Explicit modal form disposal releases the dropdown and form resources.");
            Check(typeof(SettingsForm).GetField("_titleFont", PrivateInstance).GetValue(settings) == null,
                "Explicit modal form disposal runs the settings font cleanup.");
        }

        private static void TestRepeatedCycles()
        {
            ContextMenuStrip first = null;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                ContextMenuStrip menu = Open(_combo);
                if (first == null) first = menu;
                Check(ReferenceEquals(first, menu), "Repeated cycles retain one owned menu.");
                if (cycle % 2 == 0) menu.Close();
                else Click(_host, new Point(250, 150));
                Check(!menu.IsDisposed && !menu.Visible, "Repeated close completes without a disposed active dropdown.");
            }
            OpenDialog(_host);
        }

        private static SettingsComboBox NewCombo(Control parent)
        {
            SettingsComboBox combo = new SettingsComboBox();
            combo.SetItems("PNG", "JPEG", "BMP");
            combo.Bounds = new Rectangle(24, 24, 180, 36);
            parent.Controls.Add(combo);
            return combo;
        }

        private static ContextMenuStrip Open(SettingsComboBox combo)
        {
            Call(combo, "OpenDropDown");
            Application.DoEvents();
            ContextMenuStrip menu = (ContextMenuStrip)typeof(SettingsComboBox).GetField("_menu", PrivateInstance).GetValue(combo);
            Check(menu != null && menu.Visible && !menu.IsDisposed, "Dropdown is visible in the actual WinForms message loop.");
            return menu;
        }

        private static void ClickItem(ContextMenuStrip menu, int index)
        {
            Rectangle item = menu.Items[index].Bounds;
            Click(menu, new Point(item.Left + item.Width / 2, item.Top + item.Height / 2));
        }

        private static void Click(Control target, Point point)
        {
            IntPtr coordinates = new IntPtr((point.Y << 16) | (point.X & 0xffff));
            Check(PostMessage(target.Handle, 0x0201, new IntPtr(1), coordinates), "Queue mouse down to the test-owned window.");
            Check(PostMessage(target.Handle, 0x0202, IntPtr.Zero, coordinates), "Queue mouse up to the test-owned window.");
            Application.DoEvents();
        }

        private static void OpenDialog(Form owner)
        {
            using (Form dialog = new Form())
            {
                dialog.ShowInTaskbar = false;
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(-29000, -29000);
                bool shown = false;
                dialog.Shown += delegate
                {
                    shown = true;
                    dialog.BeginInvoke((Action)delegate { dialog.Close(); });
                };
                dialog.ShowDialog(owner);
                Check(shown, "A new modal dialog opens after closing the previous dropdown.");
            }
        }

        private static void Call(object target, string method, params object[] args)
        {
            target.GetType().GetMethod(method, PrivateInstance).Invoke(target, args);
        }

        private static void Check(bool passed, string message)
        {
            _assertions++;
            if (!passed) throw new InvalidOperationException(message);
        }
    }
}
