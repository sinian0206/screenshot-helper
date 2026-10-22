#requires -version 7.0
[CmdletBinding()]
param([string]$ReportName='image-tools')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$testOut=Join-Path $root "build_image_tools\$ReportName"
New-Item -ItemType Directory -Force -Path $testOut | Out-Null
$compiler='C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$runner=Join-Path $testOut 'image-tools.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ "/out:$runner" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $root 'src\ImageImport.cs') (Join-Path $root 'src\ImageComposer.cs') (Join-Path $PSScriptRoot 'image-tools.cs')
if($LASTEXITCODE -ne 0){throw '图片工具测试编译失败'}
& $runner $testOut | Tee-Object -FilePath (Join-Path $testOut 'report.txt')
if($LASTEXITCODE -ne 0){throw '图片工具回归失败'}
