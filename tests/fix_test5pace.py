p = r'D:\Agents\微信截图助手\tests\real_ui_test5.ps1'
s = open(p, encoding='utf-8').read()
old = """    for ($i = 0; $i -lt 10; $i++) {
        [UiProbe]::WheelEvent(-120)
        Start-Sleep -Milliseconds 260
    }
    Start-Sleep -Milliseconds 900"""
new = """    for ($i = 0; $i -lt 8; $i++) {
        [UiProbe]::WheelEvent(-120)
        Start-Sleep -Milliseconds 850
    }
    Start-Sleep -Milliseconds 900"""
assert old in s
s = s.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(s)
print('patched pacing')
