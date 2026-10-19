using System;
using System.Drawing;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 设置窗口。由 TrayApp 以 ShowDialog 方式打开：
    ///   using (var f = new SettingsForm(cfg, store, firstRun)) { if (f.ShowDialog() == DialogResult.OK) ... }
    /// 约定：
    ///   - 对话框内部编辑 cfg 的副本 _working，点击“确定”时才写回传入的 cfg（cfg.CopyFrom(_working)）并调用 store.Save(cfg)。
    ///   - 点击“确定”时还要处理开机自启：Autostart.SetEnabled(cfg.StartWithWindows)。
    ///   - 保存成功后触发 Saved 事件（在对话框关闭前）。
    ///   - firstRun=true 时显示欢迎/引导文案，并默认聚焦 API Key 输入框。
    /// 负责队友：ai-ui
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private readonly AppConfig _cfg;
        private readonly ConfigStore _store;
        private readonly bool _firstRun;

        public SettingsForm(AppConfig cfg, ConfigStore store)
            : this(cfg, store, false)
        {
        }

        public SettingsForm(AppConfig cfg, ConfigStore store, bool firstRun)
        {
            _cfg = cfg;
            _store = store;
            _firstRun = firstRun;

            Text = firstRun ? "欢迎使用微信截图助手" : "微信截图助手 - 设置";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            Width = 640;
            Height = 560;
            Font = new Font("Microsoft YaHei UI", 9f);

            Label label = new Label();
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.ForeColor = Color.FromArgb(120, 120, 120);
            label.Text = "设置窗口（待实现：由 ai-ui 负责）\r\n\r\n配置项：API Key / Base URL / 模型 / 热键 / 保存目录 / 自启动 / 翻译语言";
            Controls.Add(label);

            Button ok = new Button();
            ok.Text = "确定";
            ok.Width = 88;
            ok.Height = 32;
            ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            ok.Location = new Point(Width - 200, Height - 66);
            ok.Click += delegate
            {
                if (_store != null && _cfg != null) _store.Save(_cfg);
                EventHandler h = Saved;
                if (h != null) h(this, EventArgs.Empty);
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Width = 88;
            cancel.Height = 32;
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.Location = new Point(Width - 104, Height - 66);
            cancel.Click += delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        public event EventHandler Saved;
    }
}
