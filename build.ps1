#requires -version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutDir = 'dist',
    [string]$BuildDir = 'build',
    [string]$OcrRuntime = 'C:\Dev\Tools\RapidOCR\portable-v4',
    [switch]$Clean,
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
function Resolve-BuildPath([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { throw '构建路径不能为空。' }
    if ([IO.Path]::IsPathRooted($value)) { return [IO.Path]::GetFullPath($value) }
    return [IO.Path]::GetFullPath((Join-Path $root $value))
}
$buildFull = Resolve-BuildPath $BuildDir
$outFull = Resolve-BuildPath $OutDir
if ($buildFull.TrimEnd('\') -eq $root.TrimEnd('\') -or $outFull.TrimEnd('\') -eq $root.TrimEnd('\')) {
    throw '构建输出不能是项目根目录。'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = $null
if (Test-Path -LiteralPath $vswhere) {
    $msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $msbuild) { throw '未找到已有 Visual Studio MSBuild，请在 Visual Studio 中安装 .NET 桌面开发组件。' }
$manifest = Get-Content -Raw -LiteralPath (Join-Path $root 'assets\ocr-manifest.json') | ConvertFrom-Json
foreach ($file in $manifest.files) {
    $path = Join-Path $OcrRuntime $file.path
    if (-not (Test-Path -LiteralPath $path)) { throw "OCR 组件缺失：$path。请先执行 tools\fetch-ocr.ps1。" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "OCR 组件校验失败：$path" }
}
if (-not (Test-Path -LiteralPath (Join-Path $OcrRuntime 'ocr-licenses\NOTICE.txt'))) { throw 'OCR 运行时缺少许可证与来源说明。' }
New-Item -ItemType Directory -Force -Path $buildFull, $outFull | Out-Null
$intermediate = Join-Path $buildFull 'obj'
$target = if ($Clean) { 'Rebuild' } else { 'Build' }
$watch = [Diagnostics.Stopwatch]::StartNew()
& $msbuild (Join-Path $root '截图助手.sln') /restore "/t:$target" "/p:Configuration=$Configuration" "/p:OutputPath=$buildFull\" "/p:BaseIntermediateOutputPath=$intermediate\" "/p:MSBuildProjectExtensionsPath=$intermediate\" /p:IncludeSourceRevisionInInformationalVersion=false /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Visual Studio 构建失败：$LASTEXITCODE" }
& (Join-Path $root 'native\build-recorder.ps1') -Configuration $Configuration -OutDir (Join-Path $buildFull 'native')
if ($LASTEXITCODE -ne 0) { throw '录屏组件构建失败。' }
Copy-Item -LiteralPath (Join-Path $buildFull 'native\RecorderNative.dll') -Destination (Join-Path $outFull 'RecorderNative.dll') -Force
$builtExe = Join-Path $buildFull '截图助手.exe'
$exe = Join-Path $outFull '截图助手.exe'
if ($builtExe -ne $exe) { Copy-Item -LiteralPath $builtExe -Destination $exe -Force }
$ocrOut = Join-Path $outFull 'ocr'
New-Item -ItemType Directory -Force -Path $ocrOut | Out-Null
Get-ChildItem -LiteralPath $OcrRuntime | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $ocrOut -Recurse -Force }
$watch.Stop()
Write-Host ("产物：{0}（{1:N0} B）；OCR 组件：{2}" -f $exe, (Get-Item -LiteralPath $exe).Length, $ocrOut)
Write-Host ("SHA256：{0}；构建耗时：{1:N1}s" -f (Get-FileHash -LiteralPath $exe).Hash, $watch.Elapsed.TotalSeconds)
if ($Test) {
    $reportDir = Join-Path $buildFull 'selftest'
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $report = Join-Path $reportDir 'selftest-report.txt'
    $proc = Start-Process -FilePath $exe -ArgumentList ('--selftest --out "{0}"' -f $report) -WindowStyle Hidden -PassThru
    if (-not $proc.WaitForExit(30000)) { $proc.Kill(); throw '自检超过 30 秒。' }
    if (-not (Test-Path -LiteralPath $report)) { throw '自检未生成报告。' }
    $content = Get-Content -Raw -LiteralPath $report
    Write-Output $content
    if ($proc.ExitCode -ne 0 -or $content -match '\[FAIL\]|\[FATAL\]') { throw '自检失败。' }
}
