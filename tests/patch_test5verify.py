p = r'D:\Agents\微信截图助手\tests\real_ui_test5.ps1'
s = open(p, encoding='utf-8').read()
anchor = '''            Write-Host "左侧绿色像素计数: $green"
            if ($green -gt 5) { $failures += "长截图左侧疑似有绿色边框侵入（green=$green）" }'''
add = '''            Write-Host "左侧绿色像素计数: $green"
            if ($green -gt 5) { $failures += "长截图左侧疑似有绿色边框侵入（green=$green）" }
            # 内容连续性校验（行色块唯一，允许滚动起点偏移）
            $verify = & python (Join-Path $root 'tests\verify_stitch.py') $clipPath 2>&1
            Write-Host ("内容连续性校验: " + ($verify -join ' '))
            if (($verify -join ' ') -notmatch '^PASS') { $failures += ("长截图内容不连续/有重复/错序: " + ($verify -join ' ')) }'''
assert anchor in s
s = s.replace(anchor, add, 1)
open(p, 'w', encoding='utf-8').write(s)
print('patched test5 continuity check')
