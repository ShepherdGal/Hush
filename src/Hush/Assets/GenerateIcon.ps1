# Builds Hush.ico (16-256 px) and Hush-mark*.png from Assets\Hush-icon-source.png.
#
# The source is the user's ice-blue brush artwork, prepared once from the supplied picture (-FromArtwork): the black surround is keyed
# out to real transparency so the rounded square has a crisp edge and no outer glow, and the square is cropped to its exact bounds.
# Re-run without arguments to regenerate the icon frames; pass -FromArtwork <png> to prepare a new source first.
param([string]$FromArtwork)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = $PSScriptRoot
$sourcePath = Join-Path $assetDirectory 'Hush-icon-source.png'

function New-HighQualityGraphics($bitmap) {
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    return $graphics
}

if ($FromArtwork) {
    # 1. Key the black surround to transparency. Anything darker than the artwork's own dark navy fill becomes transparent;
    #    the one or two edge pixels that were blended with black are un-blended so no dark fringe remains.
    # The artwork has no alpha channel, so copy it into a 32-bit ARGB bitmap first (otherwise the keyed alpha is discarded on unlock).
    $raw = [System.Drawing.Bitmap]::new($FromArtwork)
    $art = [System.Drawing.Bitmap]::new($raw.Width, $raw.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $copy = [System.Drawing.Graphics]::FromImage($art); $copy.DrawImage($raw, 0, 0, $raw.Width, $raw.Height); $copy.Dispose(); $raw.Dispose()
    $rect = [System.Drawing.Rectangle]::new(0, 0, $art.Width, $art.Height)
    $data = $art.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = [byte[]]::new($data.Stride * $art.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $minX = $art.Width; $minY = $art.Height; $maxX = 0; $maxY = 0
    for ($y = 0; $y -lt $art.Height; $y++) {
        for ($x = 0; $x -lt $art.Width; $x++) {
            $i = $y * $data.Stride + $x * 4
            $b = $bytes[$i]; $g = $bytes[$i + 1]; $r = $bytes[$i + 2]
            $max = [Math]::Max($r, [Math]::Max($g, $b))
            $alpha = [Math]::Min(255, [int]($max * 255 / 24))
            if ($alpha -lt 255 -and $alpha -gt 0) {
                $bytes[$i] = [byte][Math]::Min(255, [int]($b * 255 / $alpha)); $bytes[$i + 1] = [byte][Math]::Min(255, [int]($g * 255 / $alpha)); $bytes[$i + 2] = [byte][Math]::Min(255, [int]($r * 255 / $alpha))
            }
            $bytes[$i + 3] = [byte]$alpha
            if ($alpha -gt 127) { if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }; if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y } }
        }
    }
    [System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
    $art.UnlockBits($data)
    # 2. Crop to the square's exact bounds and fit it to a 512 px canvas with a 1.5% margin.
    $canvas = 512; $margin = 8
    $source = [System.Drawing.Bitmap]::new($canvas, $canvas, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = New-HighQualityGraphics $source
    $target = [System.Drawing.RectangleF]::new($margin, $margin, $canvas - 2 * $margin, $canvas - 2 * $margin)
    $crop = [System.Drawing.RectangleF]::new($minX, $minY, $maxX - $minX + 1, $maxY - $minY + 1)
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new(); $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $graphics.DrawImage($art, [System.Drawing.Rectangle]::new($target.X, $target.Y, $target.Width, $target.Height), $crop.X, $crop.Y, $crop.Width, $crop.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    # 3. Crisp border: the artwork's rim is a soft glow. Measure each pixel's distance to the edge (two-pass chamfer transform); a thin band
    #    right at the edge becomes a solid ice-blue line (brighter top-left, dimmer bottom-right, like the artwork), and the soft glow just
    #    inside it is toned down so the edge reads as a clean line instead of a halo.
    $rect = [System.Drawing.Rectangle]::new(0, 0, $canvas, $canvas)
    $data = $source.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = [byte[]]::new($data.Stride * $canvas)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $stride = $data.Stride
    $big = 1000000
    $dist = [int[]]::new($canvas * $canvas)
    for ($y = 0; $y -lt $canvas; $y++) { for ($x = 0; $x -lt $canvas; $x++) { $dist[$y * $canvas + $x] = if ($bytes[$y * $stride + $x * 4 + 3] -gt 127) { $big } else { 0 } } }
    for ($y = 0; $y -lt $canvas; $y++) { for ($x = 0; $x -lt $canvas; $x++) {
        $k = $y * $canvas + $x; $d = $dist[$k]; if ($d -eq 0) { continue }
        $left = if ($x -gt 0) { $dist[$k - 1] + 3 } else { 3 }; $up = if ($y -gt 0) { $dist[$k - $canvas] + 3 } else { 3 }
        $ul = if ($x -gt 0 -and $y -gt 0) { $dist[$k - $canvas - 1] + 4 } else { 4 }; $ur = if ($x -lt $canvas - 1 -and $y -gt 0) { $dist[$k - $canvas + 1] + 4 } else { 4 }
        $dist[$k] = [Math]::Min($d, [Math]::Min([Math]::Min($left, $up), [Math]::Min($ul, $ur)))
    } }
    for ($y = $canvas - 1; $y -ge 0; $y--) { for ($x = $canvas - 1; $x -ge 0; $x--) {
        $k = $y * $canvas + $x; $d = $dist[$k]; if ($d -eq 0) { continue }
        $right = if ($x -lt $canvas - 1) { $dist[$k + 1] + 3 } else { 3 }; $down = if ($y -lt $canvas - 1) { $dist[$k + $canvas] + 3 } else { 3 }
        $dl = if ($x -gt 0 -and $y -lt $canvas - 1) { $dist[$k + $canvas - 1] + 4 } else { 4 }; $dr = if ($x -lt $canvas - 1 -and $y -lt $canvas - 1) { $dist[$k + $canvas + 1] + 4 } else { 4 }
        $dist[$k] = [Math]::Min($d, [Math]::Min([Math]::Min($right, $down), [Math]::Min($dl, $dr)))
    } }
    $rimWidth = 2.2; $glowWidth = 12.0
    for ($y = 0; $y -lt $canvas; $y++) { for ($x = 0; $x -lt $canvas; $x++) {
        $d = $dist[$y * $canvas + $x] / 3.0
        if ($d -le 0 -or $d -gt $glowWidth) { continue }
        $i = $y * $stride + $x * 4
        $t = [Math]::Min(1.0, [Math]::Max(0.0, ($x + $y) / (2.0 * $canvas)))
        $rimR = 127 + (43 - 127) * $t; $rimG = 224 + (143 - 224) * $t; $rimB = 246 + (191 - 246) * $t
        if ($d -le $rimWidth) {
            $bytes[$i + 2] = [byte]$rimR; $bytes[$i + 1] = [byte]$rimG; $bytes[$i] = [byte]$rimB
        } elseif ($d -le $rimWidth + 1.0) {
            $m = ($d - $rimWidth)
            $bytes[$i + 2] = [byte]($rimR * (1 - $m) + $bytes[$i + 2] * $m); $bytes[$i + 1] = [byte]($rimG * (1 - $m) + $bytes[$i + 1] * $m); $bytes[$i] = [byte]($rimB * (1 - $m) + $bytes[$i] * $m)
        } else {
            $k = 0.6 * (1.0 - ($d - $rimWidth - 1.0) / ($glowWidth - $rimWidth - 1.0))
            $bytes[$i + 2] = [byte]($bytes[$i + 2] * (1 - $k) + 6 * $k); $bytes[$i + 1] = [byte]($bytes[$i + 1] * (1 - $k) + 32 * $k); $bytes[$i] = [byte]($bytes[$i] * (1 - $k) + 52 * $k)
        }
    } }
    [System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
    $source.UnlockBits($data)
    $source.Save($sourcePath, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $source.Dispose(); $art.Dispose(); $attributes.Dispose()
    Write-Host "Prepared Hush-icon-source.png from the artwork (bounds $minX,$minY - $maxX,$maxY)."
}
if (-not (Test-Path -LiteralPath $sourcePath)) { throw 'Assets\Hush-icon-source.png is missing; run with -FromArtwork <png>.' }

$source = [System.Drawing.Bitmap]::new($sourcePath)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = New-HighQualityGraphics $bitmap
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new(); $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, $size, $size), 0, 0, $source.Width, $source.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    $memory = [System.IO.MemoryStream]::new()
    $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($memory.ToArray())
    if ($size -in @(32, 256)) {
        $name = if ($size -eq 256) { 'Hush-mark.png' } else { 'Hush-mark-32.png' }
        $bitmap.Save((Join-Path $assetDirectory $name), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $memory.Dispose(); $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$source.Dispose()
$stream = [System.IO.File]::Create((Join-Path $assetDirectory 'Hush.ico'))
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = [uint32](6 + 16 * $frames.Count)
    for ($index = 0; $index -lt $frames.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write($offset)
        $offset += [uint32]$frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
Write-Host "Generated $($frames.Count) icon frames from 16 px to 256 px from Hush-icon-source.png."
