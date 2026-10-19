#requires -version 5.1
<#
  微信截图助手（DeepSeek 版）- 构建脚本
  ========================================================
  把 src\微信截图助手.csproj（.NET Framework 4.8 / WinForms）编译成单个绿色 exe。

  用法:
    pwsh -File build.ps1                        # Release 构建，输出 dist\微信截图助手.exe
    pwsh -File build.ps1 -Configuration Debug   # Debug 构建
    pwsh -File build.ps1 -OutDir build_docs     # 自定义输出目录（相对路径基于项目根目录）
    pwsh -File build.ps1 -OutDir D:\tmp\out     # 绝对路径也支持
    pwsh -File build.ps1 -Clean                 # 先清理 build\ 与输出目录再构建
    pwsh -File build.ps1 -Test                  # 构建后运行 --selftest 并校验报告

  参数:
    -Configuration  Debug / Release，默认 Release
    -OutDir         只放最终 exe 的输出目录，默认 dist
    -BuildDir       中间产物目录，默认 build（队友并行构建时可改用自己的目录）
    -Clean          构建前删除中间目录和输出目录（有安全校验，不会删项目根目录）
    -Test           构建后运行 exe 的 --selftest，报告写到 <BuildDir>\selftest\，失败即报错

  说明:
    - dotnet build 产物先在 BuildDir，然后只把 微信截图助手.exe 复制到 OutDir（绿色单文件）。
    - 构建失败会自动重试 1 次（队友同时构建时 obj 目录可能被占用）。
    - 本脚本不做任何 git 操作。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutDir = 'dist',

    [string]$BuildDir = 'build',

    [switch]$Clean,

    [switch]$Test
)

$ErrorActionPreference = 'Stop'

# ---------- 基础路径 ----------
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$root = [System.IO.Path]::GetFullPath($root).TrimEnd('\')
$proj = Join-Path $root 'src\微信截图助手.csproj'
$appName = '微信截图助手'
$exeName = $appName + '.exe'

function Resolve-ProjectPath {
    param([string]$Path, [string]$Name)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Name 不能为空" }
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $root $Path))
}

function Assert-SafeDeleteTarget {
    param([string]$Path, [string]$Label)
    $p = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($p.Length -le 2) { throw "-Clean 拒绝删除磁盘根目录: $p" }
    if ([string]::Equals($p, $root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "-Clean 拒绝删除项目根目录: $p"
    }
    if ($root.StartsWith($p + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "-Clean 拒绝删除项目根目录的上级目录（$Label）: $p"
    }
    $src = Join-Path $root 'src'
    if ([string]::Equals($p, $src, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "-Clean 拒绝删除源码目录（$Label）: $p"
    }
}

$buildFull = Resolve-ProjectPath $BuildDir 'BuildDir'
$outFull = Resolve-ProjectPath $OutDir 'OutDir'

# 中间目录与输出目录不能是项目根目录 / 源码目录
foreach ($pair in @(@($buildFull, 'BuildDir'), @($outFull, 'OutDir'))) {
    $p = $pair[0].TrimEnd('\')
    $label = $pair[1]
    if ([string]::Equals($p, $root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$label 不能是项目根目录: $p"
    }
    if ([string]::Equals($p, (Join-Path $root 'src'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$label 不能是源码目录: $p"
    }
}

if (-not (Test-Path -LiteralPath $proj)) { throw "未找到项目文件: $proj" }
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    throw '未找到 dotnet 命令。请安装 .NET SDK 10（本项目 target net48，可用新 SDK 编译），并确认 dotnet 在 PATH 中。'
}

Write-Host '=== 微信截图助手 构建 ===' -ForegroundColor Cyan
Write-Host ("  项目根目录: {0}" -f $root)
Write-Host ("  配置:       {0}" -f $Configuration)
Write-Host ("  中间目录:   {0}" -f $buildFull)
Write-Host ("  输出目录:   {0}" -f $outFull)

# ---------- 清理 ----------
if ($Clean) {
    Assert-SafeDeleteTarget $buildFull 'BuildDir'
    Assert-SafeDeleteTarget $outFull 'OutDir'
    foreach ($target in @(@($buildFull, '中间目录'), @($outFull, '输出目录'))) {
        $dir = $target[0]
        if (Test-Path -LiteralPath $dir) {
            Remove-Item -LiteralPath $dir -Recurse -Force
            Write-Host ("  已清理{0}: {1}" -f $target[1], $dir) -ForegroundColor DarkGray
        }
    }
}

New-Item -ItemType Directory -Force -Path $buildFull | Out-Null
New-Item -ItemType Directory -Force -Path $outFull | Out-Null

# ---------- dotnet build（失败自动重试 1 次） ----------
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$attempts = 2
$exitCode = -1
for ($i = 1; $i -le $attempts; $i++) {
    if ($i -gt 1) {
        Write-Host '  等待 10 秒后重试（可能遇到队友同时构建导致的 obj 目录锁）...' -ForegroundColor Yellow
        Start-Sleep -Seconds 10
    }
    Write-Host ("[构建] dotnet build -c {0} -o {1}（第 {2}/{3} 次）" -f $Configuration, $buildFull, $i, $attempts) -ForegroundColor DarkGray

    # IncludeSourceRevisionInInformationalVersion=false：让 ProductVersion 稳定为 1.0.0，
    # 不因 git 提交号变化（版本信息更干净，也便于校验）。
    & dotnet build $proj -c $Configuration -o $buildFull -v minimal --nologo -p:IncludeSourceRevisionInInformationalVersion=false
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) { break }
    Write-Warning ("dotnet build 失败（退出码 {0}）" -f $exitCode)
}
$sw.Stop()

if ($exitCode -ne 0) {
    throw ("dotnet build 失败（退出码 {0}）。如果是队友正在同时构建，请稍后重试；本脚本不会修改任何源码。" -f $exitCode)
}

$builtExe = Join-Path $buildFull $exeName
if (-not (Test-Path -LiteralPath $builtExe)) {
    throw "构建成功但未找到产物: $builtExe"
}

# ---------- 清理中间产物中的伴生文件，只保留 exe ----------
$sideExts = @('.pdb', '.xml', '.json')
Get-ChildItem -LiteralPath $buildFull -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -like '*.exe.config' -or
        $sideExts -contains $_.Extension.ToLowerInvariant()
    } |
    Remove-Item -Force -ErrorAction SilentlyContinue

# ---------- 只复制 exe 到输出目录 ----------
$targetExe = Join-Path $outFull $exeName
Copy-Item -LiteralPath $builtExe -Destination $targetExe -Force

# 防止旧构建把伴生文件带进输出目录（绿色版要求单文件）
foreach ($side in @($exeName + '.config', $appName + '.pdb')) {
    $sidePath = Join-Path $outFull $side
    if (Test-Path -LiteralPath $sidePath) {
        Remove-Item -LiteralPath $sidePath -Force
        Write-Host ("  已清理输出目录中的多余文件: {0}" -f $sidePath) -ForegroundColor DarkGray
    }
}

# ---------- 输出摘要 ----------
$item = Get-Item -LiteralPath $targetExe
$sha256 = (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash
$sizeKB = [math]::Round($item.Length / 1KB, 1)

Write-Host ''
Write-Host '构建成功' -ForegroundColor Green
Write-Host ("  产物路径: {0}" -f $item.FullName)
Write-Host ("  文件体积: {0} KB（{1:N0} 字节）" -f $sizeKB, $item.Length)
Write-Host ("  SHA256:   {0}" -f $sha256)
Write-Host ("  构建耗时: {0:N1} 秒" -f $sw.Elapsed.TotalSeconds)
Write-Host ("  输出目录: {0}" -f $outFull)

# ---------- 可选：运行内置自检 ----------
if ($Test) {
    $reportDir = Join-Path $buildFull 'selftest'
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $reportPath = Join-Path $reportDir 'selftest-report.txt'
    if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath -Force }

    Write-Host ''
    Write-Host '[自检] 运行 微信截图助手.exe --selftest ...' -ForegroundColor DarkGray
    $argLine = '--selftest --out "{0}"' -f $reportPath
    $proc = Start-Process -FilePath $targetExe -ArgumentList $argLine -PassThru
    if (-not $proc.WaitForExit(180000)) {
        try { $proc.Kill() } catch { }
        throw '自检运行超时（180 秒），已强制结束进程。'
    }
    $selftestExit = $proc.ExitCode

    if (-not (Test-Path -LiteralPath $reportPath)) {
        throw ("自检未生成报告（退出码 {0}）: {1}" -f $selftestExit, $reportPath)
    }
    $reportText = [System.IO.File]::ReadAllText($reportPath)
    Write-Host $reportText.TrimEnd()

    $ok = ($selftestExit -eq 0) -and
          $reportText.Contains('结果: 全部通过') -and
          (-not $reportText.Contains('[FAIL]')) -and
          (-not $reportText.Contains('[FATAL]'))
    if (-not $ok) {
        throw ("自检失败（退出码 {0}）。报告: {1}" -f $selftestExit, $reportPath)
    }
    Write-Host ''
    Write-Host ("自检通过（报告: {0}）" -f $reportPath) -ForegroundColor Green
}

exit 0
