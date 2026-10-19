p = r'D:\Agents\微信截图助手\tests\real_ui_test.ps1'
s = open(p, encoding='utf-8').read()
old = '''    $clipOk = $false
    try {
        $img = Get-Clipboard -Format Image
        if ($img -ne $null) {
            $clipPath = Join-Path $out '6_clipboard.png'
            $img.Save($clipPath, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Host ("剪贴板图片: {0}x{1}" -f $img.Width, $img.Height) -ForegroundColor Green
            $clipOk = $true
        }
    } catch { Write-Host "剪贴板读取失败: $($_.Exception.Message)" }
    if (-not $clipOk) { throw '完成按钮未把截图复制到剪贴板' }'''
new = '''    $clipOk = $false
    try {
        $clipPath = Join-Path $out '6_clipboard.png'
        $result = & powershell.exe -STA -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; `$img=[System.Windows.Forms.Clipboard]::GetImage(); if(`$img -ne `$null) { `$img.Save('$clipPath',[System.Drawing.Imaging.ImageFormat]::Png); Write-Output (''{0}x{1}'' -f `$img.Width, `$img.Height) } else { Write-Output 'EMPTY' }"
        Write-Host "剪贴板读取结果: $result"
        if ($result -match '^[0-9]+x[0-9]+$') { $clipOk = $true }
    } catch { Write-Host "剪贴板读取失败: $($_.Exception.Message)" }
    if (-not $clipOk) { throw '完成按钮未把截图复制到剪贴板' }'''
assert old in s
s = s.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(s)
print('patched')
