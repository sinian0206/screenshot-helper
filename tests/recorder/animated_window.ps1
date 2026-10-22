# 仅用于 recorder 自动化验证：一个自绘动画窗口，作为“画面持续变化”的录制目标。
# 由 RecordTest 测试流程临时启动，窗口置顶显示约 8 秒后自动关闭，不属于产品代码。
# 用法：pwsh -File tests\recorder\animated_window.ps1 [x] [y] [w] [h]
param(
    [int]$X = 32,
    [int]$Y = 521,
    [int]$W = 700,
    [int]$H = 400
)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Bounds = New-Object System.Drawing.Rectangle($X, $Y, $W, $H)
$form.TopMost = $true
$form.BackColor = [System.Drawing.Color]::Black
$form.ShowInTaskbar = $false
$form.Text = 'recorder-animation-test'

$script:dot = New-Object System.Windows.Forms.Panel
$script:dot.Size = New-Object System.Drawing.Size(90, 90)
$script:dot.BackColor = [System.Drawing.Color]::FromArgb(7, 193, 96)
$form.Controls.Add($script:dot)

$script:frame = 0
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 80
$timer.Add_Tick({
    $script:frame++
    $maxX = [Math]::Max(0, $W - 90)
    $maxY = [Math]::Max(0, $H - 90)
    $x = ([Math]::Abs([Math]::Sin($script:frame * 0.18)) * $maxX)
    $y = ([Math]::Abs([Math]::Cos($script:frame * 0.11)) * $maxY)
    $script:dot.Location = New-Object System.Drawing.Point([int]$x, [int]$y)
    $form.BackColor = [System.Drawing.Color]::FromArgb(
        [int](($script:frame * 37) % 200 + 30),
        [int](($script:frame * 73) % 200 + 30),
        [int](($script:frame * 113) % 200 + 30))
})
$form.Show()
$timer.Start()

$deadline = (Get-Date).AddSeconds(9)
while ((Get-Date) -lt $deadline) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 25
}
$timer.Stop()
$form.Close()
$form.Dispose()
