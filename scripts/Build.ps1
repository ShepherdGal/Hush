# Builds the things people download, into ONE flat folder: artifacts#   artifacts\Hush-Portable.zip         UPLOAD THIS: the portable app (GitHub does not accept .exe uploads, so releases carry zips)
#   artifacts\Hush-Setup.zip            UPLOAD THIS: the installer
#   artifacts\Hush-Portable.exe         the same portable app, unzipped, for using it on this PC (nothing is installed)
#   artifacts\Hush-Setup.exe            the installer, unzipped (the same program; started by that name it opens setup)
#   artifacts\LICENSE                   the MIT license (also attached to the release, and inside both zips)
#   artifacts\Hush-icon.png             the app icon (also attached to the release)
#   artifacts\WHICH FILE.txt            one paragraph saying which file to use
# Each zip holds just its exe plus LICENSE and the terms. Old layouts (artifacts\Hush\, Hush.exe, Hush-windows-x64.zip) are removed
# so there is exactly one place and one copy of each file.
# 0.9: the .NET runtime is inside the exe (self-contained, compressed), so nobody has to install .NET. -FrameworkDependent makes the small
# build that needs the .NET 10 Desktop Runtime (for quick local tests only, never for a release).
param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root '.build-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $root 'src\Hush\Hush.csproj'
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts '.staging'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$compress = if ($FrameworkDependent) { 'false' } else { 'true' }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=$compress -p:DebugType=None -p:DebugSymbols=false -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$built = Join-Path $stage 'Hush.exe'
$version = (Get-Item -LiteralPath $built).VersionInfo.ProductVersion.Split('+')[0]
# The portable exe may be locked if the person is running it from here: say so instead of leaving a half-updated folder.
foreach ($name in 'Hush-Portable.exe', 'Hush-Setup.exe') {
    try { Copy-Item -LiteralPath $built -Destination (Join-Path $artifacts $name) -Force }
    catch { throw "Could not replace $name (is Hush running from the artifacts folder?). Close it and build again. $($_.Exception.Message)" }
}
# Loose copies of the license and the icon, so they sit beside the downloads (and are attached to the release).
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $artifacts 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $root 'docs\app-icon.png') -Destination (Join-Path $artifacts 'Hush-icon.png') -Force
# One zip per exe: the exe, the license and the terms. The updater reads Hush-Portable.zip and unpacks Hush-Portable.exe from it.
foreach ($name in 'Hush-Portable', 'Hush-Setup') {
    $zipFolder = Join-Path $stage "zip-$name"
    New-Item -ItemType Directory -Force -Path $zipFolder | Out-Null
    Copy-Item -LiteralPath $built -Destination (Join-Path $zipFolder "$name.exe")
    Copy-Item -LiteralPath (Join-Path $root 'docs\TERMS.md') -Destination $zipFolder
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $zipFolder
    Compress-Archive -Path (Join-Path $zipFolder '*') -DestinationPath (Join-Path $artifacts "$name.zip") -Force
}
@"
Hush $version - which file do I use?

  Hush-Portable.zip        PORTABLE. Unzip it anywhere you can write to (for example D:\Hush\), then double-click
                           Hush-Portable.exe. Nothing is installed.
  Hush-Setup.zip           INSTALLER. Unzip it and run Hush-Setup.exe. Installing needs no administrator rights (it is for your
                           account only). If you later agree to the optional administrator mode, Hush asks once and then moves the
                           installed copy to Program Files. Setup adds a Start menu entry and an optional desktop shortcut, and lists Hush under Settings > Apps.

(GitHub does not accept .exe uploads, so the downloads are zips. In this folder the unzipped Hush-Portable.exe and Hush-Setup.exe
are also kept for using on this PC.)
Both run the same program and both update themselves from inside the app (About > Check for updates).
You only need one of them. Needs Windows 10/11 64-bit. Nothing else has to be installed.
LICENSE (MIT) and Hush-icon.png are loose copies for reading and for the project page; you do not need them to run Hush.
"@ | Set-Content -LiteralPath (Join-Path $artifacts 'WHICH FILE.txt') -Encoding utf8
# Remove staging and the old layouts (artifacts\Hush\, the pre-zip Hush.exe and the combined zip) so nothing is duplicated.
Remove-Item -LiteralPath $stage -Recurse -Force
$legacy = Join-Path $artifacts 'Hush'
if (Test-Path -LiteralPath $legacy) { try { Remove-Item -LiteralPath $legacy -Recurse -Force } catch { Write-Warning "Could not remove the old folder $legacy (in use?): $($_.Exception.Message)" } }
foreach ($old in 'Hush.exe', 'Hush-windows-x64.zip') {
    $path = Join-Path $artifacts $old
    if (Test-Path -LiteralPath $path) { try { Remove-Item -LiteralPath $path -Force } catch { Write-Warning "Could not remove the old file $path (in use?): $($_.Exception.Message)" } }
}
Write-Host "Portable:  $(Join-Path $artifacts 'Hush-Portable.exe')   (zip: Hush-Portable.zip)"
Write-Host "Installer: $(Join-Path $artifacts 'Hush-Setup.exe')   (zip: Hush-Setup.zip)"
Write-Host "Upload the two zips to GitHub; GitHub does not accept .exe files."
