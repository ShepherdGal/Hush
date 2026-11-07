# Changelog

Dates use Asia/Jerusalem.

## 0.8 — November 7, 2026

- **New app icon:** the ice-blue brush artwork you supplied, with its black surround keyed to real transparency and a crisp thin border instead of a glow (`GenerateIcon.ps1 -FromArtwork`). The 16-256 px ICO and the marks were regenerated and inspected on light and dark backgrounds.
- **Ice-blue accent:** the Dark theme's primary accent is now the icon's #7FE0F6 (hover #B5EEFB) instead of aqua #3CCFD9; Light is unchanged.
- **One red:** Advanced items now use exactly the Clean button's red; the second red (#FF5C6C / #B3263A) is gone. `ColorRuleChecks` scans every source file and fails on any other red, and checks the running app's resources in both themes.
- **Settings:** a separate **Info** card at the top holds the three bars, a one-line smart summary, Hush's own memory and the last junk check (bars turn orange, never red, when high). Smart Boost is a collapsed card of controls only.
- **Logo:** the header wordmark is Inter Italic Medium 500.
- Version 0.8 (project, README, release notes shown in About). `scripts/Verify.ps1` passes (170 engine checks, UI suites, Smart Boost stage).
## 0.7 (Review style and Settings rework) — November 7, 2026

- **Review Files:** back to the flat category list; the four section headers were removed (and their code: `CategoryGroup` and the grouping view). The catalog keeps its larger category set.
- **Settings:** the Appearance card is gone (the theme is changed with the top-bar button; `SettingsLightBox`, its handler and summary were removed). The Smart Boost card now shows its details on the closed card (summary, processor/memory/system-drive bars, Hush's own memory, last junk check); opening it shows only the controls, without the intro paragraph.
- **Titles:** the Settings page title and the card titles are Inter Italic Medium 500 (were Regular 400); other titles are unchanged.
- Tests updated (no Appearance switch; theme via the top-bar button) and extended (details on the closed card, 500-weight titles, flat Review list). `scripts/Verify.ps1` passes; both themes were inspected in rendered previews.
## 0.7 (scalable engine, expanded catalog, Smart Boost) — November 7, 2026

- **Backup:** `Backups/pre-smart-boost-engine-*` (source re-read and SHA-256 verified) before any change; baseline `scripts/Verify.ps1` passed.
- **No scan limit:** the 150,000-file cap and the 100,000-per-location cap are gone. The scan streams in one pass, keeps about 256 bytes per file, holds no global path set (overlapping targets are resolved by ownership), reports live progress, cancels within milliseconds and treats links, locked and inaccessible files safely. Synthetic stress run: 250,000 files scanned in 0.2 s (warm cache), counted exactly once, cancelled with partial results in 46 ms, reviewed with 4 realized rows, cleaned at about 13,000 files/s with exactly two audit lines per file. Standard benchmark (20,000 files): scan 14 ms (was about 19), clean 582 ms (was about 1.1 s), allocations 30 MB (was about 41).
- **Cleanup engine:** audit intent is written per batch of 64 and flushed once; progress to the UI is throttled; folder chains are verified once per folder per clean; a clean records Windows' free space before and after and the status shows what the disk really gained. Selection totals are counters kept by the items (no per-item event handlers); categories replace in one Reset; deleted items are removed in one pass.
- **Safety tiers:** Safe (cyan), Review (orange), Advanced (red; was Caution). Only Temporary Files and Thumbnail Cache are Safe and selected by default; browser caches, shader caches, logs and traces are Review; Windows.old, component cleanup, Recycle Bin, registry and cookies are Advanced and need a consent checkbox in the confirmation.
- **Catalog (32 categories in four sections):** added Temporary Internet Files (legacy IE folders only; INetCache as a whole is never touched because this PC's INetCache holds Chromium-style app data), NVIDIA/AMD/Intel/DirectX shader caches, Edge/Firefox/Chrome/other browser caches, NVIDIA app downloads, Go/VS/NuGet caches, Epic web cache, old CHKDSK fragments, Recent Access, Recent Documents, clipboard history (Windows API), event trace logs, .NET usage logs, browser history and cookies (Chromium and Firefox cookies; never passwords, bookmarks, sessions or Firefox places.sqlite). Windows.old and component cleanup run only through Disk Cleanup/DISM after UAC and consent (never `/ResetBase`); Defender data, certificates, security logs, restore points and recovery data are never offered.
- **Smart Boost (Settings):** Quick Boost, optional junk monitoring, optional Automatic Boost (one-time consent, Safe only), threshold and interval choices, low-resource mode, resource meters and startup-app management with Undo. Nothing runs in the background unless a switch is on.
- Tests: 158 engine checks plus new suites (`ScaleChecks`, `SmartBoostChecks`, `--verify-smart-boost`, `--stress-scan`); the project now targets `net10.0-windows10.0.19041.0` (clipboard API). `scripts/Verify.ps1` passes in full. Rendered previews of Review (both themes), Settings and Smart Boost were inspected.
- Not verified on this PC, by design: real Disk Cleanup/DISM/clipboard runs, real startup changes, Automatic Boost over time, behavior as administrator, and most paths for apps not installed here (only Brave is). No version bump.
## 0.7 (repository layout) — November 7, 2026

- The repository was reorganized (git moves, history kept): app source in `src/Hush/` (Cleaning, Updates, Setup, WindowsSettings, UI, Assets), tests in `tests/Hush.Tests/`, documentation in `docs/`, scripts in `scripts/` (`Build.ps1`, `Verify.ps1`, `Backup.ps1`), plus a `Hush.sln`. The top level went from 33 files to four plus folders. No code changed (the namespace is still `Hush`); project, script, `.gitignore` and documentation paths were updated. The published v0.7 release files are unaffected.

## 0.7 (installer and folder cleanup) — November 7, 2026

- Published: public repository `ShepherdGal/Hush` and release `v0.7` with `Hush-Setup.exe`, `Hush.exe` and the zip (asset digests and an anonymous download verified).

- Two ways to get Hush: `Hush-Setup.exe` (installer) and the portable `Hush.exe`. The installer is the same program started by that name (or `--setup`): per user, no administrator rights, verified copy to `%LOCALAPPDATA%\Programs\Hush`, Start menu and optional desktop shortcut, and a Settings > Apps entry whose quiet uninstall command is `Hush.exe --uninstall`. Setup also offers "Run without installing". Uninstall removes only Hush's own files, shortcuts that really point at it and its registry entry; settings, history and logs stay unless the user ticks the option; unrelated files in the folder are never deleted. After an in-app update the Settings > Apps version label is refreshed.
- New: Setup.cs, SetupWindow.xaml(.cs), App.xaml.cs mode selection, `scripts/Build.ps1` also writes `artifacts/Hush-Setup.exe`, `tests/Hush.Tests/SetupChecks.cs` and `--verify-setup` (fixtures only: temp folders and a `HKCU\Software\Hush.TestFixtures\<GUID>` key).
- Folder cleanup: deleted the old unused `_publish` checkout (the previous project's Git history), the three old backup files from that era (about 21 MB) and the rebuildable bin/obj folders; removed the leftover `_publish` references from the project file, `.gitignore` and docs. A verified snapshot (`hush-0.7-before-cleanup-*`) was taken first. Kept: the other Hush snapshot, `docs/References` (your chosen design references) and `.build-home` (a 312 MB NuGet/runtime cache that only speeds up and enables offline builds).
- `scripts/Verify.ps1` passes, including the new setup suite.

## 0.7 (header, Settings summary, Git reset) — November 7, 2026

- The header wordmark "Hush" now matches the home heading: 33 Regular Italic in the theme accent.
- Review files and Settings titles sit closer to the content below (header rows 88 to 80 and 56).
- Settings is now three collapsed summary rows (Appearance, Privacy & telemetry, More impact). Each shows its title in the theme's accent color and a live one-line summary ("Dark theme", "0 of 3 turned on", "2 waiting for review", "read-only on this PC"); clicking a row expands its switches with the shared accordion. New shared `SettingsSection` style in App.xaml.
- Repository: new organized `.gitignore` (build output, binaries, backups, previews, runtime state, secrets, editor/agent files, OS files), the project folder became the Git repository for `ShepherdGal/Hush`, and every remaining mention of the old app name was removed from the code and docs.
- Tests: Settings sections start collapsed with correct summaries; navigation and Windows-settings checks expand them first. `scripts/Verify.ps1` passes.

## 0.7 (soft blue accent in Light) — November 7, 2026

- Light theme only: the primary accent is now a soft blue (#4C7BC8, hover #3A66AE) instead of Cyan. It applies to the gauge ring and bloom, wordmark, links, Safe category lines/titles, checkboxes, switches, button glows and the update progress track. Dark keeps Cyan #3CCFD9.
- The accent is now one token per theme (`SafetyPalette.Accent`, `PrimaryAccentColor`), and TealBrush/TealTextBrush ease between themes instead of being fixed. The update progress glide builds its gradient from the current accent.
- Tests: the theme-motion check now expects blue in Light and Cyan in Dark and includes the accent in the gradual-motion roles. `scripts/Verify.ps1` passes.

## 0.7 (live scan, typography, hover glow, updates without GitHub) — November 7, 2026

- Home: the looping breathing glow under the gauge is removed (`Motion.Breathing` deleted); the "Scan complete. Take a look." link stays italic and underlined.
- Live scan: files are now counted while they are still being discovered (deduplicated at discovery, also from inside one huge folder), and the gauge advances within each location (discovery fills the first half of its share, inspection the second) instead of waiting for whole locations. Gauge eases 260 ms to each new value (was 950), the odometer trails the real count by at most 0.6 s (was 1.8 s), the finishing ramp is shorter. Benchmark unchanged (scan 18 ms, clean 1.07 s); read-only real-PC scan 1.2 s.
- Typography: the home heading is Regular 400 at 33 px (was SemiBold 32). Every title is now Inter Italic Regular 400: Review files, Settings, popup titles, the empty-state title and the Settings card headings. Small text sizes are 1 DIP larger (support/compact 15, body 16, About description 17).
- Hover: Clean and Clean selected now lift their own-color glow clearly on hover (+0.11, outer halo 12 to 18 blur) instead of the almost invisible +0.035.
- About: the "Hush" title is the aqua italic wordmark. The check animation is a soft Cyan glide on a 4 DIP track driven by a render-thread transform animation (was a 2 DIP bar repainted from a 16 ms timer); downloading shows a real determinate bar and percentage.
- Updates without GitHub: "Connect GitHub", the Git credential helper (`GitHubConnection.cs`), the token plumbing and the separate "Install & restart" confirmation are removed. Check is anonymous; after "Version X is available" one **Download & install** click downloads (live progress), verifies and hands over to the updater. Plain-language errors; the word GitHub no longer appears in the UI. This needs the release page to be public.
- Tests: update-flow suite rewritten (anonymous requests asserted, one-click flow, live progress, no GitHub wording, failed hand-over keeps the app unchanged); the Clean hover check expects the widened halo. `scripts/Verify.ps1` passes.

## 0.7 (wording, descriptions, equal gutters, Settings) — November 7, 2026

- Home: the wordmark and heading are Inter Italic; the heading now reads "Make more room to breathe" (also the window title). "Scan complete. Take a look." is italic and underlined with a tooltip, so it reads as a link while keeping the breathing glow and no arrow.
- Review: every category shows one plain-language sentence under its title (new `ReviewCategory.Summary`, written for non-technical users); the technical text stays in the tooltip. Category selection bars now stretch to the taller header.
- Review gutters are now equal on both sides (22 DIP left and right, measured), with or without a scrollbar. The overlay scrollbar is slimmer (the style's Width never applied, so it rendered as a 17 DIP system-width bar) and sits inside the right gutter, 10 DIP clear of the outlines.
- Settings rebuilt as three always-visible cards (Appearance, Privacy & telemetry, More impact) of on/off switch rows with plain descriptions and hairline dividers. New shared `SwitchCheckBox` style (still a CheckBox, same bindings). The default "Not configured by Hush" line is hidden; the Location & Widgets accordion is gone. The scroller sits beside the cards instead of over them.
- Tests: heading text, category line lookup, Settings switch animation, removal of the accordion clicks. `scripts/Verify.ps1` passes (92 engine checks plus all UI suites).

## 0.7 (Review page polish) — November 7, 2026

- Checked category outlines: wider gutters (14 DIP left, 14 DIP right so the scrollbar never touches them) and a full 1 DIP anti-aliased overlay stroke instead of a 0.5 DIP border, which looked pixelated at the corners; zoomed renders confirm smooth corners.
- Select-all row and Expand-all re-aligned to the new checkbox/chevron columns.
- Review and Settings share one header: title top-aligned, Back centered on the title line (measured, 1 DIP apart), so the heading no longer jumps between pages.

## 0.7 (deep cleaning) — November 7, 2026

- Deep clean: about 60 new location types, only where the folder exists. Browser caches now include GPU/Dawn/shader caches and Media Cache for Chrome, Edge, Brave and Vivaldi; app caches for Discord, Slack, Teams, VS Code, Steam, Remote Desktop and the Microsoft Store; every Store/MSIX package's `TempState`, `AC\Temp` and `AC\INetCache`; NVIDIA/AMD/Intel shader caches.
- Four new categories (16 total): Developer & Creator Caches (NuGet, npm, pip, Yarn, JetBrains, Android Studio, Adobe media; opt-in), Windows Logs (CBS, DISM, MoSetup, Debug, diagnostic ETL traces; protected, opt-in), Installer Leftovers (C:\NVIDIA, C:\AMD; protected, opt-in), Usage Traces (Windows/Office recent-file shortcuts only; opt-in). "Cached Files" is now "Browser & App Caches".
- Real-PC read-only scan (`--scan-report`) found about 995 MB on this machine without administrator rights (npm cache 543 MB, Android Studio caches 427 MB, Vivaldi/Brave caches 20 MB, Store-app temp 4.6 MB). It also exposed two real bugs, fixed below.
- Fix: a single 25,000-file cache made the whole scan "limited" (read-only, nothing cleanable). The limit is now 150,000.
- Fix: the scan asked Windows for the process list once per cache folder (5.2 s on this PC); it now uses one shared snapshot (1.1 s for the whole PC).
- Fix: category selection was O(n) per file change, so ticking a category with tens of thousands of files could freeze the window; it is now a running count (120,000 files in 26 ms in the test).
- Startup: default locations are discovered on the first scan, not when the window opens.
- Back arrow replaced with the Windows system icon font arrow (Segoe Fluent Icons), measured and checked in zoomed renders because the hand-drawn chevron looked unaligned.
- Tests: 92 engine checks (was 81): new categories, no duplicate default locations, traces only `*.lnk`, user caches only inside AppData, selection speed; `--scan-report` mode.

## 0.7 (cleaning expansion, audit and performance) — November 7, 2026

- New cleanup categories, organized in the existing Review screen with risk levels (Safe default-selected; Review and Caution opt-in): Thumbnail Cache, Windows Update Files (update downloads and logs), Delivery Optimization, Setup & Upgrade Logs (Panther), Recycle Bin (shell API, all drives, opt-in), plus broader Windows Error Reports (queues and dump attachments), Crash Reports & Dumps (Minidump, LiveKernelReports, MEMORY.DMP) and Shader Cache (NVIDIA/AMD alongside Windows). Browser/app caches now include Firefox, Vivaldi, Opera and Discord. Public Desktop/Start Menu broken shortcuts added.
- System-protected locations use a deletable-only probe: files are listed only if this user can delete them now, so a normal (non-admin) scan stays clean and never reports "skipped locations" for Windows folders.
- Deliberately not implemented: deleting previous Windows installations (`Windows.old`) and component-store (WinSxS) cleanup; both are irreversible and only supported through Microsoft's own tools. Documented as a permanent boundary in AGENTS.md/CLAUDE.md. INetCache is excluded because it holds Outlook attachment copies.
- Engine: one enumeration pass per directory with pre-filtering (no double stat), pattern lists (`;`), category-driven risk and default selection, cached process checks, approved-target dictionary, single reusable path buffer. Measured on 20,000 synthetic files: scan 190 ms to 19 ms (allocations 9.5 MB to 7 MB); clean 6,472 ms to 1,063 ms (686 MB to 41 MB allocated).
- Breathing glow capped at 30 fps. Removed verified-unused code: item/category accent properties, RowNumber, and eleven unused brush resources (less work per theme change).
- Confirmation text now states Recycle Bin emptying and Update/Delivery/Setup file effects; previous Windows installations stay untouched.
- Tests: 81 engine checks (was 48): multi-pattern, category risk/default selection, deletable-only probing, Recycle Bin with a fake, permission denial via ACLs (restored afterwards), mid-clean cancellation, and a "never touch" guard on default locations; `--benchmark` mode. Two existing tests were updated for the new behavior (progress is reported only for qualifying files; twelve categories).
- Verified backup before the work: Backups/pre-cleaning-expansion-* (60 source files re-read and SHA-256 checked, plus portable zip).

## 0.7 (UI refinements) — November 7, 2026

- Checkboxes redrawn: one shared geometry (no double outline), smooth 1.5 DIP border, vector tick. The checked border, tick and mixed dash are the same accent color; unchecked stays neutral; a neutral ring eases in on hover/focus.
- Back is now one shared `BackButton` style with a cleaner, proportional chevron, centered on the label and right-aligned to the page edge (Review and Settings).
- Settings and Review use the shared `PageScroll` style: overlay scrollbar (no width shift), 16 DIP end padding so the Location & Widgets card is never clipped; Location & Widgets card padding made symmetric.
- Cyan: `TealTextBrush` and Safe accents are now exactly the gauge's #3CCFD9 in Light too (previously #087F89). Light-theme contrast of Cyan text is lower as a result.
- Scan-complete link: arrow removed; its text breathes with a soft pulsing Cyan glow (`Motion.Breathing`), without moving or resizing.
- Hover standardized: new `AccentTextButton` (Cyan to `AccentHoverBrush`) for the wordmark, links and About actions; wordmark gains hover; checkbox, category chevron and scrollbar thumb gain eased hover; nothing scales.
- About: Release notes (secondary) left, Check for updates (primary Cyan) centered, Close right, giving the actions room.
- CLAUDE.md/AGENTS.md and DESIGN.md record these as global rules. Tests: theme-motion check now asserts the shared Cyan and uses `AccentHoverBrush` for palette motion; navigation check uses the new `CheckedRing` layer.

## 0.7 — November 7, 2026

- App identity: executable `Hush.exe`, namespace/assembly, window title, header wordmark, About, data folder `%LOCALAPPDATA%\Hush`, updater (`ShepherdGal/Hush`, asset `Hush.exe`), scripts and tests.
- New app icon: aqua "H" on the charcoal rounded square.
- Version 0.7; About shows "Version 0.7".
- Rewrote CLAUDE.md/AGENTS.md: removed the strict early rules so Hush can grow into a broad cleaning and tweaking tool; kept basic data-safety rules and the visual design system.
- Tests no longer open a real app window that reads the user's saved theme.
- Removed old verification reports, acceptance/plan documents, generated previews, old builds and old backups.

## Features at 0.7

Gauge scan with live file counter, reviewable cleanup categories with safety labels, confirmed cleanup with audit logs and registry backups, Dark/Light themes, About with verified in-app updates, and Windows privacy settings with Undo.
