param([string]$OutPath)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$img = [System.Windows.Forms.Clipboard]::GetImage()
if ($img -ne $null) {
    $img.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output ("{0}x{1}" -f $img.Width, $img.Height)
    $img.Dispose()
} else {
    Write-Output 'EMPTY'
}
