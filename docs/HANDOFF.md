# Hush handoff — 0.9

Updated November 7, 2026 (Asia/Jerusalem).

## Icon refresh, Pro glow, finished glow, GitHub (November 12, 2026, unreleased)

See the CHANGELOG entry of the same name. The icon-after-update fix lives in `HushSetup.RefreshRegistration` (icon stamp) and is NOT verified on a real update. Pro mode glow is `UI/LetterGlow.cs` (`ProGlow`, `WordmarkGlow`): a tight per-letter glow drawn once as a picture. README icon is `docs/app-icon.png` (pushed). Still to do by the owner: upload `docs/Hush-social-preview.png` in GitHub Settings. Old local files left for the owner to approve: `artifacts\preview-gauge`, `artifacts\RELEASE NOTES 0.8.3.md` and `0.9.0.md`, `docs\References` (untracked).

## Cleaning cue: Smash (November 12, 2026, unreleased)

`UI/ScanActivity.cs` was rewritten as the owner's pick "Smash" (see CHANGELOG): looping charge, squash, smash and wipe cycle of 3.78 s inside the gauge, a final bigger smash when the clean finishes (`FinishAsync`, called from `ConfirmDelete_Click`), a soft plate under the percentage, pulse of the percentage and ring glow through `Smashed`. NOT verified: how it feels live, `scripts/Perf.ps1` budgets for the new cue (about 150 key-frame tracks while the dust moves), a real weak PC. Fresh exe built.

## Release 0.9.5 (November 11, 2026)

Version 0.9.5 published (tag v0.9.5) with Hush-Portable.zip and Hush-Setup.zip. Contents: see docs/CHANGELOG.md (0.9.5). NOT verified: a real Turn off Windows Update run, Perf on a real weak PC.

## Simple / Pro mode (November 11, 2026)

Header "Pro" button (`UI/MainWindow.ProMode.cs`, `Preferences.ProMode`, default off for everyone). Simple = Safe-only scan, Safe list opened directly, Settings with only Info + Appearance; Pro = everything as before; turning Pro on asks via the consent popup. `MainWindow.TestProMode` makes every test window Pro except in `ProModeChecks`. Same day: icon replaced by the owner's artwork unedited, Main color swatches (Dark and Light), sonar-ripple cleaning cue (no 60 fps cap by design). NOT verified: live feel, an existing user's first start on a real PC, Perf on a real weak PC. Fresh exe built.

## Gauge hover (November 9, 2026)

`GaugeHoverCue` inside the gauge grid, driven by `MainWindow.RefreshGaugeHover` (mouse enter/leave, enabled/visible changes, end of SetBusy). Two styles (ready / complete), finite, collapsed while idle. `Perf.ps1` shows the cleaning-cue budget broken (25-33 ms median, software rendering) but it is equally broken on the previous commit, so it is environmental: re-measure on a quiet PC. Fresh exe built. NOT verified: the feel with a real mouse.

## One Review changes button (November 9, 2026)

Services card was "empty" only because Hush had already disabled everything on this PC (see `%LOCALAPPDATA%\Hush\Windows settings\services-changed.json`): rows now also show what Hush turned off (ON switch, switch OFF = turn back on). One footer button "Review changes" replaces the per-card Review buttons (`MainWindow.ReviewChanges.cs`). NOT verified on the real PC: the elevated `--review-all` restart and the combined apply. Fresh exe built.

## Tray, start with Windows, big Services card (November 9, 2026)

Administrator card: Minimize to tray (`TrayIcon`, `InitializeTray` in `Admin/MainWindow.Admin.cs`) and Start with Windows (`RunKeyStartup`, `--startup`, `StartupHint`). Services is full width with its own Actions menu; Interface size is full width too (columns are two cards each). NOT verified on a real PC: the actual icon next to the clock, the Run entry and a real sign-in start; the quiet start shows the window minimized for a moment before it hides in the tray. Tasks has no Actions menu yet (same pattern would apply). Fresh exe built.

## Info card colors and advice (November 9, 2026)

Info bars are now green/amber/red with thresholds in `ResourceSnapshot` and are 12 DIP wide; the line under "Info" comes from `ResourceSnapshot.Assess` (situation + advice, colored). Replaced the old "never red" meter rule. Not verified: real high-load readings (forced in the preview only). Fresh exe built.

## Switch direction and button hover (November 9, 2026)

Owner: Settings switches must be ON for "turn this off", like ShutUp10++, and Boost-type buttons must hover like Actions/Clean. Done: `OffSwitchChoice.On` is now `Selected` (Services, Scheduled tasks); Settings action buttons use `AccentOutlineButton`. Startup-app switches and the Process "End" buttons were deliberately left as they were. Not verified: hover by eye in the live app. Fresh exe built with `scripts/Build.ps1`.

## Scheduled tasks card (November 9, 2026)

New Settings card **Scheduled tasks** (owner: "also clean unneeded tasks"), built as the sibling of Services: `WindowsSettings/TaskTuning.cs` (catalog of 33, leftovers, never-touch list, save-first history `tasks-changed.json`, Undo, allow-listed history) and `WindowsSettings/MainWindow.Tasks.cs`; full width under the two balanced columns, before Privacy & telemetry. Details: CHANGELOG "Scheduled tasks card". Nothing committed. **Could not verify:** real `schtasks.exe` changes and read-back on real tasks, the real administrator restart with `--tasks`; without administrator rights the top task folder (where Google/Edge/Dropbox updaters live) cannot be listed, so those only show as administrator (the card says "more may show as administrator"). Topic names were made unique ("App updater tasks", "Tasks left behind by removed programs") because Settings remembers open headings by name and Services already uses "App updaters" / "Left behind by removed programs". Firefox's updater is matched in `\Mozilla\` (where Firefox actually puts it), not in the top folder. Observation for later: the Services UI test writes its history and log into the real `%LOCALAPPDATA%\Hush` folder (fake services only, Undo removes the history); the tasks tests use a throw-away folder instead.

## P2 stabilization done (November 9, 2026)

Services `Read` is cheap (one registry key + `sc queryex <name>`), `Stop`/`Start` fail fast on a non-zero `sc.exe` code, `schtasks` output is read asynchronously, a partly removed Windows.old is reported as "only partly removed (N items)" and `RemoveTree` accepts only a folder named `Windows.old`, `Preferences.Save` notes failures in the crash log, WMI COM objects are released, three doc drifts fixed. Details and tests: CHANGELOG "P2 stabilization". Nothing committed. **Could not verify:** anything against real services, real `schtasks.exe`, real WMI or a real Windows.old (fakes and fixtures only).

## Hush 0.9.0 is released (November 9, 2026)

Published as v0.9.0 on https://github.com/ShepherdGal/Hush (tag on commit 4c9f410, not a prerelease, latest). Assets: Hush-Portable.zip (SHA-256 509a94d3a0dc8fc9d4e34f466acd45db2bfd4292dff2ba9702b0979b9c7c28f4), Hush-Setup.zip, LICENSE, Hush-icon.png; digests match the local files and an anonymous download is identical. Release notes: artifacts/RELEASE NOTES 0.9.0.md (local). Added just before release: the red Advanced category "Large App Downloads and Installed App Files" (Unity Asset Store, Fusion 360 webdeploy, CapCut Apps, Codex runtimes, Zed tools, GitHub Desktop packages), the Scheduled tasks card, more cache rules. Verify.ps1 passed (427 checks). **Never run for real on this PC:** a real in-app update from 0.8.3, the real Windows.old removal, real scheduled-task and service changes, real WMI restore point, the self-contained exe on a clean PC. Older versions (0.8.1 and below) need one manual download; 0.8.2 and 0.8.3 can update in-app.

## Back to Glass, Mica parked, 60 fps cleaning cue (November 9, 2026)

Mica is removed from the app and locked in `docs/locked/mica-look` (do not touch unless the owner says so; `LockedArchiveChecks` enforces it). The cleaning cue in the gauge is now a radar sweep with no frame-rate cap (the stutter was a 30 fps cap plus a blur effect on the whole gauge button), and "60 fps native always" is a locked rule with tests and a `Perf.ps1` budget. Glass is the default window on Windows 11. Verify passes (393 checks). **Still unverified:** Glass with a real mouse (drag, resize, snap, Alt+Tab), Windows 10, how the new cue feels on a real weak PC. The P2 tasks and the commit are next.

## (earlier today) Glass default and Mica prototype (November 9, 2026)

Glass is now the default window on Windows 11 (`--layered` goes back); `--mica` is a prototype; smooth scrolling stays (owner decision). See CHANGELOG "Glass window by default". Verify.ps1 passes, exes rebuilt, nothing committed. **Open:** mouse-level checks of Glass (drag, resize, snap, Alt+Tab), Windows 10, other DPIs; Mica is refined (neutral palette, translucent cards, opaque popups) and photographed in both themes: say if it should become an option in Settings or the default on Windows 11; ReadyToRun (needs approval); the P2 tasks; the commit.

## Smoothness round and rulebook (November 9, 2026)

Owner asked for a smoother app on an Intel i3 / 8 GB. Done (CHANGELOG "Smoothness for weak PCs..."): popup blur off, smooth scrolling rewritten, lazy help marks, Services read on card open, restore-point probe removed from the Settings click, Services card clarity and order, no auto-opened sub-categories (a closed card forgets its open topics), reliable Actions toggle, clearer footer labels. New: `docs/RULES.md` (binding), `PerformanceRuleChecks.cs`, `scripts/Perf.ps1`, `--glass` experiment. Verify.ps1 passes (386 checks), exes rebuilt, nothing committed. **Open / needs the owner:** (1) the layered window is still the main cost of every animation on weak graphics: `Hush-Portable.exe --glass` now matches the layered look pixel for pixel on this Windows 11 display and cuts CPU a lot (CHANGELOG), but drag, resize, snap and Alt+Tab with a real mouse and Windows 10 are unverified: try it for a minute, then say "make glass the default" (keep `--layered` as the way back); (2) ReadyToRun for the release build would cut first-open JIT time on a slow CPU but needs the crossgen package (approval to download); (3) nothing was measured on a real i3. The P2 audit tasks and the commit are still waiting.

## Audit P1 repairs (November 9, 2026)

Done after the P0 round (CHANGELOG "Pre-release audit, P1 repairs"): update receipt before an administrator hand-over (M4), bounded close (M5), `CrashLog` + global handlers, Boost/scan/clean mutual exclusion, long-form (8.3) roots, bounded and cached Windows.old measuring. **Owner decision:** temporary files keep "any age, delete completely when the person chooses" (no floor for Automatic Boost or Windows temp). `Verify.ps1` passes (373 checks), exes rebuilt, nothing committed. **Not verified:** a real update with administrator mode on; 8.3 paths (off on this volume); a real Windows.old (none here). **Still open:** a watchdog inside `Clean` for one blocked file (closing now works regardless), Services performance (`Read` re-lists everything; `Stop` waits 15 s) and whether Manual/not-running services should be offered, `.gitattributes` + baseline commit, the low-severity list from the audit.

## Pre-release audit and P0 repairs (November 9, 2026)

An engineering audit of 0.9 was made (report in the chat; nothing in the tree changed for the investigation), then the four P0 repairs were done: root guard for cleanup locations (TEMP pointed at personal folders), administrator-only staging for the elevated update/uninstall helpers and the task XML (`Admin/AdminOnlyDirectory.cs`), an allow-list for the Services history, and the cleanup result no longer overwritten on the status line. Details and what was not verified: CHANGELOG "Pre-release audit, P0 repairs". `Verify.ps1` passes (365 checks), exes rebuilt, nothing committed. **Still open from the audit (decisions/tasks):** zero-age Temp for Windows\Temp and unattended Automatic Boost (needs the owner's decision), the update receipt when administrator mode is on but the updated copy starts without elevation, close-while-busy with no timeout and no watchdog in Clean, global exception handling, Boost/scan overlap, 8.3 short temp paths, Windows.old probe cost, Services performance and whether Manual/not-running services should be offered, line endings (`.gitattributes`; the working tree is LF, `HEAD` is CRLF) and a baseline commit, and low-severity items. The HANDOFF title below still says 0.7 (stale).

## 0.9 follow-up before release (November 9, 2026)

The user's pre-release list is done in the working tree (nothing committed or pushed; the 0.9 commit `1adaafe` is local only, which is why GitHub has no 0.9 yet): CPU/RAM/smoothness work measured with the new `Hush.Tests.exe --ui-perf` (permanent window blur removed, shadow effects attached only while visible, cached fades, deferred live readings, `TieredPGO=false`, memory trim when out of focus), the Services card, the cleaning animation inside the gauge, the Review select-all + chevron Back, the compact update popup, the upright never-dimmed logo, the cleaning status row, the Interface size chip clip, and balanced Settings columns. Details and measured numbers: CHANGELOG "0.9 follow-up". **`scripts/Verify.ps1` passes** (342 checks plus all UI suites; the first run failed one Smart Boost check because live readings now start after the page fade, and the test was updated to wait for them).

**Not verified (needs the owner):** how smooth it feels on the owner's own PC (all numbers are from this 32-thread PC's test host, not the built exe); Services on a real PC (fake-tested only: no real service was changed); the memory trim in the real exe; the cleaning "cut off" text (could not be reproduced, the row now grows with its text). **Open ideas:** a header shield icon for Administrator; `PublishReadyToRun` for the release build (needs the crossgen pack download, not tried); the first expand of a very long list still has one slow frame.

## 0.9 is built and versioned, not released (November 9, 2026)

All 23 items of [PLAN-0.9.md](PLAN-0.9.md) are implemented (details in the CHANGELOG's "0.9 work" section); the version is now 0.9 (`<Version>0.9.0</Version>`, About shows "0.9") and `ReleaseNotes.Current` has the 0.9 bullets; nothing is committed, pushed or released, and `scripts/Verify.ps1` passes (run it with `pwsh`: Windows PowerShell 5.1 cannot; add `-ExecutionPolicy Bypass` if scripts are blocked). Run `Hush.Tests.exe --benchmark` and `--stress-scan` after touching the engine.

**Not verifiable on this PC (needs the owner):** the self-contained build on a clean Windows 10/11 VM with no .NET (also its ~60-70 MB size and the in-app update of such a file); the real administrator flow (UAC prompt, the Program Files move, the `Hush (administrator)` task, start-without-prompt); real Disk Cleanup handlers (Thumbnails, Defender), DISM analyze and `/ResetBase`; the real System Restore point and System Protection toggle; the real Windows Update switch (services, tasks, which of them Windows refuses on a given build, e.g. WaaSMedicSvc); Store apps and elevated apps in the Process list. Tray-only apps are only remembered after they had a window (no tray-icon detection). **Test seams to know:** `MainWindow.ReviewShowsAllByDefault` (layout tests show the whole category list; the product shows the three groups), `MainWindow.RestorePoints`, `AdminMode.Elevation/Scheduler`, `WindowsUpdateSwitch(IWindowsUpdateSystem)`.

**Decisions for the user:** whether the Windows Update switch should also appear inside the "More impact" topic list (it is its own card); a firewall telemetry blocker and a Store app uninstaller (notes in docs/WINDOWS-SETTINGS.md).

## 0.8.3 (November 8, 2026)

Released as v0.8.3 ("Hush 0.8.3", not a prerelease, tag on commit c0a3b87) with Hush-Portable.zip, Hush-Setup.zip, LICENSE and Hush-icon.png; the portable zip SHA-256 is f3d9d478328e5b069879087a0fa808ff52995915785af14775c25bf49db4cf7a. Release notes text: artifacts/RELEASE NOTES 0.8.3.md (local, not in Git).

Home polish (heading dot, bigger gauge and status text, "Scan Completed, Take a look." as an accent outline button), compact ShutUp10++-style Settings (one line per choice, "?" popups, collapsible topics, search, Actions menu), the Temporary files age setting, new cleaning rules and app discovery (`AppDiscovery.cs`). Version is 0.8.3 in Hush.csproj; the release build is in artifacts. See CHANGELOG 0.8.3.

## 0.8.2 (November 8, 2026)

Released as v0.8.2 ("Hush 0.8.2", not a prerelease, tag on commit c6153e1) with Hush-Portable.zip, Hush-Setup.zip, LICENSE and Hush-icon.png; the portable zip SHA-256 is a9580204100f34aa0c217ad90e4eba4f5829e35fabebb6bf80be7027e6d8a002. Release notes text: artifacts/RELEASE NOTES 0.8.2.md (local, not in Git).

Clean stays on screen (dimmed) once a scan has results; the Clean popup shows green/amber/red compact groups; the Windows choices grew to about 80, rated Recommended / Somewhat recommended (no "not recommended" ones), grouped by topic, with the earlier fixes below (false "managed PC" detection, administrator restart flow, restart advice popup, strong Clean hover, Safe-first Review order). Verify.ps1 passes. See CHANGELOG 0.8.2.

## Review order, bottom bar and hover (November 8, 2026)

Review categories are ordered Safe, Review, Advanced. The bottom bar has Scan again at the left and the red Clean selected button in the middle with the count and size in plain text under it (no hint text). Clean buttons (`Motion.OuterGlow`) now show a strong hover: own-red fill plus a wider, brighter glow. ScanRipples again ignores Windows' animation switch. Verify.ps1 passes; see CHANGELOG for what was and was not checked.

Windows settings are no longer read-only: the "managed device" test was wrong on almost every PC and has been fixed, and the remaining cases (Home, outside values, device-wide choices without administrator rights) became notes plus a confirmation. Device-wide choices restart Hush as administrator (`--windows-review`, `--windows-undo`) and a result popup gives restart advice. See docs/WINDOWS-SETTINGS.md and the CHANGELOG. `Hush.Tests.exe --windows-report` prints how this PC looks to the page (read-only).

## Version 0.8.1, final (November 8, 2026)

Released as v0.8.1 ("Hush 0.8.1"), built from the final commit; assets are `Hush-Portable.zip`, `Hush-Setup.zip`, `LICENSE` and `Hush-icon.png` (GitHub refuses .exe uploads; the updater unpacks `Hush-Portable.exe` from the zip). The version number never changed from 0.8.1, so the tag was re-created after the owner cleaned GitHub. Contents: simpler Info card and Boost animation; calm green for Safe/good (`SafetyPalette.Good`, `GoodBrush`) and a golden Light amber; zero build warnings; more safe cleanup locations; fifteen new Windows privacy choices (docs/WINDOWS-SETTINGS.md); a scan watchdog (per-location workers, 30 s stall limit, `Logs\scan-issues.log`, "Still looking in ..." status) for a scan that stuck at 2% on another PC; an interface size setting (Smart/75/100/120, `UiScale`) for a window too big for a small screen; and soft ripples around the gauge while scanning (`ScanRipples`). `scripts/Verify.ps1` passes (239 checks plus the UI suites); the real single-file exe was started once as a smoke test. The exact cause of the 2% stall is unknown: if it recurs, ask for `Logs\scan-issues.log`. Versions up to 0.8 look for a release asset named `Hush.exe`, which no longer exists, so they update with one manual download. The README was rewritten by the owner on GitHub and is kept, with the Download table, terms and license lines restored (tests require them). Old tags v0.7 and v0.8 still exist on GitHub with their releases.

## MIT license and popup button order (November 8, 2026)

Added LICENSE (MIT, copyright 2026 ShepherdGal; also in the zip and the exe's file properties) and put every popup's action on the left and its exit on the right (test-enforced). scripts/Verify.ps1 passes (213 engine checks plus all UI suites). rtifacts\Hush.exe was rebuilt from this source. Still not committed or released.

## Terms, downloads and one place for files (November 8, 2026)

Added the use-at-your-own-risk terms (first run, About, installer note, docs/TERMS.md), Info bars ordered Processor / Memory / Temporary files / System drive, a README Download table, flat rtifacts\ output (Hush.exe, Hush-Setup.exe, the zip, WHICH FILE.txt), scripts/Tidy.ps1, and updater cleanup (one rollback copy, staging pruned). scripts/Verify.ps1 passes (206 engine checks plus all UI suites). Nothing is committed or released: the published v0.8 release still has the earlier binaries, so a new release (and decision on version) is needed before other people or the in-app updater see any of this.

## Hush 0.8 (November 7, 2026)

New ice-blue brush icon (crisp border, transparent corners; GenerateIcon.ps1), ice-blue Dark accent #7FE0F6, one red for the whole app (locked by ColorRuleChecks), Info card in Settings, header wordmark weight 500, version 0.8. scripts/Verify.ps1 passes (170 engine checks plus all UI suites). Built with scripts/Build.ps1; published as GitHub release v0.8 (see docs/RELEASING.md for the steps). The in-app update from 0.7 to 0.8 is for the owner to try on a real install (never run on the development PC by an agent).

## Review style and Settings rework (November 7, 2026)

Review Files is the flat list again; Settings has no Appearance card; Smart Boost shows its meters and status on the closed card; Settings titles are Medium 500. `scripts/Verify.ps1` passes and renders were inspected. Not committed.

## Smart Boost, scalable engine and expanded catalog (November 7, 2026)

See the CHANGELOG entry of the same name and the Smart Boost / No file-count limit bullets in CLAUDE.md. Verified: scripts/Verify.ps1 (158 engine checks, UI suites, --verify-smart-boost), --stress-scan (250,000 files), --benchmark, rendered Review/Settings/Smart Boost previews. Research: docs/CLEANING-RESEARCH.md. Backup: Backups/pre-smart-boost-engine-*.

**Not run on this PC on purpose:** a real Disk Cleanup/DISM/clipboard clear, real startup toggles, real Automatic Boost, elevated behavior. **Open follow-ups:** confirm per-app paths on machines that have them (--scan-report), an elevated relaunch for system categories, and a measured startup-time benchmark.

## Current state

- The app is **Hush**, version **0.7** (Hush.csproj). Namespace, assembly, executable (`Hush.exe`), window title, wordmark, About text, data folder (`%LOCALAPPDATA%\Hush`), updater repo/asset names, test fixture registry root and scripts all use Hush. The app icon is an aqua "H".
- CLAUDE.md (and its copy AGENTS.md) holds the product direction, basic data-safety rules and the visual system; the app is meant to grow into a broad cleaning and tweaking tool as the user asks for features.
- Current behavior: 16 cleanup categories (Safe ones selected by default), live scan, confirmed cleanup with audit logs, Dark/Light themes, Windows privacy switches with Undo, and one-click anonymous in-app updates.
- Backups/ holds local source and portable snapshots (never in Git).

## Verification (November 7, 2026)

- `scripts/Verify.ps1` passed in full (engine, UI, theme motion, clean selection, updates, Windows settings, controlled failure).
- Fixed a test-isolation bug found during the rename: creating `App` in the test runner let WPF fire `OnStartup`, which opened a real window using the user's saved theme from `%LOCALAPPDATA%\Hush\settings.json`; a saved Light theme made the theme-motion check fail. `App.OnStartup` now skips opening the window when the entry assembly isn't Hush.
- `scripts/Build.ps1` produced `artifacts/Hush/Hush.exe` (ProductName Hush, version 0.7) and `artifacts/Hush-windows-x64.zip`. The app was not launched manually for a visual check.

## Repository layout (November 7, 2026)

Source lives in `src/Hush/`, tests in `tests/Hush.Tests/`, docs in `docs/`, scripts in `scripts/` (run them from anywhere: they find the repository root themselves). Old habits to unlearn: `Verify.ps1`, `Build.ps1` and `Backup.ps1` are no longer at the root or in Tests. Verified by a full `scripts/Verify.ps1` run and a rebuild after the move.

## Installer, cleanup and GitHub upload (November 7, 2026)

- **Installer + portable:** `Hush-Setup.exe` installs per user; `Hush.exe` stays portable. Both come from `scripts/Build.ps1`. Verified only against fixtures (`--verify-setup`); a real install/uninstall was never run on this PC. The setup window is Dark-only (it does not read the saved theme). Release assets to attach: `Hush.exe`, `Hush-Setup.exe`, `Hush-windows-x64.zip`.
- **Cleanup:** removed the old `_publish` checkout, three old backups and bin/obj. Safety copy: `Backups/hush-0.7-before-cleanup-*`. The 312 MB `.build-home` cache was kept on purpose.
- **GitHub:** the public repository https://github.com/ShepherdGal/Hush holds the code (branch `main`), and release **v0.7** (tag `v0.7`) carries `Hush-Setup.exe`, `Hush.exe` and `Hush-windows-x64.zip`. Checked from outside the account: the release is public, each asset digest equals the local file, and an anonymous download is byte-identical. GitHub CLI was installed on this PC (winget) and signed in as ShepherdGal to do it; Git now uses it as the credential helper for github.com.

## Soft blue accent in Light (November 7, 2026)

Light uses #4C7BC8 (hover #3A66AE) as its single primary accent; Dark is unchanged. Checked in rendered Light previews of Home, Review and Settings and by `scripts/Verify.ps1`. Contrast (about 4:1 on the panel) is by calculation, not measured on a screen; the colour is one constant (`SafetyPalette.Accent`) if it should be softer or deeper. The theme-switch colour fade was tested but not watched by eye.

## Live scan, typography, hover glow and GitHub-free updates (November 7, 2026)

Done and checked in rendered previews plus `scripts/Verify.ps1`: breathing glow removed, live counter/gauge during discovery, Regular italic titles and 33 px heading, +1 DIP small fonts, visible Clean hover glow, aqua italic About title with a smoother check animation, and the one-click anonymous update flow (Connect GitHub and its credential helper deleted).

**Important:** anonymous updates only work if `ShepherdGal/Hush` and its releases are public (see docs/RELEASING.md). Until then the app says updates cannot be checked right now. Not verified: a real download/install against a live release (never run on this PC), the glide/progress animation and the live gauge by eye.

## Wording, descriptions, equal gutters and Settings (November 7, 2026)

Inter Italic wordmark/heading, underlined italic completion link, plain-language category summaries, equal 22 DIP Review gutters and the new switch-based Settings page are done and checked in rendered PNGs (dark and light, 920 and 800 wide). The Windows policy catalog itself did not change: the same five choices (diagnostic data, personalized offers, Settings suggestions, Location, Widgets) are shown as switches. Not verified: the breathing glow and switch slide animation by eye; in the light-theme Home preview the link text renders clipped, which was already the case before this change and does not happen in the dark preview (likely the preview capture, not seen in the real app).

## Review page polish (November 7, 2026)

Outline gutters/corners, shared page header and Back position were fixed and checked in rendered, zoomed PNGs (dark and light). The Settings page still lets the overlay scrollbar sit over the card edge; it looked acceptable but is a candidate for the next UI round.

## Deep cleaning pass (November 7, 2026)

- Inspired by Microsoft PC Manager's deep cleanup (the user shared a screenshot with Recommended items, Other system items, Other application items 12.6 GB, Computer Usage Traces). Microsoft publishes no official category list, so the locations come from Windows documentation and general knowledge of well-known caches; see AGENTS.md "Never touch" for what was deliberately excluded.
- `scripts/Verify.ps1` passes (92 engine checks plus all UI suites). The real-PC check is `Hush.Tests.exe --scan-report` (read-only). Real cleaning of the new categories was not run on this PC.
- Not verified: system-wide categories as administrator; exact Opera/Delivery Optimization/Teams/Steam paths on machines that have them; the breathing glow and other animations by eye.
- Next requested: further UI polish (nicer, more modern, still elegant). Check renders myself first, as before.

## Cleaning expansion, audit and performance (November 7, 2026)

- Backup: `Backups/pre-cleaning-expansion-*` (verified). The portable zip in it was published to a temp folder because the user's Hush.exe was running and locking `artifacts/Hush/Hush.exe`; `artifacts/Hush-windows-x64.zip` was refreshed, `artifacts/Hush/Hush.exe` was not rebuilt.
- Engine/catalog changes are described in CHANGELOG and in the "Cleaning architecture and permanent safety rules" section of AGENTS.md/CLAUDE.md. `scripts/Verify.ps1` passes in full (81 engine checks plus UI, theme, selection, updates, Windows settings and failure-path suites).
- Not verified on this PC by design: real cleanup of any new category, real Recycle Bin emptying, and behavior of the system-protected categories as administrator (all covered only with fixtures/fakes). The default paths for Delivery Optimization, Opera and Discord caches come from public documentation and community sources that disagree in places; wrong paths are harmless (missing folders are skipped) but should be confirmed on a real machine.
- Open follow-ups: an elevated relaunch so Update/Delivery/Setup categories appear for non-admin users; a consent-dialog integration with Microsoft's Disk Cleanup/DISM for Windows.old and component-store cleanup; startup-time measurement (no startup benchmark exists yet).

## UI refinement pass (November 7, 2026)

Checkbox, Back, PageScroll/layout, single Cyan token, breathing scan-complete link, standardized hover and About footer were reworked through shared styles (App.xaml, Motion.cs) — see CHANGELOG and the "Global UI rules" in DESIGN.md. `scripts/Verify.ps1` passed after the change (run twice; the first run exposed a test that assumed per-theme Cyan, now updated). Rendered previews of Review, Settings and About were inspected; the breathing glow itself is an animation and was not watched live, and the app was not launched manually.

## Open items

- GitHub: nothing pending. For the next version: bump the version only when asked, run scripts/Verify.ps1 and scripts/Build.ps1, commit, tag and publish with `gh release create` (see docs/RELEASING.md).

## Where things are

See the code map in CLAUDE.md. Visual spec: docs/DESIGN.md. Windows settings: docs/WINDOWS-SETTINGS.md. Releases: docs/RELEASING.md. Tests: tests/Hush.Tests/README.md.
