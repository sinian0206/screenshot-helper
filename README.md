# 微信截图助手（DeepSeek 版）

> 微信截图风格的 Windows 截图工具 + DeepSeek 文字提取 / 翻译 / 智能打码。
> .NET Framework 4.8 WinForms，单文件绿色版，版本 **1.0.0**。

面向最终用户的使用方法请阅读 **[docs/使用说明.md](docs/使用说明.md)**；
与微信 PC 截图的功能对照见 **[docs/功能清单.md](docs/功能清单.md)**；
模块划分、接口契约与协作约定见 **[ARCHITECTURE.md](ARCHITECTURE.md)**。

---

## 功能概览

- **截图编辑**：矩形、椭圆、表情贴纸、箭头、画笔、马赛克、文字、序号、颜色/线宽/字号/笔刷调节、
  撤销/重做、选区移动缩放、智能窗口识别、尺寸与色值显示（`C` 复制色值）。
- **输出**：保存 PNG/JPG、复制到剪贴板、钉在桌面（拖动/滚轮缩放/右键菜单/双击关闭）、
  转发（复制并激活微信窗口）、关闭、完成。
- **AI（DeepSeek）**：文字提取（流式 OCR）、文字翻译（原文 + 译文，目标语言可配）、
  一键智能打码（视觉模型识别隐私区域，右键马赛克按钮触发）。需要用户自己的 API Key。
- **长截图**：滚动抓帧自动拼接，结果可复制 / 保存 / 钉桌面 / 关闭。
- **系统能力**：`Alt+A` 全局热键（被占用时启动自动降级为 `Alt+Shift+A` / `Ctrl+Alt+A` 等并写入配置，也可在设置中修改）、
  托盘常驻、开机自启、单实例、首次运行引导（可跳过）、便携配置（`config.json`）、
  Windows DPAPI 加密 API Key、日志与中文错误提示。

## 技术栈

| 项 | 说明 |
| --- | --- |
| 语言 / 运行时 | C# / **.NET Framework 4.8**（Windows 10/11 系统自带，无需安装运行时） |
| UI | WinForms + GDI+，全部截图编辑器 UI 自绘；`Microsoft YaHei UI` 字体 |
| 目标框架 | `net48`，`PlatformTarget=AnyCPU`，PerMonitorV2 DPI 感知（`assets/app.manifest`） |
| 编译 | .NET SDK 10 的 `dotnet build`（项目本身 target net48） |
| 依赖 | 仅系统程序集：`System.Windows.Forms` / `System.Drawing` / `System.Web.Extensions`（JSON）/ `System.Security`（DPAPI）；**不使用 NuGet、无第三方运行时 DLL** |
| 发布形态 | 单文件 `微信截图助手.exe`，绿色免安装 |

## 目录结构

```
D:\Agents\微信截图助手\
├─ src\                         # 全部 C# 源码（见 ARCHITECTURE.md 第 3/6 节）
│  ├─ 微信截图助手.csproj        # net48 / WinForms / 图标 / 清单
│  ├─ Program.cs                # 入口、单实例、命令行、--selftest 入口
│  ├─ TrayApp.cs                # 托盘、热键、截图流程调度
│  ├─ OverlayForm.cs            # 全屏截图编辑器
│  ├─ Toolbar.cs / Annotation.cs# 自绘工具栏 / 标注模型与渲染
│  ├─ PinForm.cs                # 钉图窗口
│  ├─ AiResultPanel.cs          # AI 结果面板（OCR / 翻译）
│  ├─ SettingsForm.cs/HotkeyBox.cs
│  ├─ LongShot.cs/LongShotStitcher.cs/EmojiPicker.cs
│  ├─ Util.cs / AppConfig.cs / DeepSeekClient.cs / SelfTest.cs
├─ assets\                      # app.ico（多尺寸图标）、app.manifest、emoji\（表情素材）
├─ tools\make_icon.py           # 生成 assets/app.ico（多尺寸）
├─ tests\smoke.ps1              # 冒烟测试（构建 + 单文件/版本/图标/自检断言）
├─ build.ps1                    # 构建脚本
├─ package.ps1                  # 绿色版 zip 打包脚本
├─ docs\                        # 中文用户文档（使用说明 / 功能清单）
├─ config.example.json          # 配置字段示例（不含真实 Key）
├─ dist\                        # 发布产物（gitignore）
└─ build*\                     # 构建中间目录（gitignore）
```

## 构建

环境要求：Windows 10/11 + .NET SDK 10（`dotnet` 在 PATH 中）。不需要安装 .NET Framework SDK，系统自带的 4.8 即可。

```powershell
cd D:\Agents\微信截图助手

# 默认 Release 构建 → dist\微信截图助手.exe（只复制 exe，单文件）
pwsh -File build.ps1

# 自定义输出目录（相对路径基于项目根目录，绝对路径也支持）
pwsh -File build.ps1 -OutDir build_myname

# Debug 构建 / 清理后构建 / 构建后跑自检
pwsh -File build.ps1 -Configuration Debug -Clean
pwsh -File build.ps1 -Test
```

构建脚本会打印 exe 的**绝对路径、体积、SHA256、耗时**；`dotnet build` 失败会自动重试 1 次
（队友并行构建导致 `obj` 目录锁时常见）。脚本不做任何 git 操作。

可用参数：`-Configuration Debug|Release`（默认 Release）、`-OutDir`（默认 `dist`）、
`-BuildDir`（中间目录，默认 `build`）、`-Clean`、`-Test`。

## 自检（--selftest）

程序内置自检，输出纯文本报告，退出码 0 表示全部通过：

```powershell
.\dist\微信截图助手.exe --selftest --out .\selftest-report.txt
Get-Content .\selftest-report.txt
```

覆盖内容：配置读写、屏幕捕获、PNG 保存、Base64、热键解析、圆角绘制、JSON、API 地址拼装、
长截图拼接算法等。报告中应出现 `结果: 全部通过`，且没有 `[FAIL]` / `[FATAL]`。

## 冒烟测试

面向「最终 exe」的最小验收，可在最终验收时一条命令跑完：

```powershell
pwsh -File tests\smoke.ps1
```

脚本会在 `build_docs\smoke\` 下从零构建，然后断言：

1. 构建成功，输出目录只有 `微信截图助手.exe`（无 dll / config 等依赖）；
2. exe 体积 < 2 MB 且非空壳；
3. `FileVersionInfo` 的 ProductName 含「微信截图助手」、FileVersion 为 `1.0.0.x`；
4. exe 关联图标可提取、清单（PerMonitorV2）已嵌入；
5. `--selftest` 退出码 0，报告含「结果: 全部通过」，无 `[FAIL]` / `[FATAL]`；
6. 测试结束无残留进程。

每项输出 `[PASS]` / `[FAIL]` 并汇总，全部通过退出码 0，否则 1。构建失败（如源码正在被修改）
会等待 30 秒自动重试，最多 3 次。脚本不会修改任何源码。

## 打包绿色版

```powershell
# 构建 + 打包 → dist\微信截图助手_v1.0.0_绿色版.zip
pwsh -File package.ps1

# 复用现有 dist\微信截图助手.exe 直接打包
pwsh -File package.ps1 -SkipBuild

# 指定输出目录（exe 与 zip 都放这里，便于并行验证）
pwsh -File package.ps1 -OutDir build_docs\package
```

zip 内为扁平结构，解压即用：

```
微信截图助手.exe           # 单文件程序
使用说明.txt               # 由 docs\使用说明.md 转换的纯文本
功能清单.txt               # 由 docs\功能清单.md 转换的纯文本
config.example.json       # 配置字段示例
```

打包脚本会解压 zip 并逐个与源文件比对 SHA256，打印 zip 路径、体积、SHA256 与内容清单。

## 配置与隐私

- 配置文件：`config.json`，优先放 exe 同目录（绿色便携）；目录不可写时降级到
  `%APPDATA%\微信截图助手\config.json`；字段说明见 `config.example.json` 与 `src/AppConfig.cs`。
- API Key：使用 Windows **DPAPI（当前用户）** 加密后保存，配置文件中无明文，不能跨电脑/用户使用。
- 截图、编辑、保存、钉图、长截图全部在本机完成；仅在使用 AI 功能时把**框选区域图片**
  发送给 DeepSeek（`https://api.deepseek.com`，OpenAI 兼容接口，`deepseek-flash` 模型）。
- 无统计/埋点；日志为本地 `微信截图助手.log`，仅用于排障。
- 详细隐私与费用说明见 [docs/使用说明.md](docs/使用说明.md) 第十七节。

## 版本

当前版本 **1.0.0**（`src/微信截图助手.csproj` 的 `<Version>` / `<FileVersion>`，csproj 中 `1.0.0.0`）。
生成的 exe 文件版本信息：

- ProductName：`微信截图助手（DeepSeek 版）`
- ProductVersion：`1.0.0`（构建时已关闭源码版本号后缀，保持稳定）
- FileVersion：`1.0.0.0`

## 第三方资源与许可

- **Twemoji** 表情素材：Copyright © Twitter, Inc. and other contributors，采用
  [CC-BY 4.0](https://creativecommons.org/licenses/by/4.0/) 许可，来源与完整许可文本见
  `assets/emoji/LICENSE.txt`。如未附带表情素材，截图编辑器中的「表情」功能将不可用。
- **DeepSeek API**：第三方 AI 服务，使用需遵守 [DeepSeek 服务条款](https://platform.deepseek.com/)，
  费用由用户自行承担。
- 图标 `assets/app.ico` 由 `tools/make_icon.py`（Pillow）生成，可重新运行脚本自定义：

  ```powershell
  python tools\make_icon.py    # 需要 pip install pillow
  ```

## 免责声明

本项目为个人开发的第三方工具，**不是腾讯官方产品，与腾讯公司及微信团队无任何关系**；
DeepSeek 为第三方服务。AI 输出（OCR / 翻译 / 打码结果）可能不准确，请人工核对后使用。
本软件按「现状」提供，作者不对使用产生的损失负责。
