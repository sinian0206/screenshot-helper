#requires -version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ZipPath = [IO.Path]::GetFullPath($ZipPath)
if (-not $OutDir) { $OutDir = Join-Path $root ('build_acceptance\package-' + [DateTime]::UtcNow.Ticks) }
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (Test-Path -LiteralPath $OutDir) { throw '解压验收目录必须是新目录，避免覆盖已有数据。' }
$app = Join-Path $OutDir 'app'
New-Item -ItemType Directory -Path $app -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    foreach ($entry in $archive.Entries) {
        $target = [IO.Path]::GetFullPath((Join-Path $app $entry.FullName))
        if (-not $target.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '压缩包路径越界。' }
        if ([IO.Path]::GetFileName($entry.FullName) -eq 'config.json') { throw '分发包包含用户配置。' }
    }
} finally { $archive.Dispose() }
Expand-Archive -LiteralPath $ZipPath -DestinationPath $app
$exe = Join-Path $app '截图助手.exe'
foreach ($relative in @('截图助手.exe', 'RecorderNative.dll', 'ocr\RapidOCR-json.exe', 'ocr\ocr-licenses\NOTICE.txt', 'config.example.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $relative))) { throw "解压缺少文件：$relative" }
}
$example = Get-Content -Raw -LiteralPath (Join-Path $app 'config.example.json') | ConvertFrom-Json
if ($example.ApiKeyEnc -or $example.BaseUrl -ne 'https://api.deepseek.com') { throw '示例配置包含凭据或默认地址不符。' }
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$results = [Collections.Generic.List[object]]::new()
function Run-Phase([string]$name, [string]$script, [string[]]$arguments) {
    $log = Join-Path $OutDir ($name + '.txt')
    & $pwsh -NoProfile -File (Join-Path $PSScriptRoot $script) @arguments > $log 2>&1
    $code = $LASTEXITCODE
    $results.Add([pscustomobject]@{ Name = $name; ExitCode = $code; Log = $log })
    if ($code -ne 0) { Get-Content -LiteralPath $log -Tail 20; throw "解压验收 $name 失败。" }
    Write-Output ("[PASS] " + $name)
}
Run-Phase 'icons' 'ocr-icons.ps1' @('-EnginePath', (Join-Path $app 'ocr\RapidOCR-json.exe'), '-AppPath', $exe, '-OutDir', (Join-Path $OutDir 'icons'))
Run-Phase 'workflow' 'workflow-regression.ps1' @('-RunName', ('package_' + [DateTime]::UtcNow.Ticks), '-AssemblyPath', $exe)
Run-Phase 'objects' 'object-editor.ps1' @('-AppPath', $exe, '-TestOut', (Join-Path $OutDir 'objects'))
Run-Phase 'ui' 'ui-integration.ps1' @('-AppPath', $exe, '-EnginePath', (Join-Path $app 'ocr\RapidOCR-json.exe'), '-TestOut', (Join-Path $OutDir 'ui'))
Run-Phase 'recording' 'portable-recording.ps1' @('-AppDir', $app, '-TestOut', (Join-Path $OutDir 'recording'))
$selftest = Join-Path $OutDir 'selftest'
New-Item -ItemType Directory -Path $selftest -Force | Out-Null
$report = Join-Path $selftest 'report.txt'
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.ArgumentList.Add('--selftest'); $start.ArgumentList.Add('--out'); $start.ArgumentList.Add($report)
$start.UseShellExecute = $false; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$process = [Diagnostics.Process]::Start($start)
try {
    if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw '解压主程序自检超时。' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report) -or
        (Get-Content -Raw -LiteralPath $report) -notmatch '结果: 全部通过') { throw '解压主程序自检失败。' }
} finally { $process.Dispose() }
$results.Add([pscustomobject]@{ Name = 'selftest'; ExitCode = 0; Log = $report })
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'summary.json') -Encoding utf8
Get-FileHash -LiteralPath $exe, (Join-Path $app 'RecorderNative.dll'), $ZipPath | Format-Table -AutoSize
Write-Output ('独立解压验收全部通过：' + $OutDir)
