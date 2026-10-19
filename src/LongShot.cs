using System;
using System.Drawing;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 长截图（滚动截图）。由 OverlayForm 在用户点击“长截图”按钮后调用：
    ///   overlay.Hide();
    ///   Bitmap result = LongShot.Run(selectionScreenRect, ImageUtil.Capture);
    ///   overlay 关闭或恢复。
    /// Run 自行显示提示条、绿色边框、拼接进度，处理鼠标滚轮/键盘：
    ///   - 用户滚动窗口内容时自动抓帧拼接（纵向优先，也支持横向）；
    ///   - Enter / 点击“完成”结束并返回结果图；
    ///   - Esc / 右键 / 点击“取消”取消，返回 null。
    /// capture 回调返回指定屏幕矩形的最新画面（物理像素）。
    /// 负责队友：longshot
    /// </summary>
    public static class LongShot
    {
        public static Bitmap Run(Rectangle region, Func<Rectangle, Bitmap> capture)
        {
            Toast.Show("长截图功能尚未实现（longshot 队友负责）");
            return null;
        }
    }
}
