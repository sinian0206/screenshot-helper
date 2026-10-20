[CmdletBinding()]
param([string]$RunName = 'current')
$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName 格式无效。' }
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root ('build_privacy_rules\' + $RunName)
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
& $msbuild (Join-Path $PSScriptRoot 'privacy-rules.csproj') /restore /t:Build /v:minimal /nologo "/p:OutputPath=$output\" "/p:BaseIntermediateOutputPath=$output\obj\" "/p:IntermediateOutputPath=$output\obj\" "/p:MSBuildProjectExtensionsPath=$output\obj\"
if ($LASTEXITCODE -ne 0) { throw '隐私规则回归编译失败。' }
& (Join-Path $output 'PrivacyRulesRegression.exe') 2>&1 | Tee-Object -FilePath (Join-Path $output 'report.txt')
if ($LASTEXITCODE -ne 0) { throw '隐私规则回归失败。' }
