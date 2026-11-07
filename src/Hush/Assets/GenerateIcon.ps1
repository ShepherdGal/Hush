param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = $PSScriptRoot
$frames = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16,24,32,48,64,128,256)
$measurements = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size,$size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(17,21,27))
    $teal = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(60,207,217))
    $corner = [single]($size * 0.19)
    $diameter = [single]($corner * 2)
    $surface = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $surface.AddArc(0,0,$diameter,$diameter,180,90)
    $surface.AddArc(($size-$diameter),0,$diameter,$diameter,270,90)
    $surface.AddArc(($size-$diameter),($size-$diameter),$diameter,$diameter,0,90)
    $surface.AddArc(0,($size-$diameter),$diameter,$diameter,90,90)
    $surface.CloseFigure()
    $graphics.FillPath($background,$surface)
    $letters = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $family = [System.Drawing.FontFamily]::new('Inter SemiBold')
    # Single Hush 'H' mark.
    $letters.AddString('H',$family,[int][System.Drawing.FontStyle]::Regular,100,[System.Drawing.PointF]::new(0,0),[System.Drawing.StringFormat]::GenericTypographic)
    $bounds = $letters.GetBounds()
    $scale = [single][Math]::Min(($size * 0.66 / $bounds.Width),($size * 0.62 / $bounds.Height))
    $width = [single]($bounds.Width * $scale)
    $height = [single]($bounds.Height * $scale)
    $offsetX = [single](($size-$width)/2 - $bounds.X*$scale)
    $offsetY = [single](($size-$height)/2 - $bounds.Y*$scale)
    $matrix = [System.Drawing.Drawing2D.Matrix]::new($scale,0,0,$scale,$offsetX,$offsetY)
    $letters.Transform($matrix)
    $graphics.FillPath($teal,$letters)
    # GDI rasterization can shift a centered vector by a pixel at desktop sizes.
    # Measure the rendered aqua ink and correct the actual pixel bounds too.
    for ($pass=0; $pass -lt 3; $pass++) {
        $minX=$size; $minY=$size; $maxX=-1; $maxY=-1
        for($y=0;$y -lt $size;$y++) { for($x=0;$x -lt $size;$x++) {
            $pixel=$bitmap.GetPixel($x,$y)
            if($pixel.A -gt 127 -and $pixel.G -gt 130 -and $pixel.B -gt 130 -and $pixel.R -lt 100) {
                $minX=[Math]::Min($minX,$x);$maxX=[Math]::Max($maxX,$x)
                $minY=[Math]::Min($minY,$y);$maxY=[Math]::Max($maxY,$y)
            }
        } }
        $right=$size-1-$maxX; $bottom=$size-1-$maxY
        if([Math]::Abs($minX-$right) -le 1 -and [Math]::Abs($minY-$bottom) -le 1) { break }
        $adjust = [System.Drawing.Drawing2D.Matrix]::new()
        $adjust.Translate([single](($right-$minX)/2),[single](($bottom-$minY)/2))
        $letters.Transform($adjust); $adjust.Dispose()
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.FillPath($background,$surface); $graphics.FillPath($teal,$letters)
    }
    $measurements += [pscustomobject]@{Size=$size; Left=$minX; Right=$right; Top=$minY; Bottom=$bottom; LetterHeight=($maxY-$minY+1)}
    $memory = [System.IO.MemoryStream]::new()
    $bitmap.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($memory.ToArray())
    if ($size -in @(32,256)) {
        $name = if ($size -eq 256) { 'Hush-mark.png' } else { 'Hush-mark-32.png' }
        $bitmap.Save((Join-Path $assetDirectory $name),[System.Drawing.Imaging.ImageFormat]::Png)
    }
    $memory.Dispose(); $matrix.Dispose(); $family.Dispose(); $letters.Dispose(); $surface.Dispose()
    $background.Dispose(); $teal.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$stream = [System.IO.File]::Create((Join-Path $assetDirectory 'Hush.ico'))
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = [uint32](6+16*$frames.Count)
    for ($index=0;$index -lt $frames.Count;$index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write($offset)
        $offset += [uint32]$frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
foreach ($measurement in $measurements) {
    if ([Math]::Abs($measurement.Left-$measurement.Right) -gt 1 -or [Math]::Abs($measurement.Top-$measurement.Bottom) -gt 1) { throw 'Icon letters are not centered.' }
    if ($measurement.LetterHeight -lt $measurement.Size*0.55) { throw 'Icon letter is too small.' }
}
$measurements | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $assetDirectory 'Icon-layout.json') -Encoding utf8
Write-Host 'Generated 7 centered H icon frames from 16px to 256px; the letter is pixel-centered within 1px on both axes.'
