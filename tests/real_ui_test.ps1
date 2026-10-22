# 真实桌面 UI 冒烟测试（开发/QA 用；会短暂显示截图覆盖层并接管鼠标，请勿在用户正在操作时运行）
param(
    [string]$Exe = '',
    [int]$MockPort = 18999,
    [string]$OutDir = 'tests\real'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root
. (Join-Path $root 'tools\ui_probe.ps1')
if (-not $Exe) { $Exe = Join-Path $root 'build_lead\截图助手.exe' }
if (-not (Test-Path $Exe)) { throw "找不到 exe: $Exe" }
$out = Join-Path $root $OutDir
New-Item -ItemType Directory -Force $out | Out-Null
Get-Process 截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

# 准备测试配置（mock API + 保存到 tests\real\shots；API Key 用 DPAPI 加密写入，与 ConfigStore 一致）
$exeDir = Split-Path -Parent $Exe
$saveDir = Join-Path $out 'shots'
New-Item -ItemType Directory -Force $saveDir | Out-Null
Add-Type -AssemblyName System.Security
$keyBytes = [System.Text.Encoding]::UTF8.GetBytes('mock-key')
$protected = [System.Security.Cryptography.ProtectedData]::Protect($keyBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$apiKeyEnc = [Convert]::ToBase64String($protected)
$cfg = @{
    Version = 1; ApiKeyEnc = $apiKeyEnc; BaseUrl = "http://127.0.0.1:$MockPort"; VisionModel = 'deepseek-flash';
    TextModel = 'deepseek-flash'; TimeoutSec = 30; Hotkey = 'Alt+Shift+A'; SaveDir = $saveDir; SaveFormat = 'png';
    TranslateTarget = '中文'; AutoCopy = $true; AutoSave = $false; ShowColorValue = $true; ShowDimensions = $true;
    PinAlwaysOnTop = $true; MosaicBlock = 12; ShowUsage = $true; OcrExtraPrompt = ''; TranslateExtraPrompt = '';
    StartWithWindows = $false; FirstRunDone = $true
} | ConvertTo-Json -Depth 5
Set-Content -Encoding UTF8 (Join-Path $exeDir 'config.json') $cfg
Remove-Item (Join-Path $exeDir '截图助手.log') -ErrorAction SilentlyContinue

# 启动 mock 服务
$mockLog = Join-Path $out 'mock_requests.jsonl'
Remove-Item $mockLog -ErrorAction SilentlyContinue
$mock = Start-Process -FilePath 'python' -ArgumentList @('tools\mock_deepseek_server.py', '--port', $MockPort, '--log', $mockLog, '--key', 'mock-key') -PassThru -WindowStyle Hidden
$mockReady = $false
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 200
    $conn = Get-NetTCPConnection -LocalPort $MockPort -State Listen -ErrorAction SilentlyContinue
    if ($conn) { $mockReady = $true; break }
}
if (-not $mockReady) { throw "mock 服务未能在端口 $MockPort 启动（端口被占用？）" }
$app = $null
try {
    $app = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Milliseconds 1600
    $hidden = Find-XJWindow
    if ($hidden -eq [IntPtr]::Zero) { throw '未找到隐藏主窗口（程序未启动？）' }

    # 1) 触发截图覆盖层
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1200
    Save-ScreenPng (Join-Path $out '1_overlay.png')

    # 2) 框选区域
    [UiProbe]::Drag(300, 200, 1500, 900, 18)
    Start-Sleep -Milliseconds 600
    Save-ScreenPng (Join-Path $out '2_selection.png')

    # 3) 点击「文字提取」按钮（工具栏 17 按钮，OCR 为第 10 个工具，索引 9）
    [UiProbe]::Click(932, 938)
    Start-Sleep -Milliseconds 2800
    Save-ScreenPng (Join-Path $out '3_ai_ocr.png')

    # 4) Esc 关闭 AI 面板（发送到覆盖层窗口）
    $overlay = [UiProbe]::FindByTitle('截图助手 - 截图')
    if ($overlay -ne [IntPtr]::Zero) {
        [UiProbe]::PostMessage($overlay, 0x0100, [IntPtr]27, [IntPtr]::Zero)
        [UiProbe]::PostMessage($overlay, 0x0101, [IntPtr]27, [IntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 600
    Save-ScreenPng (Join-Path $out '4_after_esc.png')

    # 5) 点击「完成」按钮（索引 16）
    [UiProbe]::Click(1226, 938)
    Start-Sleep -Milliseconds 900
    Save-ScreenPng (Join-Path $out '5_after_confirm.png')

    # 6) 剪贴板校验
    $clipOk = $false
    try {
        $clipPath = Join-Path $out '6_clipboard.png'
        $clipScript = Join-Path $root 'tools\clipboard_save.ps1'
        $result = & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File $clipScript -OutPath $clipPath
        Write-Host "剪贴板读取结果: $result"
        if ($result -match '^[0-9]+x[0-9]+$') { $clipOk = $true }
    } catch { Write-Host "剪贴板读取失败: $($_.Exception.Message)" }
    if (-not $clipOk) { throw '完成按钮未把截图复制到剪贴板' }

    $reqCount = 0
    if (Test-Path $mockLog) { $reqCount = (Get-Content $mockLog -Encoding UTF8 | Measure-Object).Count }
    Write-Host "mock 请求数: $reqCount"
    if ($reqCount -lt 1) { throw 'AI 请求未到达 mock 服务' }
    Write-Host '真实 UI 冒烟测试通过' -ForegroundColor Green
}
finally {
    if ($app -ne $null -and -not $app.HasExited) { $app.Kill() }
    Get-Process 截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
    if (-not $mock.HasExited) { $mock.Kill() }
}
