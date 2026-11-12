# Builds the GitHub images from the app icon: docs\app-icon.png (README header, 256 px) and docs\Hush-social-preview.png (1280 x 640,
# the size GitHub recommends for a repository's social preview). Run after changing src\Hush\Assets\Hush-mark.png.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$mark = [System.Drawing.Image]::FromFile((Join-Path $root 'src\Hush\Assets\Hush-mark.png'))
Copy-Item -LiteralPath (Join-Path $root 'src\Hush\Assets\Hush-mark.png') -Destination (Join-Path $root 'docs\app-icon.png') -Force

$bitmap = [System.Drawing.Bitmap]::new(1280, 640, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bitmap)
$g.SmoothingMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::FromArgb(17, 21, 27))   # the app's dark window color
$g.DrawImage($mark, 150, 120, 400, 400)
$accent = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(63, 195, 238))   # #3FC3EE, the app's default main color
$muted = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(155, 168, 186))
$title = [System.Drawing.Font]::new('Inter', 104, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)   # upright, like the app's wordmark
$line = [System.Drawing.Font]::new('Inter', 36, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('Hush', $title, $accent, 620, 205)
$g.DrawString('A native Windows cleaner.', $line, $muted, 626, 345)
$g.DrawString('Make more room to breathe.', $line, $muted, 626, 395)
$bitmap.Save((Join-Path $root 'docs\Hush-social-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bitmap.Dispose(); $mark.Dispose()
Write-Host 'Wrote docs\app-icon.png and docs\Hush-social-preview.png'
