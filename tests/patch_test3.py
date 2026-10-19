p = r'D:\Agents\微信截图助手\tests\real_ui_test3.ps1'
s = open(p, encoding='utf-8').read()
s = s.replace("$mock = Start-Process -FilePath 'python' -ArgumentList @('tools\\mock_deepseek_server.py', '--port', $MockPort, '--log', $mockLog, '--key', 'mock-key') -PassThru -WindowStyle Hidden",
              "$mock = Start-Process -FilePath 'python' -ArgumentList @('tools\\mock_deepseek_server.py', '--port', $MockPort, '--log', $mockLog, '--key', 'mock-key', '--privacy-full') -PassThru -WindowStyle Hidden")
old = '''    $c3 = Save-Clipboard (Join-Path $out '3_clipboard.png')
    Write-Host "智能打码剪贴板: $c3"
    if ($c3 -notmatch '^840x500$') { $failures += "智能打码: 剪贴板尺寸异常 $c3" }'''
new = '''    $c3 = Save-Clipboard (Join-Path $out '3_clipboard.png')
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
    }'''
assert old in s
s = s.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(s)
print('patched test3')
