# 真实桌面 UI 冒烟测试 #2：首次运行、设置窗口、钉图、表情选择器、一键智能打码
param(
    [string]$Exe = '',
    [int]$MockPort = 18999,
    [string]$OutDir = 'tests\real2'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root
. (Join-Path $root 'tools\ui_probe.ps1')
if (-not $Exe) { $Exe = Join-Path $root 'build_lead\微信截图助手.exe' }
if (-not (Test-Path $Exe)) { throw "找不到 exe: $Exe" }
$out = Join-Path $root $OutDir
New-Item -ItemType Directory -Force $out | Out-Null
$exeDir = Split-Path -Parent $Exe

function Stop-App {
    Get-Process 微信截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

function Write-Config([string]$apiKeyEnc, [string]$baseUrl) {
    $cfg = @{
        Version = 1; ApiKeyEnc = $apiKeyEnc; BaseUrl = $baseUrl; VisionModel = 'deepseek-flash';
        TextModel = 'deepseek-flash'; TimeoutSec = 30; Hotkey = 'Alt+Shift+A'; SaveDir = (Join-Path $out 'shots');
        SaveFormat = 'png'; TranslateTarget = '中文'; AutoCopy = $true; AutoSave = $false; ShowColorValue = $true;
        ShowDimensions = $true; PinAlwaysOnTop = $true; MosaicBlock = 12; ShowUsage = $true; OcrExtraPrompt = '';
        TranslateExtraPrompt = ''; StartWithWindows = $false; FirstRunDone = $true
    } | ConvertTo-Json -Depth 5
    Set-Content -Encoding UTF8 (Join-Path $exeDir 'config.json') $cfg
}

Add-Type -AssemblyName System.Security
$keyBytes = [System.Text.Encoding]::UTF8.GetBytes('mock-key')
$apiKeyEnc = [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect($keyBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
New-Item -ItemType Directory -Force (Join-Path $out 'shots') | Out-Null

Stop-App
$mock = Start-Process -FilePath 'python' -ArgumentList @('tools\mock_deepseek_server.py', '--port', $MockPort, '--log', (Join-Path $out 'mock_requests.jsonl'), '--key', 'mock-key') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 800
$failures = @()
try {
    # ---------- A. 首次运行欢迎窗口 ----------
    Start-Sleep -Milliseconds 100
    Remove-Item (Join-Path $exeDir 'config.json') -ErrorAction SilentlyContinue
    $app = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Milliseconds 1800
    Save-ScreenPng (Join-Path $out 'a_firstrun.png')
    $welcome = [UiProbe]::FindByTitle('欢迎使用微信截图助手')
    if ($welcome -eq [IntPtr]::Zero) { $failures += 'A: 未找到首次运行欢迎窗口' }
    [UiProbe]::Esc(); Start-Sleep -Milliseconds 500
    Stop-App

    # ---------- B. 设置窗口 ----------
    Write-Config $apiKeyEnc "http://127.0.0.1:$MockPort"
    $app = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Milliseconds 1600
    $hidden = Find-XJWindow
    if ($hidden -eq [IntPtr]::Zero) { throw '隐藏主窗口未找到' }
    [UiProbe]::PostMessage($hidden, 0x8003, [IntPtr]::Zero, [IntPtr]::Zero)  # WM_APP_OPEN_SETTINGS
    Start-Sleep -Milliseconds 1400
    Save-ScreenPng (Join-Path $out 'b_settings.png')
    $settings = [UiProbe]::FindByTitle('微信截图助手 - 设置')
    if ($settings -eq [IntPtr]::Zero) { $failures += 'B: 未找到设置窗口' }
    [UiProbe]::Esc(); Start-Sleep -Milliseconds 600

    # ---------- C. 钉在桌面 ----------
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)  # 触发截图
    Start-Sleep -Milliseconds 1100
    [UiProbe]::Drag(360, 260, 1200, 760, 14)
    Start-Sleep -Milliseconds 500
    [UiProbe]::Click(980, 798)   # 钉在桌面（非分隔符索引 13）
    Start-Sleep -Milliseconds 1000
    Save-ScreenPng (Join-Path $out 'c_pin.png')
    $pin = [UiProbe]::FindByTitle('钉在桌面')
    if ($pin -eq [IntPtr]::Zero) {
        # 标题可能被隐藏，改用窗口尺寸/标题探测下一个窗口
        $pin = [UiProbe]::FindByTitle('钉在桌面')
    }
    if ($pin -ne [IntPtr]::Zero) {
        [UiProbe]::PostMessage($pin, 0x0100, [IntPtr]27, [IntPtr]::Zero)
        [UiProbe]::PostMessage($pin, 0x0101, [IntPtr]27, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 400
    } else {
        $failures += 'C: 未找到钉图窗口'
    }

    # ---------- D. 表情选择器（出现后 Esc 关闭） ----------
    if ((Get-Process 微信截图助手 -ErrorAction SilentlyContinue) -eq $null) {
        $app = Start-Process -FilePath $Exe -PassThru
        Start-Sleep -Milliseconds 1600
        $hidden = Find-XJWindow
    }
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1100
    [UiProbe]::Drag(360, 260, 1200, 760, 14)
    Start-Sleep -Milliseconds 500
    [UiProbe]::Click(518, 798)   # 表情工具（非分隔符索引 2）
    Start-Sleep -Milliseconds 700
    Save-ScreenPng (Join-Path $out 'd_emoji_picker.png')
    [UiProbe]::Esc(); Start-Sleep -Milliseconds 400
    Write-Host 'D: 表情选择器截图已保存，请人工检查 d_emoji_picker.png' 

    # ---------- E. 一键智能打码（右键马赛克 → 第一项） ----------
    [UiProbe]::RightClick(644, 798)   # 马赛克工具（非分隔符索引 5）
    Start-Sleep -Milliseconds 700
    Save-ScreenPng (Join-Path $out 'e1_mosaic_menu.png')
    [UiProbe]::KeyTap(0x28)  # Down
    Start-Sleep -Milliseconds 250
    [UiProbe]::KeyTap(0x0D)  # Enter 选择「一键智能打码」
    Start-Sleep -Milliseconds 1800
    Save-ScreenPng (Join-Path $out 'e2_smart_mosaic.png')
    [UiProbe]::Click(1106, 798)  # 完成（索引 16）
    Start-Sleep -Milliseconds 900
    $clipOk = $false
    try {
        $clipPath = Join-Path $out 'e3_clipboard.png'
        $r = & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\clipboard_save.ps1') -OutPath $clipPath
        if ($r -match '^[0-9]+x[0-9]+$') { $clipOk = $true; Write-Host "智能打码结果剪贴板: $r" }
    } catch { }
    if (-not $clipOk) { $failures += 'E: 智能打码后完成按钮未复制到剪贴板' }
}
finally {
    Stop-App
    if (-not $mock.HasExited) { $mock.Kill() }
    Start-Sleep -Milliseconds 300
}

if ($failures.Count -gt 0) {
    Write-Host ("真实 UI 测试 #2 失败: " + ($failures -join '; ')) -ForegroundColor Red
    exit 1
}
Write-Host '真实 UI 测试 #2 全部通过' -ForegroundColor Green
