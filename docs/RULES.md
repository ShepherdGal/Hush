# Hush engineering rules (binding for every agent)

Read this before touching UI, animation, scrolling, performance, the engine or anything that changes Windows. It exists so that new agents do not write
things differently and break what was measured and fixed. **The user's latest direction always wins**: when a request conflicts with a rule, follow the
request and change the rule (this file, CLAUDE.md/AGENTS.md and the test that locks it) in the same piece of work. Rules marked *(test)* are enforced by
`scripts/Verify.ps1`; the rest are enforced by you.

## 1. The machine Hush must feel good on

- **Target hardware: an Intel Core i3 with 8 GB of RAM and integrated graphics**, Windows 10 or 11, any display from 1366x768 to 4K, 100%–200% scaling.
  The developer's PC is far stronger (32 logical processors, a fast GPU): **"it is smooth here" proves nothing.** Always measure the weak case.
- `scripts/Perf.ps1` (wraps `Hush.Tests.exe --ui-perf`) runs the window under 4 logical processors, software rendering and the product's run-time settings,
  a pessimistic stand-in for that machine. Real i3 cores are also slower, so treat its numbers as a lower bound.
- **Budgets** (software rendering, 4 processors, Perf.ps1 exits 1 when broken): Home idle under 3% of a core; opening a popup under 100% of a core;
  median frame under 20 ms (about 60 fps) for wheel scrolling, expanding a card and opening a popup. Record a new baseline in CHANGELOG when you change it.
- Hush's own footprint: idle on Home about 0% CPU; after it loses focus the working set shrinks (`MainWindow.Memory.cs`); never add a background poller.

## 2. Performance rules

1. **No window-wide effect.** The popup backdrop blur exists in code but is OFF (`MainWindow.BackdropBlurEnabled = false`): it cost 80% of a core and frames
   of up to 150 ms with software rendering. Popups use the dark `ModalShadeBrush` scrim. Turning blur on needs a measurement and the owner's say-so *(test)*.
2. **No `BitmapCache`, `CacheMode`, `BitmapEffect`** *(test)*: a cached fade drew pages at a lower resolution and snapped them sharp at the end (blurry text on
   every page change) and measured no gain. Pages and popups fade by **opacity only**; never slide with a fractional `RenderTransform`.
3. **A `DropShadowEffect` is attached only while it is visible** (`Motion.Light`); only the shared glow, the help bubble, the Actions menu and the gauge glow
   may create one *(test)*. 100 idle effects made every redraw of Settings five times more expensive.
4. **The window look (`UI/WindowGlass.cs`, *test*).** On Windows 11 the real app uses **Glass** by default: the same rounded, see-through window with transparency from Windows' compositor instead of a layered window (a layered window makes WPF copy the whole window back to main memory every frame: the dominant cost of every animation on a weak PC, and 10 to 50 MB of private memory). `--layered` is the way back, `--glass` forces it (Windows 10 is unverified). The Windows 11 native Mica look was tried, refined and PARKED by the owner; it is locked in `docs/locked/mica-look` (section 9). Test processes and Windows 10 stay Layered. `AllowsTransparency` may appear only on the app windows, the two popups and WindowGlass. Never change the default without measuring (`Perf.ps1` with `HUSH_PERF_GLASS=1`) and looking at a real capture (`HUSH_PERF_CAPTURE`).
5. **No polling.** The only timers are the scan status line, the Smart Boost timer and the Settings meter timer (2 s, sampled only while Settings is open) *(test)*.
   A bar or number that moves by less than a visible amount is not redrawn (`ShowMeter(minChange)`): every redraw costs real CPU on a weak PC.
6. **Nothing animates inside a page while the page fades in**; live readings start after the fade (`BeginSettingsLiveData`).
7. **Build lazily.** Never construct a popup, effect, list or heavy object per row or at page open: a Settings page has well over a hundred help marks and each
   used to build a popup with a shadow (`HelpMark.EnsurePopup`) *(test)*. A card's data is read when the card is opened (Services, Scheduled tasks), not when Settings opens.
   Nothing at start-up or at page open may start `sc.exe`, a WMI query, a registry walk or a process list that the person did not ask for.
8. **Never block the UI thread:** no `Thread.Sleep`, `.Wait()`, `.Result` or `.GetAwaiter().GetResult()` in window code *(test)*; slow work goes to
   `Task.Run`, with cancellation, a time limit (scan watchdog, `Guarded`, the close grace) and progress. Background work runs at background priority when
   low-resource mode is on.
9. **One enumeration pass per directory, no per-file allocations, no per-file process lookups** (engine rules in CLAUDE.md). Re-measure with `--benchmark` and
   `--stress-scan` after touching the engine.
10. **Run-time settings stay:** `TieredPGO=false`, `ConcurrentGarbageCollection=false` *(test)*; the release build is self-contained and compressed. (ReadyToRun
    would cut first-open JIT time a lot on a slow CPU; it needs the crossgen package, so it needs the owner's approval to download.)
11. Memory: collect once after a scan's results are replaced or a clean ends; trim when out of focus; lists stay virtualized (`BulkCollection`, one Reset).

## 3. Animation rules

- One duration/easing family: 280 ms button interactions, 320 ms theme transitions, the shared cubic ease-out (`Motion`). Never invent a new curve or duration;
  extend `Motion` if something truly new is needed.
- Animate **opacity and transforms** (render thread). Layout is animated only by `AccordionBody` (clip to the revealed height, never the width).
- **No endless animation** except the two scan cues (`ScanActivity`, `ScanRipples`) *(test)*, which cap their frame rate (`ScanActivity.FrameInterval`), run only
  while visible and use the accent brush only. No breathing glows, no pulsing, no animation that runs while nothing is happening.
- **Hover never scales or resizes anything** (one owner-approved exception: the gauge's hover cue draws copies of light rings that grow or contract and fade, `GaugeHoverCue`; the gauge itself never moves). Text eases its colour, glowing buttons intensify their glow, checkboxes ease a neutral ring, scrollbars ease opacity.
- Loading bars: ONE shared bar that fills smoothly from empty to full (never a travelling bar); real progress where it exists.
- **60 fps native, LOCKED (owner: "the app should feel 60fps native always").** No `DesiredFrameRate`, no frame-rate cap, no slowing of animation in low-resource mode *(test)*. Moving content is pre-built shapes moved by transforms/opacity on the render thread; nothing that must be re-rasterized per frame (no opacity mask, clip or effect on it or on an ancestor: an effect on the gauge button once made the cleaning cue render through a blur every frame) *(test)*. Budget in `scripts/Perf.ps1`: median frame under 20 ms in the cleaning scene.
- Every animation must stop at once on cancel/close and must never leave a control in a half state.

## 4. Smooth scrolling rules (`UI/SmoothScroll.cs`, installed once for every `ScrollViewer`)

- The wheel/touchpad glide is **time-constant smoothing** (each rendered frame covers a fixed share of the remaining distance, scaled by real frame time): a
  further notch only moves the target, speed never restarts, a slow frame lengthens the next step instead of the glide. It hooks `CompositionTarget.Rendering`
  **only while a glide runs** *(test)* and costs nothing at rest.
- Never write per-screen wheel code *(test)*; never animate `ScrollViewer` offsets yourself; scrollbar, keyboard and `ScrollToTop` cancel a glide at once.
- A virtualizing list must set `VirtualizingPanel.ScrollUnit="Pixel"`; item-based scrolling is left to WPF. Page scrollers use the `PageScroll` style.
- Check scrolling with Perf.ps1 ("Wheel-scroll Settings") after any change to a scrolled page: effects and layout inside scrolled content are re-rendered every frame.

## 5. UI rules

- **Reuse the shared styles and tokens** (App.xaml, `Motion.cs`, `ThemePalette.cs`); never hand-style a title, back button, checkbox, switch or popup. Colours follow
  the traffic-light set and THE RED RULE (CLAUDE.md, *test*). Typography follows the locked sizes (*test*).
- **Switches work like O&O ShutUp10++ (owner, November 9, 2026): ON = the tweak is on, i.e. the thing is turned off / protected; OFF = left as it is.** A privacy
  choice's ON means "protection applied"; a service's or scheduled task's ON means "turn this off" (staged: the row says "Will be turned off after you review").
  Nothing is written until a confirmation. Say it in the card's first line and keep the "?" for the long explanation. The Settings footer has ONE "Review changes" button for everything staged on the page; cards have no Review button of their own. **Action buttons in Settings (Boost, Review ..., Turn off, Actions) use `AccentOutlineButton`** so they all share the one outer-glow hover. **A header toggle that is ON (today the "Pro" button, style `ProModeButton`, state in `Tag`) takes the same shared accent highlight as `AccentOutlineButton` (accent outline, `AccentFillBrush` fill, `Motion.OuterGlow` + `Motion.Glow`), and while OFF it is the plain caption look; never invent a second "selected" look for a header toggle, and give it a spoken on/off name.** **Simple mode (the default for everyone) is Safe-only scanning plus a short Settings page; every new Settings card or Review feature is Pro-only unless the owner says otherwise (`MainWindow.ApplyProMode` lists what Simple hides).**
- **A card opens with everything inside it closed**, always. Closing a card forgets which sub-categories were open (`TopicCard_Collapsed`); a search opens all
  while it is typed. Never auto-open a topic, even one that "has something running".
- **Order everything.** Topics in a fixed logical order (Services: `ServiceTuning.TopicRank`, Scheduled tasks: `TaskTuning.TopicRank`, left-behind last); inside a topic safest/most common first, then
  by name. Long lists split themselves (`ListGrouping`, 12 per group). Never show a flat wall of rows.
- **Popups:** the action is on the LEFT, the exit on the RIGHT (*test*); Escape closes; a button that opens a menu toggles it (Actions: `StaysOpen=true`, closed by
  a second click, a press elsewhere, Escape, losing focus or choosing an entry); one popup at a time; popups sit on the shared scrim; the first focus is the safe button.
- **Text:** short. Explanations live in `HelpMark` popups or tooltips, not on cards; one short line is the most a card shows. Plain words, no jargon, no speed
  claims, no "boost". Sentence case. A status line that reports a result stays on screen (never replaced at once by another text; *test*).
- **Every state exists and is designed:** empty, loading, partial, failed, busy, disabled (and says why), not-administrator (offers the restart), offline.
- **Both themes, 800 and 920 wide, 75% / 100% / 120% interface size, 100%–200% DPI:** render and look (`HUSH_PREVIEW_DIRECTORY`); zoom in on alignment.
  Text must be crisp in every frame (`UseLayoutRounding`, Ideal formatting, per-monitor DPI).
- Keyboard and accessibility: every interactive control has an `AutomationProperties.Name`, a logical tab order and a visible focus; Escape/Enter behave.
- Truncated text is a bug: widen, wrap or shorten it (check summaries and status lines at 800 wide).

## 6. Code rules for anything that changes the system

- Scan is read-only; every delete re-verifies; every system change is confirmed first, saved first (originals), verified after (read back), logged, and has Undo.
- Elevated code never trusts a file other programs can write (`AdminOnlyDirectory`); histories are checked against allow-lists; roots pass the root guard.
- Never run real cleaning, real Windows changes, real updates/installs, real services, real restore points to test: fakes and fixtures only
  (`HKCU\Software\Hush.TestFixtures\<GUID>`, unique temp folders).
- Errors: catch narrowly, never swallow silently where the person needs to know; a failure message is never overwritten by a later "everything fine" text;
  unhandled errors go to `Logs\crash.log` (`CrashLog`). No `async void` except event handlers, and those must not throw.
- Closing the window never waits without a limit (`MainWindow.CloseGraceMilliseconds`).

## 7. Definition of done (do all of it, in this order)

1. Code and tests: add or update a test for every rule or behaviour you touch; update tests that encode behaviour you intentionally changed.
2. `pwsh scripts/Verify.ps1` passes (0 warnings). For engine changes also `--benchmark` and `--stress-scan`.
3. **UI changed?** Render previews (`HUSH_PREVIEW_DIRECTORY=<folder>` with Verify.ps1 -NoBuild) and LOOK at them in both themes at 800 and 920; then
   `pwsh scripts/Perf.ps1` and compare with the budgets above. Say honestly what you could not see (a real GPU, a real mouse, a real display).
4. `scripts/Build.ps1` so `artifacts\Hush-Portable.exe` is the current source.
5. Update docs/HANDOFF.md and docs/CHANGELOG.md with what changed and what was **not** verified. Do not commit, push or release unless asked; do not change the
   version unless asked.

## 8. Never

A window-wide effect; a cached bitmap; a looping animation outside the scan cues; a new timer; a blocking call on the UI thread; a popup built per row; reading
Windows (services, WMI, processes, registry walks) when a page opens; a second red; a one-off style; a long text on a card; an auto-opened sub-category; a
status that is replaced at once; an unbounded wait; a real system change in a test; trust in a user-writable file by elevated code.

## 9. Locked archives

Parked work lives in `docs/locked/<name>` (today `mica-look`: the Windows 11 Mica look, built and refined on November 9, 2026, then parked because the owner preferred
Glass). **Do not use, edit, move, delete, tidy or re-enable anything there unless the owner says so in plain words.** It is not compiled and nothing references it.
`tests/Hush.Tests/LockedArchiveChecks.cs` compares every file with `CHECKSUMS.sha256`; a changed, added or missing file fails Verify.ps1 (that is the lock). After the
owner unlocks a folder and the change is done, `pwsh scripts/Lock-Archive.ps1 docs/locked/<name>` writes the new checksums, and CHANGELOG says so.
