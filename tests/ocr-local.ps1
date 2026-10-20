#requires -version 5.1
[CmdletBinding()]
param(
    [string]$EnginePath = 'C:\Dev\Tools\RapidOCR\portable-v4\RapidOCR-json.exe',
    [string]$OutDir = (Join-Path $PSScriptRoot 'ocr-local-results')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'src\LocalOcr.cs'
if (-not (Test-Path -LiteralPath $source)) { throw 'RED: 本地 OCR 模块尚未实现。' }
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '未找到 Visual Studio C# 编译器。' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$runner = Join-Path $OutDir 'ocr-local-tests.exe'
& $compiler /nologo /target:exe /optimize+ /r:System.Drawing.dll /r:System.Web.Extensions.dll /out:$runner $source (Join-Path $PSScriptRoot 'ocr-local-tests.cs')
if ($LASTEXITCODE -ne 0) { throw '本地 OCR 回归测试编译失败。' }
& $runner $EnginePath $OutDir 2>&1 | Tee-Object -FilePath (Join-Path $OutDir 'ocr-local-report.txt')
if ($LASTEXITCODE -ne 0) { throw '本地 OCR 回归测试失败。' }
