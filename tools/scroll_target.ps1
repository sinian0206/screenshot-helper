# 长截图测试目标窗口：一个可滚动的大内容窗口（供真实 UI 测试使用）
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ConsoleHider {
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    public static void Hide() { IntPtr h = GetConsoleWindow(); if (h != IntPtr.Zero) ShowWindow(h, 0); }
}
"@
[ConsoleHider]::Hide()
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TopMostHelper {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static void MakeTopMost(IntPtr h) { SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010); }
}
"@
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = '长截图测试目标窗口'
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(100, 100)
$form.Size = New-Object System.Drawing.Size(700, 500)
$form.TopMost = $true
$form.AutoScroll = $true
$form.BackColor = [System.Drawing.Color]::White

$panel = New-Object System.Windows.Forms.Panel
$panel.Size = New-Object System.Drawing.Size(660, 5000)
$panel.BackColor = [System.Drawing.Color]::White
$panel.Add_Paint({
    param($sender, $e)
    $g = $e.Graphics
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
    $small = New-Object System.Drawing.Font('Consolas', 9)
    for ($row = 0; $row -lt 100; $row++) {
        $y = $row * 50
        $band = if ($row % 2 -eq 0) { [System.Drawing.Color]::FromArgb(245, 248, 252) } else { [System.Drawing.Color]::FromArgb(232, 240, 250) }
        $brush = New-Object System.Drawing.SolidBrush($band)
        $g.FillRectangle($brush, 0, $y, 660, 50)
        $brush.Dispose()
        $g.DrawLine([System.Drawing.Pens]::LightGray, 0, $y, 660, $y)
        $g.DrawString(("ROW {0}" -f $row), $font, [System.Drawing.Brushes]::Black, 12, $y + 6)
        $g.DrawString(("内容测试 {0} - LongShot Probe - ABCDEFG 123456" -f $row), $small, [System.Drawing.Brushes]::DimGray, 12, $y + 26)
        # 每行唯一图案：用行号的 32 位哈希生成颜色与条形码，避免内容周期性导致拼接歧义
        $hash = [uint32]((($row + 1) * 2654435761) % 4294967296)
        $r = 40 + (($hash -shr 0) % 180)
        $gg = 40 + (($hash -shr 8) % 180)
        $bb = 40 + (($hash -shr 16) % 180)
        $c = [System.Drawing.Color]::FromArgb($r, $gg, $bb)
        $b = New-Object System.Drawing.SolidBrush($c)
        $g.FillRectangle($b, 560, $y + 6, 80, 38)
        $b.Dispose()
        # 行内唯一条形码（12 段，长度由哈希位决定）
        $barBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(30, 30, 40))
        for ($k = 0; $k -lt 12; $k++) {
            $bit = ($hash -shr ($k % 32)) -band 1
            $bw = if ($bit -eq 1) { 6 } else { 2 }
            $bx = 200 + $k * 12 + (($hash -shr (($k + 4) % 32)) -band 3)
            $g.FillRectangle($barBrush, $bx, $y + 30, $bw, 14)
        }
        $barBrush.Dispose()
        # 每行额外随机位置的小方块
        $rnd = New-Object System.Random([int]($hash -band 0x7FFFFFFF))
        for ($m = 0; $m -lt 3; $m++) {
            $sx = 430 + $rnd.Next(120); $sy = $y + 4 + $rnd.Next(38)
            $sw = 4 + $rnd.Next(10); $sh = 4 + $rnd.Next(8)
            $hb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90 + ($hash -shr ($m+3)) % 150, 60 + ($hash -shr ($m+7)) % 170, 50 + ($hash -shr ($m+11)) % 180))
            $g.FillRectangle($hb, $sx, $sy, $sw, $sh)
            $hb.Dispose()
        }
    }
    $font.Dispose()
    $small.Dispose()
})
$form.Controls.Add($panel)
$form.Handle | Out-Null
[TopMostHelper]::MakeTopMost($form.Handle)
[System.Windows.Forms.Application]::Run($form)
