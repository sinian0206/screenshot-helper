# 聚焦验证：智能打码（真实 API + OCR 兜底）与文字工具提交/移动
param(
    [string]$Exe = '',
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'build_lead\截图助手.exe' }
if (-not $OutDir) { $OutDir = Join-Path $root 'tests\real7d' }
Set-Location $root
. (Join-Path $root 'tools\ui_probe.ps1')
New-Item -ItemType Directory -Force $OutDir | Out-Null
function Shot([string]$n) { Save-ScreenPng (Join-Path $OutDir $n) }
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W4 {
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    public struct RECT { public int L,T,R,B; }
    public struct POINT { public int X,Y; }
}
"@
function Get-ClientScreenRect([IntPtr]$h) {
    $c = New-Object W4+RECT; [W4]::GetClientRect($h, [ref]$c) | Out-Null
    $p = New-Object W4+POINT; [W4]::ClientToScreen($h, [ref]$p) | Out-Null
    return @{ X = $p.X; Y = $p.Y; W = $c.R; H = $c.B }
}
function ToolbarPos([int]$left, [int]$top, [int]$w, [int]$h, [int]$ord) {
    $tbw = 751
    $px = $left + [int](($w - $tbw) / 2)
    $px = [Math]::Max(4, [Math]::Min(2560 - $tbw - 4, $px))
    $py = $top + $h + 14
    if ($py + 50 -gt 1440 - 4) { $py = $top - 14 - 50 }
    $rel = 30 + 42 * $ord
    if ($ord -ge 11) { $rel += 19 }
    return @{ X = $px + $rel; Y = $py + 25 }
}
Get-Process 截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
$app = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1700
$hidden = Find-XJWindow
$fails = @()
$viewer = $null
try {
    # ---------- 文字：输入 → Enter → 拖动 → Enter 完成复制 ----------
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1200
    [UiProbe]::Drag(300, 300, 900, 700, 12)
    Start-Sleep -Milliseconds 500
    $tp = ToolbarPos 300 300 600 400 6   # 文字
    [UiProbe]::Click($tp.X, $tp.Y)
    [UiProbe]::Click(500, 450)
    Start-Sleep -Milliseconds 600
    $overlay = [UiProbe]::FindByTitle('截图助手 - 截图')
    $edit = [IntPtr]::Zero
    if ($overlay -ne [IntPtr]::Zero) { $edit = [UiProbe]::FindEditControl($overlay) }
    if ($edit -eq [IntPtr]::Zero) { $fails += '文字输入框未出现' }
    else {
        [UiProbe]::PostChars($edit, 'Hello 文字')
        Start-Sleep -Milliseconds 250
        [UiProbe]::PostKeyTo($edit, 0x0D)
        Start-Sleep -Milliseconds 500
        [UiProbe]::Drag(520, 470, 700, 560, 10)   # 拖动文字
        Start-Sleep -Milliseconds 400
        Shot 'text_1_moved.png'
        [UiProbe]::KeyTap(0x0D)                    # Enter 完成 → 复制
        Start-Sleep -Milliseconds 1500
        $out = Join-Path $OutDir 'text_clipboard.png'
        $r = & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\clipboard_save.ps1') -OutPath $out
        Write-Host ("text clipboard: " + $r)
        if (-not (Test-Path $out)) { $fails += '文字流程剪贴板为空' }
    }

    # ---------- 智能打码 ----------
    $viewer = Start-Process -FilePath 'pwsh.exe' -ArgumentList @('-STA','-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $root 'tools\image_viewer.ps1'),'-ImagePath',(Join-Path $root 'tests\ai\ai_sample.png'),'-X','200','-Y','200','-Title','截图助手测试图片') -PassThru
    Start-Sleep -Milliseconds 1800
    $vh = [UiProbe]::FindByTitle('截图助手测试图片')
    $cr = Get-ClientScreenRect $vh
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1200
    [UiProbe]::Drag($cr.X, $cr.Y, ($cr.X + $cr.W - 1), ($cr.Y + $cr.H - 1), 12)
    Start-Sleep -Milliseconds 500
    $tp = ToolbarPos $cr.X $cr.Y $cr.W $cr.H 5   # 马赛克
    [UiProbe]::RightClick($tp.X, $tp.Y)
    Start-Sleep -Milliseconds 700
    [UiProbe]::KeyTap(0x28); Start-Sleep -Milliseconds 200; [UiProbe]::KeyTap(0x0D)
    Start-Sleep -Milliseconds 28000
    Shot 'mask_1_result.png'
    [UiProbe]::KeyTap(0x0D)   # Enter 完成 → 复制
    Start-Sleep -Milliseconds 1600
    $out2 = Join-Path $OutDir 'mask_clipboard.png'
    $r2 = & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\clipboard_save.ps1') -OutPath $out2
    Write-Host ("mask clipboard: " + $r2)
    if (-not (Test-Path $out2)) { $fails += '智能打码剪贴板为空' }
}
finally {
    if ($viewer -and -not $viewer.HasExited) { $viewer.Kill() }
    Get-Process 截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
}
if ($fails.Count -gt 0) { Write-Host ('FAIL: ' + ($fails -join '; ')) -ForegroundColor Red; exit 1 }
Write-Host 'text + smartmask focused PASS' -ForegroundColor Green
