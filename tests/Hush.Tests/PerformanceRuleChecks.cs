using System.IO;
using System.Text.RegularExpressions;
using Hush;

// Locks the performance and smoothness rules of docs/RULES.md (0.9 audit). Source files are scanned as text, so a new agent cannot add a window-wide effect,
// a bitmap cache, an endless animation, a timer or a blocking wait in the UI without this suite failing and pointing at the rule. When a rule must change,
// change docs/RULES.md, CLAUDE.md/AGENTS.md and the allow-list here in the same commit, after measuring with `Hush.Tests.exe --ui-perf` (scripts/Perf.ps1).
internal static class PerformanceRuleChecks
{
    private static string Relative(string source, string path) => Path.GetRelativePath(source, path).Replace('\\', '/');

    internal static void Run(Action<bool, string> check)
    {
        string testsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string source = Path.GetFullPath(Path.Combine(testsDirectory, "..", "..", "src", "Hush"));
        var files = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(path => (path.EndsWith(".cs") || path.EndsWith(".xaml")) && !path.Contains(@"\obj\") && !path.Contains(@"\bin\")).ToList();
        string Text(string path) => File.ReadAllText(path);
        // Files (relative to src/Hush) in which a pattern may appear; everything else in the source must not contain it.
        List<string> Where(Regex pattern) => files.Where(path => pattern.IsMatch(Text(path))).Select(path => Relative(source, path)).Order().ToList();
        bool Only(Regex pattern, params string[] allowed) => Where(pattern).All(path => allowed.Contains(path));
        check(files.Count > 40, "The performance rules scan the real source files");

        // 1. No bitmap caching or legacy bitmap effects (a cached fade drew pages at a lower resolution and blurred the text; measured: no gain).
        check(Only(new Regex(@"BitmapCache|CacheMode\b|BitmapEffect\b")), "Rule: no BitmapCache, CacheMode or BitmapEffect anywhere (pages must stay sharp in every frame)");
        // 2. Effects: the popup blur is the one window-wide effect, and it is off by default (it cost 80% of a core on software rendering).
        check(Only(new Regex(@"<BlurEffect|new BlurEffect"), "MainWindow.xaml") && !MainWindow.BackdropBlurEnabled,
            "Rule: no window-wide blur: the only BlurEffect is the popup backdrop one and it is OFF by default (a darker scrim separates popups instead)");
        check(Only(new Regex(@"<DropShadowEffect|new DropShadowEffect"), "MainWindow.xaml", "UI/Motion.cs", "UI/HelpMark.cs"),
            "Rule: a DropShadowEffect exists only in the shared glow (Motion), the lazily built help bubble, the Actions menu and the gauge glow");
        // 3. Looping animations only in the two scan cues (they cap their frame rate and run only while visible).
        check(Only(new Regex(@"RepeatBehavior\.Forever|RepeatBehavior=""Forever"""), "UI/ScanActivity.cs", "UI/ScanRipples.cs"), "Rule: endless animations exist only in the two scan cues, which cap their frame rate and run only while visible");
        // 3b. 60 fps native, locked (owner, November 9, 2026): no frame-rate cap anywhere, and the two scan cues stay cheap to draw (transforms and opacity of
        //     pre-built shapes only: no opacity mask, no clip, no effect) with no effect on any ancestor, so a moving cue never re-renders a blurred button.
        check(Only(new Regex(@"DesiredFrameRate|FrameInterval")), "Rule: no frame-rate cap anywhere (no DesiredFrameRate, no FrameInterval): every animation runs at the display's own refresh rate");
        check(new[] { "UI/ScanActivity.cs", "UI/ScanRipples.cs" }.All(file => !new Regex(@"OpacityMask|\.Clip\b|Clip =|Effect").IsMatch(Text(Path.Combine(source, file)))),
            "Rule: the scan cues (sweep, ripples) use no opacity mask, clip or effect: one transform or opacity animation per moving element");
        check(Text(Path.Combine(source, "MainWindow.xaml")).Contains(@"x:Name=""GlowUnderlay"" Width=""250"""),
            "Rule: the gauge button's glow sits on a disc behind the gauge (GlowUnderlay), never on the button, so animation inside the gauge never re-renders through a blur");
        // 4. Timers and per-frame hooks.
        check(Only(new Regex(@"DispatcherTimer"), "MainWindow.xaml.cs", "SmartBoost/MainWindow.SmartBoost.cs", "SmartBoost/MainWindow.BoostChip.cs"), "Rule: DispatcherTimer only in the scan status line, the Smart Boost timers and the Boost chip's 5-second timer (owner-approved, runs only while Hush is in front; no other polling)");
        check(Only(new Regex(@"CompositionTarget\.Rendering"), "UI/SmoothScroll.cs"), "Rule: a per-frame hook (CompositionTarget.Rendering) only in smooth scrolling, and only while a glide runs");
        // 5. One scrolling implementation.
        check(Only(new Regex(@"PreviewMouseWheel|MouseWheel\b"), "UI/SmoothScroll.cs", "MainWindow.xaml", "MainWindow.xaml.cs"), "Rule: mouse-wheel handling lives in SmoothScroll (no per-screen wheel code)");
        // 6. Transparent windows: the main and setup windows, the two popups. Anything else is a new layered window (each costs a full copy per frame).
        check(Only(new Regex(@"AllowsTransparency"), "MainWindow.xaml", "Setup/SetupWindow.xaml", "UI/HelpMark.cs", "UI/WindowGlass.cs"), "Rule: AllowsTransparency only on the app windows and the two popups (each layered window costs a copy of its pixels per frame)");
        // 7. Nothing on the UI path may block: sleeps and waits belong to code that already runs on a worker (the engine, Windows tools), never in window code.
        check(Only(new Regex(@"Thread\.Sleep|\.GetAwaiter\(\)\.GetResult\(\)|\.Wait\("), "Cleaning/CleanupEngine.cs", "Cleaning/SystemTools.cs", "SmartBoost/MemoryBooster.cs", "SmartBoost/ProcessManager.cs", "Admin/AdminMode.cs", "WindowsSettings/ServiceTuning.cs", "WindowsSettings/WindowsUpdateSwitch.cs"),
            "Rule: no Thread.Sleep or blocking wait in window code (only the engine and Windows-tool classes, which run on a worker)");
        // 8. The popup/help marks are built when first shown, never per row.
        check(Text(Path.Combine(source, "UI", "HelpMark.cs")).Contains("EnsurePopup()") && !new Regex(@"private readonly Popup popup = new").IsMatch(Text(Path.Combine(source, "UI", "HelpMark.cs"))),
            "Rule: a help mark builds its popup and shadow only when first shown (a Settings page has over a hundred of them)");
        // 9. The run-time settings that keep Hush light.
        string project = Text(Path.Combine(source, "Hush.csproj"));
        check(project.Contains("<TieredPGO>false</TieredPGO>") && project.Contains("<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>"), "Rule: TieredPGO and the background GC stay off (they cost about a core while a page first opens)");
        // 9b. The window look (docs/RULES.md): Glass on Windows 11 in the real app, Layered before it and in every test process, flags override.
        string[] none = [];
        check(WindowGlass.Choose(none, 26200, realApp: true) == WindowLook.Glass && WindowGlass.Choose(none, 22000, true) == WindowLook.Glass
            && WindowGlass.Choose(none, 19045, true) == WindowLook.Layered && WindowGlass.Choose(none, 26200, realApp: false) == WindowLook.Layered,
            "Window look: the real app on Windows 11 gets the Glass window by default, Windows 10 and every test process keep the Layered one");
        check(WindowGlass.Choose(["--layered"], 26200, true) == WindowLook.Layered && WindowGlass.Choose(["--LAYERED", "--glass"], 26200, true) == WindowLook.Layered
            && WindowGlass.Choose(["--glass"], 19045, true) == WindowLook.Glass && WindowGlass.Choose(["--mica"], 26200, true) == WindowLook.Glass,
            "Window look: --layered always wins, --glass forces Glass, and an unknown flag (the parked --mica) changes nothing");
        // 10. Modal popups use the shared dark scrim.
        string app = Text(Path.Combine(source, "App.xaml"));
        check(Regex.IsMatch(app, @"ModalShadeBrush"" Color=""#[A-F0-9]{2}0C1016"""), "Rule: popups sit on the shared ModalShadeBrush scrim");
    }
}
