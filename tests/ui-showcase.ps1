#requires -version 5.1
[CmdletBinding()]
param(
    [string]$OutputDir = 'build_ui\showcase'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($OutputDir)) { $OutputDir = Join-Path $root $OutputDir }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$html = Join-Path $root 'ui\style_showcase.html'
if (-not (Test-Path -LiteralPath $html)) { throw "未找到设计展示器：$html" }

$edgeCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\Application\msedge.exe')
)
$edge = $edgeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $edge) { throw '未找到 Microsoft Edge，无法运行浏览器交互验证。' }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$profile = Join-Path $OutputDir 'edge-profile'
$testProfile = Join-Path $OutputDir 'edge-test-profile'
if (Test-Path -LiteralPath $profile) { Remove-Item -LiteralPath $profile -Recurse -Force }
if (Test-Path -LiteralPath $testProfile) { Remove-Item -LiteralPath $testProfile -Recurse -Force }
New-Item -ItemType Directory -Force -Path $profile, $testProfile | Out-Null

function Invoke-Edge([string[]]$arguments, [string]$stdoutPath, [string]$stderrPath) {
    $process = Start-Process -FilePath $edge -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    if ($process.ExitCode -ne 0) { throw "Edge 退出码 $($process.ExitCode)；错误日志：$stderrPath" }
}

$baseUri = ([Uri]$html).AbsoluteUri
$selfTestUri = $baseUri + '?selftest=1'
$domPath = Join-Path $OutputDir 'selftest-dom.html'
$domRawPath = Join-Path $OutputDir 'selftest-dom.stdout.txt'
$errorPath = Join-Path $OutputDir 'edge-selftest.err.log'
$common = @(
    '--headless=new',
    '--disable-gpu',
    '--disable-features=msEdgeFirstRunExperience',
    '--no-first-run',
    '--allow-file-access-from-files',
    "--user-data-dir=$testProfile"
)
Invoke-Edge (@($common) + @('--dump-dom', $selfTestUri)) $domRawPath $errorPath
$domText = [IO.File]::ReadAllText($domRawPath)
[IO.File]::WriteAllText($domPath, $domText, (New-Object Text.UTF8Encoding($false)))

if ($domText -notmatch 'data-selftest="passed"') { throw "浏览器交互自检未通过。DOM：$domPath" }
$summaryMatch = [regex]::Match($domText, 'SUMMARY\s+(\d+)\s+PASS\s+/\s+(\d+)\s+FAIL')
if (-not $summaryMatch.Success) { throw "浏览器自检未输出汇总。DOM：$domPath" }
$passCount = [int]$summaryMatch.Groups[1].Value
$failCount = [int]$summaryMatch.Groups[2].Value
if ($failCount -ne 0 -or $passCount -lt 40) {
    throw "浏览器交互断言不足：$passCount PASS / $failCount FAIL"
}

$desktopShot = Join-Path $OutputDir 'showcase-desktop.png'
$mobileShot = Join-Path $OutputDir 'showcase-mobile.png'
$mobileDomRaw = Join-Path $OutputDir 'selftest-mobile-dom.stdout.txt'
$mobileDomPath = Join-Path $OutputDir 'selftest-mobile-dom.html'
$desktopErr = Join-Path $OutputDir 'edge-desktop.err.log'
$mobileErr = Join-Path $OutputDir 'edge-mobile.err.log'
$mobileDomErr = Join-Path $OutputDir 'edge-mobile-dom.err.log'
$desktopOut = Join-Path $OutputDir 'edge-desktop.stdout.txt'
$mobileOut = Join-Path $OutputDir 'edge-mobile.stdout.txt'
Invoke-Edge @(
    '--headless=new',
    '--disable-gpu',
    '--disable-features=msEdgeFirstRunExperience',
    '--no-first-run',
    '--hide-scrollbars',
    '--window-size=1440,1000',
    "--screenshot=$desktopShot",
    "--user-data-dir=$profile",
    $baseUri
) $desktopOut $desktopErr
Invoke-Edge @(
    '--headless=new',
    '--disable-gpu',
    '--disable-features=msEdgeFirstRunExperience',
    '--no-first-run',
    '--hide-scrollbars',
    '--window-size=390,844',
    "--screenshot=$mobileShot",
    "--user-data-dir=$profile",
    $baseUri
) $mobileOut $mobileErr

Invoke-Edge (@(
    '--headless=new',
    '--disable-gpu',
    '--disable-features=msEdgeFirstRunExperience',
    '--no-first-run',
    '--allow-file-access-from-files',
    '--window-size=390,844',
    "--user-data-dir=$testProfile",
    '--dump-dom',
    $selfTestUri
)) $mobileDomRaw $mobileDomErr
$mobileDomText = [IO.File]::ReadAllText($mobileDomRaw)
[IO.File]::WriteAllText($mobileDomPath, $mobileDomText, (New-Object Text.UTF8Encoding($false)))
if ($mobileDomText -notmatch 'data-selftest="passed"') { throw "移动视口交互自检未通过。DOM：$mobileDomPath" }
$viewportMatch = [regex]::Match($mobileDomText, 'VIEWPORT\s+(\d+)\s+(\d+)')
if (-not $viewportMatch.Success) { throw "移动视口未输出宽度信息。DOM：$mobileDomPath" }
$viewportWidth = [int]$viewportMatch.Groups[1].Value
$scrollWidth = [int]$viewportMatch.Groups[2].Value
if ($scrollWidth -gt $viewportWidth) {
    throw "移动视口存在横向溢出：viewport=$viewportWidth，scroll=$scrollWidth"
}

Add-Type -AssemblyName System.Drawing
foreach ($shot in @($desktopShot, $mobileShot)) {
    if (-not (Test-Path -LiteralPath $shot)) { throw "未生成截图：$shot" }
    $image = [Drawing.Bitmap]::FromFile($shot)
    try {
        $samples = New-Object 'System.Collections.Generic.HashSet[int]'
        for ($x = 5; $x -lt $image.Width; $x += [Math]::Max(1, [int]($image.Width / 20))) {
            for ($y = 5; $y -lt $image.Height; $y += [Math]::Max(1, [int]($image.Height / 20))) {
                [void]$samples.Add($image.GetPixel($x, $y).ToArgb())
            }
        }
        if ($samples.Count -lt 8) { throw "截图疑似空白：$shot（颜色样本 $($samples.Count)）" }
    }
    finally { $image.Dispose() }
}

$nodeScript = Join-Path $PSScriptRoot 'ui-showcase-cdp.mjs'
& node $nodeScript $edge $html $OutputDir
if ($LASTEXITCODE -ne 0) { throw "精确视口浏览器验证失败，退出码 $LASTEXITCODE。" }
$cdpReportPath = Join-Path $OutputDir 'cdp-report.json'
if (-not (Test-Path -LiteralPath $cdpReportPath)) { throw '精确视口验证未生成 CDP 报告。' }
$cdpReport = Get-Content -Raw -LiteralPath $cdpReportPath | ConvertFrom-Json
if ($cdpReport.failures -ne 0 -or $cdpReport.mobile.scrollWidth -gt 390 -or $cdpReport.desktop.scrollWidth -gt 1440) {
    throw '精确视口验证报告未通过。'
}

$report = @(
    'UI showcase browser regression',
    'Engine: Microsoft Edge headless',
    'Interactions: ' + $passCount + ' PASS / ' + $failCount + ' FAIL',
    'Mobile layout: viewport=' + $viewportWidth + ' scroll=' + $scrollWidth,
    'Exact viewports: mobile 390x844, desktop 1440x1000',
    'Desktop: ' + $desktopShot,
    'Mobile: ' + $mobileShot,
    'DOM: ' + $domPath,
    'Mobile DOM: ' + $mobileDomPath
    'CDP report: ' + $cdpReportPath
)
$reportPath = Join-Path $OutputDir 'report.txt'
$report | Set-Content -LiteralPath $reportPath -Encoding UTF8
$report | ForEach-Object { Write-Host $_ }
if ($failCount -ne 0) { exit 1 }
