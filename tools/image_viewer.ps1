# 显示一张图片的置顶测试窗口（供截图/翻译/打码真实 UI 测试使用）
param(
    [Parameter(Mandatory = $true)][string]$ImagePath,
    [int]$X = 150,
    [int]$Y = 150,
    [string]$Title = '截图助手测试图片'
)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TViewTop {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
"@
$img = [System.Drawing.Image]::FromFile((Resolve-Path $ImagePath))
$form = New-Object System.Windows.Forms.Form
$form.Text = $Title
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point($X, $Y)
$form.ClientSize = $img.Size
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedSingle
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.TopMost = $true
$form.BackColor = [System.Drawing.Color]::White
$form.Add_Paint({ param($sender, $e) $e.Graphics.DrawImage($img, 0, 0, $img.Width, $img.Height) })
$form.Show()
[System.Windows.Forms.Application]::DoEvents()
[TViewTop]::SetWindowPos($form.Handle, [IntPtr]::new(-1), $X, $Y, 0, 0, 0x0002 -bor 0x0001 -bor 0x0010) | Out-Null
[System.Windows.Forms.Application]::Run($form)
