param([ValidateSet('Release', 'Verification')][string]$Configuration = 'Verification', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $workspace 'tests\Hush.Tests'
$env:DOTNET_CLI_HOME = Join-Path $workspace '.build-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $NoBuild) {
    dotnet build (Join-Path $testProject 'Hush.Tests.csproj') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
}
$executable = Join-Path $testProject "bin\$Configuration\net10.0-windows10.0.19041.0\Hush.Tests.exe"
function Invoke-TestProcess([string[]]$TestArguments) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $TestArguments) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(55000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'The test process did not exit; a crash dialog or stuck UI thread may be blocking it.'
        }
        return [pscustomobject]@{Code=$process.ExitCode; Output=$stdout.GetAwaiter().GetResult(); Error=$stderr.GetAwaiter().GetResult()}
    }
    finally { $process.Dispose() }
}
$normal = Invoke-TestProcess @()
Write-Host $normal.Output
if ($normal.Code -ne 0) { throw "Normal verification failed with code $($normal.Code): $($normal.Error)" }
$themeMotion = Invoke-TestProcess @('--verify-theme-motion')
Write-Host $themeMotion.Output
if ($themeMotion.Code -ne 0) { throw "Theme motion verification failed with code $($themeMotion.Code): $($themeMotion.Error)" }
$cleanSelection = Invoke-TestProcess @('--verify-clean-selection')
Write-Host $cleanSelection.Output
if ($cleanSelection.Code -ne 0) { throw "Clean selection verification failed with code $($cleanSelection.Code): $($cleanSelection.Error)" }
$updates = Invoke-TestProcess @('--verify-updates')
Write-Host $updates.Output
if ($updates.Code -ne 0) { throw "Update flow verification failed with code $($updates.Code): $($updates.Error)" }
$windowsSettings = Invoke-TestProcess @('--verify-windows-settings')
Write-Host $windowsSettings.Output
if ($windowsSettings.Code -ne 0) { throw "Windows settings verification failed with code $($windowsSettings.Code): $($windowsSettings.Error)" }
$setup = Invoke-TestProcess @('--verify-setup')
Write-Host $setup.Output
if ($setup.Code -ne 0) { throw "Setup verification failed with code $($setup.Code): $($setup.Error)" }
$smartBoost = Invoke-TestProcess @('--verify-smart-boost')
Write-Host $smartBoost.Output
if ($smartBoost.Code -ne 0) { throw "Smart Boost verification failed with code $($smartBoost.Code): $($smartBoost.Error)" }
$failure = Invoke-TestProcess @('--verify-failure-path')
if ($failure.Code -ne 1 -or $failure.Error -notmatch 'Intentional UI test failure') {
    throw "Failure reporting regression: exit=$($failure.Code), detail=$($failure.Error)"
}
Write-Host 'PASS: Deliberate UI assertion failure was reported with its stack trace and exited normally with code 1. No unhandled crash or stuck process.'
