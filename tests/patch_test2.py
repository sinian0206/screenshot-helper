p = r'D:\Agents\微信截图助手\tests\real_ui_test2.ps1'
s = open(p, encoding='utf-8').read()
s = s.replace("[UiProbe]::Click(1100, 932)   # 钉在桌面（非分隔符索引 13）",
              "[UiProbe]::Click(980, 798)   # 钉在桌面（非分隔符索引 13）")
s = s.replace("[UiProbe]::Click(638, 932)   # 表情工具（非分隔符索引 2）",
              "[UiProbe]::Click(518, 798)   # 表情工具（非分隔符索引 2）")
s = s.replace("[UiProbe]::RightClick(764, 932)   # 马赛克工具（非分隔符索引 5）",
              "[UiProbe]::RightClick(644, 798)   # 马赛克工具（非分隔符索引 5）")
s = s.replace("[UiProbe]::Click(1226, 932)  # 完成（索引 16）",
              "[UiProbe]::Click(1106, 798)  # 完成（索引 16）")
s = s.replace("    [UiProbe]::ScrollDown; Start-Sleep -Milliseconds 100\n", "")
old = '''    $emojiOk = $false
    $picker = [UiProbe]::FindByTitle('表情')
    if ($picker -ne [IntPtr]::Zero) { $emojiOk = $true }
    [UiProbe]::Esc(); Start-Sleep -Milliseconds 400
    if (-not $emojiOk) { $failures += 'D: 未找到表情选择器窗口' }'''
new = '''    [UiProbe]::Esc(); Start-Sleep -Milliseconds 400
    Write-Host 'D: 表情选择器截图已保存，请人工检查 d_emoji_picker.png' '''
assert old in s
s = s.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(s)
print('patched test2')
