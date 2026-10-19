p = r'D:\Agents\微信截图助手\tests\real_ui_test.ps1'
s = open(p, encoding='utf-8').read()
start = s.index("        $clipPath = Join-Path $out '6_clipboard.png'")
end = s.index("        if ($result -match '^[0-9]+x[0-9]+$') { $clipOk = $true }")
new = '''        $clipPath = Join-Path $out '6_clipboard.png'
        $clipScript = Join-Path $root 'tools\\clipboard_save.ps1'
        $result = & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File $clipScript -OutPath $clipPath
        Write-Host "剪贴板读取结果: $result"
'''
s = s[:start] + new + s[end:]
open(p, 'w', encoding='utf-8').write(s)
print('patched')
