using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>一个表情贴纸资源。</summary>
    public sealed class EmojiItem
    {
        public string Name;   // 稳定标识，如 "smile"
        public string Label;  // 中文名，如 "微笑"
        public Bitmap Image;  // 彩色贴纸，建议 64x64 透明 PNG
    }

    /// <summary>
    /// 表情贴纸资源库（嵌入资源 emoji.*.png）。
    /// 负责队友：longshot（资源 + 选择器）；OverlayForm 用 Find(name) 取图绘制。
    /// </summary>
    public static class EmojiAssets
    {
        public static IReadOnlyList<EmojiItem> Items
        {
            get { return new List<EmojiItem>(); }
        }

        public static EmojiItem Find(string name)
        {
            return null;
        }
    }

    /// <summary>
    /// 表情选择器：在指定屏幕位置弹出网格面板，选中后回调 onPicked(emojiName)；取消/关闭回调 onPicked(null)。
    /// 负责队友：longshot
    /// </summary>
    public static class EmojiPicker
    {
        public static void Show(Point screenPoint, Action<string> onPicked)
        {
            Toast.Show("表情选择器尚未实现（longshot 队友负责）");
            if (onPicked != null) onPicked(null);
        }
    }
}
