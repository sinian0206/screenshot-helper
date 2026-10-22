[CmdletBinding()]
param([string]$AppPath = 'dist\v1.2.2\截图助手.exe', [string]$RunName = 'current')
$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName 格式无效。' }
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($AppPath)) { $AppPath = Join-Path $projectRoot $AppPath }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$compiler = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
if (-not $compiler) { throw '未找到 Visual Studio C# 编译器。' }
$outputPath = Join-Path $projectRoot ('build_features\config-' + $RunName)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$runner = Join-Path $outputPath 'feature-config.exe'
& $compiler /nologo /target:exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$runner" (Join-Path $PSScriptRoot 'feature-config.cs')
if ($LASTEXITCODE -ne 0) { throw '配置行为测试编译失败。' }
& $runner $AppPath (Join-Path $outputPath 'fixtures') 2>&1 | Tee-Object -FilePath (Join-Path $outputPath 'report.txt')
exit $LASTEXITCODE
