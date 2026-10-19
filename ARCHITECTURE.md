# 微信截图助手（DeepSeek 版）— 架构与协作说明

> 绿色免安装 Windows 截图工具：微信截图（Alt+A）风格编辑 + DeepSeek 文字提取 / 翻译 / 智能打码。
> 本文档由 Lead 维护，队友只读。**不要修改本文件。** 接口有变更需求请先消息 Lead。

## 1. 产品目标

- 复刻微信 PC 截图的完整编辑体验（矩形、椭圆、表情、箭头、画笔、马赛克、文字、翻译、文字提取、长截图、撤销、保存、钉在桌面、转发、关闭、完成），并加入微信近期能力：一键智能打码、截图时显示色值、选区像素尺寸。
- AI 能力接 DeepSeek：文字提取（OCR）、文字翻译、一键打码（视觉模型找隐私区域）。
- 全局热键 `Alt+A`（可配置）、托盘常驻、开机自启、单实例。
- 绿色便携：单个 exe（目标 < 1.5 MB），无安装、无额外 DLL；配置保存在 exe 同目录 `config.json`（不可写时降级 `%APPDATA%\微信截图助手\`）；API Key 用 Windows DPAPI 加密保存。
- 中文界面，适合普通用户；首次运行引导填写 API Key 等基本配置。

## 2. 技术栈与硬约束

- C# / **.NET Framework 4.8**（Windows 10/11 系统自带，无需安装运行时）。
- WinForms + GDI+，全部自绘截图编辑器 UI；`dotnet build`（SDK 10）编译，`net48` 目标。
- 单文件输出：`dist\微信截图助手.exe`。**不引入 NuGet 包、不增加运行时 DLL、不新建其他项目**。
- 现有引用：System.Windows.Forms / System.Drawing / System.Web.Extensions（JSON）/ System.Security（DPAPI）。不要随意改 csproj。
- 语言版本 latest（C# 现代语法可用）；不要用 `Nullable`、`ImplicitUsings`。
- 平台：Windows 11 家庭中文版，2560×1440 @100% 缩放，单显示器；但代码必须兼容多显示器/高 DPI（物理像素坐标系）。
- 进程是 PerMonitorV2 DPI 感知（见 assets/app.manifest）；截图/覆盖层全部使用**物理像素**坐标。
- UI 字符串全部中文；不要出现“待实现/TODO”等字样。
- 不要联网下载依赖（表情 PNG 资源由 longshot 负责一次性下载并提交到 assets/）。
- **不要在测试中留下运行中的进程**（结束方式：`Get-Process 微信截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force`）。

## 3. 目录结构与文件归属

```
D:\Agents\微信截图助手\
├─ src\微信截图助手.csproj      Lead
├─ src\Program.cs               Lead（入口、单实例、命令行、自检入口）
├─ src\TrayApp.cs               Lead（托盘、热键、截图流程调度、设置入口）
├─ src\OverlayForm.cs           Lead（全屏截图编辑器）
├─ src\Toolbar.cs               Lead（自绘工具栏 + 矢量图标）
├─ src\Annotation.cs            Lead（标注模型/渲染/撤销栈/马赛克）
├─ src\PinForm.cs               Lead（钉在桌面的贴图窗口）
├─ src\Util.cs                  Lead（P/Invoke、DPI、图像、剪贴板、热键、自启、Toast、日志）
├─ src\AppConfig.cs             Lead（配置 + ConfigStore + DPAPI）
├─ src\DeepSeekClient.cs        Lead（DeepSeek API：OCR/翻译/隐私检测/测试连接）
├─ src\SelfTest.cs              Lead（--selftest 自检框架）
├─ src\AiResultPanel.cs         ai-ui（AI 结果面板，嵌入编辑器）
├─ src\SettingsForm.cs          ai-ui（设置窗口）
├─ src\HotkeyBox.cs             ai-ui（热键录入控件）
├─ src\LongShot.cs              longshot（长截图交互）
├─ src/LongShotStitcher.cs      longshot（拼接算法，可单测）
├─ src/EmojiPicker.cs           longshot（EmojiAssets 表情资源 + 选择器）
├─ assets\app.ico / app.manifest  build-docs（图标可美化，manifest 不要动）
├─ assets\emoji\*.png           longshot（Twemoji 彩色贴纸 + LICENSE.txt）
├─ build.ps1                    build-docs（Lead 有初版）
├─ package.ps1                  build-docs（打包 zip）
├─ tests\smoke.ps1              build-docs（冒烟测试）
├─ docs\使用说明.md              build-docs（用户手册）
├─ docs\功能清单.md              build-docs（功能对照表）
├─ README.md                    build-docs（开发说明）
└─ ARCHITECTURE.md              Lead（本文件）
```

**只改自己名下的文件。** 需要新增文件时，用自己前缀命名并先消息 Lead 说明。共享文件（Util/AppConfig/DeepSeekClient）只读，接口不够用时消息 Lead，不要自己改。

## 4. 构建与测试

```powershell
cd D:\Agents\微信截图助手
# 构建（输出到自己的目录，避免与他人冲突；不要写 dist\）
pwsh -File build.ps1 -OutDir build_<你的名字>
# 冒烟/自检
.\build_<你的名字>\微信截图助手.exe --selftest --out .\build_<你的名字>\selftest.txt
# 清理测试进程
Get-Process 微信截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
```

- 构建偶发失败可能因为队友同时构建（obj 目录锁）：等 30~60 秒重试，别乱改代码。
- `--selftest` 输出纯文本报告，退出码 0 = 全部通过；报告里有 `[FAIL]` 要修。
- 自检目前覆盖：配置读写、屏幕捕获、PNG 保存、Base64、热键解析、圆角绘制、JSON、API 地址拼装、长截图拼接算法 `LongShotStitcher.SelfTest()`。
- 真实 UI 验证：可 `Start-Process .\build_xxx\微信截图助手.exe`，用 PowerShell 截屏 + `read_image` 工具看效果，测完关掉进程。
- 本机**没有配置 DeepSeek API Key**；ai-ui 测试连接/流式显示请用本地 mock HTTP 服务（Python/PowerShell HttpListener 返回 OpenAI 兼容 JSON/SSE），把设置里的 BaseUrl 临时改成 `http://127.0.0.1:端口`，测完删除 config.json 恢复。

## 5. DeepSeek API 要点（2026-10 官方文档）

- Base URL：`https://api.deepseek.com`；接口：`POST /chat/completions`（OpenAI 兼容）。
- 模型：`deepseek-flash`（**支持图像理解**，V4.1 Flash）；`deepseek-v4-pro`（纯文本）。
- 鉴权：`Authorization: Bearer <API_KEY>`。
- 关闭思考模式（OCR/翻译要求低延迟）：请求体加 `"thinking": {"type": "disabled"}`（仅 DeepSeek 官方端点，DeepSeekClient 已按 host 自动处理）。
- 图片输入：`content` 为数组，`{"type":"image_url","image_url":{"url":"data:image/png;base64,...","detail":"original"}}`；格式 JPEG/PNG/GIF/WebP；单图 ≤32 MiB；单边 ≤8192 px；图片 token 上限 1024/张；请求体 48 MiB。
- 流式：`"stream": true`，SSE：`data: {...}` 行、保持连接空行、`data: [DONE]` 结束；`stream_options.include_usage=true` 时最后一块带 usage。
- 非流式响应：`choices[0].message.content`；错误：400/401/402/422/429/500/503，响应体 `{"error":{"message":...}}`。
- 价格（官方 2026-10，可能变动）：deepseek-flash 输入缓存未命中 空闲 1 元/百万 tokens、高峰 2 元；输出 空闲 4 元、高峰 8 元；空闲/高峰见官方说明。一张截图视觉输入 ≤1024 tokens，OCR/翻译单次约几厘～几分钱。
- 文档：https://api-docs.deepseek.com/zh-cn/guides/vision/ （图像理解）

## 6. 模块契约（公开接口，勿改签名）

### 6.1 DeepSeekClient（Lead 已实现，src/DeepSeekClient.cs）

```csharp
public sealed class DeepSeekClient {
    public DeepSeekClient(AppConfig cfg);
    public AppConfig Config { get; }
    public bool HasApiKey { get; }
    public string Endpoint { get; }
    public void CancelAll();
    public Task<AiUsage> TestConnectionAsync(CancellationToken ct);   // 失败抛 AiException(中文消息)
    // OCR：onDelta 为流式增量（后台线程回调！UI 需 BeginInvoke）
    public Task<string> OcrAsync(Bitmap image, Action<string> onDelta, Action<AiUsage> onUsage, CancellationToken ct);
    // 先 OCR 再翻译；返回 [原文, 译文]
    public Task<string[]> TranslateImageAsync(Bitmap image, string targetLang,
        Action<string> onOriginalDelta, Action<string> onTranslationDelta, Action<AiUsage> onUsage, CancellationToken ct);
    public Task<string> TranslateTextAsync(string text, string targetLang, Action<string> onDelta, Action<AiUsage> onUsage, CancellationToken ct);
    public Task<List<PrivacyRegion>> DetectPrivacyAsync(Bitmap image, CancellationToken ct);
}
// AiUsage: PromptTokens/CompletionTokens/TotalTokens/ElapsedMs, ToString() 已格式化
// AiException: HttpStatus, Message 为可直接展示的中文
```

注意：`onDelta` 在后台线程调用；必须在 UI 线程 `BeginInvoke` 更新控件，且更新前判断 `IsDisposed/IsHandleCreated`。取消用 `CancellationTokenSource`，面板关闭时取消。

### 6.2 AiResultPanel（ai-ui 实现）

```csharp
public enum AiPanelMode { Ocr, Translate }
public sealed class AiResultPanel : Panel {
    public AiResultPanel(Bitmap aiImage, AiPanelMode mode, AppConfig cfg, DeepSeekClient client, float uiScale);
    public event EventHandler CloseRequested;
    public void Start();       // 加到父容器后调用一次，启动请求
    public void CancelWork();  // 父容器在移除/关闭前调用
}
```
- 尺寸自定（约 520×560 × uiScale），由 OverlayForm 负责定位/加入 Controls。面板是编辑器的子控件，不是独立窗口。
- 自绘白色圆角卡片（1px #E2E2E2 边框）；标题「文字提取」/「文字翻译」；右上关闭按钮触发 CloseRequested；主体只读可复制文本框；底部复制按钮 + 状态/用量；加载中动画；失败显示中文错误 + 重试按钮。
- 翻译模式：上下两块「原文」「译文」，流式填充；复制原文/译文/全部。
- 复制用 `ClipUtil.CopyText` + `Toast.Show("已复制")`。
- 样式用 `UiTheme` 颜色/`UiTheme.Font(px)`；不得出现英文占位符。

### 6.3 SettingsForm（ai-ui 实现）

```csharp
public sealed class SettingsForm : Form {
    public SettingsForm(AppConfig cfg, ConfigStore store);
    public SettingsForm(AppConfig cfg, ConfigStore store, bool firstRun);
    public event EventHandler Saved;   // 保存成功后触发
}
```
- 编辑内部副本，点「确定」时：校验 → `Autostart.SetEnabled(cfg.StartWithWindows)` → `store.Save(cfg)` → 写回传入 cfg（`cfg.CopyFrom(working)`）→ 触发 Saved → DialogResult.OK。
- 「取消」不保存。
- 必含配置项见第 1 节/README；测试连接用当前输入框值构造临时 `DeepSeekClient` + `TestConnectionAsync`。
- 首次运行（firstRun=true）显示欢迎引导；API Key 解密失败时提示重新填写。
- 控件中文标签，字体 `Microsoft YaHei UI` 9pt，窗口约 720×640，固定大小、居中。

### 6.4 HotkeyBox（ai-ui 实现）

```csharp
public sealed class HotkeyBox : Control {
    public string HotkeyText { get; set; }   // 规范格式 "Ctrl+Alt+A"
    public event EventHandler HotkeyChanged;
}
```
- 点击进入录制：按下组合键完成（仅修饰键无效；纯字母/数字要求带修饰键；允许 F1~F24 单键）；Esc 取消录制/清空；Delete/Backspace 清空；聚焦时画绿色边框；结果通过 `HotkeyUtil.Format/TryParse` 规范化。

### 6.5 LongShot（longshot 实现）

```csharp
public static class LongShot {
    // 调用前由 OverlayForm 隐藏自己；region 为选区屏幕物理像素矩形
    // capture 返回该矩形当前画面；返回拼接图（调用方释放），取消返回 null
    public static Bitmap Run(Rectangle region, Func<Rectangle, Bitmap> capture);
}
public sealed class LongShotStitcher {
    public LongShotStitcher(int width);
    public int AppendedFrames { get; }
    public Bitmap Canvas { get; }
    public bool AddFrame(Bitmap frame);     // 内容有延伸返回 true
    public Bitmap TakeResult();             // 调用方释放
    public static string SelfTest();        // --selftest 调用；失败返回 FAIL 开头或抛异常
}
```
- 交互：不要遮挡选区内鼠标/滚轮（用边框线/外部控制条/透明点击穿透窗口）；鼠标滚轮或键盘滚动时自动抓帧拼接；Enter/完成按钮结束，Esc/取消按钮取消；显示进度与提示；纵向优先，兼容横向；单帧无滚动时返回该帧。
- 拼接算法必须带自检（合成大图切重叠帧 → 还原一致）。

### 6.6 EmojiPicker / EmojiAssets（longshot 实现）

```csharp
public sealed class EmojiItem { public string Name; public string Label; public Bitmap Image; }
public static class EmojiAssets {
    public static IReadOnlyList<EmojiItem> Items;
    public static EmojiItem Find(string name);
}
public static class EmojiPicker {
    public static void Show(Point screenPoint, Action<string> onPicked); // 取消时 onPicked(null)
}
```
- 资源：`assets/emoji/<文件名>.png`，csproj 以 `LogicalName="emoji.<文件名>.png"` 嵌入，加载用 `Assembly.GetManifestResourceStream("emoji."+文件名+".png")`（文件名可用 unicode 码点，如 `1f600.png`；如需中文名映射在 EmojiAssets 里维护）。
- 资源来源建议 Twemoji（CC-BY 4.0），在 `assets/emoji/LICENSE.txt` 写清来源与许可；40 个左右常用表情即可。
- 选择器：白底圆角、8 列网格、悬停高亮、点击回调后关闭、Esc/失焦关闭、按屏幕边界调整位置。

## 7. 截图编辑器交互约定（Lead 实现，供队友理解上下文）

- 热键触发 → 先隐藏自身窗口 → 抓全屏（物理像素）→ 显示全屏 OverlayForm（TopMost、无边框、覆盖虚拟屏幕、双缓冲）。
- 选区：拖拽框选；Hover 智能识别窗口点击选中；8 个绿色手柄可调整；拖动选区内部移动；Esc/右键取消。
- 选区左上角显示「宽 × 高」绿色尺寸；选择阶段鼠标旁显示 `#RRGGBB` 色值（按 C 复制）。
- 工具栏：白色圆角条，16 个图标（矩形/椭圆/表情/箭头/画笔/马赛克/文字/翻译/文字提取/长截图 | 撤销/保存/钉桌面/转发/关闭/完成）；选中工具绿色高亮；悬停灰底；图标全部 GDI+ 矢量绘制。
- 工具快捷键：R/O/E/A/P/M/T/N；Ctrl+Z 撤销，Ctrl+Y 重做，Ctrl+C 完成（复制），Ctrl+S 保存，Enter 完成，Esc 取消。
- 马赛克：右键马赛克按钮 → 「一键智能打码」（DeepSeek 视觉找隐私区域）。颜色/线宽：右键形状类工具按钮弹菜单。文字：点击输入，Enter 确认、Esc 取消，Ctrl+滚轮调字号。
- 完成：复制到剪贴板（cfg.AutoCopy）+ 可选保存（cfg.AutoSave）+ 关闭；保存：按 cfg.SaveDir/格式保存并关闭；钉桌面：PinForm；转发：复制 + 尝试激活微信窗口；关闭：放弃。
- OCR/翻译：合成选区图（含标注）→ 在编辑器内显示 AiResultPanel（放在选区右侧/下方，不要遮挡选区）→ 完成后可继续编辑。
- 长截图：隐藏编辑器 → `LongShot.Run(选区屏幕矩形, ImageUtil.Capture)` → 结果用独立结果窗口提供「复制/保存/钉桌面/关闭」。
- 一键打码：PrivacyRegion 坐标是 0~1 相对比例，换算成屏幕坐标后加为矩形马赛克标注；无区域则 Toast 提示。

## 8. 视觉规范（WeChat 风格）

- 主色 `#07C160`（UiTheme.Green）；危险/关闭 `#FA5151`；工具栏白底 `#FFFFFF`，边框 `#E2E2E2`，图标 `#484848`，悬停底 `#F2F2F2`，选中底 `#E8F8F0` 选中图标绿色。
- 圆角：工具栏 8~10px；面板 12px；字体 `Microsoft YaHei UI`；Toast 深灰底白字。
- 截图遮罩：选区外 45% 黑色；选区边框 2px 绿色；手柄 7px 绿色方块；尺寸文字绿色加轻微阴影。
- 所有 UI 尺寸乘以 `uiScale`（= 选区所在显示器 DPI/96，100% 时 = 1）。

## 9. 功能清单（验收目标）

基础编辑：矩形、椭圆、表情贴纸、箭头、画笔、马赛克笔刷、文字、序号标注、撤销/重做、颜色/线宽选择、移动/缩放选区、智能窗口识别、尺寸显示、色值显示与复制。
工具条：保存 PNG/JPG、复制、钉在桌面（拖动/滚轮缩放/右键菜单）、转发（复制+激活微信）、关闭、完成。
AI：文字提取（流式/可复制）、文字翻译（原文+译文，目标语言可配）、一键智能打码（视觉检测隐私区域）。
长截图：滚动拼接，结果预览/保存/复制/钉图。
系统：Alt+A 全局热键（可改）、托盘菜单、开机自启、单实例、首次运行配置、便携配置、DPAPI 加密 API Key、日志与错误提示。
质量：无未处理异常；AI 请求可取消；网络错误中文提示；`--selftest` 全绿；exe 单文件绿色版；中文使用说明。

## 10. 协作规则

1. 先 `team_task_get` 读取任务，再 `team_task_update`（action=claim, expected_revision=最新）认领；完成时 action=complete。
2. 只写自己 scopes 内文件；构建输出到 `build_<name>\`，不要写 `dist\`（打包由 build-docs/Lead 做）。
3. 公共接口不得擅自变更；需要变化先消息 Lead。
4. 完成后 `send_message` 给 `lead`：做了什么、改了哪些文件、构建结果、如何验证、遗留问题/风险。
5. 不跑 `git commit`；不修改 ARCHITECTURE.md、src/微信截图助手.csproj、Program/TrayApp/Overlay/Toolbar/Annotation/Pin/Util/AppConfig/DeepSeekClient/SelfTest（这些是 Lead 的文件）。
6. 不要留下后台进程、临时 mock 服务、测试产生的 config.json（测完清理）。
