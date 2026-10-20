#requires -version 7.0
[CmdletBinding()]
param([string]$TestOut=(Join-Path $PSScriptRoot 'object-editor-artifacts'), [string]$AppPath='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$TestOut=[IO.Path]::GetFullPath($TestOut)
$appOut=Join-Path $TestOut 'app'
New-Item -ItemType Directory -Force -Path $appOut | Out-Null
$msbuild='C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$compiler=Join-Path (Split-Path -Parent $msbuild) 'Roslyn\csc.exe'
$application=Join-Path $appOut '截图助手.exe'
if([string]::IsNullOrWhiteSpace($AppPath)) {
    & $msbuild (Join-Path $root 'src\截图助手.csproj') /t:Build /p:Configuration=Release "/p:OutputPath=$appOut\" "/p:IntermediateOutputPath=$TestOut\obj\" /p:IncludeSourceRevisionInInformationalVersion=false /verbosity:minimal /nologo
    if($LASTEXITCODE -ne 0){throw 'VS 构建失败'}
} else {Copy-Item -LiteralPath $AppPath -Destination $application -Force}
$runner=Join-Path $appOut 'object-editor.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ "/out:$runner" "/reference:$application" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'object-editor.cs')
if($LASTEXITCODE -ne 0){throw '对象编辑测试编译失败'}
$start=[Diagnostics.ProcessStartInfo]::new($runner)
$start.ArgumentList.Add($TestOut)
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::new()
$process.StartInfo=$start
try {
    $null=$process.Start()
    $stdout=$process.StandardOutput.ReadToEndAsync()
    $stderr=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit(30000)){ $process.Kill($true);throw '对象编辑脚本超时' }
    $stdout.GetAwaiter().GetResult() | Write-Output
    $stderr.GetAwaiter().GetResult() | Write-Output
    if($process.ExitCode -ne 0){throw "对象编辑回归失败: $TestOut\report.txt"}
} finally {if(-not $process.HasExited){$process.Kill($true)};$process.Dispose()}
