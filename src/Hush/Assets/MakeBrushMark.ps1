# Makes Hush-brush.png: the brush alone from the supplied icon artwork (Hush-icon-source.png), with no tile, background or outline (owner, November 20, 2026:
# "only the brush, without background or outline", for the About popup and Setup). Nothing is redrawn: each pixel's transparency comes from how far it stands
# out from the navy tile (its brightness: the brush is light cyan and white, the tile and most of its blue glow are dark), and its colour is un-blended from that navy so the
# edges stay clean on any background. Only the middle of the tile is read, so the tile's lighter rim never comes along. Windows PowerShell 5.1 (System.Drawing).
param([string]$Source = (Join-Path $PSScriptRoot 'Hush-icon-source.png'), [string]$Target = (Join-Path $PSScriptRoot 'Hush-brush.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$image = [System.Drawing.Bitmap]::FromFile($Source)
try {
    $w = $image.Width; $h = $image.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $image.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $image.UnlockBits($data)
    $stride = $data.Stride
    # The tile's navy behind the brush (sampled from the artwork, inside the tile).
    $bgR = 12.0; $bgG = 36.0; $bgB = 66.0
    $low = 72.0; $high = 118.0          # brightness at or below $low = background (the tile and most of its glow), at or above $high = solid brush
    $inset = [int]($w * 0.12)           # stay inside the tile, away from its rim
    $n = $w * $h
    $alpha = New-Object double[] $n
    for ($y = $inset; $y -lt $h - $inset; $y++) {
        for ($x = $inset; $x -lt $w - $inset; $x++) {
            $i = $y * $stride + $x * 4
            $l = 0.3 * $bytes[$i + 2] + 0.59 * $bytes[$i + 1] + 0.11 * $bytes[$i]
            if ($l -le $low) { continue }
            $t = [Math]::Min(1.0, ($l - $low) / ($high - $low))
            $alpha[$y * $w + $x] = $t * $t * (3 - 2 * $t)   # smoothstep
        }
    }
    # Keep only the brush: the largest connected shape of solid-ish pixels (this drops any piece of the tile's rim).
    $label = New-Object int[] $n; $queue = New-Object int[] $n; $next = 0; $best = 0; $bestSize = 0
    for ($p = 0; $p -lt $n; $p++) {
        if ($alpha[$p] -lt 0.35 -or $label[$p] -ne 0) { continue }
        $next++; $head = 0; $tail = 0; $queue[$tail++] = $p; $label[$p] = $next
        while ($head -lt $tail) {
            $q = $queue[$head++]; $qx = $q % $w
            foreach ($d in @(-1, 1, -$w, $w)) {
                $r2 = $q + $d
                if ($r2 -lt 0 -or $r2 -ge $n) { continue }
                if (($d -eq -1 -and $qx -eq 0) -or ($d -eq 1 -and $qx -eq $w - 1)) { continue }
                if ($alpha[$r2] -ge 0.35 -and $label[$r2] -eq 0) { $label[$r2] = $next; $queue[$tail++] = $r2 }
            }
        }
        if ($tail -gt $bestSize) { $bestSize = $tail; $best = $next }
    }
    # The background is everything reachable from the image border without crossing the brush; whatever the brush encloses (dark shading) is a hole to fill.
    $outside = New-Object bool[] $n; $head = 0; $tail = 0
    for ($x = 0; $x -lt $w; $x++) { foreach ($p in @(($x), (($h - 1) * $w + $x))) { if (-not $outside[$p]) { $outside[$p] = $true; $queue[$tail++] = $p } } }
    for ($y = 0; $y -lt $h; $y++) { foreach ($p in @(($y * $w), ($y * $w + $w - 1))) { if (-not $outside[$p]) { $outside[$p] = $true; $queue[$tail++] = $p } } }
    while ($head -lt $tail) {
        $q = $queue[$head++]; $qx = $q % $w
        foreach ($d in @(-1, 1, -$w, $w)) {
            $r2 = $q + $d
            if ($r2 -lt 0 -or $r2 -ge $n) { continue }
            if (($d -eq -1 -and $qx -eq 0) -or ($d -eq 1 -and $qx -eq $w - 1)) { continue }
            if (-not $outside[$r2] -and $label[$r2] -ne $best) { $outside[$r2] = $true; $queue[$tail++] = $r2 }
        }
    }
    # Distance of 3 pixels around the brush keeps its soft edge; anything farther (and every other shape) is dropped.
    $near = New-Object bool[] $n
    for ($p = 0; $p -lt $n; $p++) {
        if ($label[$p] -ne $best) { continue }
        $px = $p % $w; $py = [int][Math]::Floor($p / $w)
        for ($dy = -3; $dy -le 3; $dy++) { for ($dx = -3; $dx -le 3; $dx++) { $yy = $py + $dy; $xx = $px + $dx; if ($yy -ge 0 -and $yy -lt $h -and $xx -ge 0 -and $xx -lt $w) { $near[$yy * $w + $xx] = $true } } }
    }
    $out = New-Object byte[] ($stride * $h)
    $minX = $w; $minY = $h; $maxX = 0; $maxY = 0
    for ($p = 0; $p -lt $n; $p++) {
        $x = $p % $w; $y = [int][Math]::Floor($p / $w); $i = $y * $stride + $x * 4
        $b = [double]$bytes[$i]; $g = [double]$bytes[$i + 1]; $r = [double]$bytes[$i + 2]
        $solidHole = $false
        if ($label[$p] -eq $best) { $a = $alpha[$p] }                            # the brush
        elseif (-not $outside[$p] -and $alpha[$p] -gt 0.02) { $a = $alpha[$p] }  # enclosed by the brush: its glass head, where the navy shows through, stays see-through
        elseif ($near[$p] -and $alpha[$p] -gt 0.02) { $a = $alpha[$p] }         # the soft edge of the brush
        else { continue }
        if ($solidHole) { $cr = $r; $cg = $g; $cb = $b }
        else {
            # Un-blend from the navy: colour = (seen - (1 - a) * navy) / a
            $cr = [Math]::Max(0, [Math]::Min(255, ($r - (1 - $a) * $bgR) / $a))
            $cg = [Math]::Max(0, [Math]::Min(255, ($g - (1 - $a) * $bgG) / $a))
            $cb = [Math]::Max(0, [Math]::Min(255, ($b - (1 - $a) * $bgB) / $a))
        }
        $out[$i] = [byte]$cb; $out[$i + 1] = [byte]$cg; $out[$i + 2] = [byte]$cr; $out[$i + 3] = [byte][Math]::Round($a * 255)
        if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }; if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
    }
    # Crop to the brush as a square with a small margin, so it centres in any square slot.
    $size = [Math]::Max($maxX - $minX, $maxY - $minY) + 16
    $cx = [int](($minX + $maxX) / 2); $cy = [int](($minY + $maxY) / 2)
    $left = $cx - [int]($size / 2); $top = $cy - [int]($size / 2)
    $result = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $resultData = $result.LockBits((New-Object System.Drawing.Rectangle 0, 0, $size, $size), [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $resultBytes = New-Object byte[] ($resultData.Stride * $size)
    for ($y = 0; $y -lt $size; $y++) {
        $sy = $top + $y; if ($sy -lt 0 -or $sy -ge $h) { continue }
        for ($x = 0; $x -lt $size; $x++) {
            $sx = $left + $x; if ($sx -lt 0 -or $sx -ge $w) { continue }
            $si = $sy * $stride + $sx * 4; $di = $y * $resultData.Stride + $x * 4
            $resultBytes[$di] = $out[$si]; $resultBytes[$di + 1] = $out[$si + 1]; $resultBytes[$di + 2] = $out[$si + 2]; $resultBytes[$di + 3] = $out[$si + 3]
        }
    }
    [System.Runtime.InteropServices.Marshal]::Copy($resultBytes, 0, $resultData.Scan0, $resultBytes.Length)
    $result.UnlockBits($resultData)
    $result.Save($Target, [System.Drawing.Imaging.ImageFormat]::Png)
    $result.Dispose()
    "Wrote $Target ($size x $size)"
}
finally { $image.Dispose() }
