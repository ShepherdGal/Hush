using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hush;

public enum CleanupKind { Temp, Cache, Registry, Junk, Shortcut, RecycleBin }
public enum SafetyLevel { Safe, Review, Caution }

public sealed class CleanupItem : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public required string Root { get; init; }
    public required string Group { get; init; }
    public CleanupKind Kind { get; init; }
    public long Bytes { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string? RegistryExecutable { get; init; }
    public string? RegistrySnapshot { get; init; }
    public string? ShortcutTarget { get; init; }
    public string? ShortcutHash { get; init; }
    // The review category (and so the risk level) comes from the target that found the item; older items fall back to Kind/Group.
    public string? CategoryId { get; init; }
    public long ItemCount { get; init; }
    public string? Detail { get; init; }
    public SafetyLevel Safety => ReviewCategory.SafetyOf(ReviewCategory.CategoryFor(this));
    private bool light;
    public string SelectionAccent => SafetyPalette.Text(Safety, light);
    internal void SetTheme(bool value) { light = value; Changed(nameof(SelectionAccent)); }
    public string Name => Kind == CleanupKind.RecycleBin ? Path : System.IO.Path.GetFileName(Path);
    public string DirectoryLabel => Detail ?? System.IO.Path.GetDirectoryName(Path) ?? Path;
    public string GroupLabel => Group.Replace("Brave-Browser", "Brave", StringComparison.Ordinal);
    public string SizeLabel => Kind == CleanupKind.Registry ? "REGISTRY" : Sizes.Format(Bytes);
    private bool selected = true;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Changed(); } }
    private string outcome = "";
    public string Outcome { get => outcome; set { outcome = value; Changed(); } }
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
// and permission errors there are not reported as skipped locations.
internal sealed record CleanupTarget(string Root, string Group, CleanupKind Kind, TimeSpan MinimumAge, string? Pattern = null, string[]? Processes = null, bool Recursive = true,
    string? Category = null, bool DeletableOnly = false);
internal sealed record ScanProgress(string Location, int Examined, int Found, long Bytes, double Fraction, int RegistryEntries = 0)
{
    public int FilesFound => Math.Max(0, Found - RegistryEntries);
}
internal sealed record ScanResult(List<CleanupItem> Items, int Inaccessible, int BusyCaches, bool Stopped, bool Limited);
internal sealed record CleanupResult(int Deleted, int Skipped, long Bytes, bool Stopped, string LogPath);

public static class SafetyPalette
{
    // The one primary accent per theme: the aqua Cyan in Dark, a softer, readable blue in Light (about 4:1 on the Light panel).
    // The gauge, accent text, checkboxes, switches and Safe items all take this single color; never a per-control shade.
    public static string Accent(bool light) => light ? "#4C7BC8" : "#3CCFD9";
    public static string Color(SafetyLevel level) => level == SafetyLevel.Safe ? Accent(false) : "#FF795C";
    // Safe uses the theme's primary accent; only the red-orange family darkens in Light.
    public static string Text(SafetyLevel level, bool light) => level == SafetyLevel.Safe ? Accent(light) : light ? "#B54428" : "#FF795C";
    public static string Label(SafetyLevel level) => level switch { SafetyLevel.Safe => "Safe", SafetyLevel.Review => "Review recommended", _ => "Requires caution" };
}
