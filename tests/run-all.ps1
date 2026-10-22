[CmdletBinding()]
param([string]$OcrRuntime = 'C:\Dev\Tools\RapidOCR\portable-v4', [string]$RunName = 'v1_2_2', [switch]$ContinueOnFailure)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName 格式无效。' }
$acceptance = Join-Path $root ('build_acceptance\' + $RunName)
$logs = Join-Path $acceptance 'logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '未找到 Visual Studio MSBuild。' }
$env:PATH = (Split-Path -Parent $msbuild) + ';' + $env:PATH
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$results = @()
& (Join-Path $root 'build.ps1') -OutDir (Join-Path $acceptance 'app') -BuildDir (Join-Path $acceptance 'compile') -OcrRuntime $OcrRuntime
$application = Join-Path $acceptance 'app\截图助手.exe'
function Run-Phase([string]$name, [string]$script, [string[]]$arguments) {
    $log = Join-Path $logs ($name + '.txt')
    Write-Host ('执行：' + $name)
    & $pwsh -NoProfile -File (Join-Path $PSScriptRoot $script) @arguments > $log 2>&1
    $code = $LASTEXITCODE
    $script:results += [pscustomobject]@{ Name = $name; ExitCode = $code; Log = $log }
    if ($code -ne 0) {
        Get-Content -LiteralPath $log -Tail 30
        if (-not $ContinueOnFailure) { throw "$name 失败，完整日志：$log" }
        Write-Host ("失败：$name，继续收集其他脚本结果。")
    } else { Write-Host ('通过：' + $name) }
}
Run-Phase 'local-ocr' 'ocr-local.ps1' @('-EnginePath', (Join-Path $OcrRuntime 'RapidOCR-json.exe'))
Run-Phase 'ocr-icons' 'ocr-icons.ps1' @('-EnginePath', (Join-Path $OcrRuntime 'RapidOCR-json.exe'))
Run-Phase 'official-defaults' 'official-defaults.ps1' @('-RunName', $RunName)
Run-Phase 'settings-menu' 'settings-menu-regression.ps1' @('-ReportName', $RunName)
Run-Phase 'drag-performance' 'drag-performance.ps1' @('-RunName', $RunName)
Run-Phase 'recording-ready' 'recording-ready.ps1' @('-ReportName', $RunName)
Run-Phase 'ocr-pipeline' 'ocr-pipeline.ps1' @('-OcrRuntime', $OcrRuntime)
Run-Phase 'privacy-speed' 'privacy-speed.ps1' @('-RunName', $RunName, '-EnginePath', (Join-Path $OcrRuntime 'RapidOCR-json.exe'))
Run-Phase 'editor' 'editor-regression.ps1' @('-RunName', $RunName)
Run-Phase 'object-editor' 'object-editor.ps1' @('-TestOut', (Join-Path $acceptance 'object-editor'), '-AppPath', $application)
Run-Phase 'workflow' 'workflow-regression.ps1' @('-RunName', $RunName, '-AssemblyPath', $application)
Run-Phase 'workflow-ui' 'workflow-ui-review.ps1' @('-ReportName', $RunName)
Run-Phase 'contrast' 'contrast-regression.ps1' @('-ReportName', $RunName)
Run-Phase 'longshot' 'longshot-regression.ps1' @('-ReportName', $RunName)
Run-Phase 'display-longshot' 'display-longshot-regression.ps1' @('-ReportName', $RunName)
Run-Phase 'recorder' 'recorder-regression.ps1' @('-ReportName', $RunName)
Run-Phase 'recorder-refresh' 'recorder-upgrade.ps1' @('-ReportName', $RunName)
Run-Phase 'recorder-media' 'recorder-media.ps1' @('-ReportName', $RunName)
Run-Phase 'portable-recording' 'portable-recording.ps1' @('-AppDir', (Split-Path -Parent $application), '-TestOut', (Join-Path $acceptance 'portable-recording'))
Run-Phase 'ui-integration' 'ui-integration.ps1' @('-AppPath', $application, '-TestOut', (Join-Path $acceptance 'ui'))
Run-Phase 'ui-controls' 'ui-controls-regression.ps1' @('-OutputDir', (Join-Path $acceptance 'ui-controls'))
Run-Phase 'ui-showcase' 'ui-showcase.ps1' @('-OutputDir', (Join-Path $acceptance 'ui-showcase'))
Run-Phase 'text-selection' 'text-selection-regression.ps1' @('-AppPath', $application, '-OutputDir', (Join-Path $acceptance 'text-selection'))
Run-Phase 'seven-features' 'seven-features-regression.ps1' @('-OutputDir', (Join-Path $acceptance 'seven-features'))
Run-Phase 'smoke' 'smoke.ps1' @('-BuildRetries', '1')
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logs 'summary.json') -Encoding utf8
if (@($results | Where-Object ExitCode -ne 0).Count -gt 0) { throw "存在失败脚本，汇总目录：$logs" }
Write-Host ('全部脚本通过，报告目录：' + $logs)
