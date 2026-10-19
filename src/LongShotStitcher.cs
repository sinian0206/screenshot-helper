using System;
using System.Drawing;

namespace XiaoJie
{
    /// <summary>
    /// 滚动内容拼接器（长截图核心算法，可独立单测）。
    /// 用法：new LongShotStitcher(regionWidth) → 逐帧 AddFrame(frame) → TakeResult()。
    /// 负责队友：longshot。约定 SelfTest() 返回一行摘要（失败请返回以 "FAIL" 开头的说明或抛异常）。
    /// </summary>
    public sealed class LongShotStitcher
    {
        public LongShotStitcher(int width)
        {
        }

        /// <summary>已稳定拼接的帧数（第一帧成功后从 1 开始计）。</summary>
        public int AppendedFrames
        {
            get { return 0; }
        }

        /// <summary>当前累积画面（可能为 null）。</summary>
        public Bitmap Canvas
        {
            get { return null; }
        }

        /// <summary>加入一帧；内容有延伸时返回 true。</summary>
        public bool AddFrame(Bitmap frame)
        {
            return false;
        }

        /// <summary>结束并返回最终图像（由调用方负责释放）。</summary>
        public Bitmap TakeResult()
        {
            return null;
        }

        /// <summary>离线自检：返回摘要文本，失败时以 FAIL 开头或抛异常。</summary>
        public static string SelfTest()
        {
            return "SKIP（longshot 队友尚未实现）";
        }
    }
}
