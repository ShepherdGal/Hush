# Hush

Version 0.8. A compact native Windows cleaner: scan, review results by category, clean what you choose, with a light or dark theme. No ads, telemetry, startup tasks, or third-party application packages.

## Project layout

```
src/Hush/       the app: Cleaning/, Updates/, Setup/, WindowsSettings/, UI/, Assets/
tests/Hush.Tests/   verification runner and fixtures-only checks
docs/           design system, handoff, changelog, releasing, Windows settings
scripts/        Build.ps1, Verify.ps1, Backup.ps1
Hush.sln        opens the app and its tests together
```

## Run

Two ways to get Hush, both from the release page (both need the **.NET 10 Desktop Runtime** and are unsigned):

- **Installer:** run `Hush-Setup.exe`. It installs Hush for your account only (no administrator rights) to `%LOCALAPPDATA%\Programs\Hush`, adds a Start menu entry (and, if you like, a desktop shortcut) and lists Hush under Settings > Apps, where Windows can uninstall it. Setup also offers "Run without installing".
- **Portable:** run `Hush.exe` from any writable folder, or unzip `Hush-windows-x64.zip`. Nothing is installed or registered.

Both keep their settings in `%LOCALAPPDATA%\Hush` and both update themselves in place. Uninstalling keeps your settings and history unless you tick "Also delete my Hush settings, history and logs".

1. The gauge starts empty at **0%**. Click to scan; click again to stop. The arc starts at twelve o'clock and fills clockwise. A live files-found counter runs beneath it; byte totals appear when the scan ends.
2. Click the completed gauge to rescan, or the **Scan complete. Take a look. →** link to open Review Files. Real file count and combined size appear beneath the link; registry entries are counted separately.
3. Review categories or expand individual sections. Category checkboxes select their own files; Select All Categories selects all candidates. Mixed selection shows a dash. **Expand all / Collapse all** controls every section.
4. Ice blue means **Safe**; orange marks **Review recommended** and red (the same red as the Clean button) marks **Advanced**, with written labels. Review/caution candidates start unselected. Checked category titles and filenames adopt their checkbox accent; unchecked text returns to normal.
5. After a successful complete scan, Home **Clean** and Review **Clean selected** confirm the current selection over a softly blurred app. Partial results are read-only; scan again before selecting/cleaning. Deletion is permanent and bypasses the Recycle Bin. Keep my files or Escape cancels.
6. Click **Hush** for About, version, current patch notes and updates. A finished scan never forces navigation; Back preserves results and selections.

Normal actions share 44-DIP height and smooth 320 ms incoming-page fades; outgoing pages disappear immediately. Light/Dark colors separately ease over 320 ms, including when reversed mid-transition, without keeping old-page images. Home Clean is smaller, nearly transparent and outlined, with restrained outer lighting. Categories/files use neutral surfaces and safety accent lines. Hover never enlarges elements. The 320-DIP gauge stays stationary when results appear. Checkbox, Clean and thin checked-category outlines are the only exceptions to borderless surfaces; the rounded window retains transparent corners.

## Cleanup categories (0.8)

These are the categories and rules as of 0.8 (the full catalog is in docs/DESIGN.md and ReviewCategory.cs); more categories and broader cleaning are planned.

| Category | Eligibility and default |
| --- | --- |
| Temp Files | Current user/application temp: created AND modified over 48 hours ago. Accessible Windows Temp: both over seven days. Included automatically in the normal selected Clean flow. |
| Cached Files | Chrome/Edge/Brave profile Cache, Code Cache, GPUCache over seven days; browser must be closed at scan and deletion. Safe/default selected. |
| DirectX Cache | Rebuildable local D3DSCache over seven days. Safe/default selected. |
| Junk Files | Only .wer files in approved Windows error-report archives over 30 days. Review/default unselected. |
| Broken Shortcuts | Old .lnk files in user Desktop/Start-menu locations; no arguments; local approved application target AND installation directory verified missing. Target/content rechecked. Review/default unselected. Only the link is removed. |
| Crash Reports | Local CrashDumps/*.dmp over 14 days. Review/default unselected; diagnostics can help troubleshooting. |
| Registry Leftovers | Current-user App Paths only, missing executable AND installation folder under approved program roots. Caution/default unselected; revalidated and exported before removal. |

Files are rechecked under an exclusive Windows handle before deletion. Recent, locked, read-only/system, linked, moved or changed files are skipped. Directories are never recursively removed. Cancellation preserves unprocessed files. Limits: 25,000 candidates per batch, 100,000 discovered files per location. Partial/limited scans are labeled accurately.

Audit reports and registry backups still live under %LOCALAPPDATA%/Hush/Logs and Registry backups. They stay local and contain file paths. Registry .reg backups may be reviewed/imported with Registry Editor. Hiding the utility controls does not disable these safety records.

## Windows settings

Settings separates app Appearance from Windows privacy choices. Checkboxes stage changes; **Review changes** lists their effects before **Apply changes** writes anything. **Undo changes** restores the tracked prior values after confirmation. Location and device-wide Widgets appear in a separate disclosure because they can remove features. Widgets requires running the app as administrator; there is no automatic elevation or restart.

Policy controls support Windows 11 Pro-family, Enterprise, Education and IoT Enterprise, builds 22000–26200 (21H2–25H2). Home, unknown builds, detected work/school management and externally configured policies are read-only. Suggestions in Windows Settings requires Enterprise/Education/IoT. Diagnostics is set to required data (Windows has no full telemetry-off for these editions). Policy changes may need a later sign-out/restart. Local Undo history stays in %LOCALAPPDATA%/Hush/Windows settings; keep it to preserve restoration. These choices never run with file cleanup. See docs/WINDOWS-SETTINGS.md for exact policies and limits.

## Updates

Click the title, then **Check for updates**. About shows installed-version notes; a newer stable release shows its version, plain patch notes and **Download & install**. One click downloads it with a progress bar, verifies the SHA-256 digest, size, source and x64 application identity/version, then a helper quietly replaces the portable app and restarts it. A rollback copy is kept; failed startup restores it.

There is no sign-in, account or token: Hush reads the public release page of [ShepherdGal/Hush](https://github.com/ShepherdGal/Hush) anonymously. **Updates only work for other people while that repository and its releases are public**; while it is private, Check reports that updates cannot be checked right now. See docs/RELEASING.md for release steps and verification limits.

## Build and development

Requires .NET 10 SDK on Windows. No NuGet application packages.

```powershell
.\scripts\Verify.ps1          # Safety, updater and WPF checks; controlled failure regression
.\scripts\Build.ps1                 # Small portable executable and ZIP
.\scripts\Build.ps1 -Standalone     # Larger build including Desktop Runtime
.\scripts\Backup.ps1                # Immutable source/portable snapshots with verified hashes
```

Tests delete only uniquely named synthetic fixtures; they never run real user cleanup or an installed-app self-update. .gitignore excludes caches, bin/obj, artifacts, private backups, local screenshots/references, test fixtures, credentials, reports and editor state. Release binaries are attached to GitHub releases, never committed.

Read CLAUDE.md (same as AGENTS.md), docs/DESIGN.md, docs/HANDOFF.md and docs/RELEASING.md before extending the app. Original visual references and synthetic previews are kept locally and in immutable Backups, not Git.

Updater protocol follows [GitHub release metadata](https://docs.github.com/en/rest/releases/releases) and [release asset downloads](https://docs.github.com/en/rest/releases/assets).
