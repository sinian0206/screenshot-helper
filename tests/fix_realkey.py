p = r'D:\Agents\微信截图助手\tests\real_ui_test.ps1'
s = open(p, encoding='utf-8').read()
old = '''# 准备测试配置（mock API + 保存到 tests\\real\\shots）
$exeDir = Split-Path -Parent $Exe
$saveDir = Join-Path $out 'shots'
New-Item -ItemType Directory -Force $saveDir | Out-Null
$cfg = @{
    Version = 1; ApiKeyEnc = ''; BaseUrl = "http://127.0.0.1:$MockPort"; VisionModel = 'deepseek-flash';'''
new = '''# 准备测试配置（mock API + 保存到 tests\\real\\shots；API Key 用 DPAPI 加密写入，与 ConfigStore 一致）
$exeDir = Split-Path -Parent $Exe
$saveDir = Join-Path $out 'shots'
New-Item -ItemType Directory -Force $saveDir | Out-Null
Add-Type -AssemblyName System.Security
$keyBytes = [System.Text.Encoding]::UTF8.GetBytes('mock-key')
$protected = [System.Security.Cryptography.ProtectedData]::Protect($keyBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$apiKeyEnc = [Convert]::ToBase64String($protected)
$cfg = @{
    Version = 1; ApiKeyEnc = $apiKeyEnc; BaseUrl = "http://127.0.0.1:$MockPort"; VisionModel = 'deepseek-flash';'''
assert old in s, 'config block not found'
s = s.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(s)
print('patched real_ui_test with DPAPI key')
