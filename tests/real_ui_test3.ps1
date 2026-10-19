# 真实桌面 UI 测试 #3：表情贴纸、文字输入、一键智能打码
param(
    [string]$Exe = '',
    [int]$MockPort = 18999,
    [string]$OutDir = 'tests\real3'
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
$cfg = @{
    Version = 1; ApiKeyEnc = $apiKeyEnc; BaseUrl = "http://127.0.0.1:$MockPort"; VisionModel = 'deepseek-flash';
    TextModel = 'deepseek-flash'; TimeoutSec = 30; Hotkey = 'Alt+Shift+A'; SaveDir = (Join-Path $out 'shots');
    SaveFormat = 'png'; TranslateTarget = '中文'; AutoCopy = $true; AutoSave = $false; ShowColorValue = $true;
    ShowDimensions = $true; PinAlwaysOnTop = $true; MosaicBlock = 12; ShowUsage = $true; OcrExtraPrompt = '';
    TranslateExtraPrompt = ''; StartWithWindows = $false; FirstRunDone = $true
} | ConvertTo-Json -Depth 5
Set-Content -Encoding UTF8 (Join-Path $exeDir 'config.json') $cfg
New-Item -ItemType Directory -Force (Join-Path $out 'shots') | Out-Null

Get-Process 微信截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
$mockLog = Join-Path $out 'mock_requests.jsonl'
Remove-Item $mockLog -ErrorAction SilentlyContinue
$mock = Start-Process -FilePath 'python' -ArgumentList @('tools\mock_deepseek_server.py', '--port', $MockPort, '--log', $mockLog, '--key', 'mock-key', '--privacy-full') -PassThru -WindowStyle Hidden
$mockReady = $false
for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 200; if (Get-NetTCPConnection -LocalPort $MockPort -State Listen -ErrorAction SilentlyContinue) { $mockReady = $true; break } }
if (-not $mockReady) { throw "mock 服务未能在端口 $MockPort 启动" }

function Save-Clipboard([string]$path) {
    $r = & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\clipboard_save.ps1') -OutPath $path
    return $r
}
function Start-Overlay([IntPtr]$hidden, [string]$screenshotPath) {
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1100
    [UiProbe]::Drag(360, 260, 1200, 760, 14)
    Start-Sleep -Milliseconds 500
    if ($screenshotPath) { Save-ScreenPng $screenshotPath }
}

$app = $null
try {
    $app = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Milliseconds 1600
    $hidden = Find-XJWindow
    if ($hidden -eq [IntPtr]::Zero) { throw '未找到隐藏主窗口' }

    # ---------- 1. 表情贴纸：工具栏 → 选表情 → 点击画布 → 完成 ----------
    Start-Overlay $hidden (Join-Path $out '1_selection.png')
    [UiProbe]::Click(518, 798)    # 表情工具
    Start-Sleep -Milliseconds 700
    Save-ScreenPng (Join-Path $out '1_emoji_picker.png')
    [UiProbe]::Click(453, 841)    # 选择器第 1 个表情
    Start-Sleep -Milliseconds 500
    [UiProbe]::Click(700, 520)    # 放到画布
    Start-Sleep -Milliseconds 400
    Save-ScreenPng (Join-Path $out '1_emoji_placed.png')
    [UiProbe]::Click(1106, 798)   # 完成
    Start-Sleep -Milliseconds 900
    $c1 = Save-Clipboard (Join-Path $out '1_clipboard.png')
    Write-Host "表情贴纸剪贴板: $c1"
    if ($c1 -notmatch '^840x500$') { $failures += "表情贴纸: 剪贴板尺寸异常 $c1" }
    else {
        Add-Type -AssemblyName System.Drawing
        $img = [System.Drawing.Image]::FromFile((Join-Path $out '1_clipboard.png'))
        $found = $false
        for ($y = 230; $y -le 290; $y++) {
            for ($x = 310; $x -le 370; $x++) {
                $p = $img.GetPixel($x, $y)
                if ($p.R -gt 190 -and $p.G -gt 150 -and $p.B -lt 110) { $found = $true; break }
            }
            if ($found) { break }
        }
        $img.Dispose()
        if (-not $found) { $failures += '表情贴纸: 剪贴板图片未检测到黄色表情像素' }
    }

    # ---------- 2. 文字输入：工具栏 → 点击 → 输入 hello → Enter → 完成 ----------
    Start-Overlay $hidden (Join-Path $out '2_selection.png')
    [UiProbe]::Click(686, 798)    # 文字工具
    Start-Sleep -Milliseconds 300
    [UiProbe]::Click(650, 450)    # 输入位置
    Start-Sleep -Milliseconds 500
    $overlay2 = [UiProbe]::FindByTitle('微信截图助手 - 截图')
    $edit = [IntPtr]::Zero
    if ($overlay2 -ne [IntPtr]::Zero) { $edit = [UiProbe]::FindEditControl($overlay2) }
    if ($edit -eq [IntPtr]::Zero) {
        $failures += '文字输入: 未找到文本框'
    } else {
        # 直接向文本控件投递字符，绕开中文输入法候选框
        [UiProbe]::PostChars($edit, 'hello')
        Start-Sleep -Milliseconds 200
        Save-ScreenPng (Join-Path $out '2_text_edit.png')
        [UiProbe]::PostKeyTo($edit, 0x0D)  # Enter 提交文字
        Start-Sleep -Milliseconds 400
    }
    Save-ScreenPng (Join-Path $out '2_text_committed.png')
    [UiProbe]::Click(1106, 798)   # 完成
    Start-Sleep -Milliseconds 900
    $c2 = Save-Clipboard (Join-Path $out '2_clipboard.png')
    Write-Host "文字输入剪贴板: $c2"
    if ($c2 -notmatch '^840x500$') { $failures += "文字输入: 剪贴板尺寸异常 $c2" }
    else {
        Add-Type -AssemblyName System.Drawing
        $img = [System.Drawing.Image]::FromFile((Join-Path $out '2_clipboard.png'))
        $found = $false
        for ($y = 170; $y -le 230; $y++) {
            for ($x = 270; $x -le 400; $x++) {
                $p = $img.GetPixel($x, $y)
                if ($p.R -gt 190 -and $p.G -lt 140 -and $p.B -lt 140) { $found = $true; break }
            }
            if ($found) { break }
        }
        $img.Dispose()
        if (-not $found) { $failures += '文字输入: 剪贴板图片未检测到红色文字像素' }
    }

    # ---------- 3. 一键智能打码：右键马赛克 → 第一项 → 等结果 → 完成 ----------
    Start-Overlay $hidden (Join-Path $out '3_selection.png')
    [UiProbe]::RightClick(644, 798)   # 马赛克工具右键
    Start-Sleep -Milliseconds 700
    Save-ScreenPng (Join-Path $out '3_menu.png')
    [UiProbe]::KeyTap(0x28)           # Down
    Start-Sleep -Milliseconds 250
    [UiProbe]::KeyTap(0x0D)           # Enter
    Start-Sleep -Milliseconds 2000
    Save-ScreenPng (Join-Path $out '3_smart_mosaic.png')
    $reqCount = 0
    if (Test-Path $mockLog) { $reqCount = (Get-Content $mockLog -Encoding UTF8 | Measure-Object).Count }
    if ($reqCount -lt 1) { $failures += "一键打码: 未发出 API 请求（mock=0）" }
    [UiProbe]::Click(1106, 798)       # 完成
    Start-Sleep -Milliseconds 900
    $c3 = Save-Clipboard (Join-Path $out '3_clipboard.png')
    Write-Host "智能打码剪贴板: $c3"
    if ($c3 -notmatch '^840x500$') { $failures += "智能打码: 剪贴板尺寸异常 $c3" }
    else {
        # 整块打码后，与打码前屏幕内容相比应有大量像素变化
        Add-Type -AssemblyName System.Drawing
        $before = [System.Drawing.Bitmap]::FromFile((Join-Path $out '3_selection.png'))
        $after = [System.Drawing.Bitmap]::FromFile((Join-Path $out '3_clipboard.png'))
        $diff = 0; $count = 0
        for ($y = 20; $y -lt 480; $y += 20) {
            for ($x = 20; $x -lt 820; $x += 20) {
                $a = $after.GetPixel($x, $y); $b = $before.GetPixel($x + 360, $y + 260)
                $diff += [Math]::Abs($a.R - $b.R) + [Math]::Abs($a.G - $b.G) + [Math]::Abs($a.B - $b.B)
                $count++
            }
        }
        $before.Dispose(); $after.Dispose()
        $avg = $diff / [Math]::Max(1, $count)
        Write-Host ("智能打码前后平均像素差: {0:N1}" -f $avg)
        if ($avg -lt 5) { $failures += "一键打码: 截图与打码前差异过小（avg=$avg），疑似未生效" }
    }
}
finally {
    Get-Process 微信截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
    if (-not $mock.HasExited) { $mock.Kill() }
    Start-Sleep -Milliseconds 300
}

if ($failures.Count -gt 0) {
    Write-Host ("真实 UI 测试 #3 失败: " + ($failures -join '; ')) -ForegroundColor Red
    exit 1
}
Write-Host '真实 UI 测试 #3 全部通过' -ForegroundColor Green
