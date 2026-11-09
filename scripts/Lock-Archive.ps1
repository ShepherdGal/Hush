# Writes CHECKSUMS.sha256 for a locked archive folder (docs/locked/<name>): one line per file, "<sha256>  <relative path>", sorted, CHECKSUMS.sha256 itself excluded.
# tests/Hush.Tests/LockedArchiveChecks.cs verifies every locked folder against it, so a changed, added or removed file fails Verify.ps1. Run this ONLY after the
# owner has unlocked the folder in plain words and the change is done (docs/locked/*/README.md says the same).
param([Parameter(Mandatory)][string]$Folder)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Folder).Path
$lines = Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Name -ne 'CHECKSUMS.sha256' } | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
}
[System.IO.File]::WriteAllText((Join-Path $root 'CHECKSUMS.sha256'), (($lines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
Write-Host "Locked $($lines.Count) files in $root"
