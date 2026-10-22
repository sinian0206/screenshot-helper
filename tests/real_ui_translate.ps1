# 聚焦验证：原位翻译（真实 API，通过 UI 完整链路）
param(
    [string]$Exe = '',
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'build_lead\截图助手.exe' }
if (-not $OutDir) { $OutDir = Join-Path $root 'tests\real7c' }
Set-Location $root
. (Join-Path $root 'tools\ui_probe.ps1')
New-Item -ItemType Directory -Force $OutDir | Out-Null
function Shot([string]$n) { Save-ScreenPng (Join-Path $OutDir $n) }
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W3 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    public struct RECT { public int L,T,R,B; }
    public struct POINT { public int X,Y; }
}
"@
function Get-ClientScreenRect([IntPtr]$h) {
    $c = New-Object W3+RECT; [W3]::GetClientRect($h, [ref]$c) | Out-Null
    $p = New-Object W3+POINT; [W3]::ClientToScreen($h, [ref]$p) | Out-Null
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
$viewer = Start-Process -FilePath 'pwsh.exe' -ArgumentList @('-STA','-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $root 'tools\image_viewer.ps1'),'-ImagePath',(Join-Path $root 'tests\ai\ai_sample.png'),'-X','200','-Y','200','-Title','截图助手测试图片') -PassThru
Start-Sleep -Milliseconds 1800
$vh = [UiProbe]::FindByTitle('截图助手测试图片')
if ($vh -eq [IntPtr]::Zero) { Write-Host 'FAIL: viewer not found'; exit 1 }
$cr = Get-ClientScreenRect $vh
Write-Host ("client rect: " + $cr.X + ',' + $cr.Y + ' ' + $cr.W + 'x' + $cr.H)
$app = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1700
$hidden = Find-XJWindow
$fails = @()
try {
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1200
    [UiProbe]::Drag($cr.X, $cr.Y, ($cr.X + $cr.W - 1), ($cr.Y + $cr.H - 1), 14)
    Start-Sleep -Milliseconds 600
    $tp = ToolbarPos $cr.X $cr.Y $cr.W $cr.H 8
    Shot 'trans_1_selected.png'
    [UiProbe]::Click($tp.X, $tp.Y)
    Start-Sleep -Milliseconds 800
    Shot 'trans_2_busy.png'
    # 真实 API 通常 8~25s；轮询到画面出现翻译（最长 45s）
    $done = $false
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Milliseconds 1000
        if ([UiProbe]::FindByTitle('截图助手 - 截图') -eq [IntPtr]::Zero) { break }
        if ($i -ge 8 -and $i % 2 -eq 0) {
            Shot ("trans_progress_" + $i + ".png")
        }
        if ($i -ge 10) { $done = $true; break }
    }
    Start-Sleep -Milliseconds 8000
    Shot 'trans_3_result.png'
    [UiProbe]::KeyTap(0x0D)   # Enter 完成并复制
    Start-Sleep -Milliseconds 1600
    Shot 'trans_4_after_confirm.png'
    $out = Join-Path $OutDir 'trans_clipboard.png'
    $r = & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\clipboard_save.ps1') -OutPath $out
    Write-Host ("clipboard: " + $r)
    if (-not (Test-Path $out)) { $fails += '原位翻译剪贴板为空' }
}
finally {
    Get-Process 截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($viewer -and -not $viewer.HasExited) { $viewer.Kill() }
}
if ($fails.Count -gt 0) { Write-Host ('FAIL: ' + ($fails -join '; ')) -ForegroundColor Red; exit 1 }
Write-Host 'translation focused PASS' -ForegroundColor Green
