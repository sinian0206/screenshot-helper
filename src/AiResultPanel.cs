using System;
using System.Drawing;
using System.Windows.Forms;

namespace XiaoJie
{
    public enum AiPanelMode
    {
        Ocr,
        Translate
    }

    /// <summary>
    /// 内嵌在截图编辑器里的 AI 结果面板（文字提取 / 文字翻译）。
    /// 由 OverlayForm 创建、设定位置/大小并 Add 到 Controls，然后调用 Start()。
    /// 面板自己负责调用 DeepSeekClient、流式刷新、复制、关闭、错误提示。
    /// 关闭时触发 CloseRequested，由 OverlayForm 移除并释放。
    /// </summary>
    public sealed class AiResultPanel : Panel
    {
        private readonly Bitmap _aiImage;
        private readonly AiPanelMode _mode;
        private readonly AppConfig _cfg;
        private readonly DeepSeekClient _client;
        private readonly float _scale;
        private Label _placeholder;
        private Button _stubClose;

        public AiResultPanel(Bitmap aiImage, AiPanelMode mode, AppConfig cfg, DeepSeekClient client, float uiScale)
        {
            _aiImage = aiImage;
            _mode = mode;
            _cfg = cfg;
            _client = client;
            _scale = uiScale <= 0 ? 1f : uiScale;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(250, 250, 250);
            Size = new Size((int)(520 * _scale), (int)(560 * _scale));
            BorderStyle = BorderStyle.FixedSingle;

            _placeholder = new Label();
            _placeholder.AutoSize = false;
            _placeholder.Dock = DockStyle.Fill;
            _placeholder.TextAlign = ContentAlignment.MiddleCenter;
            _placeholder.ForeColor = Color.FromArgb(120, 120, 120);
            _placeholder.Font = new Font("Microsoft YaHei UI", 10f * _scale);
            _placeholder.Text = "AI 面板（待实现：由 ai-ui 负责）\n\n" + (mode == AiPanelMode.Ocr ? "文字提取" : "文字翻译");
            Controls.Add(_placeholder);

            _stubClose = new Button();
            _stubClose.Text = "关闭";
            _stubClose.Size = new Size((int)(80 * _scale), (int)(30 * _scale));
            _stubClose.Location = new Point(Width - _stubClose.Width - (int)(12 * _scale), Height - _stubClose.Height - (int)(10 * _scale));
            _stubClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _stubClose.Click += delegate
            {
                EventHandler h = CloseRequested;
                if (h != null) h(this, EventArgs.Empty);
            };
            Controls.Add(_stubClose);
        }

        public event EventHandler CloseRequested;

        /// <summary>开始请求（由 OverlayForm 调用一次）。</summary>
        public void Start()
        {
            Toast.Show("AI 面板尚未实现（ai-ui 队友负责）");
        }

        /// <summary>取消进行中的请求（由 OverlayForm 在删除面板前调用）。</summary>
        public void CancelWork()
        {
            if (_client != null) _client.CancelAll();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelWork();
                if (_aiImage != null) _aiImage.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
