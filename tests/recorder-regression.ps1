#requires -version 5.1
[CmdletBinding()]
param([string]$ReportName = 'green')

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path $projectRoot 'build_recorder\regression'
$testIntermediate = Join-Path $testOutput 'obj\'
$msbuild = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
if (-not $msbuild) { throw '未找到既有 Visual Studio MSBuild。' }
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
& $msbuild.Source (Join-Path $PSScriptRoot 'recorder-regression.csproj') /restore /nologo /verbosity:minimal /p:Configuration=Release "/p:OutputPath=$testOutput\" "/p:BaseIntermediateOutputPath=$testIntermediate"
if ($LASTEXITCODE -ne 0) { throw "录屏回归项目构建失败：$LASTEXITCODE" }
& (Join-Path $testOutput 'recorder-regression.exe') $testOutput 2>&1 | Tee-Object -FilePath (Join-Path $testOutput ($ReportName + '.txt'))
exit $LASTEXITCODE
