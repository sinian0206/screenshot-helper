#requires -version 5.1
[CmdletBinding()]
param([string]$ReportName = 'review')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path $projectRoot 'build_workflow_ui'
$testIntermediate = Join-Path $testOutput 'obj\'
$msbuild = Get-Command MSBuild.exe -ErrorAction Stop
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
& $msbuild.Source (Join-Path $PSScriptRoot 'workflow-ui-review.csproj') /restore /nologo /verbosity:minimal /p:Configuration=Release "/p:OutputPath=$testOutput\" "/p:BaseIntermediateOutputPath=$testIntermediate"
if ($LASTEXITCODE -ne 0) { throw "工作流 UI 审查构建失败：$LASTEXITCODE" }
& (Join-Path $testOutput 'workflow-ui-review.exe') 2>&1 | Tee-Object -FilePath (Join-Path $testOutput ($ReportName + '.txt'))
exit $LASTEXITCODE
