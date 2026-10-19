# 真实桌面 UI 测试 #4：全局热键、窗口智能识别、保存按钮、关闭按钮、文字翻译面板
param(
    [string]$Exe = '',
    [int]$MockPort = 18999,
    [string]$OutDir = 'tests\real4'
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
$failures = @()

Add-Type -AssemblyName System.Security
$keyBytes = [System.Text.Encoding]::UTF8.GetBytes('mock-key')
$apiKeyEnc = [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect($keyBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
$saveDir = Join-Path $out 'shots'
New-Item -ItemType Directory -Force $saveDir | Out-Null
$cfg = @{
    Version = 1; ApiKeyEnc = $apiKeyEnc; BaseUrl = "http://127.0.0.1:$MockPort"; VisionModel = 'deepseek-flash';
    TextModel = 'deepseek-flash'; TimeoutSec = 30; Hotkey = 'Alt+Shift+A'; SaveDir = $saveDir;
    SaveFormat = 'png'; TranslateTarget = '中文'; AutoCopy = $true; AutoSave = $false; ShowColorValue = $true;
    ShowDimensions = $true; PinAlwaysOnTop = $true; MosaicBlock = 12; ShowUsage = $true; OcrExtraPrompt = '';
    TranslateExtraPrompt = ''; StartWithWindows = $false; FirstRunDone = $true
} | ConvertTo-Json -Depth 5
Set-Content -Encoding UTF8 (Join-Path $exeDir 'config.json') $cfg

Get-Process 微信截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
$mockLog = Join-Path $out 'mock_requests.jsonl'
Remove-Item $mockLog -ErrorAction SilentlyContinue
$mock = Start-Process -FilePath 'python' -ArgumentList @('tools\mock_deepseek_server.py', '--port', $MockPort, '--log', $mockLog, '--key', 'mock-key') -PassThru -WindowStyle Hidden
$mockReady = $false
for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 200; if (Get-NetTCPConnection -LocalPort $MockPort -State Listen -ErrorAction SilentlyContinue) { $mockReady = $true; break } }
if (-not $mockReady) { throw "mock 服务未能在端口 $MockPort 启动" }

function Wait-OverlayGone([int]$timeoutMs = 3000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        if ([UiProbe]::FindByTitle('微信截图助手 - 截图') -eq [IntPtr]::Zero) { return $true }
        Start-Sleep -Milliseconds 120
    }
    return $false
}
function Start-Overlay([IntPtr]$hidden) {
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1100
    [UiProbe]::Drag(360, 260, 1200, 760, 14)
    Start-Sleep -Milliseconds 500
}

$app = $null
try {
    $app = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Milliseconds 1700
    $hidden = Find-XJWindow
    if ($hidden -eq [IntPtr]::Zero) { throw '未找到隐藏主窗口' }

    # ---------- A. 全局热键 Alt+Shift+A 真实按键 ----------
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class KeyCombo {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public static void AltShiftA() {
        keybd_event(0x12, 0, 0, UIntPtr.Zero);        // Alt down
        keybd_event(0x10, 0, 0, UIntPtr.Zero);        // Shift down
        System.Threading.Thread.Sleep(60);
        keybd_event(0x41, 0, 0, UIntPtr.Zero);        // A down
        System.Threading.Thread.Sleep(40);
        keybd_event(0x41, 0, 2, UIntPtr.Zero);        // A up
        keybd_event(0x10, 0, 2, UIntPtr.Zero);        // Shift up
        keybd_event(0x12, 0, 2, UIntPtr.Zero);        // Alt up
    }
}
"@
    [KeyCombo]::AltShiftA()
    Start-Sleep -Milliseconds 1100
    Save-ScreenPng (Join-Path $out 'a_hotkey_overlay.png')
    $overlay = [UiProbe]::FindByTitle('微信截图助手 - 截图')
    if ($overlay -eq [IntPtr]::Zero) { $failures += 'A: 全局热键未触发截图覆盖层' }
    [UiProbe]::Esc(); Start-Sleep -Milliseconds 500
    if (-not (Wait-OverlayGone 2000)) { $failures += 'A: 覆盖层未关闭' }

    # ---------- B. 单击智能识别窗口 ----------
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1100
    [UiProbe]::Click(1200, 400)    # 单击（不拖拽）选中鼠标下的窗口
    Start-Sleep -Milliseconds 700
    Save-ScreenPng (Join-Path $out 'b_window_detect.png')
    if ([UiProbe]::FindByTitle('微信截图助手 - 截图') -eq [IntPtr]::Zero) { $failures += 'B: 窗口识别后覆盖层消失' }
    [UiProbe]::Esc(); Start-Sleep -Milliseconds 500

    # ---------- C. 保存按钮（下载图标） ----------
    $beforeFiles = @(Get-ChildItem $saveDir -Filter '*.png' -ErrorAction SilentlyContinue).Count
    Start-Overlay $hidden
    [UiProbe]::Click(938, 798)     # 保存（非分隔符索引 12）
    Start-Sleep -Milliseconds 1000
    $afterFiles = @(Get-ChildItem $saveDir -Filter '*.png' -ErrorAction SilentlyContinue).Count
    if ($afterFiles -le $beforeFiles) { $failures += 'C: 保存按钮未生成文件' }
    if (-not (Wait-OverlayGone 2500)) { $failures += 'C: 保存后覆盖层未关闭' }
    Save-ScreenPng (Join-Path $out 'c_after_save.png')

    # ---------- D. 关闭按钮 ----------
    Start-Overlay $hidden
    [UiProbe]::Click(1064, 798)    # 关闭（非分隔符索引 15）
    Start-Sleep -Milliseconds 700
    if (-not (Wait-OverlayGone 2500)) { $failures += 'D: 关闭按钮未关闭覆盖层' }

    # ---------- E. 文字翻译面板 ----------
    Start-Overlay $hidden
    [UiProbe]::Click(770, 798)     # 文字翻译（非分隔符索引 8）
    Start-Sleep -Milliseconds 2600
    Save-ScreenPng (Join-Path $out 'e_translate_panel.png')
    $reqCount = 0
    if (Test-Path $mockLog) { $reqCount = (Get-Content $mockLog -Encoding UTF8 | Measure-Object).Count }
    if ($reqCount -lt 2) { $failures += "E: 翻译请求数不足（mock=$reqCount，期望 OCR+翻译 2 次）" }
    [UiProbe]::Esc(); Start-Sleep -Milliseconds 500   # 关面板
    [UiProbe]::Esc(); Start-Sleep -Milliseconds 500   # 关编辑器
    if (-not (Wait-OverlayGone 2500)) { $failures += 'E: 翻译面板关闭后覆盖层未关闭' }
}
finally {
    Get-Process 微信截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
    if (-not $mock.HasExited) { $mock.Kill() }
    Start-Sleep -Milliseconds 300
}

if ($failures.Count -gt 0) {
    Write-Host ("真实 UI 测试 #4 失败: " + ($failures -join '; ')) -ForegroundColor Red
    exit 1
}
Write-Host '真实 UI 测试 #4 全部通过' -ForegroundColor Green
