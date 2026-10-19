#requires -version 5.1
<#
  微信截图助手 - 构建脚本（初版，由构建/文档负责人继续完善）
  用法:
    pwsh -File build.ps1                 # 构建到 dist\微信截图助手.exe
    pwsh -File build.ps1 -OutDir out     # 自定义输出目录
    pwsh -File build.ps1 -Clean          # 清理后构建
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutDir = 'dist',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src\微信截图助手.csproj'
$buildDir = Join-Path $root 'build'
$outFull = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }

if ($Clean) {
    if (Test-Path $buildDir) { Remove-Item $buildDir -Recurse -Force }
    if (Test-Path $outFull) { Remove-Item $outFull -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $outFull | Out-Null

$sw = [System.Diagnostics.Stopwatch]::StartNew()
dotnet build $proj -c $Configuration -o $buildDir -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败 (exit $LASTEXITCODE)" }
$sw.Stop()

$builtExe = Join-Path $buildDir '微信截图助手.exe'
if (-not (Test-Path $builtExe)) { throw "未找到构建产物: $builtExe" }
$targetExe = Join-Path $outFull '微信截图助手.exe'
Copy-Item $builtExe $targetExe -Force

$sizeKB = [math]::Round((Get-Item $targetExe).Length / 1KB, 1)
Write-Host ("构建成功: {0}  ({1} KB, {2:N1}s)" -f $targetExe, $sizeKB, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
