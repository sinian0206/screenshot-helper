#requires -version 5.1
[CmdletBinding()]
param([string]$ReportName = 'acceptance')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path $projectRoot 'build_recording_devices'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
& $msbuild (Join-Path $PSScriptRoot 'recording-devices.csproj') /restore /nologo /verbosity:minimal /p:Configuration=Release "/p:OutputPath=$testOutput\" "/p:BaseIntermediateOutputPath=$testOutput\obj\"
if ($LASTEXITCODE -ne 0) { throw "录制设备测试构建失败：$LASTEXITCODE" }
& (Join-Path $projectRoot 'native\build-recorder.ps1') -OutDir $testOutput
& (Join-Path $testOutput 'recording-devices.exe') $testOutput 2>&1 | Tee-Object -FilePath (Join-Path $testOutput ($ReportName + '.txt'))
exit $LASTEXITCODE
