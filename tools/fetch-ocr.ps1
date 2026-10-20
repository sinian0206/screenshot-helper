#requires -version 5.1
<#
下载已固定哈希的官方 RapidOCR-json 包，并裁剪为简中英文 PP-OCRv4 运行时。
默认复用 C:\Dev，不安装环境、不注册组件、不把大模型写入源码目录。
#>
[CmdletBinding()]
param(
    [string]$StoreRoot = 'C:\Dev\Tools\RapidOCR',
    [string]$SevenZipPath = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root 'assets\ocr-manifest.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$store = [IO.Path]::GetFullPath($StoreRoot)
$archive = Join-Path $store 'RapidOCR-json_v0.2.0.7z'
$extract = Join-Path $store 'v0.2.0'
$source = Join-Path $extract 'RapidOCR-json_v0.2.0'
$portable = Join-Path $store 'portable-v4'
New-Item -ItemType Directory -Path $store -Force | Out-Null

if (-not (Test-Path -LiteralPath $archive)) {
    Write-Host '下载 RapidOCR-json v0.2.0 官方包（约 70 MiB）...'
    Invoke-WebRequest -Uri $manifest.archive_url -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $manifest.archive_sha256) {
    throw "OCR 压缩包 SHA256 不匹配，已停止。请检查文件：$archive"
}

$missing = @($manifest.files | Where-Object { -not (Test-Path -LiteralPath (Join-Path $source $_.path)) })
if ($missing.Count -gt 0) {
    if ([string]::IsNullOrWhiteSpace($SevenZipPath)) {
        $command = Get-Command 7z.exe -ErrorAction SilentlyContinue
        if ($command) { $SevenZipPath = $command.Source }
        if (-not $SevenZipPath) {
            $toolRoot = 'C:\Dev\vcpkg\downloads\tools'
            if (Test-Path -LiteralPath $toolRoot) {
                $candidate = Get-ChildItem -LiteralPath $toolRoot -Directory -Filter '7zip*' |
                    ForEach-Object { Join-Path $_.FullName '7z.exe' } |
                    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
                if ($candidate) { $SevenZipPath = $candidate }
            }
        }
    }
    if (-not $SevenZipPath -or -not (Test-Path -LiteralPath $SevenZipPath)) {
        throw '未找到已有的 7z.exe。请用 -SevenZipPath 指定 C:\Dev 中可用的 7-Zip。'
    }
    $entries = @($manifest.files | ForEach-Object { 'RapidOCR-json_v0.2.0/' + $_.path })
    & $SevenZipPath x $archive "-o$extract" @entries -y
    if ($LASTEXITCODE -ne 0) { throw 'OCR 压缩包解压失败。' }
}

foreach ($file in $manifest.files) {
    $from = Join-Path $source $file.path
    if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ne $file.sha256) {
        throw "OCR 文件 SHA256 不匹配：$from"
    }
    $target = Join-Path $portable $file.path
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $from -Destination $target -Force
}
$licenses = Join-Path $root 'assets\ocr-licenses'
if (-not (Test-Path -LiteralPath (Join-Path $licenses 'NOTICE.txt'))) { throw 'OCR 许可证与来源说明缺失。' }
Copy-Item -LiteralPath $licenses -Destination $portable -Recurse -Force
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $portable 'manifest.json') -Force
$bytes = (Get-ChildItem -LiteralPath $portable -File -Recurse | Measure-Object Length -Sum).Sum
Write-Host ("OCR 运行时就绪：{0}（{1:N2} MiB）" -f $portable, ($bytes / 1MB))
Write-Output $portable
