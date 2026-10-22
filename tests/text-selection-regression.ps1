#requires -version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppPath,
    [string]$OutputDir = 'build_text_selection'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$appPathFull = [IO.Path]::GetFullPath($AppPath)
if ((Test-Path -LiteralPath $appPathFull -PathType Leaf) -and ([IO.Path]::GetExtension($appPathFull) -ieq '.exe')) {
    $application = $appPathFull
    $app = Split-Path -Parent $application
} else {
    $app = $appPathFull
    $application = Join-Path $app '截图助手.exe'
}
if (-not (Test-Path -LiteralPath $application -PathType Leaf)) { throw "主程序不存在：$application" }
$out = if ([IO.Path]::IsPathFullyQualified($OutputDir)) {
    [IO.Path]::GetFullPath($OutputDir)
} else {
    [IO.Path]::GetFullPath((Join-Path $root $OutputDir))
}
$build = Join-Path $out 'build'
New-Item -ItemType Directory -Force -Path $out, $build | Out-Null
$msbuild = & (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe') -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '未找到 Visual Studio MSBuild。' }
$runner = Join-Path $build 'text-selection-regression.exe'
$compiler = Join-Path (Split-Path -Parent $msbuild) 'Roslyn\csc.exe'
& $compiler /nologo /target:exe /platform:anycpu "/out:$runner" "/reference:$application" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'text-selection-regression.cs')
if ($LASTEXITCODE -ne 0) { throw "文字选择回归编译失败：$LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $app 'RecorderNative.dll') -Destination $build -Force
Copy-Item -LiteralPath (Join-Path $app 'ocr') -Destination $build -Recurse -Force
& $runner $app 2>&1 | Tee-Object -FilePath (Join-Path $out 'report.txt')
if ($LASTEXITCODE -ne 0) { throw "文字选择回归失败：$LASTEXITCODE" }
