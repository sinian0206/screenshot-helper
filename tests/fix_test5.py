p = r'D:\Agents\微信截图助手\tests\real_ui_test5.ps1'
s = open(p, encoding='utf-8').read()
s = s.replace("$target = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-STA', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\\scroll_target.ps1')) -PassThru -WindowStyle Hidden",
              "$targetErr = Join-Path $out 'target_err.txt'\n$targetOut = Join-Path $out 'target_out.txt'\nRemove-Item $targetErr, $targetOut -ErrorAction SilentlyContinue\n$target = Start-Process -FilePath 'pwsh.exe' -ArgumentList @('-STA', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\\scroll_target.ps1')) -PassThru -WindowStyle Hidden -RedirectStandardError $targetErr -RedirectStandardOutput $targetOut", 1)
s = s.replace("for ($i = 0; $i -lt 25; $i++) { Start-Sleep -Milliseconds 200; if ([UiProbe]::FindByTitle('长截图测试目标窗口') -ne [IntPtr]::Zero) { $targetReady = $true; break } }\nif (-not $targetReady) { if (-not $target.HasExited) { $target.Kill() }; throw '长截图测试目标窗口未启动' }",
              "for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 250; if ([UiProbe]::FindByTitle('长截图测试目标窗口') -ne [IntPtr]::Zero) { $targetReady = $true; break } }\nif (-not $targetReady) {\n    Write-Host (\"target hasExited={0} code={1}\" -f $target.HasExited, $target.ExitCode)\n    if (Test-Path $targetErr) { Get-Content $targetErr -Encoding UTF8 | Select-Object -First 20 }\n    if (-not $target.HasExited) { $target.Kill() }\n    throw '长截图测试目标窗口未启动'\n}", 1)
open(p, 'w', encoding='utf-8').write(s)
print('patched test5 target launch')
