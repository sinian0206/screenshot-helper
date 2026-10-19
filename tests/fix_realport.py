p = r'D:\Agents\微信截图助手\tests\real_ui_test.ps1'
s = open(p, encoding='utf-8').read()
s = s.replace('[int]$MockPort = 18080', '[int]$MockPort = 18999')
marker = '''$mock = Start-Process -FilePath 'python' -ArgumentList @('tools\\mock_deepseek_server.py', '--port', $MockPort, '--log', $mockLog, '--key', 'mock-key') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 900'''
new = '''$mock = Start-Process -FilePath 'python' -ArgumentList @('tools\\mock_deepseek_server.py', '--port', $MockPort, '--log', $mockLog, '--key', 'mock-key') -PassThru -WindowStyle Hidden
$mockReady = $false
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 200
    $conn = Get-NetTCPConnection -LocalPort $MockPort -State Listen -ErrorAction SilentlyContinue
    if ($conn) { $mockReady = $true; break }
}
if (-not $mockReady) { throw "mock 服务未能在端口 $MockPort 启动（端口被占用？）" }'''
assert marker in s
s = s.replace(marker, new, 1)
open(p, 'w', encoding='utf-8').write(s)
print('patched port 18999 + mock readiness check')
