using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace XiaoJie
{
    public sealed class HistoryForm : Form
    {
        private readonly ConfigStore _store;
        private readonly AppConfig _cfg;
        private readonly Action<Bitmap> _onOpenInEditor;
        private readonly FlowLayoutPanel _flow;
        private readonly TextBox _searchBox;
        private readonly Label _countLabel;
        private readonly float _scale;

        public HistoryForm(ConfigStore store, AppConfig cfg, Action<Bitmap> onOpenInEditor)
        {
            _store = store;
            _cfg = cfg;
            _onOpenInEditor = onOpenInEditor;
            _scale = DpiUtil.ScaleAtPoint(Cursor.Position);

            Text = "截图历史记录";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size((int)(860 * _scale), (int)(600 * _scale));
            MinimumSize = new Size(640, 440);
            BackColor = SettingsColors.PageBg;
            Font = UiTheme.Font(13f * _scale);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // 1. 深色命令栏
            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = (int)(56 * _scale),
                BackColor = Color.White,
                Padding = new Padding((int)(16 * _scale), (int)(10 * _scale), (int)(16 * _scale), (int)(10 * _scale))
            };
            topBar.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(SettingsColors.Divider, Math.Max(1f, _scale)))
                    e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };

            var searchContainer = new Panel
            {
                Location = new Point((int)(16 * _scale), (int)(11 * _scale)),
                Size = new Size((int)(320 * _scale), (int)(34 * _scale)),
                BackColor = Color.FromArgb(243, 245, 247)
            };
            searchContainer.Paint += delegate (object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF r = new RectangleF(0.5f, 0.5f, searchContainer.Width - 1f, searchContainer.Height - 1f);
                using (GraphicsPath p = ImageUtil.RoundedRect(r, 8 * _scale))
                {
                    using (Pen pen = new Pen(SettingsColors.InputBorder, 1f))
                        e.Graphics.DrawPath(pen, p);
                }
            };

            _searchBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(243, 245, 247),
                Location = new Point((int)(12 * _scale), (int)(8 * _scale)),
                Width = (int)(296 * _scale),
                Font = UiTheme.Font(11f * _scale),
                ForeColor = UiTheme.TextDark
            };
            _searchBox.TextChanged += delegate { ReloadItems(); };
            searchContainer.Controls.Add(_searchBox);
            topBar.Controls.Add(searchContainer);

            _countLabel = new Label
            {
                AutoSize = true,
                Location = new Point((int)(350 * _scale), (int)(18 * _scale)),
                ForeColor = SettingsColors.TextSecondary,
                Font = UiTheme.Font(10.5f * _scale)
            };
            topBar.Controls.Add(_countLabel);

            var clearBtn = new Button
            {
                Text = "清空未收藏",
                Width = (int)(100 * _scale),
                Height = (int)(32 * _scale),
                Location = new Point(ClientSize.Width - (int)(120 * _scale), (int)(12 * _scale)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = UiTheme.Critical,
                Font = UiTheme.Font(10f * _scale),
                Cursor = Cursors.Hand
            };
            clearBtn.FlatAppearance.BorderColor = Color.FromArgb(226, 205, 208);
            clearBtn.Click += delegate
            {
                if (MessageBox.Show(this, "确定要清空所有未收藏的历史截图吗？\r\n（已收藏的截图将安全保留）", AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    HistoryManager.ClearAll(_store);
                    ReloadItems();
                }
            };
            topBar.Controls.Add(clearBtn);

            Controls.Add(topBar);

            // 2. 瀑布流卡片区域
            _flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding((int)(16 * _scale)),
                BackColor = SettingsColors.PageBg
            };
            Controls.Add(_flow);

            ReloadItems();
        }

        private void ReloadItems()
        {
            _flow.SuspendLayout();
            _flow.Controls.Clear();

            string keyword = _searchBox.Text.Trim();
            var all = HistoryManager.GetItems(_store);
            int matchCount = 0;

            foreach (var item in all)
            {
                if (!string.IsNullOrEmpty(keyword))
                {
                    bool matchTime = item.Timestamp.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool matchText = !string.IsNullOrEmpty(item.OcrText) && item.OcrText.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!matchTime && !matchText) continue;
                }

                matchCount++;
                var card = CreateCard(item);
                _flow.Controls.Add(card);
            }

            _countLabel.Text = "共 " + matchCount + " 张截图记录";
            _flow.ResumeLayout();
        }

        private Control CreateCard(HistoryItem item)
        {
            int cardW = (int)(190 * _scale);
            int cardH = (int)(195 * _scale);

            var panel = new Panel
            {
                Width = cardW,
                Height = cardH,
                Margin = new Padding((int)(8 * _scale)),
                BackColor = Color.White,
                Cursor = Cursors.Hand
            };

            bool isHovered = false;
            panel.Paint += delegate (object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF rect = new RectangleF(0.5f, 0.5f, cardW - 1f, cardH - 1f);
                using (GraphicsPath path = ImageUtil.RoundedRect(rect, 12 * _scale))
                {
                    Color borderC = isHovered ? UiTheme.TelemetryBorder : SettingsColors.CardBorder;
                    float borderW = isHovered ? 1.5f : 1f;
                    using (Pen p = new Pen(borderC, borderW))
                        g.DrawPath(p, path);
                }
            };
            panel.MouseEnter += delegate { isHovered = true; panel.Invalidate(); };
            panel.MouseLeave += delegate { isHovered = false; panel.Invalidate(); };

            // 缩略图容器
            var picBox = new PictureBox
            {
                Location = new Point((int)(8 * _scale), (int)(8 * _scale)),
                Size = new Size(cardW - (int)(16 * _scale), (int)(125 * _scale)),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(243, 245, 247)
            };

            string thumb = File.Exists(item.ThumbFile) ? item.ThumbFile : item.ImageFile;
            if (File.Exists(thumb))
            {
                try
                {
                    using (var fs = new FileStream(thumb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        picBox.Image = new Bitmap(Image.FromStream(fs));
                    }
                }
                catch { }
            }

            // 右上角收藏星标小按钮
            var favBtn = new Label
            {
                AutoSize = false,
                Size = new Size((int)(26 * _scale), (int)(26 * _scale)),
                Location = new Point(picBox.Width - (int)(28 * _scale), (int)(2 * _scale)),
                BackColor = Color.FromArgb(140, 255, 255, 255),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Symbol", 12f * _scale, FontStyle.Bold),
                Text = item.IsFavorite ? "★" : "☆",
                ForeColor = item.IsFavorite ? Color.FromArgb(234, 179, 8) : Color.FromArgb(140, 145, 155),
                Cursor = Cursors.Hand
            };
            favBtn.Click += delegate
            {
                HistoryManager.ToggleFavorite(_store, item.Id);
                ReloadItems();
            };
            picBox.Controls.Add(favBtn);

            // 底部信息条
            var infoPanel = new Panel
            {
                Location = new Point((int)(8 * _scale), picBox.Bottom + (int)(4 * _scale)),
                Size = new Size(cardW - (int)(16 * _scale), (int)(50 * _scale)),
                BackColor = Color.Transparent
            };

            var timeLbl = new Label
            {
                Location = new Point(0, 0),
                Size = new Size(infoPanel.Width, (int)(18 * _scale)),
                Text = item.Timestamp,
                ForeColor = Color.FromArgb(100, 110, 125),
                Font = UiTheme.Font(9.5f * _scale)
            };

            string sizeText = item.Width + " × " + item.Height;
            if (!string.IsNullOrEmpty(item.OcrText))
            {
                string snippet = item.OcrText.Replace("\r", " ").Replace("\n", " ").Trim();
                if (snippet.Length > 12) snippet = snippet.Substring(0, 12) + "…";
                sizeText += " · " + snippet;
            }

            var sizeLbl = new Label
            {
                Location = new Point(0, (int)(20 * _scale)),
                Size = new Size(infoPanel.Width, (int)(24 * _scale)),
                Text = sizeText,
                ForeColor = Color.FromArgb(30, 41, 59),
                Font = UiTheme.Font(9.5f * _scale, FontStyle.Bold)
            };

            infoPanel.Controls.Add(timeLbl);
            infoPanel.Controls.Add(sizeLbl);

            panel.Controls.Add(picBox);
            panel.Controls.Add(infoPanel);

            // 交互操作
            Action openAction = delegate
            {
                if (File.Exists(item.ImageFile))
                {
                    try
                    {
                        Bitmap bmp;
                        using (var fs = new FileStream(item.ImageFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            bmp = new Bitmap(Image.FromStream(fs));
                        }
                        Close();
                        _onOpenInEditor?.Invoke(bmp);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "打开图片失败: " + ex.Message);
                    }
                }
            };

            picBox.DoubleClick += delegate { openAction(); };
            sizeLbl.DoubleClick += delegate { openAction(); };
            panel.DoubleClick += delegate { openAction(); };

            // 右键菜单
            ContextMenuStrip cardMenu = new ContextMenuStrip();
            cardMenu.Items.Add("在编辑器中继续标注", null, delegate { openAction(); });
            cardMenu.Items.Add("复制图片", null, delegate
            {
                if (File.Exists(item.ImageFile))
                {
                    try
                    {
                        using (var fs = new FileStream(item.ImageFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            Clipboard.SetImage(Image.FromStream(fs));
                            Toast.Show("图片已复制到剪贴板");
                        }
                    }
                    catch { }
                }
            });
            cardMenu.Items.Add(item.IsFavorite ? "取消收藏" : "收藏置顶 ★", null, delegate
            {
                HistoryManager.ToggleFavorite(_store, item.Id);
                ReloadItems();
            });

            panel.ContextMenuStrip = cardMenu;
            picBox.ContextMenuStrip = cardMenu;
            infoPanel.ContextMenuStrip = cardMenu;

            return panel;
        }
    }
}
