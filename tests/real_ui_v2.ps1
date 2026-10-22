# 真实桌面 UI 测试 v2：新版修复验证（框选实时绿框、文字透明/移动、钉图关闭、原位翻译、智能打码、录制、设置、改名）
param(
    [string]$Exe = '',
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'build_lead\截图助手.exe' }
if (-not $OutDir) { $OutDir = Join-Path $root 'tests\real7' }
Set-Location $root
. (Join-Path $root 'tools\ui_probe.ps1')
New-Item -ItemType Directory -Force $OutDir | Out-Null
$saveDir = Join-Path $OutDir 'shots'
New-Item -ItemType Directory -Force $saveDir | Out-Null
$failures = @()

function Save-Shot([string]$name) { Save-ScreenPng (Join-Path $OutDir $name) }
function Clip-Save([string]$name) {
    $path = Join-Path $OutDir $name
    $r = & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\clipboard_save.ps1') -OutPath $path
    return $r
}
function Wait-NoWindow([string]$title, [int]$timeoutMs = 2500) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        if ([UiProbe]::FindByTitle($title) -eq [IntPtr]::Zero) { return $true }
        Start-Sleep -Milliseconds 120
    }
    return $false
}
function Kill-App {
    Get-Process 截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}
function Start-App([string[]]$extraArgs = @()) {
    $args = @{}
    $p = Start-Process -FilePath $Exe -ArgumentList $extraArgs -PassThru
    Start-Sleep -Milliseconds 1700
    return $p
}
function Get-Overlay { return [UiProbe]::FindByTitle('截图助手 - 截图') }
function Trigger-Overlay {
    $hidden = Find-XJWindow
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1100
}
function Select-Region([int]$x1, [int]$y1, [int]$x2, [int]$y2) {
    [UiProbe]::Drag($x1, $y1, $x2, $y2, 16)
    Start-Sleep -Milliseconds 500
}
function Toolbar-Click([int]$selLeft, [int]$selTop, [int]$selWidth, [int]$selHeight, [int]$index) {
    $pillW = 751
    $pillX = $selLeft + [int](($selWidth - $pillW) / 2)
    if ($pillX -lt 4) { $pillX = 4 }
    $pillY = $selTop + $selHeight + 14
    $toolY = $pillY + 24
    $relX = 30 + 42 * $index
    if ($index -ge 11) { $relX += 19 }
    $toolX = $pillX + $relX
    [UiProbe]::Click($toolX, $toolY)
    return @{ X = $toolX; Y = $toolY }
}

# 更新配置：真实 API + 输出目录
Add-Type -AssemblyName System.Security
$apiKeyEnc = (Get-Content (Join-Path (Split-Path $Exe) 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json).ApiKeyEnc
$cfg = @{
    Version = 1; ApiKeyEnc = $apiKeyEnc; BaseUrl = 'https://www.yyapi.cloud/v1';
    VisionModel = 'deepseek-v4.1-flash'; TextModel = 'deepseek-v4.1-flash'; TimeoutSec = 180;
    Hotkey = 'Alt+Shift+A'; SaveDir = $saveDir; SaveFormat = 'png'; TranslateTarget = 'English';
    AutoCopy = $true; AutoSave = $false; ShowColorValue = $true; ShowDimensions = $true;
    PinAlwaysOnTop = $true; MosaicBlock = 24; ShowUsage = $true; OcrExtraPrompt = ''; TranslateExtraPrompt = '';
    RecordHotkey = 'Alt+Shift+R'; RecordFps = 15; RecordQuality = 85; RecordIncludeCursor = $true;
    StartWithWindows = $false; FirstRunDone = $true
} | ConvertTo-Json -Depth 5
Set-Content -Encoding UTF8 (Join-Path (Split-Path $Exe) 'config.json') $cfg
Kill-App

$app = $null
$viewer = $null
try {
    $app = Start-App
    $hidden = Find-XJWindow
    if ($hidden -eq [IntPtr]::Zero) { throw '主程序未启动' }
    $vi = (Get-Item $Exe).VersionInfo
    if ($vi.ProductName -notlike '截图助手*') { $failures += "ProductName 未改名: $($vi.ProductName)" }

    # ---------- 1. 框选实时绿框（拖拽中途截图） ----------
    Trigger-Overlay
    # 用 mouse_event 手动做“按下-移动-截图-继续移动-松开”
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MidDrag {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
    public static void Down(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(80); mouse_event(0x02, 0, 0, 0, UIntPtr.Zero); }
    public static void Move(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(80); }
    public static void Up(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(80); mouse_event(0x04, 0, 0, 0, UIntPtr.Zero); }
}
"@
    [MidDrag]::Down(300, 300)
    for ($i = 1; $i -le 5; $i++) { [MidDrag]::Move(300 + 120 * $i, 300 + 70 * $i) }
    Save-Shot '1_rubberband_mid.png'
    [MidDrag]::Move(1100, 700)
    [MidDrag]::Up(1100, 700)
    Start-Sleep -Milliseconds 400
    [UiProbe]::Esc(); Start-Sleep -Milliseconds 500

    # ---------- 2. 文字：点击输入 → 输入中文/英文 → Enter → 拖动移动 → 完成 ----------
    Trigger-Overlay
    Select-Region 300 300 1300 800
    $pos = Toolbar-Click 300 300 1000 500 6   # 文字工具
    [UiProbe]::Click(650, 480)
    Start-Sleep -Milliseconds 600
    $overlay = Get-Overlay
    $edit = [IntPtr]::Zero
    if ($overlay -ne [IntPtr]::Zero) { $edit = [UiProbe]::FindEditControl($overlay) }
    if ($edit -eq [IntPtr]::Zero) { $failures += '文字工具未出现输入框' }
    else {
        [UiProbe]::PostChars($edit, 'Hello 截图')
        Start-Sleep -Milliseconds 300
        Save-Shot '2_text_editing.png'
        [UiProbe]::PostKeyTo($edit, 0x0D)
        Start-Sleep -Milliseconds 400
        Save-Shot '2_text_committed.png'
        # 拖动文字位置（文字锚点约 650,480，抓 680,500）
        [UiProbe]::Drag(680, 500, 880, 600, 10)
        Start-Sleep -Milliseconds 400
        Save-Shot '2_text_moved.png'
    }
    [UiProbe]::Click(1106, 768)   # 完成（selection 300,300,1000x500 → pill x=424, y=814, confirm idx16 x=424+30+672=1126? 用计算: 424+30+42*16=1126）
    Start-Sleep -Milliseconds 900
    $c = Clip-Save '2_clipboard.png'
    if ($c -notmatch '^1000x500$') { $failures += "文字流程剪贴板尺寸异常: $c" }

    # ---------- 3. 钉图 + 关闭按钮 ----------
    Trigger-Overlay
    Select-Region 400 300 1000 700
    Toolbar-Click 400 300 600 400 13   # 钉在桌面
    Start-Sleep -Milliseconds 900
    $pin = [UiProbe]::FindByTitle('钉在桌面')
    if ($pin -eq [IntPtr]::Zero) { $failures += '钉图窗口未出现' }
    else {
        Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PinRect { [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r); public struct RECT { public int L,T,R,B; } }
"@
        $r = New-Object PinRect+RECT
        [PinRect]::GetWindowRect($pin, [ref]$r) | Out-Null
        Save-Shot '3_pin.png'
        [UiProbe]::Click($r.R - 20, $r.T + 20)
        Start-Sleep -Milliseconds 700
        if ([UiProbe]::FindByTitle('钉在桌面') -ne [IntPtr]::Zero) { $failures += '钉图关闭按钮无效' }
    }

    # ---------- 4. 原位翻译（真实 API） ----------
    $viewer = Start-Process -FilePath 'pwsh.exe' -ArgumentList @('-STA','-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $root 'tools\image_viewer.ps1'),'-ImagePath',(Join-Path $root 'tests\ai\ai_sample.png'),'-X','150','-Y','150','-Title','截图助手测试图片') -PassThru
    Start-Sleep -Milliseconds 1500
    Trigger-Overlay
    Select-Region 158 178 1102 700   # 图片客户区大致范围（含标题栏偏移）
    Toolbar-Click 158 178 944 522 8  # 文字翻译
    Start-Sleep -Milliseconds 26000
    Save-Shot '4_translate_overlay.png'
    Toolbar-Click 158 178 944 522 16 # 完成
    Start-Sleep -Milliseconds 1200
    $c4 = Clip-Save '4_translate_clipboard.png'
    if ($c4 -notmatch '^944x522$') { $failures += "原位翻译剪贴板尺寸异常: $c4" }
    if ($viewer -ne $null -and -not $viewer.HasExited) { $viewer.Kill() }

    # ---------- 5. 智能打码（真实 API，含人脸） ----------
    $viewer = Start-Process -FilePath 'pwsh.exe' -ArgumentList @('-STA','-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $root 'tools\image_viewer.ps1'),'-ImagePath',(Join-Path $root 'tests\ai\ai_sample.png'),'-X','300','-Y','250','-Title','截图助手测试图片') -PassThru
    Start-Sleep -Milliseconds 1500
    Trigger-Overlay
    Select-Region 308 278 1268 818
    # 右键马赛克工具（idx5）+ 选择「一键智能打码」
    $pillW = 751; $pillX = 308 + [int]((960 - $pillW) / 2); if ($pillX -lt 4) { $pillX = 4 }
    $pillY = 818 + 14
    [UiProbe]::RightClick($pillX + 30 + 42 * 5, $pillY + 24)
    Start-Sleep -Milliseconds 700
    [UiProbe]::KeyTap(0x28); Start-Sleep -Milliseconds 200; [UiProbe]::KeyTap(0x0D)
    Start-Sleep -Milliseconds 26000
    Save-Shot '5_smartmask_overlay.png'
    [UiProbe]::Click($pillX + 30 + 42 * 16, $pillY + 24)  # 完成
    Start-Sleep -Milliseconds 1200
    $c5 = Clip-Save '5_smartmask_clipboard.png'
    if ($c5 -notmatch '^960x540$') { $failures += "智能打码剪贴板尺寸异常: $c5" }
    if ($viewer -ne $null -and -not $viewer.HasExited) { $viewer.Kill() }

    # ---------- 6. 视频录制（托盘热键 0x5A12 → 框选 → 停止） ----------
    $beforeAvi = @(Get-ChildItem $saveDir -Filter '录屏_*.avi' -ErrorAction SilentlyContinue).Count
    [UiProbe]::PostMessage($hidden, 0x0312, [IntPtr]0x5A12, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1200
    Save-Shot '6_record_overlay.png'
    Select-Region 500 400 1100 800
    Start-Sleep -Milliseconds 1500
    Save-Shot '6_recording.png'
    Start-Sleep -Milliseconds 1800
    [UiProbe]::Esc()    # 停止录制
    Start-Sleep -Milliseconds 2500
    Save-Shot '6_after_record.png'
    $avi = @(Get-ChildItem $saveDir -Filter '录屏_*.avi' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    if ($avi.Count -le $beforeAvi) { $failures += '录制未生成 avi 文件' }
    else {
        $parse = & python (Join-Path $root 'tests\recorder\parse_avi.py') $avi[0].FullName 2>&1
        Write-Host ("AVI 解析: " + ($parse -join ' '))
        if (($parse -join ' ') -notmatch 'PASS|OK') { $failures += "AVI 解析异常: $($parse -join ' ')" }
    }

    # ---------- 7. 设置窗口（截图/标题/关闭） ----------
    [UiProbe]::PostMessage($hidden, 0x8003, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1400
    Save-Shot '7_settings.png'
    if (-not ([UiProbe]::FindByTitle('截图助手 · 设置') -ne [IntPtr]::Zero -or ([WinTitles2]::All() | Where-Object { $_ -like '*设置*' }))) { $failures += '设置窗口标题/窗口未出现' }
    [UiProbe]::Esc(); Start-Sleep -Milliseconds 600
}
finally {
    if ($viewer -ne $null -and -not $viewer.HasExited) { $viewer.Kill() }
    if ($app -ne $null -and -not $app.HasExited) { $app.Kill() }
    Kill-App
}

if ($failures.Count -gt 0) {
    Write-Host ("真实 UI 测试 v2 失败: " + ($failures -join '; ')) -ForegroundColor Red
    exit 1
}
Write-Host '真实 UI 测试 v2 全部通过' -ForegroundColor Green
