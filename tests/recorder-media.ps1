#requires -version 5.1
[CmdletBinding()]
param([string]$ReportName = 'green', [switch]$SkipSystemAudio)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'build_recorder\upgrade'
New-Item -ItemType Directory -Path $out -Force | Out-Null
& (Join-Path $root 'native\build-recorder.ps1') -OutDir $out
& MSBuild.exe (Join-Path $PSScriptRoot 'recorder-upgrade.csproj') /restore /nologo /v:minimal /p:Configuration=Release "/p:OutputPath=$out\" "/p:BaseIntermediateOutputPath=$out\obj\"
if ($LASTEXITCODE -ne 0) { throw 'Recorder test build failed.' }
& MSBuild.exe (Join-Path $PSScriptRoot 'recorder-media-verify.vcxproj') /nologo /v:minimal /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$out\" "/p:IntDir=$out\obj-verify\"
if ($LASTEXITCODE -ne 0) { throw 'Media verifier build failed.' }
$log = [Collections.Generic.List[string]]::new()
function Run-Fixture([string]$mode) {
    $lines = @(& (Join-Path $out 'recorder-upgrade.exe') $out $mode 2>&1)
    $code = $LASTEXITCODE
    foreach ($line in $lines) { Write-Output $line; $log.Add([string]$line) }
    if ($code -ne 0) { throw "Recorder fixture $mode failed with exit $code." }
}
function Read-Media([string]$name) {
    $json = & (Join-Path $out 'recorder-media-verify.exe') (Join-Path $out $name)
    if ($LASTEXITCODE -ne 0) { throw "Media decode failed: $json" }
    $log.Add($name + ' ' + $json)
    Write-Host "$name $json"
    return ($json | ConvertFrom-Json)
}
Run-Fixture '--media'
$video = Read-Media 'fixture-video.mp4'
if (-not $video.h264 -or $video.aac -or $video.frames -ne 20 -or [Math]::Abs($video.videoSeconds - 2) -gt 0.01) { throw 'H.264 frame count/duration/track assertion failed.' }
if ($video.topRed -lt 170 -or $video.bottomRed -gt 60) { throw 'Encoded video orientation/color assertion failed.' }
Run-Fixture '--pause'
$paused = Read-Media 'fixture-paused.mp4'
if ($paused.videoSeconds -lt 0.8 -or $paused.videoSeconds -gt 1.2) { throw 'Paused wall time leaked into the MP4 timeline.' }
Run-Fixture '--odd'
$odd = Read-Media 'fixture-odd.mp4'
if ($odd.width -ne 322 -or $odd.height -ne 182 -or $odd.edgeGreen -lt 140) { throw 'Odd dimensions cropped the right/bottom content.' }
Run-Fixture ''
Run-Fixture '--capture-exclusion'
Run-Fixture '--no-exclusion'
if (-not $SkipSystemAudio) {
    Run-Fixture '--system'
    $audio = Read-Media 'fixture-system.mp4'
    if (-not $audio.h264 -or -not $audio.aac -or $audio.audioValues -lt 48000 -or $audio.audioRms -lt 0.005) { throw 'WASAPI/AAC synthetic tone was not recorded.' }
    if ([Math]::Abs($audio.videoSeconds - 2) -gt 0.01 -or [Math]::Abs($audio.audioSeconds - 2) -gt 0.06) { throw 'Paused audio/video timeline assertion failed.' }
    if ($audio.tone440 -lt 0.02 -or $audio.tone880 -gt $audio.tone440 * 0.15) { throw 'The audio played only while paused leaked into the final recording.' }
}
Run-Fixture '--devices'
$log.Add('[LIMIT] Microphone and mixed mode devices opened successfully; microphone speech capture was not tested, to avoid recording user content.')
$log | Set-Content -LiteralPath (Join-Path $out ($ReportName + '-media.txt')) -Encoding UTF8
Write-Output '[PASS] MP4 decoding, timeline, pause/resume, flicker, tracks and synthetic system audio assertions passed.'
