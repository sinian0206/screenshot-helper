[CmdletBinding()]
param(
    [string]$RunName = 'current',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$compiler = Join-Path (Split-Path -Parent $msbuild) 'Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw "Visual Studio MSBuild 未找到: $msbuild" }
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName 只能包含字母、数字、下划线和连字符' }
$artifacts = Join-Path $PSScriptRoot 'editor-regression-artifacts'
$output = Join-Path $artifacts $RunName
$intermediate = Join-Path $artifacts ('obj-' + $RunName)
New-Item -ItemType Directory -Force -Path $output | Out-Null
if (-not $SkipBuild) {
    & $msbuild (Join-Path $root 'src\截图助手.csproj') /t:Build /p:Configuration=Release "/p:OutputPath=$output\" "/p:IntermediateOutputPath=$intermediate\" /p:IncludeSourceRevisionInInformationalVersion=false /verbosity:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "Visual Studio 构建失败: $LASTEXITCODE" }
}
$application = Join-Path $output '截图助手.exe'
$test = Join-Path $output 'editor-regression.exe'
& $compiler /nologo /target:exe /platform:anycpu "/out:$test" "/reference:$application" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'editor-regression.cs')
if ($LASTEXITCODE -ne 0) { throw "回归测试编译失败: $LASTEXITCODE" }
$report = & $test $output
$result = $LASTEXITCODE
$report | Set-Content -LiteralPath (Join-Path $output 'report.txt') -Encoding utf8
$report | Write-Output
exit $result
