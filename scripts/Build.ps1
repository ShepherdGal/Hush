param([switch]$Standalone)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root '.build-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $root 'src\Hush\Hush.csproj'
$output = Join-Path $root 'artifacts\Hush'
$selfContained = if ($Standalone) { 'true' } else { 'false' }
dotnet publish $project -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $output
Compress-Archive -Path (Join-Path $output '*') -DestinationPath (Join-Path $root 'artifacts\Hush-windows-x64.zip') -Force
# Two ways to get Hush from the same program: Hush.exe is portable; Hush-Setup.exe (the same file, so it starts the installer by name) installs it.
Copy-Item -LiteralPath (Join-Path $output 'Hush.exe') -Destination (Join-Path $root 'artifacts\Hush-Setup.exe') -Force
Write-Host "Portable: $output\Hush.exe"
Write-Host "Installer: $(Join-Path $root 'artifacts\Hush-Setup.exe')"
