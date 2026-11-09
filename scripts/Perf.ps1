# Measures what Hush costs on a weak PC, with the checks of docs/RULES.md. Read-only: it shows a real window for about 40 seconds per run (not activated, not in
# the taskbar), uses a synthetic fixture and never cleans anything. Run it with `pwsh`, before and after touching animation, layout, scrolling or popups.
#   normal      this PC as it is
#   weak        4 logical processors + software rendering (a stand-in for an Intel i3 with a small graphics chip; real i3 cores are also slower, so read the
#               numbers as a lower bound) and the product's run-time settings (no profile-guided tiering)
# Budgets (docs/RULES.md "Performance budgets") are checked against the 'weak' run and the script exits 1 when one is broken.
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $workspace '.build-home'
if (-not $NoBuild) { dotnet build (Join-Path $workspace 'tests\Hush.Tests\Hush.Tests.csproj') -c Verification | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'Build failed.' } }
$exe = Join-Path $workspace 'tests\Hush.Tests\bin\Verification\net10.0-windows10.0.19041.0\Hush.Tests.exe'
function Run-Perf([hashtable]$Environment) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($exe); $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.ArgumentList.Add('--ui-perf')
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    $process = [System.Diagnostics.Process]::Start($start); $text = $process.StandardOutput.ReadToEnd(); $process.WaitForExit(); $text
}
Write-Host '=== normal'; $normal = Run-Perf @{}; Write-Host $normal
Write-Host '=== weak (4 processors, software rendering)'; $weak = Run-Perf @{ HUSH_PERF_CORES = '4'; HUSH_PERF_SW = '1'; DOTNET_TieredPGO = '0' }; Write-Host $weak
$failures = @()
function Cpu([string]$text, [string]$label) { $m = [regex]::Match($text, [regex]::Escape($label) + '\s+CPU\s+([\d.]+)%'); if ($m.Success) { [double]$m.Groups[1].Value } else { -1 } }
function Median([string]$text, [string]$label) { $m = [regex]::Match($text, [regex]::Escape($label) + '.*?median ([\d.]+) ms'); if ($m.Success) { [double]$m.Groups[1].Value } else { -1 } }
if ((Cpu $weak 'Home, idle for 8 s (warmed up)') -gt 3) { $failures += 'Home idle must stay under 3% of a core' }
if ((Cpu $weak 'Open About popup') -gt 100) { $failures += 'Opening a popup must stay under 100% of a core (software rendering)' }
foreach ($label in 'Wheel-scroll Settings down, 10 notches', 'Wheel-scroll Settings up, 10 notches', 'Expand Privacy card', 'Open About popup', 'Cleaning cue in the gauge, 5 s') {
    if ((Median $weak $label) -gt 20) { $failures += "${label}: median frame must stay under 20 ms" }
}
if ($failures.Count -gt 0) { Write-Host 'BUDGETS BROKEN:'; $failures | ForEach-Object { Write-Host "  - $_" }; exit 1 }
Write-Host 'Budgets kept.'
