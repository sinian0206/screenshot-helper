using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>一个表情贴纸资源。</summary>
    public sealed class EmojiItem
    {
        public string Name;   // 稳定标识，如 "1f600"（= 资源文件名去掉扩展名）
        public string Label;  // 中文名，如 "微笑"

        private Bitmap _image;
        private bool _loaded;
        private bool _loading;

        internal string ResourceName;

        /// <summary>彩色贴纸（72x72 透明 PNG），首次访问时从嵌入资源懒加载并缓存。</summary>
        public Bitmap Image
        {
            get
            {
                EnsureImage();
                return _image;
            }
        }

        /// <summary>图片是否已成功加载。</summary>
        public bool HasImage
        {
            get { return _image != null; }
        }

        internal bool EnsureImage()
        {
            if (_loaded) return _image != null;
            if (_loading) return false;
            _loading = true;
            try
            {
                string res = ResourceName;
                if (!string.IsNullOrEmpty(res))
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    using (Stream stream = asm.GetManifestResourceStream(res))
                    {
                        if (stream != null)
                        {
                            using (Bitmap tmp = new Bitmap(stream))
                            {
                                // 深拷贝，避免 Bitmap 依赖已释放的流
                                _image = new Bitmap(tmp);
                            }
                        }
                    }
                    if (_image != null)
                    {
                        try { _image.SetResolution(96, 96); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("表情资源加载失败 " + Name + ": " + ex.Message);
                _image = null;
            }
            finally
            {
                _loaded = true;
                _loading = false;
            }
            return _image != null;
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Label) ? Name : Label;
        }
    }

    /// <summary>
    /// 表情贴纸资源库（嵌入资源 emoji.*.png，csproj 已配置 LogicalName="emoji.%(Filename)%(Extension)"）。
    /// 资源来自 Twemoji（CC-BY 4.0），详见 assets/emoji/LICENSE.txt。
    /// 负责队友：longshot（资源 + 选择器）；OverlayForm 用 Find(name) 取图绘制。
    /// </summary>
    public static class EmojiAssets
    {
        private const string ResourcePrefix = "emoji.";
        private const string ResourceSuffix = ".png";

        private static readonly object Sync = new object();
        private static IReadOnlyList<EmojiItem> _items;
        private static Dictionary<string, EmojiItem> _byName;

        // 展示顺序 + 中文名（码点文件名 → 中文名）
        private static readonly string[,] PreferredOrder = new string[,]
        {
            { "1f600", "微笑" }, { "1f602", "笑哭" }, { "1f60a", "害羞" }, { "1f60d", "花痴" },
            { "1f61c", "调皮" }, { "1f914", "思考" }, { "1f644", "无语" }, { "1f62d", "大哭" },
            { "1f621", "生气" }, { "1f631", "惊恐" }, { "1f633", "脸红" }, { "1f60e", "酷" },
            { "1f929", "星星眼" }, { "1f970", "爱心眼" }, { "1f44d", "赞" }, { "1f44e", "踩" },
            { "1f44c", "OK" }, { "1f64f", "抱拳" }, { "1f4aa", "肌肉" }, { "1f91d", "握手" },
            { "1f525", "火" }, { "2b50", "星" }, { "2764", "红心" }, { "1f389", "礼花" },
            { "1f381", "礼物" }, { "1f339", "玫瑰" }, { "2615", "咖啡" }, { "1f436", "狗" },
            { "1f431", "猫" }, { "1f4a9", "便便" }, { "1f680", "火箭" }, { "1f4a1", "灯泡" },
            { "2705", "对勾" }, { "274c", "叉" }, { "2757", "感叹" }, { "2753", "问号" },
            { "1f4cc", "图钉" }, { "1f4f7", "相机" }, { "1f3af", "靶心" }, { "1f4b0", "钱" }
        };

        /// <summary>全部表情贴纸（按常用顺序，懒加载图片）。</summary>
        public static IReadOnlyList<EmojiItem> Items
        {
            get
            {
                EnsureItems();
                return _items;
            }
        }

        /// <summary>表情数量。</summary>
        public static int Count
        {
            get { return Items.Count; }
        }

        /// <summary>按名称查找表情（不区分大小写），找不到返回 null。</summary>
        public static EmojiItem Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            EnsureItems();
            EmojiItem item;
            if (_byName != null && _byName.TryGetValue(name, out item)) return item;
            return null;
        }

        /// <summary>首次加载时扫描嵌入资源；重复调用无副作用。</summary>
        public static void EnsureItems()
        {
            if (_items != null) return;
            lock (Sync)
            {
                if (_items != null) return;
                List<EmojiItem> items = new List<EmojiItem>();
                Dictionary<string, EmojiItem> byName = new Dictionary<string, EmojiItem>(StringComparer.OrdinalIgnoreCase);

                Dictionary<string, string> resources = DiscoverResources();
                HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 先按常用顺序加入资源确实存在的表情
                for (int i = 0; i < PreferredOrder.GetLength(0); i++)
                {
                    string code = PreferredOrder[i, 0];
                    string label = PreferredOrder[i, 1];
                    string resourceName;
                    if (!resources.TryGetValue(code, out resourceName)) continue;
                    if (used.Contains(code)) continue;
                    used.Add(code);
                    EmojiItem item = new EmojiItem();
                    item.Name = code;
                    item.Label = label;
                    item.ResourceName = resourceName;
                    items.Add(item);
                    byName[code] = item;
                }

                // 再有额外资源也一并加入（中文名缺失时用码点兜底）
                foreach (KeyValuePair<string, string> pair in resources)
                {
                    if (used.Contains(pair.Key)) continue;
                    EmojiItem item = new EmojiItem();
                    item.Name = pair.Key;
                    item.Label = pair.Key;
                    item.ResourceName = pair.Value;
                    items.Add(item);
                    byName[pair.Key] = item;
                }

                _byName = byName;
                _items = items.AsReadOnly();
                if (items.Count == 0)
                    Log.Write("未在程序集中找到表情资源（emoji.*.png），请检查 assets/emoji 是否已配置嵌入");
            }
        }

        private static Dictionary<string, string> DiscoverResources()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string[] names = asm.GetManifestResourceNames();
                for (int i = 0; i < names.Length; i++)
                {
                    string name = names[i];
                    if (name == null) continue;
                    if (!name.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    string code = name.Substring(ResourcePrefix.Length,
                        name.Length - ResourcePrefix.Length - ResourceSuffix.Length);
                    if (code.Length == 0 || code.IndexOf('.') >= 0) continue;
                    if (!result.ContainsKey(code)) result[code] = name;
                }
            }
            catch (Exception ex)
            {
                Log.Write("扫描表情嵌入资源失败: " + ex.Message);
            }
            return result;
        }
    }

    /// <summary>
    /// 表情选择器：在指定屏幕位置弹出网格面板，选中后回调 onPicked(emojiName)；取消/关闭回调 onPicked(null)。
    /// 白底圆角卡片、8 列网格、悬停高亮、ToolTip 中文名；不阻塞调用方（modeless），关闭/取消时回调整理。
    /// 负责队友：longshot
    /// </summary>
    public static class EmojiPicker
    {
        private static EmojiPickerForm _current;

        /// <summary>在 screenPoint 处弹出表情选择器。modeless：立即返回，结果通过 onPicked 回调。</summary>
        public static void Show(Point screenPoint, Action<string> onPicked)
        {
            try
            {
                EmojiPickerForm old = _current;
                _current = null;
                if (old != null && !old.IsDisposed)
                {
                    try { old.CloseSilently(); } catch { }
                }

                IReadOnlyList<EmojiItem> items = EmojiAssets.Items;
                if (items == null || items.Count == 0)
                {
                    Toast.Show("表情资源缺失，请检查安装包是否完整");
                    if (onPicked != null) onPicked(null);
                    return;
                }

                EmojiPickerForm form = new EmojiPickerForm(items, screenPoint, onPicked);
                _current = form;
                form.FormClosed += delegate
                {
                    if (ReferenceEquals(_current, form)) _current = null;
                };
                form.Show();
            }
            catch (Exception ex)
            {
                Log.Write("打开表情选择器失败: " + ex);
                Toast.Show("表情选择器打开失败：" + ex.Message);
                if (onPicked != null) onPicked(null);
            }
        }

        /// <summary>关闭当前选择器（例如编辑器关闭时调用），不会触发回调。</summary>
        public static void CloseCurrent()
        {
            EmojiPickerForm form = _current;
            _current = null;
            if (form != null && !form.IsDisposed)
            {
                try { form.CloseSilently(); } catch { }
            }
        }
    }

    /// <summary>表情选择器窗口（无边框圆角卡片，8 列网格）。</summary>
    internal sealed class EmojiPickerForm : Form
    {
        private const int Columns = 8;

        private readonly IReadOnlyList<EmojiItem> _items;
        private readonly Action<string> _onPicked;
        private readonly float _scale;
        private readonly int _cell;
        private readonly int _iconSize;
        private readonly int _pad;
        private readonly int _titleHeight;
        private readonly ToolTip _toolTip;

        private int _hoverIndex = -1;
        private bool _callbackDone;

        public EmojiPickerForm(IReadOnlyList<EmojiItem> items, Point screenPoint, Action<string> onPicked)
        {
            _items = items;
            _onPicked = onPicked;
            _scale = DpiUtil.ScaleAtPoint(screenPoint);
            if (_scale <= 0.5f || _scale > 4f) _scale = 1f;

            _cell = S(56);
            _iconSize = S(40);
            _pad = S(12);
            _titleHeight = S(36);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            int rows = (items.Count + Columns - 1) / Columns;
            if (rows < 1) rows = 1;
            Size = new Size(_pad * 2 + Columns * _cell, _titleHeight + _pad / 2 + rows * _cell + _pad * 2 / 3);
            using (GraphicsPath path = ImageUtil.RoundedRect(new RectangleF(0, 0, Width, Height), S(10f)))
            {
                Region = new Region(path);
            }

            _toolTip = new ToolTip();
            _toolTip.InitialDelay = Math.Max(200, S(250));
            _toolTip.AutoPopDelay = 6000;
            _toolTip.ShowAlways = true;

            Point location = ComputeLocation(screenPoint);
            Location = location;
        }

        private int S(float v)
        {
            return (int)Math.Round(v * _scale);
        }

        private Point ComputeLocation(Point screenPoint)
        {
            Rectangle work = Screen.FromPoint(screenPoint).WorkingArea;
            int gap = S(6);
            int x = screenPoint.X + gap;
            int y = screenPoint.Y + gap;
            if (x + Width > work.Right) x = screenPoint.X - Width - gap;
            if (y + Height > work.Bottom) y = work.Bottom - Height;
            if (x < work.Left) x = work.Left;
            if (y < work.Top) y = work.Top;
            return new Point(x, y);
        }

        protected override bool ShowWithoutActivation
        {
            get { return false; } // 需要获得焦点以支持 Esc / 失焦关闭
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                Activate();
                BringToFront();
            }
            catch { }
            Invalidate();
        }

        private Rectangle CellRect(int index)
        {
            int col = index % Columns;
            int row = index / Columns;
            int gridTop = _titleHeight + _pad / 2;
            return new Rectangle(_pad + col * _cell, gridTop + row * _cell, _cell, _cell);
        }

        private int HitTest(Point point)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (CellRect(i).Contains(point)) return i;
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Color.White)) g.FillRectangle(bg, ClientRectangle);
            ImageUtil.DrawRounded(g, UiTheme.ToolbarBorder, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), S(10f), 1f);

            using (Font titleFont = UiTheme.Font(S(14f), FontStyle.Bold))
                TextRenderer.DrawText(g, "表情", titleFont, new Rectangle(_pad, 0, Width - _pad * 2, _titleHeight),
                    UiTheme.TextDark, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            using (Font hintFont = UiTheme.Font(S(11f)))
                TextRenderer.DrawText(g, "Esc 关闭", hintFont, new Rectangle(_pad, 0, Width - _pad * 2, _titleHeight),
                    UiTheme.TextGray, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            using (Pen line = new Pen(UiTheme.ToolbarBorder))
                g.DrawLine(line, _pad, _titleHeight - 1, Width - _pad, _titleHeight - 1);

            for (int i = 0; i < _items.Count; i++)
            {
                Rectangle cell = CellRect(i);
                if (i == _hoverIndex)
                {
                    Rectangle hover = cell;
                    hover.Inflate(-S(2), -S(2));
                    using (GraphicsPath path = ImageUtil.RoundedRect(hover, S(6f)))
                    using (SolidBrush brush = new SolidBrush(UiTheme.GreenLight))
                        g.FillPath(brush, path);
                }
                Bitmap image = _items[i].Image;
                if (image != null && image.Width > 0 && image.Height > 0)
                {
                    int icon = _iconSize;
                    int x = cell.X + (cell.Width - icon) / 2;
                    int y = cell.Y + (cell.Height - icon) / 2;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(image, new Rectangle(x, y, icon, icon));
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = HitTest(e.Location);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
                if (index >= 0 && index < _items.Count)
                {
                    string label = _items[index].Label;
                    if (string.IsNullOrEmpty(label)) label = _items[index].Name;
                    _toolTip.SetToolTip(this, label);
                }
                else
                {
                    _toolTip.SetToolTip(this, "");
                }
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                _toolTip.SetToolTip(this, "");
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int index = HitTest(e.Location);
            if (index >= 0 && index < _items.Count) Pick(_items[index].Name);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // 点击外部 / 焦点回到编辑器：按取消处理
            if (!_callbackDone) Cancel();
        }

        private void Pick(string name)
        {
            if (_callbackDone) return;
            _callbackDone = true;

            try
            {
                if (_onPicked != null) _onPicked(name);
            }
            catch (Exception ex)
            {
                Log.Write("表情选择回调异常: " + ex.Message);
            }
            Close();
        }

        private void Cancel()
        {
            if (_callbackDone) return;
            _callbackDone = true;

            try
            {
                if (_onPicked != null) _onPicked(null);
            }
            catch (Exception ex)
            {
                Log.Write("表情选择取消失败: " + ex.Message);
            }
            Close();
        }

        /// <summary>外部静默关闭（不触发回调），用于重复打开/编辑器关闭。</summary>
        internal void CloseSilently()
        {
            _callbackDone = true;

            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (!_callbackDone)
            {
                _callbackDone = true;
                try
                {
                    if (_onPicked != null) _onPicked(null);
                }
                catch { }
            }
            try { _toolTip.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _toolTip.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
