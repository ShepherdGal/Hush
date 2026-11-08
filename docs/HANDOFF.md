# Hush handoff — 0.7

Updated November 7, 2026 (Asia/Jerusalem).

## 0.8.2 (November 8, 2026)

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
- Backups/ holds local source and portable snapshots (never in Git); docs/References holds design reference images (also not in Git).

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
