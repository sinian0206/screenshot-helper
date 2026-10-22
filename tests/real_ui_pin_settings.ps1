# 聚焦验证：钉图边框/关闭、设置窗口、原位翻译（真实 API）
param(
    [string]$Exe = '',
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'build_lead\截图助手.exe' }
if (-not $OutDir) { $OutDir = Join-Path $root 'tests\real7b' }
Set-Location $root
. (Join-Path $root 'tools\ui_probe.ps1')
New-Item -ItemType Directory -Force $OutDir | Out-Null
function Shot([string]$n) { Save-ScreenPng (Join-Path $OutDir $n) }
Add-Type @"
using System; using System.Collections.Generic; using System.Text; using System.Runtime.InteropServices;
public static class WinTitles2 {
    public delegate bool EP(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EP cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    public static string[] All() { var list = new List<string>(); EnumWindows(delegate(IntPtr h, IntPtr l) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); if (sb.Length > 0) list.Add(sb.ToString()); return true; }, IntPtr.Zero); return list.ToArray(); }
}
"@
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
function Trigger {
    $hidden = Find-XJWindow
    [UiProbe]::PostMessage($hidden, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1100
}
Get-Process 截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
$app = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1700
$fails = @()
try {
    # ---- 钉图 ----
    Trigger
    [UiProbe]::Drag(400, 300, 1000, 700, 14)
    Start-Sleep -Milliseconds 500
    $p = ToolbarPos 400 300 600 400 13
    Shot 'pin_1_toolbar.png'
    [UiProbe]::Click($p.X, $p.Y)
    Start-Sleep -Milliseconds 1200
    Shot 'pin_2_after_click.png'
    $found = @()
    for ($i = 0; $i -lt 60; $i++) {
        $found = [WinTitles2]::All()
        $hit = $found | Where-Object { $_ -like '*钉*' }
        if ($hit) { break }
        Start-Sleep -Milliseconds 100
    }
    Write-Host ("window titles: " + (($found | Select-Object -First 40) -join ' | '))
    if (-not $hit) { $fails += '钉图窗口未出现' }
    else {
        $pin = [UiProbe]::FindByTitle('钉在桌面')
        Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PR2 { [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r); public struct RECT { public int L,T,R,B; } }
"@
        $r = New-Object PR2+RECT
        [PR2]::GetWindowRect($pin, [ref]$r) | Out-Null
        Write-Host ("pin rect: " + $r.L + ',' + $r.T + ',' + $r.R + ',' + $r.B)
        Shot 'pin_3_pin.png'
        [UiProbe]::Click($r.R - 17, $r.T + 17)
        Start-Sleep -Milliseconds 800
        Shot 'pin_4_closed.png'
        if ([UiProbe]::FindByTitle('钉在桌面') -ne [IntPtr]::Zero) { $fails += '钉图关闭无效' }
    }
    # ---- 设置（第二实例 --settings） ----
    $p2 = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
    Start-Sleep -Milliseconds 1800
    Shot 'settings_1.png'
    $titles = [WinTitles2]::All()
    Write-Host ("settings titles: " + (($titles | Where-Object { $_ -like '*设置*' }) -join ' | '))
    if (-not ($titles | Where-Object { $_ -like '*设置*' })) { $fails += '设置窗口未出现' }
    [UiProbe]::KeyTap(0x1B)
    Start-Sleep -Milliseconds 600
}
finally {
    Get-Process 截图助手 -ErrorAction SilentlyContinue | Stop-Process -Force
}
if ($fails.Count -gt 0) { Write-Host ('FAIL: ' + ($fails -join '; ')) -ForegroundColor Red; exit 1 }
Write-Host 'pin/settings focused PASS' -ForegroundColor Green
