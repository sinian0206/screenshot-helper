#requires -version 5.1
<#
  截图助手（DeepSeek 版）- 冒烟测试
  ========================================================
  在临时目录（build_docs\smoke）里从零构建，然后对最终 exe 做一组最小验收断言：
    1. 构建成功，输出目录包含主程序和完整的 ocr 本地识别目录
    2. exe 体积 < 2 MB
    3. 文件版本信息 ProductName / FileVersion 正确
    4. exe 关联图标可提取、清单（PerMonitorV2）已嵌入
    5. 运行 截图助手.exe --selftest：退出码 0，报告含「结果: 全部通过」，无 [FAIL]/[FATAL]
  每项输出 PASS/FAIL，最后汇总；全部通过退出码 0，否则 1。

  用法:
    pwsh -File tests\smoke.ps1              # 默认 Release
    pwsh -File tests\smoke.ps1 -Configuration Debug
    pwsh -File tests\smoke.ps1 -RetryWaitSec 60

  说明:
    - 构建失败（通常是队友正在同时改代码 / 构建，obj 目录被占用）会自动等待
      RetryWaitSec 秒重试，最多 BuildRetries 次；不会修改任何源码。
    - 测试产物都在 build_docs\smoke 下（已被 .gitignore 忽略），不会占用 dist\。
    - 测试结束会结束本脚本启动的 截图助手 进程。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [int]$BuildRetries = 3,

    [int]$RetryWaitSec = 30
)

$ErrorActionPreference = 'Stop'

$testsDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($testsDir)) {
    $testsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $testsDir)).TrimEnd('\')

$workRoot = Join-Path $root 'build_docs\smoke'
$outDir = Join-Path $workRoot 'out'
$buildDir = Join-Path $workRoot 'build'
$selftestDir = Join-Path $workRoot 'selftest'
$reportPath = Join-Path $selftestDir 'selftest-report.txt'
$buildLog = Join-Path $workRoot 'build.log'
$exeName = '截图助手.exe'
$exe = Join-Path $outDir $exeName
$buildScript = Join-Path $root 'build.ps1'

# 所有递归清理仅限固定的测试产物目录。
foreach ($testPath in @($workRoot, $outDir, $buildDir)) {
    $absolute = [IO.Path]::GetFullPath($testPath).TrimEnd('\')
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'build_docs\smoke')).TrimEnd('\')
    if ($absolute -ne $allowed -and -not $absolute.StartsWith($allowed + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "测试清理路径超出产物目录：$absolute"
    }
    if ((Test-Path -LiteralPath $absolute) -and ((Get-Item -LiteralPath $absolute).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "测试清理拒绝重解析目录：$absolute"
    }
}

$checks = @()

function Add-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    $script:checks += [pscustomobject]@{ Name = $Name; Pass = $Ok; Detail = $Detail }
    $line = $(if ($Ok) { '[PASS] ' } else { '[FAIL] ' }) + $Name
    if ($Detail) { $line += '  ' + $Detail }
    if ($Ok) { Write-Host $line -ForegroundColor Green } else { Write-Host $line -ForegroundColor Red }
}

function Get-PwshPath {
    $p = Join-Path $PSHOME 'pwsh.exe'
    if (Test-Path -LiteralPath $p) { return $p }
    $cmd = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw '未找到 pwsh，请用 pwsh -File tests\smoke.ps1 运行本脚本。'
}

function Stop-SmokeProcess {
    param([string]$ExePath)
    try {
        Get-Process -Name '截图助手' -ErrorAction SilentlyContinue | ForEach-Object {
            $path = $null
            try { $path = $_.Path } catch { }
            if ($path -and [string]::Equals($path, $ExePath, [System.StringComparison]::OrdinalIgnoreCase)) {
                try { $_.Kill() } catch { }
            }
        }
    }
    catch { }
}

$swAll = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host '=== 截图助手 冒烟测试 ===' -ForegroundColor Cyan
Write-Host ("  项目根目录: {0}" -f $root)
Write-Host ("  配置:       {0}" -f $Configuration)

# ---------- 0. 清理临时目录并构建 ----------
try {
    if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $selftestDir | Out-Null

    if (-not (Test-Path -LiteralPath $buildScript)) {
        throw "未找到构建脚本: $buildScript"
    }

    $pwshExe = Get-PwshPath
    $built = $false
    for ($attempt = 1; $attempt -le $BuildRetries; $attempt++) {
        Write-Host ''
        Write-Host ("[构建] 第 {0}/{1} 次尝试（临时目录: {2}）" -f $attempt, $BuildRetries, $outDir) -ForegroundColor DarkGray
        if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
        if (Test-Path -LiteralPath $buildDir) { Remove-Item -LiteralPath $buildDir -Recurse -Force }
        if (Test-Path -LiteralPath $buildLog) { Remove-Item -LiteralPath $buildLog -Force }

        & $pwshExe -NoProfile -File $buildScript -Configuration $Configuration -OutDir $outDir -BuildDir $buildDir 2>&1 |
            Tee-Object -FilePath $buildLog | Out-Null
        $buildExit = $LASTEXITCODE

        if ($buildExit -eq 0 -and (Test-Path -LiteralPath $exe)) {
            $built = $true
            break
        }

        if ($attempt -lt $BuildRetries) {
            Write-Warning ("构建失败（退出码 {0}）。可能是队友正在同时改代码/构建（obj 目录锁），等待 {1} 秒后重试..." -f $buildExit, $RetryWaitSec)
            if (Test-Path -LiteralPath $buildLog) {
                Write-Host '  —— 构建日志末尾 ——' -ForegroundColor DarkYellow
                Get-Content -LiteralPath $buildLog -Tail 25 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ('  ' + $_) -ForegroundColor DarkYellow }
            }
            Start-Sleep -Seconds $RetryWaitSec
        }
    }

    Add-Check '从零构建成功' $built ("退出码 $buildExit；exe: $exe")
    if (-not $built) {
        Write-Host ''
        Write-Host ('结果: 构建失败，日志：' + $buildLog) -ForegroundColor Red
        Get-Content -LiteralPath $buildLog -Tail 20 -ErrorAction SilentlyContinue
        exit 1
    }
}
catch {
    Write-Host ('[FAIL] 冒烟测试异常: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}

# 构建日志成功时只回显末尾几行，便于确认
if (Test-Path -LiteralPath $buildLog) {
    Get-Content -LiteralPath $buildLog -Tail 7 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ('  ' + $_) -ForegroundColor DarkGray }
}

# ---------- 1. 便携程序与完整本地 OCR ----------
$children = @(Get-ChildItem -LiteralPath $outDir -Force -ErrorAction SilentlyContinue)
$fileChildren = @($children | Where-Object { -not $_.PSIsContainer })
$dirChildren = @($children | Where-Object { $_.PSIsContainer })
$onlyExe = ($fileChildren.Count -eq 2) -and ($fileChildren.Name -contains $exeName) -and ($fileChildren.Name -contains 'RecorderNative.dll') -and (Test-Path -LiteralPath (Join-Path $outDir 'ocr\RapidOCR-json.exe'))
$detail = ($children | ForEach-Object { if ($_.PSIsContainer) { $_.Name + '\' } else { $_.Name } }) -join ', '
Add-Check '便携程序、系统录制组件与本地 OCR 引擎存在' $onlyExe $detail

# ---------- 2. 体积 ----------
$exeItem = Get-Item -LiteralPath $exe
$sizeBytes = $exeItem.Length
$sizeKB = [math]::Round($sizeBytes / 1KB, 1)
Add-Check 'exe 体积 < 2 MB' ($sizeBytes -lt 2MB) ("{0} KB（{1:N0} 字节）" -f $sizeKB, $sizeBytes)
Add-Check 'exe 体积合理（> 50 KB，非空壳）' ($sizeBytes -gt 50KB) ("{0} KB" -f $sizeKB)

# ---------- 3. 文件版本信息 ----------
try {
    $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    $expectedProduct = '截图助手'
    Add-Check "ProductName 精确为「$expectedProduct」" ([string]::Equals($vi.ProductName, $expectedProduct, [System.StringComparison]::Ordinal)) ("ProductName: " + $vi.ProductName)
    Add-Check 'ProductName 不含旧名称' (-not ([string]$vi.ProductName).Contains('微信')) ("ProductName: " + $vi.ProductName)
    Add-Check 'FileVersion 为 1.2.4.x' ($vi.FileVersion -like '1.2.4.*') ("FileVersion: " + $vi.FileVersion)
    Add-Check 'ProductVersion 为 1.2.4' ($vi.ProductVersion -like '1.2.4*') ("ProductVersion: " + $vi.ProductVersion)
}
catch {
    Add-Check '读取文件版本信息' $false $_.Exception.Message
}

# ---------- 4. 图标 + 清单 ----------
try {
    Add-Type -AssemblyName System.Drawing -ErrorAction Stop
    $icon = $null
    try { $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe) } catch { }
    $iconOk = ($null -ne $icon) -and ($icon.Width -ge 16) -and ($icon.Height -ge 16)
    Add-Check 'exe 关联程序图标可提取' $iconOk $(if ($icon) { "{0}x{1}" -f $icon.Width, $icon.Height } else { '未提取到图标' })
    if ($icon) { $icon.Dispose() }
}
catch {
    Add-Check 'exe 关联程序图标可提取' $false $_.Exception.Message
}

try {
    $bytes = [System.IO.File]::ReadAllBytes($exe)
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    $hasManifest = $ascii.Contains('PerMonitorV2') -and $ascii.Contains('dpiAware')
    Add-Check '清单已嵌入（PerMonitorV2 高 DPI 感知）' $hasManifest
}
catch {
    Add-Check '清单已嵌入（PerMonitorV2 高 DPI 感知）' $false $_.Exception.Message
}

# ---------- 5. 运行 --selftest ----------
$selftestExit = -1
try {
    if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath -Force }
    $argLine = '--selftest --out "{0}"' -f $reportPath
    Write-Host ''
    Write-Host '[自检] 运行 截图助手.exe --selftest ...' -ForegroundColor DarkGray
    $proc = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $outDir -WindowStyle Hidden -PassThru
    if (-not $proc.WaitForExit(180000)) {
        try { $proc.Kill() } catch { }
        Add-Check '自检运行完成（未超时）' $false '超时 180 秒，已强制结束'
    }
    else {
        $selftestExit = $proc.ExitCode
        Add-Check '自检运行完成（未超时）' $true ("退出码 " + $selftestExit)
        Add-Check '自检退出码为 0' ($selftestExit -eq 0) ("退出码 " + $selftestExit)

        if (Test-Path -LiteralPath $reportPath) {
            $reportText = [System.IO.File]::ReadAllText($reportPath)
            Add-Check '自检报告已生成' $true $reportPath
            Add-Check '报告含「结果: 全部通过」' $reportText.Contains('结果: 全部通过')
            Add-Check '报告无 [FAIL]' (-not $reportText.Contains('[FAIL]'))
            Add-Check '报告无 [FATAL]' (-not $reportText.Contains('[FATAL]'))
            $passCount = [regex]::Matches($reportText, '\[PASS\]').Count
            Add-Check '自检通过项不少于 8 项' ($passCount -ge 8) ("[PASS] 共 $passCount 项")
            Write-Host ''
            Write-Host '  —— 自检报告 ——' -ForegroundColor DarkGray
            $reportText.TrimEnd() -split "`r?`n" | ForEach-Object { Write-Host ('  ' + $_) }
        }
        else {
            Add-Check '自检报告已生成' $false $reportPath
        }
    }
}
catch {
    Add-Check '自检运行完成（未超时）' $false $_.Exception.Message
}
finally {
    Stop-SmokeProcess -ExePath $exe
}

# ---------- 6. 无残留进程 ----------
$left = @{}
try {
    Get-Process -Name '截图助手' -ErrorAction SilentlyContinue | ForEach-Object {
        $p = $null
        try { $p = $_.Path } catch { }
        if ($p -and [string]::Equals($p, $exe, [System.StringComparison]::OrdinalIgnoreCase)) { $left[$p] = $true }
    }
}
catch { }
Add-Check '测试结束无残留进程' ($left.Count -eq 0)

# ---------- 汇总 ----------
$swAll.Stop()
$failed = @($checks | Where-Object { -not $_.Pass })
Write-Host ''
Write-Host '----------------------------------------'
if ($failed.Count -eq 0) {
    Write-Host ("冒烟测试: {0}/{1} 项全部通过（耗时 {2:N1} 秒）" -f $checks.Count, $checks.Count, $swAll.Elapsed.TotalSeconds) -ForegroundColor Green
    Write-Host '结果: PASS' -ForegroundColor Green
    exit 0
}
else {
    Write-Host ("冒烟测试: {0}/{1} 项通过，{2} 项失败（耗时 {3:N1} 秒）" -f ($checks.Count - $failed.Count), $checks.Count, $failed.Count, $swAll.Elapsed.TotalSeconds) -ForegroundColor Red
    foreach ($f in $failed) { Write-Host ('  失败: ' + $f.Name + $(if ($f.Detail) { '  ' + $f.Detail } else { '' })) -ForegroundColor Red }
    Write-Host '结果: FAIL' -ForegroundColor Red
    exit 1
}
