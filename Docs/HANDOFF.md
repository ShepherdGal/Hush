# Hush handoff — 0.7

Updated November 7, 2026 (Asia/Jerusalem).

## Current state

- The app is **Hush**, version **0.7** (Hush.csproj). Namespace, assembly, executable (`Hush.exe`), window title, wordmark, About text, data folder (`%LOCALAPPDATA%\Hush`), updater repo/asset names, test fixture registry root and scripts all use Hush. The app icon is an aqua "H".
- CLAUDE.md (and its copy AGENTS.md) holds the product direction, basic data-safety rules and the visual system; the app is meant to grow into a broad cleaning and tweaking tool as the user asks for features.
- Current behavior: 16 cleanup categories (Safe ones selected by default), live scan, confirmed cleanup with audit logs, Dark/Light themes, Windows privacy switches with Undo, and one-click anonymous in-app updates.
- Backups/ holds local source and portable snapshots (never in Git); Docs/References holds design reference images (also not in Git).

## Verification (November 7, 2026)

- `Tests/Verify.ps1` passed in full (engine, UI, theme motion, clean selection, updates, Windows settings, controlled failure).
- Fixed a test-isolation bug found during the rename: creating `App` in the test runner let WPF fire `OnStartup`, which opened a real window using the user's saved theme from `%LOCALAPPDATA%\Hush\settings.json`; a saved Light theme made the theme-motion check fail. `App.OnStartup` now skips opening the window when the entry assembly isn't Hush.
- `Build.ps1` produced `artifacts/Hush/Hush.exe` (ProductName Hush, version 0.7) and `artifacts/Hush-windows-x64.zip`. The app was not launched manually for a visual check.

## Soft blue accent in Light (November 7, 2026)

Light uses #4C7BC8 (hover #3A66AE) as its single primary accent; Dark is unchanged. Checked in rendered Light previews of Home, Review and Settings and by `Tests/Verify.ps1`. Contrast (about 4:1 on the panel) is by calculation, not measured on a screen; the colour is one constant (`SafetyPalette.Accent`) if it should be softer or deeper. The theme-switch colour fade was tested but not watched by eye.

## Live scan, typography, hover glow and GitHub-free updates (November 7, 2026)

Done and checked in rendered previews plus `Tests/Verify.ps1`: breathing glow removed, live counter/gauge during discovery, Regular italic titles and 33 px heading, +1 DIP small fonts, visible Clean hover glow, aqua italic About title with a smoother check animation, and the one-click anonymous update flow (Connect GitHub and its credential helper deleted).

**Important:** anonymous updates only work if `ShepherdGal/Hush` and its releases are public (see Docs/RELEASING.md). Until then the app says updates cannot be checked right now. Not verified: a real download/install against a live release (never run on this PC), the glide/progress animation and the live gauge by eye.

## Wording, descriptions, equal gutters and Settings (November 7, 2026)

Inter Italic wordmark/heading, underlined italic completion link, plain-language category summaries, equal 22 DIP Review gutters and the new switch-based Settings page are done and checked in rendered PNGs (dark and light, 920 and 800 wide). The Windows policy catalog itself did not change: the same five choices (diagnostic data, personalized offers, Settings suggestions, Location, Widgets) are shown as switches. Not verified: the breathing glow and switch slide animation by eye; in the light-theme Home preview the link text renders clipped, which was already the case before this change and does not happen in the dark preview (likely the preview capture, not seen in the real app).

## Review page polish (November 7, 2026)

Outline gutters/corners, shared page header and Back position were fixed and checked in rendered, zoomed PNGs (dark and light). The Settings page still lets the overlay scrollbar sit over the card edge; it looked acceptable but is a candidate for the next UI round.

## Deep cleaning pass (November 7, 2026)

- Inspired by Microsoft PC Manager's deep cleanup (the user shared a screenshot with Recommended items, Other system items, Other application items 12.6 GB, Computer Usage Traces). Microsoft publishes no official category list, so the locations come from Windows documentation and general knowledge of well-known caches; see AGENTS.md "Never touch" for what was deliberately excluded.
- `Tests/Verify.ps1` passes (92 engine checks plus all UI suites). The real-PC check is `Hush.Tests.exe --scan-report` (read-only). Real cleaning of the new categories was not run on this PC.
- Not verified: system-wide categories as administrator; exact Opera/Delivery Optimization/Teams/Steam paths on machines that have them; the breathing glow and other animations by eye.
- Next requested: further UI polish (nicer, more modern, still elegant). Check renders myself first, as before.

## Cleaning expansion, audit and performance (November 7, 2026)

- Backup: `Backups/pre-cleaning-expansion-*` (verified). The portable zip in it was published to a temp folder because the user's Hush.exe was running and locking `artifacts/Hush/Hush.exe`; `artifacts/Hush-windows-x64.zip` was refreshed, `artifacts/Hush/Hush.exe` was not rebuilt.
- Engine/catalog changes are described in CHANGELOG and in the "Cleaning architecture and permanent safety rules" section of AGENTS.md/CLAUDE.md. `Tests/Verify.ps1` passes in full (81 engine checks plus UI, theme, selection, updates, Windows settings and failure-path suites).
- Not verified on this PC by design: real cleanup of any new category, real Recycle Bin emptying, and behavior of the system-protected categories as administrator (all covered only with fixtures/fakes). The default paths for Delivery Optimization, Opera and Discord caches come from public documentation and community sources that disagree in places; wrong paths are harmless (missing folders are skipped) but should be confirmed on a real machine.
- Open follow-ups: an elevated relaunch so Update/Delivery/Setup categories appear for non-admin users; a consent-dialog integration with Microsoft's Disk Cleanup/DISM for Windows.old and component-store cleanup; startup-time measurement (no startup benchmark exists yet).

## UI refinement pass (November 7, 2026)

Checkbox, Back, PageScroll/layout, single Cyan token, breathing scan-complete link, standardized hover and About footer were reworked through shared styles (App.xaml, Motion.cs) — see CHANGELOG and the "Global UI rules" in DESIGN.md. `Tests/Verify.ps1` passed after the change (run twice; the first run exposed a test that assumed per-theme Cyan, now updated). Rendered previews of Review, Settings and About were inspected; the breathing glow itself is an animation and was not watched live, and the app was not launched manually.

## Open items

- GitHub: the project folder is now a fresh Git repository (single commit, remote `ShepherdGal/Hush` configured, nothing pushed). The owner still has to delete the old repository and create/push the new one (steps in Docs/RELEASING.md). I cannot do that from here: it needs the owner's GitHub sign-in and is irreversible.
- `_publish/repository` is an old unused checkout; it can be deleted once the new repository is on GitHub.

## Where things are

See the code map in CLAUDE.md. Visual spec: Docs/DESIGN.md. Windows settings: Docs/WINDOWS-SETTINGS.md. Releases: Docs/RELEASING.md. Tests: Tests/README.md.
