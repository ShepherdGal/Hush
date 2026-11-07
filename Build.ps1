param([switch]$Standalone)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $PSScriptRoot 'Hush.csproj'
$output = Join-Path $PSScriptRoot 'artifacts\Hush'
$selfContained = if ($Standalone) { 'true' } else { 'false' }
dotnet publish $project -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output
Compress-Archive -Path (Join-Path $output '*') -DestinationPath (Join-Path $PSScriptRoot 'artifacts\Hush-windows-x64.zip') -Force
Write-Host "Ready: $output\Hush.exe"
