using System;
using System.Drawing;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 托盘/主控窗口（隐藏窗口承载热键、托盘图标、单实例消息）。
    /// 负责队友：Lead。骨架阶段先放一个最简实现，保证编译通过。
    /// 真正的实现包括：NotifyIcon 菜单、全局热键注册、截图流程（Capture → OverlayForm）、设置窗口、
    /// 首次运行引导、退出处理。
    /// </summary>
    internal sealed class TrayApp : Form
    {
        private readonly AppConfig _cfg;
        private readonly ConfigStore _store;
        private readonly string[] _args;
        private readonly bool _firstRun;
        private readonly NotifyIcon _tray;

        public TrayApp(AppConfig cfg, ConfigStore store, string[] args, bool firstRun)
        {
            _cfg = cfg;
            _store = store;
            _args = args;
            _firstRun = firstRun;

            Text = AppInfo.HiddenWindowTitle;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-30000, -30000);
            Size = new Size(1, 1);
            Opacity = 0;
            ShowIcon = false;

            _tray = new NotifyIcon();
            try { _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { _tray.Icon = SystemIcons.Application; }
            _tray.Text = AppInfo.Name;
            _tray.Visible = true;

            Label label = new Label();
            label.Text = AppInfo.FullName;
            label.Location = new Point(10, 10);
            label.AutoSize = true;
            Controls.Add(label);

            Toast.Show("骨架版本已启动（核心实现进行中）");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _tray.Visible = false;
            _tray.Dispose();
            base.OnFormClosed(e);
        }
    }
}
