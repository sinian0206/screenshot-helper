p = r'D:\Agents\微信截图助手\tests\api_mock_test.ps1'
t = open(p, encoding='utf-8').read()
old_start = t.index('    # 校验请求体结构')
old_end = t.index('    Write-Host ("mock 请求结构校验通过')
new_block = '''    # 校验请求体结构：模型名、视觉请求携带 data:image/png;base64、thinking 参数（如出现必须为 disabled）
    $records = Get-Content $log -Encoding UTF8
    $visionFound = $false
    $thinkingSeen = $false
    foreach ($line in $records) {
        $rec = $line | ConvertFrom-Json
        $raw = [string]$rec.body
        if ($raw -notmatch '"model"\\s*:\\s*"deepseek-flash"') { throw "model 字段异常（原始请求前 200 字）：$($raw.Substring(0, [Math]::Min(200, $raw.Length)))" }
        if ($raw -match '"thinking"\\s*:\\s*\\{\\s*"type"\\s*:\\s*"disabled"') { $thinkingSeen = $true }
        if ($raw -match 'data:image/(png|jpeg);base64,') { $visionFound = $true }
    }
    if (-not $visionFound) { throw '未检测到携带图片的视觉请求' }
'''
t = t[:old_start] + new_block + t[old_end:]
open(p, 'w', encoding='utf-8').write(t)
print('patched')
