// Targeted runs (owner, November 21, 2026: "run targeted tests by default, full sweeps only when asked"): `Hush.Tests.exe --only services,tasks` runs just
// those areas (their engine checks and their UI checks); no argument runs everything. `--list-areas` prints the names.
internal static class TestAreas
{
    // Engine areas (no window) and UI areas (one hidden app instance hosts their windows).
    internal static readonly (string Name, string What)[] Engine =
    [
        ("engine", "the cleanup engine itself: scan, delete re-checks, links, patterns, categories, discovery, reliability, scale"),
        ("process", "open apps and helpers left behind"),
        ("admin", "administrator mode and trusted staging folders"),
        ("services", "Tweaks > Services"), ("tasks", "Tweaks > Tasks"), ("startup", "Tweaks > Startup"),
        ("registry", "registry traces and issues"),
        ("tools", "Tools: programs, uninstall leftovers, Store apps, browser extensions, shredder"),
        ("registry-tools", "Tools: registry backup and search, Windows tweaks"),
        ("updates", "Tools: Software Updates, duplicates, disk check, registry size"),
        ("repair", "Tools: Device repair (Bluetooth)"),
        ("tray", "tray icon and start with Windows"), ("silent", "no Windows cleanup tool is ever started"),
        ("windows-update", "the Windows Update switch"), ("ui-size", "interface size and the window size"), ("accent", "main color"),
        ("review-order", "Clean page order and lists"), ("rules", "colour, performance, locked-archive and typography rules (source scans)"),
        ("distribution", "build and release files"), ("smart-boost", "Smart Boost"),
    ];
    internal static readonly (string Name, string What)[] Ui =
    [
        ("layout", "the main window: Home, gauge, Clean page, buttons, navigation, scan and cleaning cues"),
        ("ui-size", "interface size (real window)"), ("process", "Open apps"), ("review-groups", "Clean page groups"), ("windows-update", "Windows Update card"),
        ("services", "Services card"), ("tasks", "Tasks card"), ("startup", "Startup card"), ("tools", "Tools page"), ("registry-tools", "Registry card"),
        ("updates", "Software Updates and system care"), ("repair", "Device repair"), ("tray", "tray"), ("accent", "main color"), ("backdrop", "window background"),
        ("tweaks", "Tweaks page"), ("settings", "Settings page"), ("motion", "motion timings"), ("pro-mode", "Simple and Expert mode"),
        ("review-order", "Clean lists"), ("gauge-hover", "gauge hover"), ("smooth-scroll", "smooth scrolling"),
    ];

    // null = every area (a full sweep).
    internal static HashSet<string>? Only { get; private set; }
    internal static bool Wanted(string area) => Only == null || Only.Contains(area);
    internal static bool AnyUiWanted => Only == null || Ui.Any(area => Only.Contains(area.Name));

    internal static void Choose(string list)
    {
        var chosen = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(name => name.ToLowerInvariant()).ToHashSet();
        var known = Engine.Select(area => area.Name).Concat(Ui.Select(area => area.Name)).ToHashSet();
        var unknown = chosen.Where(name => !known.Contains(name)).ToList();
        if (unknown.Count > 0 || chosen.Count == 0) throw new ArgumentException("Unknown test area: " + string.Join(", ", unknown) + ". Run --list-areas to see them.");
        Only = chosen;
    }

    internal static void Print()
    {
        foreach (var (name, what) in Engine.Concat(Ui).GroupBy(area => area.Name).Select(group => group.First()).OrderBy(area => area.Name)) Console.WriteLine($"{name,-15} {what}");
    }
}
