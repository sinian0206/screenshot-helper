#requires -version 5.1
[CmdletBinding()]
param([string]$ReportName = 'acceptance')
$ErrorActionPreference = 'Stop'
if ($ReportName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'ReportName 格式无效。' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path $projectRoot 'build_settings_menu'
$testIntermediate = Join-Path $testOutput 'obj\'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuildPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $msbuildPath) { throw '未找到 Visual Studio MSBuild。' }
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
& $msbuildPath (Join-Path $PSScriptRoot 'settings-menu-regression.csproj') /restore /nologo /verbosity:minimal /p:Configuration=Release "/p:OutputPath=$testOutput\" "/p:BaseIntermediateOutputPath=$testIntermediate"
if ($LASTEXITCODE -ne 0) { throw "设置菜单回归构建失败：$LASTEXITCODE" }
& (Join-Path $testOutput 'settings-menu-regression.exe') 2>&1 | Tee-Object -FilePath (Join-Path $testOutput ($ReportName + '.txt'))
exit $LASTEXITCODE
