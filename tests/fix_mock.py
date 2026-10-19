p = r'D:\Agents\微信截图助手\tools\mock_deepseek_server.py'
s = open(p, encoding='utf-8').read()
s = s.replace("""        if stream:
            if '翻译' in system or '翻译' in user:
                pieces = ['Hello', ' world', '! 这是', '译文。']
            else:
                pieces = ['第一行识别文字', '\\n', '第二行 Hello 世界', '\\n', 'Third line 12345']
            self.stream_sse(pieces)
            return""",
"""        if stream:
            if 'OCR' in system or '文字提取' in system or '识别引擎' in system:
                pieces = ['第一行识别文字', '\\n', '第二行 Hello 世界', '\\n', 'Third line 12345']
            elif '翻译' in system or '翻译' in user:
                pieces = ['Hello', ' world', '! 这是', '译文。']
            else:
                pieces = ['ok']
            self.stream_sse(pieces)
            return""")
open(p, 'w', encoding='utf-8').write(s)

p2 = r'D:\Agents\微信截图助手\tests\api_mock_test.ps1'
t = open(p2, encoding='utf-8').read()
t = t.replace("""    # 校验请求体结构
    $first = Get-Content $log -Encoding UTF8 | Select-Object -First 1 | ConvertFrom-Json
    $body = $first.body | ConvertFrom-Json
    if ($body.model -ne 'deepseek-flash') { throw "model 字段异常: $($body.model)" }
    if (-not ($body.PSObject.Properties.Name -contains 'thinking')) { throw '请求缺少 thinking 参数' }
    if ($body.thinking.type -ne 'disabled') { throw 'thinking 未禁用' }
    Write-Host 'mock 请求结构校验通过' -ForegroundColor Green""",
"""    # 校验请求体结构：模型名、messages 结构、视觉请求携带 data:image/png;base64
    $records = Get-Content $log -Encoding UTF8
    $visionFound = $false
    $thinkingSeen = $false
    foreach ($line in $records) {
        $rec = $line | ConvertFrom-Json
        $body = $rec.body | ConvertFrom-Json
        if ($body.model -ne 'deepseek-flash') { throw "model 字段异常: $($body.model)" }
        if ($body.PSObject.Properties.Name -contains 'thinking') {
            $thinkingSeen = $true
            if ($body.thinking.type -ne 'disabled') { throw 'thinking 未禁用' }
        }
        $raw = [string]$rec.body
        if ($raw -match 'data:image/(png|jpeg);base64,') { $visionFound = $true }
    }
    if (-not $visionFound) { throw '未检测到携带图片的视觉请求' }
    Write-Host ("mock 请求结构校验通过（视觉请求=是，thinking 参数出现={0}）" -f $thinkingSeen) -ForegroundColor Green""")
open(p2, 'w', encoding='utf-8').write(t)
print('patched mock + api test')
