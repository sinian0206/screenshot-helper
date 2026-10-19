# DeepSeek API mock 回归测试（开发/QA 用）
# 启动本地 mock 服务 → 运行微信截图助手 --selftest（带 --mock-url）→ 校验报告
param(
    [int]$Port = 18765,
    [string]$Exe = '',
    [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root
if (-not $Exe) {
    $Exe = Join-Path $root 'build_lead\微信截图助手.exe'
    if (-not (Test-Path $Exe)) { $Exe = Join-Path $root 'dist\微信截图助手.exe' }
}
if (-not (Test-Path $Exe)) { throw "找不到 exe: $Exe" }
if (-not $Report) { $Report = Join-Path $root 'tests\selftest-api.txt' }
$log = Join-Path $root 'tests\mock_requests.jsonl'
Remove-Item $log -ErrorAction SilentlyContinue

$py = Start-Process -FilePath 'python' -ArgumentList @('tools\mock_deepseek_server.py', '--port', $Port, '--log', $log, '--key', 'mock-key') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 900
try {
    $p = Start-Process -FilePath $Exe -ArgumentList @('--selftest', '--out', $Report, '--mock-url', "http://127.0.0.1:$Port", '--mock-key', 'mock-key') -PassThru -Wait
    Write-Host "selftest exit=$($p.ExitCode)"
    if (Test-Path $Report) { Get-Content $Report -Encoding UTF8 | Select-Object -Last 20 }
    $reqCount = 0
    if (Test-Path $log) { $reqCount = (Get-Content $log -Encoding UTF8 | Measure-Object).Count }
    Write-Host "mock requests: $reqCount"
    if ($p.ExitCode -ne 0) { exit 1 }
    if ($reqCount -lt 3) { throw "mock 请求数量异常: $reqCount" }
    # 校验请求体结构：模型名、视觉请求携带 data:image/png;base64、thinking 参数（如出现必须为 disabled）
    $records = Get-Content $log -Encoding UTF8
    $visionFound = $false
    $thinkingSeen = $false
    foreach ($line in $records) {
        $rec = $line | ConvertFrom-Json
        $raw = [string]$rec.body
        if ($raw -notmatch '"model"\s*:\s*"deepseek-flash"') { throw "model 字段异常（原始请求前 200 字）：$($raw.Substring(0, [Math]::Min(200, $raw.Length)))" }
        if ($raw -match '"thinking"\s*:\s*\{\s*"type"\s*:\s*"disabled"') { $thinkingSeen = $true }
        if ($raw -match 'data:image/(png|jpeg);base64,') { $visionFound = $true }
    }
    if (-not $visionFound) { throw '未检测到携带图片的视觉请求' }
    Write-Host ("mock 请求结构校验通过（视觉请求=是，thinking 参数出现={0}）" -f $thinkingSeen) -ForegroundColor Green
}
finally {
    if (-not $py.HasExited) { $py.Kill() }
}
