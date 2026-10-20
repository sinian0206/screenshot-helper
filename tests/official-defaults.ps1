[CmdletBinding()]
param([string]$RunName = 'current')
$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName 只能包含字母、数字、下划线和连字符。' }
$root = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '未找到 Visual Studio MSBuild。' }
$out = Join-Path $root ('build_official_defaults\' + $RunName)
$obj = Join-Path $out 'obj'
New-Item -ItemType Directory -Force -Path $out | Out-Null
& $msbuild (Join-Path $PSScriptRoot 'official-defaults.csproj') /restore /t:Build "/p:OutputPath=$out\" "/p:BaseIntermediateOutputPath=$obj\" "/p:MSBuildProjectExtensionsPath=$obj\" /p:IncludeSourceRevisionInInformationalVersion=false /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw '官方默认配置回归测试编译失败。' }
& (Join-Path $out 'OfficialDefaultsRegression.exe') (Join-Path $root 'config.example.json') 2>&1 | Tee-Object -FilePath (Join-Path $out 'report.txt')
exit $LASTEXITCODE
