p = r'D:\Agents\微信截图助手\tools\scroll_target.ps1'
s = open(p, encoding='utf-8').read()
old = '''        $rnd = New-Object System.Random($row + 1)
        $c = [System.Drawing.Color]::FromArgb(60 + ($row * 7) % 160, 80 + ($row * 11) % 150, 100 + ($row * 13) % 140)
        $b = New-Object System.Drawing.SolidBrush($c)
        $g.FillRectangle($b, 520, $y + 8, 110, 32)
        $b.Dispose()'''
new = '''        # 每行唯一图案：用行号的 32 位哈希生成颜色与条形码，避免内容周期性导致拼接歧义
        $hash = [uint32](($row + 1) * 2654435761) -band 0xFFFFFFFF
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
        }'''
assert old in s
s = s.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(s)
print('scroll target content now non-periodic')
