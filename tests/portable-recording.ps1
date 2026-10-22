#requires -version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppDir,
    [string]$TestOut = '',
    [string]$VerifyPath = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$AppDir = [IO.Path]::GetFullPath($AppDir).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ([string]::IsNullOrWhiteSpace($TestOut)) {
    $TestOut = Join-Path $projectRoot ('build_acceptance\portable-recording-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$TestOut = [IO.Path]::GetFullPath($TestOut)
if ($TestOut -eq $AppDir -or $TestOut.StartsWith($AppDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '测试输出必须位于待验收绿色版目录之外，避免修改发布物。'
}
$sourceExe = Join-Path $AppDir '截图助手.exe'
$sourceDll = Join-Path $AppDir 'RecorderNative.dll'
foreach ($path in @($sourceExe, $sourceDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "发布物缺少文件: $path" }
}

function Get-DirectoryFingerprint([string]$Directory) {
    $result = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName)
        $result[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return ($result | ConvertTo-Json -Compress -Depth 4)
}

function Invoke-TestProcess([string]$File, [string[]]$Arguments, [string]$WorkingDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(20000)) { $process.Kill($true); throw "验证进程超过 20 秒: $File" }
        $text = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "验证进程失败 ($($process.ExitCode)): $text $errorText" }
        return $text.Trim()
    } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

$before = Get-DirectoryFingerprint $AppDir
$applicationOut = Join-Path $TestOut 'app'
$mediaOut = Join-Path $TestOut 'media'
New-Item -ItemType Directory -Force -Path $applicationOut, $mediaOut | Out-Null
$report = [Collections.Generic.List[string]]::new()
$report.Add('绿色版录制独立验收：只编译测试 runner 与媒体解码器，不构建应用源码。')
$report.Add("待验收目录: $AppDir")
try {
    foreach ($name in @('截图助手.exe', 'RecorderNative.dll')) {
        $source = Join-Path $AppDir $name
        $target = Join-Path $applicationOut $name
        Copy-Item -LiteralPath $source -Destination $target -Force
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $sourceHash) { throw "测试副本哈希不一致: $name" }
        $report.Add("[PASS] 原包与测试副本 SHA256 一致: $name $sourceHash")
    }
    $msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
    $compiler = Join-Path (Split-Path -Parent $msbuild) 'Roslyn\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw 'Visual Studio C# 编译器未找到。' }
    $runner = Join-Path $applicationOut 'portable-recording.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ "/out:$runner" "/reference:$sourceExe" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'portable-recording.cs')
    if ($LASTEXITCODE -ne 0) { throw '绿色版录制测试 runner 编译失败。' }
    if ([string]::IsNullOrWhiteSpace($VerifyPath)) {
        $VerifyPath = Join-Path $projectRoot 'build_recorder\upgrade\recorder-media-verify.exe'
        if (-not (Test-Path -LiteralPath $VerifyPath)) {
            $verifyOut = Join-Path $TestOut 'verifier'
            & $msbuild (Join-Path $PSScriptRoot 'recorder-media-verify.vcxproj') /nologo /v:minimal /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$verifyOut\" "/p:IntDir=$verifyOut\obj\"
            if ($LASTEXITCODE -ne 0) { throw '现有媒体解码验证器构建失败。' }
            $VerifyPath = Join-Path $verifyOut 'recorder-media-verify.exe'
        }
    }
    $VerifyPath = [IO.Path]::GetFullPath($VerifyPath)
    if (-not (Test-Path -LiteralPath $VerifyPath)) { throw "媒体解码器不存在: $VerifyPath" }
    $runnerText = Invoke-TestProcess $runner @($mediaOut) $applicationOut
    $report.Add($runnerText)
    foreach ($kind in @('writer', 'session')) {
        $path = Join-Path $mediaOut ("portable-$kind.mp4")
        $decoded = Invoke-TestProcess $VerifyPath @($path) $applicationOut
        $decoded | Set-Content -LiteralPath (Join-Path $TestOut ("decode-$kind.json")) -Encoding utf8
        $metadata = $decoded | ConvertFrom-Json
        if (-not $metadata.h264 -or $metadata.aac -or $metadata.audioValues -ne 0) { throw "$kind 编码或音轨与 H.264 无声配置不一致: $decoded" }
        $expectedWidth = if ($kind -eq 'writer') { 322 } else { 320 }
        $expectedHeight = if ($kind -eq 'writer') { 182 } else { 180 }
        if ($metadata.width -ne $expectedWidth -or $metadata.height -ne $expectedHeight) { throw "$kind 编码尺寸错误: $decoded" }
        if ($kind -eq 'writer') {
            if ($metadata.frames -ne 5 -or [Math]::Abs($metadata.videoSeconds - 0.5) -gt 0.025) { throw "5 帧测试的时长/帧数不一致: $decoded" }
        } elseif ($metadata.frames -lt 4 -or $metadata.frames -gt 10 -or $metadata.videoSeconds -lt 0.4 -or $metadata.videoSeconds -gt 1.0) {
            throw "500 ms 录制会话帧数/时长超出范围: $decoded"
        }
        if ($metadata.topRed -le 140 -or $metadata.bottomRed -ge 90) { throw "$kind 解码画面颜色或方向错误: $decoded" }
        $report.Add("[PASS] $kind 媒体解码验证: $decoded")
    }
    $after = Get-DirectoryFingerprint $AppDir
    if ($before -ne $after) { throw '测试前后原包内容发生变化，目录指纹不一致。' }
    $report.Add('[PASS] 测试前后原包完整目录 SHA256 指纹一致；未创建或修改原包配置。')
    $before | Set-Content -LiteralPath (Join-Path $TestOut 'source-fingerprint.json') -Encoding utf8
    $report.Add('结果: 全部通过')
} catch {
    $report.Add('[FAIL] ' + $_.Exception.Message)
    throw
} finally {
    $report | Set-Content -LiteralPath (Join-Path $TestOut 'report.txt') -Encoding utf8
    $report | Write-Output
}
Write-Output "报告: $TestOut\report.txt"
