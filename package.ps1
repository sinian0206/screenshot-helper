#requires -version 5.1
<#
  微信截图助手（DeepSeek 版）- 绿色版打包脚本
  ========================================================
  先构建（可跳过），再把绿色版需要的文件打成一个 zip：
    dist\微信截图助手_v<版本>_绿色版.zip
      ├─ 微信截图助手.exe          （单文件程序）
      ├─ 使用说明.txt              （由 docs\使用说明.md 转成的纯文本）
      ├─ 功能清单.txt              （由 docs\功能清单.md 转成的纯文本）
      └─ config.example.json       （配置示例，可选参考）

  用法:
    pwsh -File package.ps1                 # 构建 + 打包到 dist\
    pwsh -File package.ps1 -SkipBuild      # 复用 dist\微信截图助手.exe 直接打包
    pwsh -File package.ps1 -OutDir D:\tmp  # 指定输出目录（exe 与 zip 都放这里）
    pwsh -File package.ps1 -BuildDir build_docs\my-build   # 自定义中间目录

  说明:
    - 默认会调用 build.ps1（Release）重新构建 exe；不想重建就加 -SkipBuild。
    - 解压后是扁平目录：双击 微信截图助手.exe 即可使用（绿色免安装）。
    - 打包后会解压校验每个文件与源文件一致，并打印 zip 的体积与 SHA256。
    - 本脚本不做任何 git 操作。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutDir = 'dist',

    [string]$BuildDir = 'build',

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

# ---------- 基础路径 ----------
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$root = [System.IO.Path]::GetFullPath($root).TrimEnd('\')

function Resolve-ProjectPath {
    param([string]$Path, [string]$Name)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Name 不能为空" }
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $root $Path))
}

$outFull = Resolve-ProjectPath $OutDir 'OutDir'
if ([string]::Equals($outFull.TrimEnd('\'), $root, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutDir 不能是项目根目录: $outFull"
}

$exeName = '微信截图助手.exe'
$exePath = Join-Path $outFull $exeName
$csproj = Join-Path $root 'src\微信截图助手.csproj'
$docsDir = Join-Path $root 'docs'
$manualMd = Join-Path $docsDir '使用说明.md'
$featuresMd = Join-Path $docsDir '功能清单.md'
$configExample = Join-Path $root 'config.example.json'
$stageRoot = Join-Path $root 'build_docs'
$stageDir = Join-Path $stageRoot 'package'
$verifyDir = Join-Path $stageRoot 'package-verify'

Write-Host '=== 微信截图助手 绿色版打包 ===' -ForegroundColor Cyan
Write-Host ("  输出目录: {0}" -f $outFull)

# ---------- 版本号（取 csproj 的 <Version>） ----------
$version = '1.0.0'
if (Test-Path -LiteralPath $csproj) {
    $m = Select-String -LiteralPath $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if ($m -and $m.Matches.Count -gt 0) { $version = $m.Matches[0].Groups[1].Value.Trim() }
}
Write-Host ("  版本号:   v{0}" -f $version)

# ---------- 构建（可选） ----------
if (-not $SkipBuild) {
    $buildScript = Join-Path $root 'build.ps1'
    if (-not (Test-Path -LiteralPath $buildScript)) { throw "未找到构建脚本: $buildScript" }
    $pwshExe = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $pwshExe)) {
        $cmd = Get-Command pwsh -ErrorAction SilentlyContinue
        if (-not $cmd) { throw '未找到 pwsh，请用 pwsh -File package.ps1 运行本脚本。' }
        $pwshExe = $cmd.Source
    }
    Write-Host ''
    Write-Host '[构建] 调用 build.ps1 ...' -ForegroundColor DarkGray
    & $pwshExe -NoProfile -File $buildScript -Configuration $Configuration -OutDir $outFull -BuildDir $BuildDir
    $buildExit = $LASTEXITCODE
    if ($buildExit -ne 0) {
        throw ("构建失败（退出码 {0}），未生成绿色版 zip。若是队友正在同时构建，请稍后重试。" -f $buildExit)
    }
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "构建脚本成功退出但未找到 exe: $exePath"
    }
}
else {
    Write-Host ''
    Write-Host '[构建] 已指定 -SkipBuild，复用现有 exe。' -ForegroundColor DarkGray
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "找不到 {0}。请先运行 pwsh -File build.ps1 -OutDir {1}，或去掉 -SkipBuild。" -f $exePath, $OutDir
    }
}

# ---------- 检查源文件 ----------
$required = @(
    @($manualMd, '使用说明'),
    @($featuresMd, '功能清单'),
    @($configExample, '配置示例')
)
foreach ($item in $required) {
    if (-not (Test-Path -LiteralPath $item[0])) {
        throw ("缺少{0}文件: {1}" -f $item[1], $item[0])
    }
}

# ---------- Markdown -> 纯文本 ----------
function Convert-MarkdownToPlainText {
    param([string]$Markdown)
    $text = $Markdown -replace "`r`n", "`n"
    # 图片与链接
    $text = $text -replace '!\[[^\]]*\]\([^)]*\)', ''
    $text = $text -replace '\[([^\]]+)\]\(([^)]+)\)', '$1'
    # 代码块围栏
    $text = $text -replace '(?m)^\s*```.*$', ''
    # 标题符号
    $text = $text -replace '(?m)^#{1,6}\s*', ''
    # 粗体 / 斜体
    $text = $text -replace '\*\*([^*]+)\*\*', '$1'
    $text = $text -replace '(?<!\*)\*([^*\n]+)\*(?!\*)', '$1'
    $text = $text -replace '~~([^~]+)~~', '$1'
    # 行内代码
    $text = $text -replace '`([^`]+)`', '$1'
    # 引用
    $text = $text -replace '(?m)^>\s?', ''
    # 表格分隔行（| --- | --- |）
    $text = $text -replace '(?m)^\s*\|?[\s:|-]+\|[\s:|-]*$', ''
    # 表格行 -> “单元格 | 单元格”
    $lines = $text -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*\|.*\|\s*$') {
            $cells = $line.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() }
            $lines[$i] = ($cells -join '  |  ').Trim()
        }
    }
    $text = $lines -join "`n"
    # 水平线
    $text = $text -replace '(?m)^-{3,}\s*$', ('-' * 40)
    # HTML 注释
    $text = $text -replace '(?s)<!--.*?-->', ''
    # 多余空行
    $text = $text -replace '\n{3,}', "`n`n"
    return $text.Trim() + "`n"
}

function Write-Utf8BomFile {
    param([string]$Path, [string]$Text)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($true)))
}

# ---------- 准备打包目录 ----------
if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Copy-Item -LiteralPath $exePath -Destination (Join-Path $stageDir $exeName) -Force
Copy-Item -LiteralPath $configExample -Destination (Join-Path $stageDir 'config.example.json') -Force

$manualText = Convert-MarkdownToPlainText ([System.IO.File]::ReadAllText($manualMd, [System.Text.Encoding]::UTF8))
Write-Utf8BomFile (Join-Path $stageDir '使用说明.txt') $manualText

$featuresText = Convert-MarkdownToPlainText ([System.IO.File]::ReadAllText($featuresMd, [System.Text.Encoding]::UTF8))
Write-Utf8BomFile (Join-Path $stageDir '功能清单.txt') $featuresText

Write-Host ''
Write-Host '[打包] 生成 zip ...' -ForegroundColor DarkGray
$zipPath = Join-Path $outFull ("微信截图助手_v{0}_绿色版.zip" -f $version)
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stageDir,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false,
    [System.Text.Encoding]::UTF8
)

# ---------- 校验 zip 内容 ----------
$expectedNames = @('微信截图助手.exe', '使用说明.txt', '功能清单.txt', 'config.example.json')

$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
$entries = @()
try {
    $entries = @($zip.Entries | ForEach-Object {
        [pscustomobject]@{ Name = $_.FullName; Size = $_.Length }
    })
}
finally {
    $zip.Dispose()
}

$names = @($entries | ForEach-Object { $_.Name })
$missing = @($expectedNames | Where-Object { $names -notcontains $_ })
$extra = @($names | Where-Object { $expectedNames -notcontains $_ })
if ($missing.Count -gt 0 -or $extra.Count -gt 0 -or $entries.Count -ne 4) {
    throw ("zip 内容与预期不符。缺少: [{0}]；多余: [{1}]；实际: [{2}]" -f ($missing -join ', '), ($extra -join ', '), ($names -join ', '))
}
foreach ($entry in $entries) {
    if ($entry.Size -le 0) { throw ("zip 内文件为空: {0}" -f $entry.Name) }
}

# 解压到临时目录，逐个与源文件比对
if (Test-Path -LiteralPath $verifyDir) { Remove-Item -LiteralPath $verifyDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $verifyDir | Out-Null
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($entry in $zip.Entries) {
        $dest = Join-Path $verifyDir $entry.FullName
        $destDir = Split-Path -Parent $dest
        if ($destDir -and -not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
        $inStream = $entry.Open()
        try {
            $outStream = [System.IO.File]::Create($dest)
            try { $inStream.CopyTo($outStream) }
            finally { $outStream.Dispose() }
        }
        finally { $inStream.Dispose() }
    }
}
finally {
    $zip.Dispose()
}

$pairs = @(
    @($exeName, (Join-Path $stageDir $exeName)),
    @('使用说明.txt', (Join-Path $stageDir '使用说明.txt')),
    @('功能清单.txt', (Join-Path $stageDir '功能清单.txt')),
    @('config.example.json', (Join-Path $stageDir 'config.example.json'))
)
foreach ($pair in $pairs) {
    $extracted = Join-Path $verifyDir $pair[0]
    if (-not (Test-Path -LiteralPath $extracted)) { throw ("解压后缺少文件: {0}" -f $pair[0]) }
    $h1 = (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash
    $h2 = (Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash
    if ($h1 -ne $h2) { throw ("解压校验失败（内容不一致）: {0}" -f $pair[0]) }
}

# ---------- 输出摘要 ----------
$zipItem = Get-Item -LiteralPath $zipPath
$zipSha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$zipSizeMB = [math]::Round($zipItem.Length / 1MB, 2)

Write-Host ''
Write-Host '打包成功' -ForegroundColor Green
Write-Host ("  zip 路径: {0}" -f $zipItem.FullName)
Write-Host ("  体积:     {0} MB（{1:N0} 字节）" -f $zipSizeMB, $zipItem.Length)
Write-Host ("  SHA256:   {0}" -f $zipSha)
Write-Host '  内容清单:'
foreach ($entry in ($entries | Sort-Object Name)) {
    $kb = [math]::Round($entry.Size / 1KB, 1)
    Write-Host ("    {0,-24} {1,10} KB" -f $entry.Name, $kb)
}
Write-Host '  校验: 4/4 个文件解压后与源文件完全一致' -ForegroundColor Green

exit 0
