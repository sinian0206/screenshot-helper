# 架构说明

项目使用 Windows 原生桌面技术栈，入口为 `截图助手.sln`。主程序是 C# / .NET Framework 4.8 / WinForms；录屏桥接组件是 x64 C++；本地文字识别通过独立子进程运行。

## 模块

| 模块 | 责任 |
| --- | --- |
| `Program`、`TrayApp` | 单实例、托盘、全局热键、启动参数和窗口协调 |
| `OverlayForm`、`OverlayEditing`、`AnnotationEditing` | 选区、标注对象、编辑命令和输出合成 |
| `Toolbar`、`UiTheme`、`SettingsSkin` | 冷白浮层视觉系统、图标和控件状态 |
| `LocalOcr`、`LocalTextPipeline` | 离线识别进程、坐标转换、切片与文本关联 |
| 文本接口客户端 | 兼容聊天补全接口的翻译与文本分类能力 |
| `PrivacyRules`、`PrivacyReview` | 本地规则、候选合并与人工审阅 |
| `LongShot`、`LongShotStitcher` | 滚动信号、捕获段和图像拼接 |
| `ScreenRecorder`、`AviWriter`、`native/RecorderNative` | 录制时间线、音频、MP4 与 AVI 输出 |
| `HistoryManager`、`HistoryForm` | 历史索引、容量修剪、检索和收藏 |
| `ImageImport`、`ImageComposer` | 外部图片解码、剪贴板导入和多图拼接 |

## 配置与隐私

- `AppConfig` 是内存中的配置模型；`ConfigStore` 负责便携目录选择、迁移和 JSON 持久化。
- API Key 通过 Windows DPAPI 按当前用户加密。
- 普通文字提取、规则命中与基础截图不需要联网。
- 翻译、语义补漏和可选的视觉检测只使用设置中的兼容接口。
- 截图原文、译文、完整请求体和凭据不写诊断日志。
- 分发脚本不包含实际 `config.json` 或用户凭据。

## 视觉系统

- 背景：`#F5F5F7`。
- 浮层与卡片：白色或近白半透明色。
- 文字：深灰主文案与中性灰辅助文案。
- 强调色：系统蓝，仅用于选中、聚焦、链接和主操作。
- 图标：深灰线性图标；选中工具使用浅蓝底和蓝图标。
- 几何：工具栏 16px 左右圆角，卡片 14–18px 圆角，输入框 8–10px 圆角。
- 选区：白色外轨、系统蓝内轨和圆形控制点。

视觉常量集中在 `UiTheme` 与 `SettingsColors`，避免各窗口独立硬编码。

## 测试边界

- `tests/ui-controls-regression.ps1` 验证工具栏全部点击、切换、右键事件、禁用态和 DPI 渲染。
- `tests/ui-showcase.ps1` 使用无头浏览器执行交互矩阵，并在桌面与窄视口生成截图。
- `tests/seven-features-regression.ps1` 覆盖历史、自定义规则、OCR 格式、拼图和放大标注。
- `tests/run-all.ps1` 协调 OCR、编辑器、录制、长截图、对比度、设置、打包和冒烟回归。
- 真实外网、真实多屏混合 DPI、全部音频设备和超长录制仍需在对应硬件与账号条件下单独验收。

## 构建约束

- 以 Visual Studio 解决方案和 MSBuild 为入口。
- 复用已有工具链与 `C:\Dev` 依赖。
- 绿色包必须保留 `RecorderNative.dll` 和 `ocr` 目录。
- 模型和运行时文件使用固定哈希清单校验。
