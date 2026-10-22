[CmdletBinding()]
param(
    [string]$RunName = 'current',
    [string]$EnginePath = 'C:\Dev\Tools\RapidOCR\portable-v4\RapidOCR-json.exe'
)
$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName 格式无效。' }
$root = Split-Path -Parent $PSScriptRoot
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$output = Join-Path $root ('build_privacy_speed\' + $RunName)
& $msbuild (Join-Path $PSScriptRoot 'privacy-speed.csproj') /restore /t:Build /v:minimal /nologo "/p:OutputPath=$output\" "/p:IntermediateOutputPath=$output\obj\"
if ($LASTEXITCODE -ne 0) { throw '智能打码速度回归编译失败。' }
& (Join-Path $output 'PrivacySpeedRegression.exe') $EnginePath 2>&1 | Tee-Object -FilePath (Join-Path $output 'report.txt')
if ($LASTEXITCODE -ne 0) { throw '智能打码速度回归失败。' }
