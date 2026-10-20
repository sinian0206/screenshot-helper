#requires -version 5.1
[CmdletBinding()]
param(
    [string]$OutDir = '..\build_recorder\native',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $PSScriptRoot $OutDir }
$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio C++ MSBuild was not found.' }
& $msbuild (Join-Path $PSScriptRoot 'RecorderNative\RecorderNative.vcxproj') /nologo /v:minimal "/p:Configuration=$Configuration" /p:Platform=x64 "/p:OutDir=$OutDir\" "/p:IntDir=$OutDir\obj\"
if ($LASTEXITCODE -ne 0) { throw 'RecorderNative build failed.' }
Write-Output (Join-Path $OutDir 'RecorderNative.dll')
