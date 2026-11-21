# Shows (and with -Apply removes) the rebuildable clutter in the Hush folder, so there is only one place to look for the app:
#   artifacts\          <- the real files (Hush-Portable.exe, Hush-Setup.exe and their zips). Never touched here.
# Removed with -Apply: every bin\ and obj\ folder under src\ and tests\ (dozens of extra Hush.exe copies from test builds), leftover
# test fixtures and preview pictures. Never touched: artifacts\, Backups\, docs\, source, .build-home (NuGet cache) and other projects.
# A build folder that has a running program in it is skipped, so nothing you have open is ever pulled out from under you.
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $_.Path })
function Size($path) { [math]::Round((Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB, 1) }
$targets = @()
foreach ($base in 'src', 'tests') {
    $targets += Get-ChildItem -LiteralPath (Join-Path $root $base) -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in 'bin', 'obj' -and $_.Parent.FullName -notmatch '\\(bin|obj)\\' }
}
$targets += Get-ChildItem -LiteralPath $root -Recurse -Directory -Force -ErrorAction SilentlyContinue -Include '.fixtures-*', '.ui-fixture-*', '.preview-tmp' | Where-Object { $_.FullName -notmatch '\\\.build-home\\' }
$total = 0
foreach ($target in $targets) {
    $inUse = $running | Where-Object { $_.StartsWith($target.FullName + '\', [StringComparison]::OrdinalIgnoreCase) }
    $mb = Size $target.FullName
    if ($inUse) { Write-Host ("SKIP  {0,7:N1} MB  {1}  (a program is running from here)" -f $mb, $target.FullName); continue }
    $total += $mb
    if ($Apply) {
        try { Remove-Item -LiteralPath $target.FullName -Recurse -Force; Write-Host ("Removed {0,7:N1} MB  {1}" -f $mb, $target.FullName) }
        catch { Write-Host ("SKIP  {0,7:N1} MB  {1}  ({2})" -f $mb, $target.FullName, $_.Exception.Message.Split("`n")[0]) }
    } else { Write-Host ("Would remove {0,7:N1} MB  {1}" -f $mb, $target.FullName) }
}
Write-Host ("{0} {1:N1} MB of rebuildable build output. Run with -Apply to do it; the next build or Verify recreates what it needs." -f ($(if ($Apply) { 'Freed about' } else { 'Total' }), $total))
Write-Host ''
Write-Host 'Your real files (never touched by this script):'
Get-ChildItem -LiteralPath (Join-Path $root 'artifacts') -File -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("  {0,7:N1} MB  {1}" -f ($_.Length / 1MB), $_.FullName) }
$backups = Get-ChildItem -LiteralPath (Join-Path $root 'Backups') -File -Filter '*.zip' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime
Write-Host ''
Write-Host ("Backups ({0} zip files, {1:N1} MB): old snapshots are yours to delete by hand when you no longer need them." -f $backups.Count, (($backups | Measure-Object Length -Sum).Sum / 1MB))
