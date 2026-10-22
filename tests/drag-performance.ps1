[CmdletBinding()]
param(
    [string]$RunName = 'current',
    [switch]$RecordBaseline,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName 包含不支持的字符。' }
$root = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio 实例发现工具未找到。' }
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild 未找到。' }
$compiler = Join-Path (Split-Path -Parent $msbuild) 'Roslyn\csc.exe'
$output = Join-Path $root ('build_drag_performance\' + $RunName)
$intermediate = Join-Path $root ('build_drag_performance\obj-' + $RunName)
$baseline = Join-Path $root 'build_drag_performance\baseline'
New-Item -ItemType Directory -Force -Path $output | Out-Null
if (-not $SkipBuild) {
    & $msbuild (Join-Path $root 'src\截图助手.csproj') /t:Build /p:Configuration=Release "/p:OutputPath=$output\" "/p:IntermediateOutputPath=$intermediate\" /p:IncludeSourceRevisionInInformationalVersion=false /verbosity:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'Visual Studio 构建失败。' }
}
$application = Join-Path $output '截图助手.exe'
$test = Join-Path $output 'drag-performance.exe'
& $compiler /nologo /target:exe /platform:anycpu "/out:$test" "/reference:$application" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'drag-performance.cs')
if ($LASTEXITCODE -ne 0) { throw '性能测试编译失败。' }
& $test $output $baseline ([string]$RecordBaseline.IsPresent)
exit $LASTEXITCODE
