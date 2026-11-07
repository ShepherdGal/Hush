# Hush — instructions for agents

Hush is a native, portable Windows cleaning and tweaking tool (.NET 10, WPF). Read docs/DESIGN.md for the visual system and docs/HANDOFF.md for the current state before editing.

**The user's latest directions always win over anything written here or in Docs.** When a request conflicts with a rule in this file, follow the request and update this file so the rule stops blocking future work.

## Product direction

- Hush is meant to grow into a broad, capable cleaner. New cleanup categories, deeper cleaning, Windows tweaks, startup/app management and other tools are welcome when the user asks for them. Do not refuse or water down a requested feature because earlier rules were stricter.
- Features may write to the system: delete files, change registry values, change Windows settings, manage services/tasks/apps, run built-in Windows tools (cleanmgr, DISM, etc.) and request administrator rights when a feature needs them.
- Default selections, age thresholds, which categories exist and how aggressive each one is are product decisions made per feature with the user, not fixed rules.

## Version

- Current version: **0.8** (Hush.csproj `<Version>`). The About screen shows the short form ("0.8").
- **Never change the version number unless the user explicitly says to.** Feature work does not bump it automatically.

## Safety basics (keep these)

These are the minimum protections; they exist to keep users' data safe, not to limit features.

- Show the user what will happen before any destructive or system-changing action, and require a confirmation click. Scanning/previewing must not change anything.
- Log what was deleted or changed (local logs in `%LOCALAPPDATA%\Hush`). Back up registry keys before deleting or changing them, and offer Undo for Windows setting changes where Windows makes it possible.
- Never delete the user's personal files (Documents, Pictures, Desktop content, etc.) unless the user explicitly picks them in a feature built for that.
- Be honest in the UI: no fake "speed boost" or performance claims, no claiming success that wasn't verified.
- Never store, log or embed credentials or tokens.

## Cleaning architecture and permanent safety rules

All cleanup rules live in two places only: `CleanupEngine.DefaultTargets()` (locations, patterns, ages, protected flag, category) and `ReviewCategory.Definitions` (category id, title, risk level, description, section). Add a category or location there, nothing elsewhere.

- **Risk levels** (`SafetyLevel`, three colors): *Safe* (primary accent, cyan/soft blue) = verified disposable files: today only Temporary Files and Thumbnail Cache; only Safe items are selected by default. *Review* (orange) = browser caches, shader caches, developer caches, logs, dumps, traces, Windows Update/Delivery Optimization/setup files, broken shortcuts; always opt-in. *Advanced* (red) = irreversible or recovery-related (Previous Windows Installations, component cleanup, Recycle Bin, registry, browser cookies); always opt-in and the confirmation keeps Clean disabled until the consent checkbox is ticked. Never raise a category's default selection without the user asking.
- **Catalog:** `ReviewCategory.Definitions` lists every category (with a `GroupId` for documentation: Recommended, Additional system items, Application cleaning, Computer usage traces). The Review page shows them as one flat list in catalog order, as before; section headers were tried and removed at the user's request. Only categories with detected items are shown.
- **Never remove automatically (even as Advanced): Defender protection history/quarantine, certificates, security logs, restore points, recovery data.** Previous Windows installations and component cleanup run only through Microsoft's tools (Disk Cleanup handler, DISM `/StartComponentCleanup`, never `/ResetBase`) after a UAC prompt and explicit consent; Hush never deletes Windows.old/WinSxS itself. Browser passwords, bookmarks, saved sessions and form data are never in any rule; history is Review, cookies are Advanced and need the browser closed. Firefox `places.sqlite` (also holds bookmarks) is never listed.
- **Scan is read-only; every delete re-verifies.** Delete uses an exclusive handle and re-checks approved target, pattern, final path, link count, size/timestamps and age. Keep these checks when changing the engine. Directory chains are link-checked once per folder per clean; the per-file final-path comparison is what catches a swapped link.
- **No file-count limit.** The scan streams (one enumeration pass, no candidate list, no global path set); where targets overlap the most specific target owns the files (`PlanOverlaps`). A `CleanupItem` is about 256 bytes retained per file (shared folder string, side object for rare fields). `ScanOptions` can filter targets (Safe-only for Quick Boost), count without keeping items (monitoring), slow progress and lower thread priority (low-resource). Clean logs intent per batch of 64 before acting, throttles progress to ~50 ms, and records free disk space before and after (`CleanupResult.MeasuredFreed`).
- **`DeletableOnly` targets** are system-protected locations: a file is listed only if this user can open it for deletion right now; permission errors there are not "skipped locations". Every location under the Windows folder (except Windows temp) must use it.
- **Patterns**: `Pattern` may list several wildcards separated by `;`. Only temp and cache targets may use no pattern. `ExcludeFolders` skips named sub-folders. INetCache is never cleaned as a whole (it holds Outlook/Word attachment copies and, on some PCs, whole app data folders): only its legacy `IE`, `Content.IE5` and `Low\Content.IE5` folders. The Windows folder itself may only be a target for the single file `MEMORY.DMP`.
- **Never touch (the first group is enforced by tests/Hush.Tests/CategoryChecks.cs):** personal folders (Documents, Pictures, Music, Videos, Downloads, user profile root), Program Files, System32/SysWOW64, `Windows.old`, `WinSxS`/the component store, `Installer`/`$PatchCache$`, `Package Cache`, recovery/boot data, INetCache as a whole (see Patterns). Also never add: jump lists (`AutomaticDestinations` hold pinned items), Office `UnsavedFiles`/OfficeFileCache (unsaved or unsynced work), Spotify `Storage`/`Data` (may hold offline downloads), the pnpm store (hard-linked), Windows Defender data, WMI/driver stores, fonts/icon caches that Windows needs to rebuild, and registry MRU lists (Recent Documents History uses only Office recent-file shortcuts). Prefetch is skipped on purpose: deleting it frees almost nothing and slows the next launches.
- **Deep-clean scope:** user caches only inside the current user's AppData (enforced); system-wide categories (Update, Delivery, Setup, Windows Logs, Installer Leftovers, system dumps) must use `DeletableOnly`. Targets are added only when their folder exists, so a scan visits just what is installed. Usage Traces removes only recent-file shortcuts (`*.lnk`) from the Windows and Office `Recent` folders, never the files they point to.
- **Honesty:** cleaning frees disk space; do not claim it makes Windows or apps faster (caches and shader caches make the next launch slower until rebuilt). Never add "speed boost" wording.
- **System tools** (`ISystemTools`, `SystemTools.cs`): Previous Windows Installations, component cleanup and clipboard history are `CleanupKind.SystemTool` items. The engine re-probes before running and rejects unknown tool ids. Tests use a fake; never start a real tool, UAC prompt or clipboard clear to test.
- **Smart Boost** (`src/Hush/SmartBoost/`, Settings): Quick Boost (Safe-only scan that ends in the normal confirmation), optional junk monitoring (counts Safe junk while Hush is open, background priority, nothing deleted; no timer exists unless the switch is on), optional Automatic Boost (needs a one-time consent recorded in settings.json; cleans only Safe items, re-verified and logged, skipped while the person is reviewing results), threshold and interval choices (never faster than 15 minutes), low-resource mode (Hush's own priority/EcoQoS and slower redraws only), resource meters (in the separate **Info** card at the top of Settings, always visible, with a one-line smart summary; bars turn Review-orange, never red, when high; sampled only while Settings is open) and startup apps (Task Manager's StartupApproved flag; the original entry is never touched; Undo; scheduled tasks and services are never listed or changed). Smart Boost never closes programs, forces RAM clear or changes Windows services.
- **Recycle Bin** is accessed only through the shell API (`IRecycleBin`/`ShellRecycleBin`); scanning queries, cleaning empties all drives and only if the bin still matches what was reviewed. Tests use a fake.
- **Privacy tweaks stay separate** from file cleaning (Settings page, WindowsSettings.cs); a cleanup category never changes Windows settings.
- **Performance rules:** one enumeration pass per directory (`FileSystemEnumerable`), no per-file stat or per-file process enumeration, no per-file large allocations. `Hush.Tests.exe --benchmark` measures scan/clean on a synthetic 20,000-file fixture; compare before/after when touching the engine (current: scan ~19 ms, clean ~1.1 s, ~41 MB allocated). Looping animations must cap frame rate (`ScanActivity.FrameInterval`) and run only while visible; prefer transform/opacity animations over repainting from a timer. The scan reports live: files are counted as they are discovered (deduplicated at discovery) and the gauge advances within each location. The process list is read once per snapshot (`ProcessGate`), never per target or per file. Default targets are built lazily on first scan, not at window startup. Category selection state is a running count (O(1)) kept by the items themselves (`CleanupItem.Owner`), never by per-item event handlers; never loop all files inside a property getter. Review lists stay virtualized and categories are replaced with one Reset notification (`BulkCollection`). `Hush.Tests.exe --stress-scan [files]` (default 250,000) scans, cancels, reviews and cleans a synthetic fixture above any old limit; run it after touching the engine.
- **Removing code:** only after grepping XAML, code and tests for every reference; rebuild and run `scripts/Verify.ps1` afterwards.

## Development safety (this machine is the user's real PC)

- Never run real cleanup, real Windows setting changes, a real self-update or a real install/uninstall on this PC to test. Tests use synthetic fixtures only (unique temp folders, `HKCU\Software\Hush.TestFixtures\<GUID>` registry roots).
- Do not publish, push, create releases or change GitHub settings unless the user asks.

## Design

Keep the established look (details and exact values in docs/DESIGN.md):
- Inter / Segoe UI; one primary accent per theme, `PrimaryAccentColor` (`SafetyPalette.Accent`): ice blue #7FE0F6 in Dark (taken from the app icon, replacing the old aqua #3CCFD9), soft blue #4C7BC8 in Light, used for the gauge **and all primary accent text/Safe accents** in that theme (never a different shade per control); Hush red #FF795C (#B54428 in Light) for the Clean button **and for Advanced items: it is the only red in the app**; orange #FFA24D (#B35F0C in Light) for Review items; neutral surfaces; no decorative extra colors.
- Dark and Light themes with smooth 320 ms theme transitions; 280 ms button interactions; quiet rounded surfaces, no outlines on panels, no hover scaling.
- Wordmark "Hush" in the header opens About; it is Inter Italic **Medium 500** (user's direction) and the home heading ("Make more room to breathe") is Inter Italic Regular. Settings choices are on/off switch rows (`SwitchCheckBox`). The app icon is the user's ice-blue brush on a navy rounded square with a crisp thin border and no outer glow (`src/Hush/Assets/Hush-icon-source.png`, frames and marks built by `GenerateIcon.ps1`; `-FromArtwork <png>` prepares a new source from supplied artwork).
- **THE RED RULE (locked by `ColorRuleChecks`):** the Clean button's red (`SafetyPalette.Danger`: #FF795C Dark, #B54428 Light) is the ONLY red in Hush. Advanced items, the consent checkbox and anything else that needs red must use that exact value (the `CleanTextBrush` resource or `SafetyPalette.Danger`); never add another red, pink, coral or "slightly different" red. Warnings that are not destructive use Review orange (resource meters use `MeterWarnBrush`). The literal values appear only in Models.cs, App.xaml and MainWindow.xaml.cs; the test scans every source file for red-family hex values and named red colors and fails on anything else. If the user wants a different red, change `SafetyPalette.Danger`, App.xaml and the allow-list in `ColorRuleChecks` together.
- Transparent window with 24 DIP rounded corners and custom drag/resize.
New screens and controls should reuse the shared styles in App.xaml and Motion.cs rather than inventing new ones.

Global UI rules (fix through shared styles/tokens, never one-off per screen):
- **Hover never scales or resizes anything.** Text controls ease their color (`TextButton`: Muted→Text; `AccentTextButton`: accent→`AccentHoverBrush`); glowing buttons gently intensify their existing glow; checkboxes ease a neutral hover ring; scrollbar thumbs ease opacity.
- **Checkbox** (App.xaml): checked border, tick and mixed dash share the same accent brush; unchecked stays neutral. All rings share one geometry with layout rounding off, so edges stay smooth.
- **Back** is the shared `BackButton` style (`Content="Back"`, chevron + label centered, label right edge aligned to the page edge). Never hand-build a Back button.
- **Scrolling**: page-level scrollers use `PageScroll` (10 DIP overlay bar, 16 DIP end-of-scroll padding) so the last card is never clipped and content width never shifts. Gutters on both sides of a scrolled list are equal; the bar lives inside the right gutter. Cards in a scrolled page have 16 DIP gaps.
- **Calls to action in text** (scan-complete link) are italic and underlined: never an arrow and never a looping animation (the breathing glow was removed by request).
- **Typography**: every title is Inter Italic Regular 400 (page, popup and empty-state titles; the wordmark and the home heading too), **except the header wordmark (Medium 500) and the Settings page**: its page title and card titles are Inter Italic Medium 500 (user's direction); small text sizes are the shared 15/16 resources in App.xaml, never per-screen literals.

## Code map

Layout: the app is `src/Hush/` (App.xaml, MainWindow.xaml(.cs) and Models.cs at its root; `Cleaning/`, `Updates/`, `Setup/`, `WindowsSettings/` and `UI/` hold the rest, `Assets/` the icon). Tests are `tests/Hush.Tests/`, documentation `docs/`, scripts `scripts/`; `Hush.sln` opens everything. Folders are organization only: everything stays in the `Hush` namespace.

- CleanupEngine.cs: scanning and deletion (default targets list, file checks, registry App Paths leftovers, Recycle Bin step, audit log). RecycleBin.cs: shell Recycle Bin service. ReviewCategory.cs: category catalog and risk levels. Models.cs: items, targets, palettes.
- MainWindow.xaml(.cs): all screens (Home gauge, Review categories, Settings, About, confirmation popups). MainWindow.WindowsSettings.cs: Settings page Windows choices.
- WindowsSettings.cs: Windows policy catalog, apply/undo with history. See docs/WINDOWS-SETTINGS.md.
- UpdateService.cs / UpdateInstaller.cs: in-app updates from the public GitHub release page `ShepherdGal/Hush` (asset `Hush.exe`), read anonymously: no sign-in, token or GitHub wording in the UI. UpdateActivity.cs is the check/download progress track.
- SmartBoost/: BoostSettings.cs (settings + the shared `Preferences` file), StartupApps.cs, ResourceMonitor.cs, LowResourceMode.cs, SmartBoostController.cs, MainWindow.SmartBoost.cs. Cleaning/SystemTools.cs: Windows tools behind an interface. Setup.cs / SetupWindow.xaml(.cs): per-user installer and uninstaller (no administrator rights): verified copy to `%LOCALAPPDATA%\Programs\Hush`, Start menu/desktop shortcuts, Settings > Apps entry (HKCU uninstall key). Everything is injectable (`SetupLocations`) so tests use temp folders and `HKCU\Software\Hush.TestFixtures\<GUID>`. App.xaml.cs picks the mode: portable (default), setup (`Hush-Setup.exe` or `--setup`), uninstall (`--uninstall` from the installed copy).
- Motion.cs, ThemePalette.cs, RollingFileCount.cs, ProgressRing.cs: animation and theming.
- tests/Hush.Tests/: verification runner (`scripts/Verify.ps1`).

## Build and test

- `scripts/Verify.ps1` runs the whole suite (including `--verify-smart-boost`; use `Hush.Tests.exe --stress-scan` separately) (add `-Configuration Release` if an open preview locks files). Keep it passing before handing work back.
- When a feature intentionally changes behavior, update or remove the tests that encoded the old behavior instead of bending the feature to satisfy them. Add tests for new deletion/system-change logic using fixtures.
- `scripts/Build.ps1` creates the portable `artifacts/Hush/Hush.exe` (+ `artifacts/Hush-windows-x64.zip`) and the installer `artifacts/Hush-Setup.exe` (the same program; started by that name it opens setup). Needs .NET 10 Desktop Runtime; `-Standalone` bundles it.
- `scripts/Backup.ps1 -Label <name>` makes a source + portable snapshot in Backups/ (local only, not in Git). It re-reads every archived file and verifies its SHA-256; it needs `artifacts/Hush-windows-x64.zip`. If Hush.exe is running, `scripts/Build.ps1` cannot overwrite `artifacts/Hush/Hush.exe` (never kill the user's app): publish to a temp folder and zip that instead. Take a verified backup before large engine/cleanup changes.
- `Hush.Tests.exe --benchmark` prints scan/clean timing and allocations on a synthetic fixture; `--scan-report` runs a read-only scan of this PC and prints what each category would offer (it never cleans). Use it after adding locations to check paths against the real machine (see tests/Hush.Tests/README.md).
- **UI work: look at it.** Tests render both themes to PNGs when `HUSH_PREVIEW_DIRECTORY` is set; open them (and zoom/measure glyph alignment) before and after UI changes instead of trusting numbers alone.
- The project folder itself is the Git repository (remote `ShepherdGal/Hush`, branch `main`). `.gitignore` keeps bin/obj/artifacts/Backups/settings/logs/binaries/secrets out; release binaries go to GitHub Releases, never into Git. Commit, push, tag and release only when the user asks.

## After changes

Update docs/HANDOFF.md (current state) and docs/CHANGELOG.md with what actually changed and what was or wasn't verified.
