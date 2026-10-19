# 真实桌面 UI 测试 #5：长截图端到端（app 工具栏按钮 → LongShot → 结果窗口 → 复制）
param(
    [string]$Exe = '',
    [string]$OutDir = 'tests\real5'
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

$cfg = @{
    Version = 1; ApiKeyEnc = ''; BaseUrl = 'https://api.deepseek.com'; VisionModel = 'deepseek-flash';
    TextModel = 'deepseek-flash'; TimeoutSec = 30; Hotkey = 'Alt+Shift+A'; SaveDir = (Join-Path $out 'shots');
    SaveFormat = 'png'; TranslateTarget = '中文'; AutoCopy = $true; AutoSave = $false; ShowColorValue = $true;
    ShowDimensions = $true; PinAlwaysOnTop = $true; MosaicBlock = 12; ShowUsage = $true; OcrExtraPrompt = '';
    TranslateExtraPrompt = ''; StartWithWindows = $false; FirstRunDone = $true
} | ConvertTo-Json -Depth 5
Set-Content -Encoding UTF8 (Join-Path $exeDir 'config.json') $cfg

Get-Process 微信截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
$targetErr = Join-Path $out 'target_err.txt'
$targetOut = Join-Path $out 'target_out.txt'
Remove-Item $targetErr, $targetOut -ErrorAction SilentlyContinue
$target = Start-Process -FilePath 'pwsh.exe' -ArgumentList @('-STA', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\scroll_target.ps1')) -PassThru -WindowStyle Normal -RedirectStandardError $targetErr -RedirectStandardOutput $targetOut
$targetReady = $false
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 250; if ([UiProbe]::FindByTitle('长截图测试目标窗口') -ne [IntPtr]::Zero) { $targetReady = $true; break } }
if (-not $targetReady) {
    Write-Host ("target hasExited={0} code={1}" -f $target.HasExited, $target.ExitCode)
    if (Test-Path $targetErr) { Get-Content $targetErr -Encoding UTF8 | Select-Object -First 20 }
    if (-not $target.HasExited) { $target.Kill() }
    throw '长截图测试目标窗口未启动'
}

$app = $null
try {
    $app = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Milliseconds 1700
    $hidden = Find-XJWindow
    if ($hidden -eq [IntPtr]::Zero) { throw '未找到隐藏主窗口' }

    # 触发截图并框选目标窗口内部区域（目标窗口位于 100,100,700x500）
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1100
    [UiProbe]::Drag(120, 120, 780, 580, 14)
    Start-Sleep -Milliseconds 500
    Save-ScreenPng (Join-Path $out '1_selection.png')

    # 点击「长截图」按钮（非分隔符索引 10；选区 660x460，pill 左≈74，按钮 y≈618）
    [UiProbe]::Click(524, 618)
    Start-Sleep -Milliseconds 1200
    Save-ScreenPng (Join-Path $out '2_longshot_ui.png')

    # 让目标窗口获得焦点后，在选区中心滚动滚轮
    $t = [UiProbe]::FindByTitle('长截图测试目标窗口')
    if ($t -ne [IntPtr]::Zero) { [UiProbe]::SetForegroundWindow($t) | Out-Null }
    [UiProbe]::SetCursorPos(450, 350)
    for ($i = 0; $i -lt 8; $i++) {
        [UiProbe]::WheelEvent(-120)
        Start-Sleep -Milliseconds 850
    }
    Start-Sleep -Milliseconds 900
    Save-ScreenPng (Join-Path $out '3_after_scroll.png')

    # Enter 完成长截图
    [UiProbe]::KeyTap(0x0D)
    Start-Sleep -Milliseconds 1300
    Save-ScreenPng (Join-Path $out '4_result_window.png')

    $resultTitle = ''
    $resultHwnd = [IntPtr]::Zero
    Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class TitleSearch {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    public struct RECT { public int L, T, R, B; }
    public struct POINT { public int X, Y; }
    public static POINT ClientOrigin(IntPtr h) { POINT p; p.X = 0; p.Y = 0; ClientToScreen(h, ref p); return p; }
    public static IntPtr FindPrefix(string prefix, out string title, out RECT rect) {
        IntPtr found = IntPtr.Zero; string ft = ""; RECT fr = new RECT();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
            string s = sb.ToString();
            if (IsWindowVisible(h) && s.StartsWith(prefix)) {
                RECT r; GetWindowRect(h, out r);
                if (r.R - r.L > 100) { found = h; ft = s; fr = r; return false; }
            }
            return true;
        }, IntPtr.Zero);
        title = ft; rect = fr; return found;
    }
}
"@
    $rr = New-Object TitleSearch+RECT
    $resultHwnd = [TitleSearch]::FindPrefix('长截图结果', [ref]$resultTitle, [ref]$rr)
    if ($resultHwnd -eq [IntPtr]::Zero) {
        $failures += '未出现长截图结果窗口'
    } else {
        Write-Host "长截图结果窗口: $resultTitle"
        $w = $rr.R - $rr.L; $h = $rr.B - $rr.T
        Write-Host "结果窗口尺寸: ${w}x${h}"
        if ($h -le 460) { $failures += "长截图结果高度未增加（窗口 h=$h）" }
        # 点击「复制图片」按钮（客户区内 (12,10,96x32) 中心约 (60,26)）
        $clientOrigin = [TitleSearch]::ClientOrigin($resultHwnd)
        [UiProbe]::Click($clientOrigin.X + 60, $clientOrigin.Y + 26)
        Start-Sleep -Milliseconds 700
        $clipPath = Join-Path $out '5_clipboard.png'
        $clipResult = & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\clipboard_save.ps1') -OutPath $clipPath
        Write-Host "长截图剪贴板: $clipResult"
        if ($clipResult -match '^(\d+)x(\d+)$') {
            $iw = [int]$Matches[1]; $ih = [int]$Matches[2]
            if ($iw -ne 660) { $failures += "长截图宽度异常: $iw" }
            if ($ih -le 460) { $failures += "长截图高度未增加: $ih" }
            # 校验左侧无绿色边条侵入（取前 4 列统计绿色像素）
            Add-Type -AssemblyName System.Drawing
            $img = [System.Drawing.Image]::FromFile($clipPath)
            $green = 0
            for ($y = 0; $y -lt $img.Height; $y += 7) {
                for ($x = 0; $x -lt 4; $x++) {
                    $p = $img.GetPixel($x, $y)
                    if ($p.G -gt 140 -and $p.R -lt 120 -and $p.B -lt 140) { $green++ }
                }
            }
            $img.Dispose()
            Write-Host "左侧绿色像素计数: $green"
            if ($green -gt 5) { $failures += "长截图左侧疑似有绿色边框侵入（green=$green）" }
            # 内容连续性校验（行色块唯一，允许滚动起点偏移）
            $verify = & python (Join-Path $root 'testserify_stitch.py') $clipPath 2>&1
            Write-Host ("内容连续性校验: " + ($verify -join ' '))
            if (($verify -join ' ') -notmatch '^PASS') { $failures += ("长截图内容不连续/有重复/错序: " + ($verify -join ' ')) }
        } else {
            $failures += "长截图复制到剪贴板失败: $clipResult"
        }
        # 关闭结果窗口
        [UiProbe]::KeyTap(0x1B)
        Start-Sleep -Milliseconds 400
    }
}
finally {
    Get-Process 微信截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
    if (-not $target.HasExited) { $target.Kill() }
    Start-Sleep -Milliseconds 300
}

if ($failures.Count -gt 0) {
    Write-Host ("真实 UI 测试 #5 失败: " + ($failures -join '; ')) -ForegroundColor Red
    exit 1
}
Write-Host '真实 UI 测试 #5（长截图端到端）全部通过' -ForegroundColor Green


