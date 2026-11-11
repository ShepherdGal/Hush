using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hush;

// SystemTool items are run through a supported Windows tool (Disk Cleanup, DISM) or API (clipboard history) behind an injectable service.
public enum CleanupKind { Temp, Cache, Registry, Junk, Shortcut, RecycleBin, SystemTool }
// Safe (primary accent): verified disposable, selected by default. Review (orange): caches, logs and traces with possible side effects, opt-in.
// Advanced (red): irreversible or recovery-related, opt-in with explicit consent.
public enum SafetyLevel { Safe, Review, Advanced }

// One row of the scan result. A scan can produce hundreds of thousands or millions of these, so the object is deliberately small:
// the folder string is shared by every file of that folder, rarely used fields live in one side object, and times are stored as ticks.
public sealed class CleanupItem : INotifyPropertyChanged
{
    private string? folder, fileName;
    private long modifiedTicks, createdTicks;
    private Extra? extra;
    private sealed class Extra
    {
        public string? RegistryExecutable, RegistrySnapshot, ShortcutTarget, ShortcutHash, Detail, ToolId;
    }
    private Extra Side => extra ??= new();

    // The full path. Setting it splits it into the shared folder and the file name; the engine sets Folder/FileName directly.
    public string Path
    {
        get => folder == null ? fileName ?? "" : folder + "\\" + fileName;
        init
        {
            int separator = value.LastIndexOf('\\');
            if (separator < 0) { folder = null; fileName = value; }
            else { folder = value[..separator]; fileName = value[(separator + 1)..]; }
        }
    }
    internal string? Folder { get => folder; init => folder = value; }
    internal string? FileName { get => fileName; init => fileName = value; }
    public required string Root { get; init; }
    public required string Group { get; init; }
    public CleanupKind Kind { get; init; }
    public long Bytes { get; init; }
    public DateTime ModifiedUtc { get => new(modifiedTicks, DateTimeKind.Utc); init => modifiedTicks = value.Ticks; }
    public DateTime CreatedUtc { get => new(createdTicks, DateTimeKind.Utc); init => createdTicks = value.Ticks; }
    internal long ModifiedTicks { get => modifiedTicks; init => modifiedTicks = value; }
    internal long CreatedTicks { get => createdTicks; init => createdTicks = value; }
    public string? RegistryExecutable { get => extra?.RegistryExecutable; init => Side.RegistryExecutable = value; }
    public string? RegistrySnapshot { get => extra?.RegistrySnapshot; init => Side.RegistrySnapshot = value; }
    public string? ShortcutTarget { get => extra?.ShortcutTarget; init => Side.ShortcutTarget = value; }
    public string? ShortcutHash { get => extra?.ShortcutHash; init => Side.ShortcutHash = value; }
    // Identifies a SystemTool item (for example "windows-old" or "clipboard-history").
    public string? ToolId { get => extra?.ToolId; init => Side.ToolId = value; }
    // The review category (and so the risk level) comes from the target that found the item; older items fall back to Kind/Group.
    public string? CategoryId { get; init; }
    public long ItemCount { get; init; }
    public string? Detail { get => extra?.Detail; init => Side.Detail = value; }
    public SafetyLevel Safety => ReviewCategory.SafetyOf(ReviewCategory.CategoryFor(this));
    internal ReviewCategory? Owner { get; set; }
    public string SelectionAccent => SafetyPalette.Text(Safety, Owner?.IsLight ?? false);
    internal void ThemeChanged() => Changed(nameof(SelectionAccent));
    public string Name => Kind is CleanupKind.RecycleBin or CleanupKind.SystemTool ? Path : fileName ?? "";
    public string DirectoryLabel => Detail ?? folder ?? Path;
    public string GroupLabel => Group.Replace("Brave-Browser", "Brave", StringComparison.Ordinal);
    public string SizeLabel => Kind == CleanupKind.Registry ? "REGISTRY" : Kind == CleanupKind.SystemTool && Bytes == 0 ? "—" : Sizes.Format(Bytes);
    private bool selected = true;
    public bool Selected
    {
        get => selected;
        set { if (selected == value) return; selected = value; Owner?.ItemSelectionChanged(this); Changed(); }
    }
    private string? outcome;
    public string Outcome { get => outcome ?? ""; set { outcome = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public static class Sizes
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return index == 0 ? $"{bytes:N0} B" : $"{size:0.#} {units[index]}";
    }
}

// Pattern may list several simple wildcards separated by ';'. Category assigns the review category (and risk level).
// DeletableOnly marks a system-protected location: a file is listed only if it can be opened for deletion right now,
// and permission errors there are not reported as skipped locations. ExcludeFolders names sub-folders (any depth) that are never entered.
// LockFiles are files an app keeps while it runs (Chromium's "lockfile"): if one exists the app is open and the location is skipped, like a running browser.
internal sealed record CleanupTarget(string Root, string Group, CleanupKind Kind, TimeSpan MinimumAge, string? Pattern = null, string[]? Processes = null, bool Recursive = true,
    string? Category = null, bool DeletableOnly = false, string[]? ExcludeFolders = null, string[]? LockFiles = null);
internal sealed record ScanProgress(string Location, int Examined, int Found, long Bytes, double Fraction, int RegistryEntries = 0)
{
    public int FilesFound => Math.Max(0, Found - RegistryEntries);
}
// Items is empty for a count-only scan (background junk monitoring); FileCount and TotalBytes are always filled.
// Stalled counts locations or Windows steps that stopped answering and were skipped so the scan could finish.
// ProtectedFiles counts files in system-protected locations that qualify but could not be opened for deletion (in use, or needs administrator rights).
internal sealed record ScanResult(List<CleanupItem> Items, int Inaccessible, int BusyCaches, bool Stopped, long FileCount = 0, long TotalBytes = 0, int Stalled = 0, int ProtectedFiles = 0);
// Restricts or lightens a scan. Include picks targets (Quick Boost: Safe only); CountOnly measures without keeping items;
// ReportIntervalMs slows progress reporting in low-resource mode; Background lowers the scanning thread to background CPU and disk priority.
// StallMilliseconds: a location that produces nothing for this long is skipped (0 turns the watchdog off); StepTimeoutMilliseconds limits the registry and
// Recycle Bin steps (Disk Cleanup gets three times as long). OnTargetStart is a test hook that runs when a location begins.
internal sealed record ScanOptions(Func<CleanupTarget, bool>? Include = null, bool CountOnly = false, int ReportIntervalMs = 40, bool Background = false,
    int StallMilliseconds = 30000, int StepTimeoutMilliseconds = 30000, Action<CleanupTarget>? OnTargetStart = null, int SlowNoteMilliseconds = 3000);
// FreeSpaceBefore/After are the free bytes Windows reports for the drives that held the selected items, measured around the clean.
internal sealed record CleanupResult(int Deleted, int Skipped, long Bytes, bool Stopped, string LogPath, long FreeSpaceBefore = 0, long FreeSpaceAfter = 0,
    IReadOnlyDictionary<string, int>? SkipReasons = null)
{
    public long MeasuredFreed => Math.Max(0, FreeSpaceAfter - FreeSpaceBefore);
    // Why files were skipped, in plain words with counts (for the result line): "12 in use by a program right now · 3 changed since the scan".
    public string SkipSummary => SkipReasons == null || SkipReasons.Count == 0 ? ""
        : string.Join(" · ", SkipReasons.OrderByDescending(pair => pair.Value).Take(3).Select(pair => $"{pair.Value:N0} {char.ToLowerInvariant(pair.Key[0])}{pair.Key[1..]}"));
}

public static class SafetyPalette
{
    // The one primary accent per theme: in Dark the person's choice (Settings > Appearance, AccentPalette; this is the DEFAULT, the icon's blue), a softer,
    // readable blue in Light (about 4:1 on the Light panel). The gauge, accent text, checkboxes, switches and Safe items all take this single color; never a
    // per-control shade. The window resolves the chosen Dark swatch itself (MainWindow.AccentColor).
    public static string Accent(bool light) => light ? "#4C7BC8" : AccentPalette.All[0].Color;
    // The ONE red in Hush: the Clean button. Advanced items and every other red element use exactly this value (never a second red).
    // The literals below and in App.xaml are the only red-family colors allowed in the source; a test scans for any other.
    public static string Danger(bool light) => light ? "#B54428" : "#FF795C";
    // Review is orange and Advanced is the Clean button's red, so the three risk levels read apart at a glance.
    public static string Color(SafetyLevel level) => level switch { SafetyLevel.Safe => Good(false), SafetyLevel.Review => "#FFA24D", _ => Danger(false) };
    // The calm green: Safe items on the Review page and positive status ("You're up to date"). Green, amber, red read as one traffic-light set.
    public static string Good(bool light) => light ? "#2E8B57" : "#5CC98F";
    // Safe is the calm green, Review is amber (a golden amber in Light so it stays clearly apart from the red), Advanced is the Clean red.
    public static string Text(SafetyLevel level, bool light) => level switch
    {
        SafetyLevel.Safe => Good(light),
        SafetyLevel.Review => light ? "#E8A02E" : "#FFA24D",
        _ => Danger(light)
    };
    public static string Label(SafetyLevel level) => level switch { SafetyLevel.Safe => "Safe", SafetyLevel.Review => "Review recommended", _ => "Advanced · explicit consent" };
}
