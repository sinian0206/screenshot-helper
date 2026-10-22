#requires -version 5.1
param([string]$ReportName = 'green')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'build_recorder\upgrade'
New-Item -ItemType Directory -Path $out -Force | Out-Null
& (Join-Path $root 'native\build-recorder.ps1') -OutDir $out
& MSBuild.exe (Join-Path $PSScriptRoot 'recorder-upgrade.csproj') /restore /nologo /v:minimal /p:Configuration=Release "/p:OutputPath=$out\" "/p:BaseIntermediateOutputPath=$out\obj\"
if ($LASTEXITCODE -ne 0) { throw '录屏升级测试构建失败。' }
& (Join-Path $out 'recorder-upgrade.exe') $out 2>&1 | Tee-Object -FilePath (Join-Path $out ($ReportName + '.txt'))
exit $LASTEXITCODE
