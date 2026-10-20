# 截图助手

截图助手是面向 Windows 的桌面截图与内容处理工具，提供截图标注、本地文字识别、原位翻译、隐私遮挡、长截图和区域录制等功能。

## 功能

### 截图与编辑

- 支持区域框选和窗口识别。
- 显示选区尺寸与鼠标位置颜色值。
- 提供矩形、椭圆、箭头、画笔、文字、序号、表情、马赛克和局部放大标注。
- 支持对象选择、移动、缩放、删除、撤销和重做。
- 支持 PNG、JPG 和剪贴板输出。
- 支持钉图、拖动、缩放和置顶。

### 本地识别与文本处理

- 支持离线文字识别，不上传截图。
- 支持长图分片识别和原图像素坐标定位。
- 支持保留换行、合并段落、清理空格和表格文本输出。
- 支持原位翻译，译文显示在对应识别区域内。
- 支持自定义敏感词、忽略词和隐私候选审阅。
- 联网能力仅使用用户配置的兼容接口。

### 导入、历史与辅助工具

- 支持图片文件、拖放和剪贴板导入。
- 支持多图横向或纵向拼接。
- 支持延时截图和上次选区复用。
- 支持可选的截图历史，包括文字检索、日期检索、收藏和容量限制。

### 长截图与录制

- 支持滚动长截图、暂停、继续、撤回和恢复捕获段。
- 支持区域录制、暂停和继续。
- 支持无音频、系统音频、麦克风和混合音频。
- 支持 MP4 输出，并兼容无音频 AVI。
- 录制前可选择本次音频来源和格式。

### 系统集成

- 支持托盘驻留、开机自启和单实例运行。
- 支持截图、录制、识别、翻译和遮挡的独立快捷键。
- 支持多显示器坐标与 DPI 映射。
- API Key 使用 Windows DPAPI 按当前用户加密保存。
- 配置优先保存在程序目录；目录不可写时保存到当前用户配置目录。

## 默认快捷键

| 操作 | 快捷键 |
| --- | --- |
| 截图 | `Alt+A` |
| 区域录制 | `Ctrl+Shift+P` |
| 文字提取 | `Alt+Shift+O` |
| 翻译 | `Alt+Shift+T` |
| 隐私遮挡 | `Alt+Shift+M` |

快捷键可在设置中修改。热键冲突或格式无效时，程序会显示当前注册状态。

## 运行要求

- Windows 10 或 Windows 11，x64。
- .NET Framework 4.8。
- 完整解压绿色版，并保留 `RecorderNative.dll` 与 `ocr` 目录。
- MP4 录制依赖系统媒体组件。

## 数据与网络

- 截图、标注和基础文字识别默认在本机完成。
- 翻译、语义补漏和可选的视觉检测需要用户明确配置接口。
- 普通文字识别不需要 API Key。
- 诊断日志不记录截图原文、译文、完整请求体或 API Key。
- 发布包不包含用户配置、历史记录或凭据。

## 构建

项目入口为 Visual Studio 解决方案 `截图助手.sln`，使用 MSBuild 构建。

```powershell
pwsh -NoProfile -File tools\fetch-ocr.ps1

pwsh -NoProfile -File build.ps1 `
  -Configuration Release `
  -OutDir build_release\app `
  -BuildDir build_release\compile `
  -Test
```

生成绿色发布包：

```powershell
pwsh -NoProfile -File package.ps1 `
  -OutDir build_release\app `
  -BuildDir build_release\compile `
  -SkipBuild
```

## 测试

```powershell
pwsh -NoProfile -File tests\ui-controls-regression.ps1
pwsh -NoProfile -File tests\ui-showcase.ps1
pwsh -NoProfile -File tests\seven-features-regression.ps1
pwsh -NoProfile -File tests\run-all.ps1 -ContinueOnFailure
```

测试范围包括工具栏交互、控件状态、DPI 渲染、OCR、录制、长截图、历史记录、配置持久化、打包和安装目录外运行。

## 文档

- [架构说明](ARCHITECTURE.md)
- [验证报告](VERIFICATION.md)
- [使用说明](docs/使用说明.md)
- [功能清单](docs/功能清单.md)

## License

Source code is available under the [MIT License](LICENSE). Third-party runtimes, models, and components remain subject to their respective licenses. See `assets/ocr-licenses` and the release package for notices.

## English

Screenshot Helper is a Windows desktop application for screenshot capture, annotation, local OCR, in-place translation, redaction review, long screenshots, and region recording.

### Features

- Region and window capture.
- Coordinates, dimensions, and color value display.
- Rectangle, ellipse, arrow, freehand, text, numbering, emoji, redaction, and loupe annotations.
- Object editing, undo, and redo.
- PNG, JPG, and clipboard output.
- Pin-to-desktop operation.
- Offline OCR with long-image tiling and pixel coordinates.
- Line-preserving, paragraph, whitespace-cleaned, and table-oriented text output.
- In-place translation.
- Custom privacy terms, ignored terms, and reviewable redaction candidates.
- File, drag-and-drop, and clipboard image import.
- Image composition.
- Delayed capture and last-region reuse.
- Optional searchable history with favorites and storage limits.
- Long screenshots and region recording.
- Optional system audio, microphone audio, or mixed audio.
- MP4 output and audio-free AVI compatibility.
- Tray operation, optional startup, single-instance forwarding, and configurable hotkeys.
- DPAPI-protected API credentials.

### Requirements

- Windows 10 or Windows 11, x64.
- .NET Framework 4.8.
- The complete portable directory, including `RecorderNative.dll` and `ocr`.
- System media components for MP4 recording.

### Build

```powershell
pwsh -NoProfile -File build.ps1 `
  -Configuration Release `
  -OutDir build_release\app `
  -BuildDir build_release\compile `
  -Test
```

### Tests

```powershell
pwsh -NoProfile -File tests\run-all.ps1 -ContinueOnFailure
```

### License

Source code is licensed under the MIT License. Third-party runtimes and models retain their original licenses.
