param([ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$Label = ('hush-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') + 'Z'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$workspace = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$backupDirectory = Join-Path $workspace 'Backups'
New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
$sourcePath = Join-Path $backupDirectory ($Label + '-source.zip')
$portablePath = Join-Path $backupDirectory ($Label + '-portable.zip')
$inventoryPath = Join-Path $backupDirectory ($Label + '-inventory.json')
foreach ($targetPath in @($sourcePath,$portablePath,$inventoryPath)) {
    if (-not ([System.IO.Path]::GetFullPath($targetPath)).StartsWith($backupDirectory + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Backup path escaped its directory.' }
    if (Test-Path -LiteralPath $targetPath) { throw "Refusing to overwrite an existing snapshot: $targetPath" }
}
$portable = Join-Path $workspace 'artifacts\Hush-windows-x64.zip'
if (-not (Test-Path -LiteralPath $portable)) { throw 'Build the portable app before taking a final snapshot.' }
# Source only: the repository's own folders, never build output (bin/obj), backups, artifacts or caches.
$buildFolders = '\bin\', '\obj\'
function Source-Files($relative) {
    Get-ChildItem -LiteralPath (Join-Path $workspace $relative) -File -Recurse |
        Where-Object { $path = $_.FullName; -not ($buildFolders | Where-Object { $path.Contains($_) }) -and $_.Name -notlike '.fixtures-*' }
}
$files = @(
    Get-ChildItem -LiteralPath $workspace -File | Where-Object { $_.Extension -in '.md','.sln','.ps1' -or $_.Name -eq '.gitignore' }
    Source-Files 'src'
    Source-Files 'tests'
    Source-Files 'scripts'
    Source-Files 'docs'
) | Sort-Object FullName -Unique
$entries = @($files | ForEach-Object {
    [pscustomobject]@{
        Path = [System.IO.Path]::GetRelativePath($workspace,$_.FullName).Replace('\','/')
        Bytes = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$sourceManifest = [pscustomobject]@{Label=$Label; CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o'); Files=$entries}
$archive = [System.IO.Compression.ZipFile]::Open($sourcePath,[System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $entries) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $workspace $entry.Path),$entry.Path,[System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $manifestEntry = $archive.CreateEntry('_snapshot-manifest.json')
    $writer = [System.IO.StreamWriter]::new($manifestEntry.Open())
    try { $writer.Write(($sourceManifest | ConvertTo-Json -Depth 6)) } finally { $writer.Dispose() }
} finally { $archive.Dispose() }
# Re-read every archived source file and verify its content against the pre-archive hash.
$archive = [System.IO.Compression.ZipFile]::OpenRead($sourcePath)
try {
    if ($archive.Entries.Count -ne ($entries.Count + 1)) { throw 'Unexpected source archive entry count.' }
    foreach ($entry in $entries) {
        $stream = $archive.GetEntry($entry.Path).Open()
        try { $actual = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($actual -ne $entry.Sha256) { throw "Archive verification failed: $($entry.Path)" }
    }
} finally { $archive.Dispose() }
[System.IO.File]::Copy($portable,$portablePath,$false)
if ((Get-FileHash -LiteralPath $portable).Hash -ne (Get-FileHash -LiteralPath $portablePath).Hash) { throw 'Portable backup hash mismatch.' }
$snapshots = @($sourcePath,$portablePath | ForEach-Object {
    [pscustomobject]@{Path=[System.IO.Path]::GetFileName($_); Sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash; Bytes=(Get-Item -LiteralPath $_).Length}
})
[pscustomobject]@{Label=$Label; CreatedUtc=$sourceManifest.CreatedUtc; VerifiedSourceFiles=$entries.Count; Snapshots=$snapshots; Files=$entries} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $inventoryPath -Encoding utf8
Write-Host "Verified $($entries.Count) source files."
Write-Host "Source: $sourcePath"
Write-Host "Portable: $portablePath"
Write-Host "Inventory: $inventoryPath"
