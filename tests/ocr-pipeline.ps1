[CmdletBinding()]
param([string]$OcrRuntime = 'C:\Dev\Tools\RapidOCR\portable-v4')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$msbuild = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
if (-not $msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
} else { $msbuildPath = $msbuild.Source }
if (-not $msbuildPath) { throw '未找到 Visual Studio MSBuild。' }
$out = Join-Path $root 'build_ocr_regression\net48'
& $msbuildPath (Join-Path $PSScriptRoot 'OcrPipelineRegression.csproj') /restore /t:Build /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'OCR 流水线测试编译失败。' }
$ocrOut = Join-Path $out 'ocr'
New-Item -ItemType Directory -Force -Path $ocrOut | Out-Null
Get-ChildItem -LiteralPath $OcrRuntime | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $ocrOut -Recurse -Force }
& (Join-Path $out 'OcrPipelineRegression.exe') 2>&1 | Tee-Object -FilePath (Join-Path $out 'report.txt')
if ($LASTEXITCODE -ne 0) { throw 'OCR 流水线回归失败。' }
