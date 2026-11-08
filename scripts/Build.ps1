# Builds the three things people download, into ONE flat folder: artifacts\
#   artifacts\Hush.exe                  portable app (run it anywhere; nothing is installed)
#   artifacts\Hush-Setup.exe            installer (the same program; started by that name it opens setup)
#   artifacts\Hush-windows-x64.zip      the portable app plus README and Terms, for manual downloads
#   artifacts\WHICH FILE.txt            one paragraph saying which file to use
# Old layouts (artifacts\Hush\ and stray copies) are removed so there is exactly one place and one copy of each file.
param([switch]$Standalone)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root '.build-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $root 'src\Hush\Hush.csproj'
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts '.staging'
$selfContained = if ($Standalone) { 'true' } else { 'false' }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$built = Join-Path $stage 'Hush.exe'
$version = (Get-Item -LiteralPath $built).VersionInfo.ProductVersion.Split('+')[0]
# The portable exe may be locked if the person is running it from here: say so instead of leaving a half-updated folder.
foreach ($name in 'Hush.exe', 'Hush-Setup.exe') {
    try { Copy-Item -LiteralPath $built -Destination (Join-Path $artifacts $name) -Force }
    catch { throw "Could not replace $name (is Hush running from the artifacts folder?). Close it and build again. $($_.Exception.Message)" }
}
# Zip: the portable app, README and the terms.
$zipFolder = Join-Path $stage 'zip'
New-Item -ItemType Directory -Force -Path $zipFolder | Out-Null
Copy-Item -LiteralPath $built -Destination $zipFolder
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $zipFolder
Copy-Item -LiteralPath (Join-Path $root 'docs\TERMS.md') -Destination $zipFolder
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $zipFolder
Compress-Archive -Path (Join-Path $zipFolder '*') -DestinationPath (Join-Path $artifacts 'Hush-windows-x64.zip') -Force
@"
Hush $version - which file do I use?

  Hush.exe                 PORTABLE. Double-click to run; nothing is installed. Put it anywhere you can write to
                           (for example D:\Hush\) and make a desktop shortcut to it.
  Hush-Setup.exe           INSTALLER. Installs Hush for your account only (no administrator rights), adds a Start menu
                           entry and an optional desktop shortcut, and lists Hush under Settings > Apps.
  Hush-windows-x64.zip     The portable app plus README, Terms and the MIT license in one zip, for manual downloads.

Both run the same program and both update themselves from inside the app (About > Check for updates).
You only need one of them. Needs Windows 10/11 64-bit and the .NET 10 Desktop Runtime.
"@ | Set-Content -LiteralPath (Join-Path $artifacts 'WHICH FILE.txt') -Encoding utf8
# Remove staging and the old nested layout (artifacts\Hush\) so nothing is duplicated.
Remove-Item -LiteralPath $stage -Recurse -Force
$legacy = Join-Path $artifacts 'Hush'
if (Test-Path -LiteralPath $legacy) { try { Remove-Item -LiteralPath $legacy -Recurse -Force } catch { Write-Warning "Could not remove the old folder $legacy (in use?): $($_.Exception.Message)" } }
Write-Host "Portable:  $(Join-Path $artifacts 'Hush.exe')"
Write-Host "Installer: $(Join-Path $artifacts 'Hush-Setup.exe')"
Write-Host "Zip:       $(Join-Path $artifacts 'Hush-windows-x64.zip')"
