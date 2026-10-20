#requires -version 5.1
[CmdletBinding()]
param([string]$OutputDir = 'build_ui\seven-features')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($OutputDir)) { $OutputDir = Join-Path $root $OutputDir }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$buildDir = Join-Path $OutputDir 'build'
New-Item -ItemType Directory -Force -Path $OutputDir, $buildDir | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '未找到 Visual Studio MSBuild。' }

& $msbuild (Join-Path $PSScriptRoot 'seven-features-regression.csproj') /restore /t:Build `
    '/p:Configuration=Release' '/p:Platform=x64' `
    "/p:OutputPath=$buildDir\" "/p:BaseIntermediateOutputPath=$buildDir\obj\" `
    "/p:IntermediateOutputPath=$buildDir\obj\" "/p:MSBuildProjectExtensionsPath=$buildDir\obj\" `
    /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "七项功能回归编译失败：$LASTEXITCODE" }

$runner = Join-Path $buildDir 'seven-features-regression.exe'
& $runner 2>&1 | Tee-Object -FilePath (Join-Path $OutputDir 'report.txt')
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) { throw "七项功能回归失败：$exitCode" }
