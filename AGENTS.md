# Hush — instructions for agents

Hush is a native, portable Windows cleaning and tweaking tool (.NET 10, WPF). Read Docs/DESIGN.md for the visual system and Docs/HANDOFF.md for the current state before editing.

**The user's latest directions always win over anything written here or in Docs.** When a request conflicts with a rule in this file, follow the request and update this file so the rule stops blocking future work.

## Product direction

- Hush is meant to grow into a broad, capable cleaner. New cleanup categories, deeper cleaning, Windows tweaks, startup/app management and other tools are welcome when the user asks for them. Do not refuse or water down a requested feature because earlier rules were stricter.
- Features may write to the system: delete files, change registry values, change Windows settings, manage services/tasks/apps, run built-in Windows tools (cleanmgr, DISM, etc.) and request administrator rights when a feature needs them.
- Default selections, age thresholds, which categories exist and how aggressive each one is are product decisions made per feature with the user, not fixed rules.

## Version

- Current version: **0.7** (Hush.csproj `<Version>`). The About screen shows the short form ("0.7").
- **Never change the version number unless the user explicitly says to.** Feature work does not bump it automatically.

## Safety basics (keep these)

These are the minimum protections; they exist to keep users' data safe, not to limit features.

- Show the user what will happen before any destructive or system-changing action, and require a confirmation click. Scanning/previewing must not change anything.
- Log what was deleted or changed (local logs in `%LOCALAPPDATA%\Hush`). Back up registry keys before deleting or changing them, and offer Undo for Windows setting changes where Windows makes it possible.
- Never delete the user's personal files (Documents, Pictures, Desktop content, etc.) unless the user explicitly picks them in a feature built for that.
- Be honest in the UI: no fake "speed boost" or performance claims, no claiming success that wasn't verified.
- Never store, log or embed credentials or tokens.

## Cleaning architecture and permanent safety rules

All cleanup rules live in two places only: `CleanupEngine.DefaultTargets()` (locations, patterns, ages, protected flag, category) and `ReviewCategory.Definitions` (category id, title, risk level, description). Add a category or location there, nothing elsewhere.

- **Risk levels** (`SafetyLevel`): *Safe* = rebuildable data (temp, caches, shader and thumbnail caches); only Safe items are selected by default. *Review* = diagnostics, dumps, Windows Update/Delivery Optimization/setup files, broken shortcuts; always opt-in. *Caution* = permanent or user-visible loss (Recycle Bin, registry); always opt-in with explicit confirmation text. Never raise a category's default selection without the user asking.
- **Scan is read-only; every delete re-verifies.** Delete uses an exclusive handle and re-checks approved target, pattern, final path, link count, size/timestamps and age. Keep these checks when changing the engine.
- **`DeletableOnly` targets** are system-protected locations: a file is listed only if this user can open it for deletion right now; permission errors there are not "skipped locations". Every location under the Windows folder (except Windows temp) must use it.
- **Patterns**: `Pattern` may list several wildcards separated by `;`. Only temp and cache targets may use no pattern. The Windows folder itself may only be a target for the single file `MEMORY.DMP`.
- **Never touch (the first group is enforced by Tests/CategoryChecks.cs):** personal folders (Documents, Pictures, Music, Videos, Downloads, user profile root), Program Files, System32/SysWOW64, `Windows.old`, `WinSxS`/the component store, `Installer`/`$PatchCache$`, `Package Cache`, recovery/boot data, INetCache (holds Outlook attachment copies). Also never add: jump lists (`AutomaticDestinations` hold pinned items), Office `UnsavedFiles`/OfficeFileCache (unsaved or unsynced work), Spotify `Storage`/`Data` (may hold offline downloads), the pnpm store (hard-linked), Windows Defender data, WMI/driver stores, fonts/icon caches that Windows needs to rebuild, and registry MRU lists. Prefetch is skipped on purpose: deleting it frees almost nothing and slows the next launches.
- **Deep-clean scope:** user caches only inside the current user's AppData (enforced); system-wide categories (Update, Delivery, Setup, Windows Logs, Installer Leftovers, system dumps) must use `DeletableOnly`. Targets are added only when their folder exists, so a scan visits just what is installed. Usage Traces removes only recent-file shortcuts (`*.lnk`) from the Windows and Office `Recent` folders, never the files they point to.
- **Honesty:** cleaning frees disk space; do not claim it makes Windows or apps faster (caches and shader caches make the next launch slower until rebuilt). Never add "speed boost" wording.
- **Not implemented on purpose:** removing previous Windows installations and component-store (WinSxS/DISM) cleanup. Microsoft supports these only through Settings > System > Storage > Temporary files, Disk Cleanup and DISM, and they are irreversible. If the user later asks, run Microsoft's tool with an explicit-consent dialog; never delete those folders directly.
- **Recycle Bin** is accessed only through the shell API (`IRecycleBin`/`ShellRecycleBin`); scanning queries, cleaning empties all drives and only if the bin still matches what was reviewed. Tests use a fake.
- **Privacy tweaks stay separate** from file cleaning (Settings page, WindowsSettings.cs); a cleanup category never changes Windows settings.
- **Performance rules:** one enumeration pass per directory (`FileSystemEnumerable`), no per-file stat or per-file process enumeration, no per-file large allocations. `Hush.Tests.exe --benchmark` measures scan/clean on a synthetic 20,000-file fixture; compare before/after when touching the engine (current: scan ~19 ms, clean ~1.1 s, ~41 MB allocated). Looping animations must cap frame rate and run only while visible; prefer transform/opacity animations over repainting from a timer. The scan reports live: files are counted as they are discovered (deduplicated at discovery) and the gauge advances within each location. The process list is read once per snapshot (`ProcessGate`), never per target or per file. Default targets are built lazily on first scan, not at window startup. Category selection state is a running count (O(1)); never loop all files inside a property getter. The scan candidate limit is 150,000 files; a limited scan is read-only, so do not let one huge cache hit it again without raising or grouping.
- **Removing code:** only after grepping XAML, code and tests for every reference; rebuild and run `Tests/Verify.ps1` afterwards.

## Development safety (this machine is the user's real PC)

- Never run real cleanup, real Windows setting changes, a real self-update or a real install/uninstall on this PC to test. Tests use synthetic fixtures only (unique temp folders, `HKCU\Software\Hush.TestFixtures\<GUID>` registry roots).
- Do not publish, push, create releases or change GitHub settings unless the user asks.

## Design

Keep the established look (details and exact values in Docs/DESIGN.md):
- Inter / Segoe UI; one primary accent per theme, `PrimaryAccentColor` (`SafetyPalette.Accent`): Cyan #3CCFD9 in Dark, soft blue #4C7BC8 in Light (changed from Cyan by the user's direction), used for the gauge **and all primary accent text/Safe accents** in that theme (never a different shade per control); Hush red-orange #FF795C (#B54428 in Light) for Clean and Review/Caution items; neutral surfaces; no decorative extra colors.
- Dark and Light themes with smooth 320 ms theme transitions; 280 ms button interactions; quiet rounded surfaces, no outlines on panels, no hover scaling.
- Wordmark "Hush" in the header opens About; it and the home heading ("Make more room to breathe") are Inter Italic. Settings choices are on/off switch rows (`SwitchCheckBox`). The app icon is an aqua "H" on a charcoal rounded square (Assets/GenerateIcon.ps1).
- Transparent window with 24 DIP rounded corners and custom drag/resize.
New screens and controls should reuse the shared styles in App.xaml and Motion.cs rather than inventing new ones.

Global UI rules (fix through shared styles/tokens, never one-off per screen):
- **Hover never scales or resizes anything.** Text controls ease their color (`TextButton`: Muted→Text; `AccentTextButton`: Cyan→`AccentHoverBrush`); glowing buttons gently intensify their existing glow; checkboxes ease a neutral hover ring; scrollbar thumbs ease opacity.
- **Checkbox** (App.xaml): checked border, tick and mixed dash share the same accent brush; unchecked stays neutral. All rings share one geometry with layout rounding off, so edges stay smooth.
- **Back** is the shared `BackButton` style (`Content="Back"`, chevron + label centered, label right edge aligned to the page edge). Never hand-build a Back button.
- **Scrolling**: page-level scrollers use `PageScroll` (10 DIP overlay bar, 16 DIP end-of-scroll padding) so the last card is never clipped and content width never shifts. Gutters on both sides of a scrolled list are equal; the bar lives inside the right gutter. Cards in a scrolled page have 16 DIP gaps.
- **Calls to action in text** (scan-complete link) are italic and underlined: never an arrow and never a looping animation (the breathing glow was removed by request).
- **Typography**: every title is Inter Italic Regular 400 (page, popup, empty-state and Settings card titles; the wordmark and the home heading too); small text sizes are the shared 15/16 resources in App.xaml, never per-screen literals.

## Code map

- CleanupEngine.cs: scanning and deletion (default targets list, file checks, registry App Paths leftovers, Recycle Bin step, audit log). RecycleBin.cs: shell Recycle Bin service. ReviewCategory.cs: category catalog and risk levels. Models.cs: items, targets, palettes.
- MainWindow.xaml(.cs): all screens (Home gauge, Review categories, Settings, About, confirmation popups). MainWindow.WindowsSettings.cs: Settings page Windows choices.
- WindowsSettings.cs: Windows policy catalog, apply/undo with history. See Docs/WINDOWS-SETTINGS.md.
- UpdateService.cs / UpdateInstaller.cs: in-app updates from the public GitHub release page `ShepherdGal/Hush` (asset `Hush.exe`), read anonymously: no sign-in, token or GitHub wording in the UI. UpdateActivity.cs is the check/download progress track.
- Setup.cs / SetupWindow.xaml(.cs): per-user installer and uninstaller (no administrator rights): verified copy to `%LOCALAPPDATA%\Programs\Hush`, Start menu/desktop shortcuts, Settings > Apps entry (HKCU uninstall key). Everything is injectable (`SetupLocations`) so tests use temp folders and `HKCU\Software\Hush.TestFixtures\<GUID>`. App.xaml.cs picks the mode: portable (default), setup (`Hush-Setup.exe` or `--setup`), uninstall (`--uninstall` from the installed copy).
- Motion.cs, ThemePalette.cs, RollingFileCount.cs, ProgressRing.cs: animation and theming.
- Tests/: verification runner (`Tests/Verify.ps1`).

## Build and test

- `Tests/Verify.ps1` runs the whole suite (add `-Configuration Release` if an open preview locks files). Keep it passing before handing work back.
- When a feature intentionally changes behavior, update or remove the tests that encoded the old behavior instead of bending the feature to satisfy them. Add tests for new deletion/system-change logic using fixtures.
- `Build.ps1` creates the portable `artifacts/Hush/Hush.exe` (+ `artifacts/Hush-windows-x64.zip`) and the installer `artifacts/Hush-Setup.exe` (the same program; started by that name it opens setup). Needs .NET 10 Desktop Runtime; `-Standalone` bundles it.
- `Backup.ps1 -Label <name>` makes a source + portable snapshot in Backups/ (local only, not in Git). It re-reads every archived file and verifies its SHA-256; it needs `artifacts/Hush-windows-x64.zip`. If Hush.exe is running, `Build.ps1` cannot overwrite `artifacts/Hush/Hush.exe` (never kill the user's app): publish to a temp folder and zip that instead. Take a verified backup before large engine/cleanup changes.
- `Hush.Tests.exe --benchmark` prints scan/clean timing and allocations on a synthetic fixture; `--scan-report` runs a read-only scan of this PC and prints what each category would offer (it never cleans). Use it after adding locations to check paths against the real machine (see Tests/README.md).
- **UI work: look at it.** Tests render both themes to PNGs when `HUSH_PREVIEW_DIRECTORY` is set; open them (and zoom/measure glyph alignment) before and after UI changes instead of trusting numbers alone.
- The project folder itself is the Git repository (remote `ShepherdGal/Hush`, branch `main`). `.gitignore` keeps bin/obj/artifacts/Backups/settings/logs/binaries/secrets out; release binaries go to GitHub Releases, never into Git. Commit, push, tag and release only when the user asks.

## After changes

Update Docs/HANDOFF.md (current state) and Docs/CHANGELOG.md with what actually changed and what was or wasn't verified.
