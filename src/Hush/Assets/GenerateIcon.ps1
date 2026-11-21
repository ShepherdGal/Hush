# Builds Hush.ico (16-256 px) and Hush-mark.png from Assets\Hush-icon-source.png.
#
# The source is the user's brush artwork (latest supplied: the glass rounded square, November 11, 2026), exactly as supplied (no color or style edits), prepared once from the supplied picture (-FromArtwork): the black surround is keyed
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
    # The surround is a dark navy (not pure black) with a faint glow, and the artwork's own rim is bright, so the outside is found by flooding in from the corner
    # through dark pixels; the bright rim stops the flood. Outside pixels become transparent, and the first inside pixels get a soft alpha and are un-blended from the navy.
    Add-Type -TypeDefinition @"
public static class IconKey {
    public static int[] Run(byte[] px, int stride, int w, int h) {
        var outside = new bool[w * h]; var queue = new int[w * h]; int head = 0, tail = 0;
        System.Func<int, int> mx = k => { int i = (k / w) * stride + (k % w) * 4; return System.Math.Max(px[i], System.Math.Max(px[i + 1], px[i + 2])); };
        int[] seeds = { 0, w - 1, (h - 1) * w, h * w - 1 };
        foreach (int s in seeds) { if (!outside[s]) { outside[s] = true; queue[tail++] = s; } }
        while (head < tail) {
            int k = queue[head++]; int x = k % w, y = k / w;
            for (int d = 0; d < 4; d++) {
                int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue; int n = ny * w + nx;
                if (outside[n] || mx(n) > 62) continue; outside[n] = true; queue[tail++] = n;
            }
        }
        int minX = w, minY = h, maxX = 0, maxY = 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
            int k = y * w + x, i = y * stride + x * 4;
            if (outside[k]) { px[i] = 0; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = 0; continue; }
            bool edge = (x > 0 && outside[k - 1]) || (x < w - 1 && outside[k + 1]) || (y > 0 && outside[k - w]) || (y < h - 1 && outside[k + w]);
            double alpha = 1.0;
            if (edge) { alpha = System.Math.Min(1.0, System.Math.Max(0.0, (mx(k) - 34) / 56.0)); if (alpha > 0 && alpha < 1) { px[i] = (byte)System.Math.Max(0, System.Math.Min(255, (px[i] - 32 * (1 - alpha)) / alpha)); px[i + 1] = (byte)System.Math.Max(0, System.Math.Min(255, (px[i + 1] - 9 * (1 - alpha)) / alpha)); px[i + 2] = (byte)System.Math.Max(0, System.Math.Min(255, (px[i + 2] - 3 * (1 - alpha)) / alpha)); } }
            px[i + 3] = (byte)(alpha * 255);
            if (alpha > 0.5) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        }
        return new int[] { minX, minY, maxX, maxY };
    }
}
"@
    $bounds = [IconKey]::Run($bytes, $data.Stride, $art.Width, $art.Height)
    $minX = $bounds[0]; $minY = $bounds[1]; $maxX = $bounds[2]; $maxY = $bounds[3]
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
    # The artwork is used exactly as supplied: no recolor, no repainted rim, no tone changes (owner, November 11, 2026). Only the surround is made transparent and the square cropped.
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
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetDirectory 'Hush-mark.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
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
