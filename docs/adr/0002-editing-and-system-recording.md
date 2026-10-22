# ADR 0002：可撤销编辑、隐私审阅与系统媒体录制

状态：已采用。日期：2026-10-19。版本：1.2.0。

## 背景

用户要求实现候选打码审阅、统一对象操作、可改译文、MP4 与音频录制、长图预览纠错、多屏 DPI 与独立快捷键，并明确排除 OCR 可视化校对。录制计时条每帧隐藏/显示造成闪烁，脚本录制 8 帧时观测到 16 次可见性变化。

## 决策

1. 标注保持稳定对象身份，编辑命令保存修改前后状态。路径遮挡深复制几何；最多保存 100 条历史命令，分支编辑释放重做快照。选择、拖动、缩放、删除和译文编辑通过同一历史入口；后台打码仍以初次新增对象为一轮撤销。
2. 隐私候选使用独立本地审阅会话，记录类别和逐处选择。默认显示不透明遮挡，人工取消会修改实际输出并可撤销。后台合并不恢复已取消项；更改审阅、继续编辑、输出及关闭均取消未完成补漏。类别按 OCR 整行归属，列表只显示类别和位置，不复述敏感原文。
3. 保持当前本地 OCR 组件，不新增 OCR 校对界面。翻译继续发送编号文字；译文对象保存原位置背景覆盖与可编辑目标框，移动译文时不露出原字。原文切换与单块删除有独立编辑命令。
4. 新增 x64 C++ 桌面项目 `RecorderNative.vcxproj`，通过小型同步 P/Invoke 接口调用 Windows Media Foundation H.264/AAC 与 WASAPI。静态 CRT，不包含 ffmpeg 或独立编码器包。默认 MP4、无音频；旧配置迁移保留 AVI，录音必须显式选择来源。音频设备错误直接报告，禁止静默丢音轨。暂停冻结视频时间线并重置音频设备缓冲，编码时压缩暂停时间。
5. 控制条持续显示并按变化区域重绘；支持的系统用 `WDA_EXCLUDEFROMCAPTURE` 排除捕获。旧系统无选区外空间时只隐藏一次并说明 F8/Esc 控制。奇数 MP4 尺寸向上补齐，保留原选区全部像素。
6. 长截图保存内存 PNG 原帧 journal，支持撤回/恢复末段与接缝预览，压缩历史预算 128 MiB。错位、手动暂停及迟到帧检查都在提交前执行；撤回重建复用原拼接算法，可能随图长增加耗时。历史和截图不写临时磁盘。
7. 截图、识别框、视频始终使用物理像素；只对浮窗控件按所在屏幕 DPI 缩放。坐标转换集中于 `DisplayGeometry`，窄屏工具栏换行，设置和审阅保持全部控件可滚动到达。新增 OCR/翻译/打码独立热键和单实例消息，空字符串分别禁用，冲突不覆盖其他动作。

## 验证与限制

脚本验证真实输出像素和撤销、真实 OCR、合成多屏布局及缩放、真实桌面捕获排除、MP4 解码、系统声合成音调与暂停标记。麦克风和混合模式验证设备打开，未录制真实语音；真实多屏硬件、超长录屏及磁盘写满尚未验收。具体最终命令、数据和发布哈希见 `VERIFICATION.md`。

## 参考

- [Microsoft：SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
- [Microsoft：Media Foundation Sink Writer](https://learn.microsoft.com/en-us/windows/win32/medfound/sink-writer)
- [Microsoft：IAudioCaptureClient](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-iaudiocaptureclient)
