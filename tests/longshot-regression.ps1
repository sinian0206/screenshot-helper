#requires -version 5.1
[CmdletBinding()]
param([string]$ReportName = 'green')

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path $projectRoot 'build_longshot\regression'
$testIntermediate = Join-Path $testOutput 'obj\'
$msbuild = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
if (-not $msbuild) {
    throw '需要既有 Visual Studio MSBuild；未安装或未配置时请先配置开发环境。'
}
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
& $msbuild.Source (Join-Path $PSScriptRoot 'longshot-regression.csproj') /restore /nologo /verbosity:minimal /p:Configuration=Release "/p:OutputPath=$testOutput\" "/p:BaseIntermediateOutputPath=$testIntermediate"
if ($LASTEXITCODE -ne 0) { throw "长截图回归项目构建失败：$LASTEXITCODE" }
& (Join-Path $testOutput 'longshot-regression.exe') 2>&1 | Tee-Object -FilePath (Join-Path $testOutput ($ReportName + '.txt'))
exit $LASTEXITCODE
