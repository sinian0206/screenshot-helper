#requires -version 5.1
[CmdletBinding()]
param([string]$ReportName = 'acceptance')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path $projectRoot 'build_contrast'
$testIntermediate = Join-Path $testOutput 'obj\'
$msbuild = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
if (-not $msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuildPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
    if (-not $msbuildPath) { throw '未找到 Visual Studio MSBuild。' }
} else { $msbuildPath = $msbuild.Source }
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
& $msbuildPath (Join-Path $PSScriptRoot 'contrast-regression.csproj') /restore /nologo /verbosity:minimal /p:Configuration=Release "/p:OutputPath=$testOutput\" "/p:BaseIntermediateOutputPath=$testIntermediate"
if ($LASTEXITCODE -ne 0) { throw "文字对比度回归构建失败：$LASTEXITCODE" }
& (Join-Path $testOutput 'contrast-regression.exe') $ReportName 2>&1 | Tee-Object -FilePath (Join-Path $testOutput ($ReportName + '.txt'))
exit $LASTEXITCODE
