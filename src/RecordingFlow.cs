using System;
using System.Drawing;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>记录准备阶段是否得到用户确认，区分取消准备与已开始后的录制失败。</summary>
    internal sealed class RecordingFlowResult
    {
        internal bool Started { get; private set; }
        internal string SavePath { get; private set; }

        internal RecordingFlowResult(bool started, string savePath)
        {
            Started = started;
            SavePath = savePath;
        }
    }

    /// <summary>把参数准备与媒体会话隔开，确认前不会创建文件或打开音频设备。</summary>
    internal static class RecordingFlow
    {
        internal static RecordingFlowResult Run(Rectangle region, AppConfig config, IWin32Window owner)
        {
            return Run(region, config, owner, ScreenRecorder.RecordRegion);
        }

        internal static RecordingFlowResult Run(Rectangle region, AppConfig config, IWin32Window owner,
            Func<Rectangle, AppConfig, string> record)
        {
            if (record == null) throw new ArgumentNullException("record");
            AppConfig sessionConfig;
            using (RecordReadyForm ready = new RecordReadyForm(region, config ?? new AppConfig()))
            {
                DialogResult result = owner == null ? ready.ShowDialog() : ready.ShowDialog(owner);
                if (result != DialogResult.OK) return new RecordingFlowResult(false, null);
                sessionConfig = ready.SelectedConfig;
                // 隐藏并销毁准备窗口后才进入媒体会话，准备窗口不会进入第一帧。
                ready.Hide();
            }
            try { return new RecordingFlowResult(true, record(region, sessionConfig)); }
            catch (Exception error)
            {
                // 已确认的会话异常不能返回准备态，否则用户会误认为捕获尚未开始。
                Log.Write("录制会话启动或执行失败: " + error);
                Toast.Show("录制失败：" + error.Message, 4500);
                return new RecordingFlowResult(true, null);
            }
        }
    }
}
