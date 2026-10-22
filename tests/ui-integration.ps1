#requires -version 7.0
[CmdletBinding()]
param(
    [string]$TestOut = (Join-Path $PSScriptRoot 'ui-integration-artifacts'),
    [string]$AppPath = '',
    [string]$EnginePath = 'C:\Dev\Tools\RapidOCR\portable-v4\RapidOCR-json.exe'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$TestOut = [IO.Path]::GetFullPath($TestOut)
$output = Join-Path $TestOut 'app'
$intermediate = Join-Path $TestOut 'obj'
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$compiler = Join-Path (Split-Path -Parent $msbuild) 'Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw 'Visual Studio MSBuild 未找到。' }
if (-not (Test-Path -LiteralPath $EnginePath)) { throw "OCR 引擎未找到: $EnginePath" }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$application = Join-Path $output '截图助手.exe'
if ([string]::IsNullOrWhiteSpace($AppPath)) {
    & $msbuild (Join-Path $root 'src\截图助手.csproj') /t:Build /p:Configuration=Release "/p:OutputPath=$output\" "/p:IntermediateOutputPath=$intermediate\" /p:IncludeSourceRevisionInInformationalVersion=false /verbosity:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'Visual Studio 构建失败。' }
} else {
    $AppPath = [IO.Path]::GetFullPath($AppPath)
    if ($AppPath -ne $application) { Copy-Item -LiteralPath $AppPath -Destination $application -Force }
}
$runner = Join-Path $output 'ui-integration.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ "/out:$runner" "/reference:$application" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'ui-integration.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI 集成脚本编译失败。' }
$process = [Diagnostics.Process]::new()
$process.StartInfo.FileName = $runner
$process.StartInfo.ArgumentList.Add([IO.Path]::GetFullPath($EnginePath))
$process.StartInfo.ArgumentList.Add($TestOut)
$process.StartInfo.WorkingDirectory = $output
$process.StartInfo.UseShellExecute = $false
$process.StartInfo.CreateNoWindow = $true
$process.StartInfo.RedirectStandardOutput = $true
$process.StartInfo.RedirectStandardError = $true
$process.StartInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$process.StartInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
try {
    $null = $process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        $process.WaitForExit(3000) | Out-Null
        throw "UI 集成测试超过 30 秒，已结束自己的测试进程及其子进程。报告目录: $TestOut"
    }
    $stdout.GetAwaiter().GetResult() | Write-Output
    $errorOutput = $stderr.GetAwaiter().GetResult()
    if ($errorOutput) { $errorOutput | Write-Output }
    if ($process.ExitCode -ne 0) { throw "UI 逻辑集成测试失败，见 $TestOut\report.txt" }
} finally {
    if (-not $process.HasExited) { $process.Kill($true) }
    $process.Dispose()
}
Write-Output "报告: $TestOut\report.txt"
