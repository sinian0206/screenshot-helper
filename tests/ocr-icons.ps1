#requires -version 5.1
[CmdletBinding()]
param(
    [string]$EnginePath = 'C:\Dev\Tools\RapidOCR\portable-v4\RapidOCR-json.exe',
    [string]$OutDir = (Join-Path $PSScriptRoot 'ocr-icons-results'),
    [string]$AppPath = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '未找到 Visual Studio C# 编译器。' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$runner = Join-Path $OutDir 'ocr-icons-tests.exe'
if ($AppPath) {
    $AppPath = [IO.Path]::GetFullPath($AppPath)
    Copy-Item -LiteralPath $AppPath -Destination (Join-Path $OutDir '截图助手.exe') -Force
    & $compiler /nologo /target:exe /optimize+ /r:System.Drawing.dll /r:System.Web.Extensions.dll "/reference:$AppPath" /out:$runner (Join-Path $PSScriptRoot 'ocr-icons-tests.cs')
} else {
    & $compiler /nologo /target:exe /optimize+ /r:System.Drawing.dll /r:System.Web.Extensions.dll /out:$runner (Join-Path $root 'src\LocalOcr.cs') (Join-Path $PSScriptRoot 'ocr-icons-tests.cs')
}
if ($LASTEXITCODE -ne 0) { throw '图标误识别测试编译失败。' }
& $runner $EnginePath (Join-Path $PSScriptRoot 'fixtures\ocr-icons-user.png') $OutDir 2>&1 | Tee-Object -FilePath (Join-Path $OutDir 'report.txt')
if ($LASTEXITCODE -ne 0) { throw '图标误识别回归测试失败。' }
