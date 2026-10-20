#requires -version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutDir = 'dist',
    [string]$BuildDir = 'build',
    [string]$OcrRuntime = 'C:\Dev\Tools\RapidOCR\portable-v4',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $root $OutDir }
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not $SkipBuild) { & (Join-Path $root 'build.ps1') -Configuration $Configuration -OutDir $OutDir -BuildDir $BuildDir -OcrRuntime $OcrRuntime -Test }
$exe = Join-Path $OutDir '截图助手.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "未找到产物：$exe" }
$version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion -replace '\.0$', ''
$packageName = '截图助手_v' + $version + '_绿色版'
$stage = Join-Path $root ('build_package\' + $packageName + '_' + [DateTime]::UtcNow.Ticks)
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath $exe -Destination $stage
Copy-Item -LiteralPath (Join-Path $OutDir 'RecorderNative.dll') -Destination $stage
Copy-Item -LiteralPath (Join-Path $OutDir 'ocr') -Destination (Join-Path $stage 'ocr') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'config.example.json') -Destination $stage
foreach ($doc in @('使用说明', '功能清单', '本次优化')) {
    $source = Join-Path $root ('docs\' + $doc + '.md')
    [IO.File]::WriteAllText((Join-Path $stage ($doc + '.txt')), [IO.File]::ReadAllText($source), (New-Object Text.UTF8Encoding($true)))
}
$zip = Join-Path $OutDir ($packageName + '.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    if ($archive.Entries.FullName -contains 'config.json') { throw '分发包不应包含用户配置或 API Key。' }
    foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse) {
        $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $entry = $archive.GetEntry($relative)
        if ($null -eq $entry) { throw "压缩包缺少文件：$relative" }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "压缩包文件校验失败：$relative" }
    }
}
finally { $archive.Dispose() }
Write-Host ("绿色包：{0}（{1:N2} MiB）" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
Write-Host ("SHA256：{0}" -f (Get-FileHash -LiteralPath $zip).Hash)
