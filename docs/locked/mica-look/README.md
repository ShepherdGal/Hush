# LOCKED: the Mica look (parked on the owner's instruction, November 9, 2026)

**Do not use, change, move, delete or "tidy" anything in this folder unless the owner says so in plain words** ("unlock the Mica archive", or similar).
It is not compiled, not referenced by the app and not part of any build. `tests/Hush.Tests/LockedArchiveChecks.cs` verifies every file here against
`CHECKSUMS.sha256` and fails if a file changes, appears or disappears: that failure is the lock. If the owner unlocks the folder, change the files, then
regenerate the checksums with `pwsh scripts/Lock-Archive.ps1 docs/locked/mica-look` and say so in CHANGELOG.

## What it is
A prototype of the Windows 11 native look for Hush's main window (`Hush-Portable.exe --mica`), built and refined on November 9, 2026, then parked: the owner
preferred the Glass look ("it feels like worse quality"). Glass (Hush's own rounded, see-through window with the compositor's transparency, no layered window)
is the default on Windows 11; `--layered` goes back to the old window.

## How it worked (so it can be rebuilt)
- **Window:** `AllowsTransparency=false`, `WindowChrome` with `GlassFrameThickness=-1`, `CaptionHeight=0`, `ResizeBorderThickness=0`; `CompositionTarget.BackgroundColor`
  transparent; DWM attributes on the window handle: `DWMWA_SYSTEMBACKDROP_TYPE (38) = 2` (Mica), `DWMWA_WINDOW_CORNER_PREFERENCE (33) = 2` (round), and
  `DWMWA_USE_IMMERSIVE_DARK_MODE (20)` set from Hush's own theme on start and on every theme change. Needs Windows 11 22H2 (build 22621) or newer; Windows
  follows the system "Transparency effects" switch by itself (off = a solid colour). Windows draws the corners (8 px), border and shadow. Full code:
  `WindowGlass.with-mica.cs.txt` (the whole file as it was, with the Glass and Layered looks beside it).
- **Surface:** `WindowSurface.CornerRadius = 0` and `Background = Transparent` so the material shows; the other looks keep 24 DIP and `WindowBrush`.
- **Palette (`MainWindow.ApplyTheme`):** keys `WindowBrush, PanelBrush, RaisedBrush, BorderBrush, TextBrush, MutedBrush, FaintBrush, HeadingBrush`
  - dark: `#202020, #0FFFFFFF, #17FFFFFF, #14FFFFFF, #E3E3E3, #A6A6A6, #8D8D8D, #F5F5F5`
  - light: `#F3F3F3, #B3FFFFFF, #0F000000, #14000000, #3A3A3A, #5E5E5E, #6B6B6B, #1B1B1B`
  - `ActionFillBrush`: dark `#17FFFFFF`, light `#0F000000`; `ModalShadeBrush`: dark `#A0000000`, light `#66000000`.
  - cards were translucent layers, so popups, menus and help bubbles used a new opaque `PopupSurfaceBrush` (`#2B2B2B` dark, `#FBFBFB` light; equal to `PanelBrush`
    in the other looks) on the four popup cards, the Actions menu and `HelpMark`'s bubble.
- **Selection:** `WindowGlass.Choose(args, build, realApp)`: `--mica` gave Mica on build 22621+, Glass on Windows 11 before that, Layered before Windows 11.
- **Test and measure:** a UI check built each look; `Hush.Tests.exe --ui-perf` took `HUSH_PERF_MICA=1`, `HUSH_PERF_LIGHT=1` and `HUSH_PERF_CAPTURE=<script>|<png>`.

## What was measured and seen
Photographed on screen in both themes (it looked like the Windows 11 Settings app). Costs inside the Hush process were a little higher than Glass: wheel scroll
about 10% of a core against 3-7%, Settings private memory about 140 MB against 108-125 MB. No screenshots are kept here (they show the desktop behind the window).
