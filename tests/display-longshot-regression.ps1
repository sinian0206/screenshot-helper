#requires -version 5.1
[CmdletBinding()]
param([string]$ReportName = 'green')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path $projectRoot 'build_longshot\display'
$testIntermediate = Join-Path $testOutput 'obj\'
$msbuild = Get-Command MSBuild.exe -ErrorAction Stop
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
& $msbuild.Source (Join-Path $PSScriptRoot 'display-longshot-regression.csproj') /restore /nologo /verbosity:minimal /p:Configuration=Release "/p:OutputPath=$testOutput\" "/p:BaseIntermediateOutputPath=$testIntermediate"
if ($LASTEXITCODE -ne 0) { throw "显示与长截图回归构建失败：$LASTEXITCODE" }
& (Join-Path $testOutput 'display-longshot-regression.exe') 2>&1 | Tee-Object -FilePath (Join-Path $testOutput ($ReportName + '.txt'))
exit $LASTEXITCODE
