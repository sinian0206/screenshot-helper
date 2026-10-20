[CmdletBinding()]
param([string]$RunName = 'current', [string]$AssemblyPath = '')
$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName 格式无效。' }
$root = Split-Path -Parent $PSScriptRoot
if (-not $AssemblyPath) { $AssemblyPath = Join-Path $root 'dist\v1.2.2\截图助手.exe' }
$AssemblyPath = [IO.Path]::GetFullPath($AssemblyPath)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '未找到 Visual Studio MSBuild。' }
$out = Join-Path $root ('build_workflow\' + $RunName)
$obj = Join-Path $out 'obj'
New-Item -ItemType Directory -Force -Path $out | Out-Null
& $msbuild (Join-Path $PSScriptRoot 'workflow-regression.csproj') /restore /t:Build "/p:OutputPath=$out\" "/p:BaseIntermediateOutputPath=$obj\" "/p:MSBuildProjectExtensionsPath=$obj\" "/p:ScreenshotAssemblyPath=$AssemblyPath" /p:IncludeSourceRevisionInInformationalVersion=false /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw '工作流回归测试编译失败。' }
& (Join-Path $out 'WorkflowRegression.exe') 2>&1 | Tee-Object -FilePath (Join-Path $out 'report.txt')
exit $LASTEXITCODE
