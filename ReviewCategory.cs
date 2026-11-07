using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hush;

public sealed class ReviewCategory(string id, string title, SafetyLevel safety, string description, string summary = "") : INotifyPropertyChanged
{
    // Plain-language sentence shown under the title; Description stays as the technical tooltip.
    public string Summary { get; } = summary;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public SafetyLevel Safety { get; } = safety;
    private bool light;
    public string TextAccent => SafetyPalette.Text(Safety, light);
    internal void SetTheme(bool value) { light = value; Notify(nameof(TextAccent)); foreach (var file in Files) file.SetTheme(value); }
    public string SafetyLabel => SafetyPalette.Label(Safety);
    public string Description { get; } = description;
    public ObservableCollection<CleanupItem> Files { get; } = [];
    public string CountLabel => $"{(Files.Count == 1 ? "1 item" : $"{Files.Count:N0} items")} · {SafetyLabel}";
    public string TotalLabel => Id == "Registry" ? (Files.Count == 1 ? "1 entry" : $"{Files.Count:N0} entries") : Sizes.Format(Files.Sum(file => file.Bytes));
    private bool expanded, selecting;
    internal Action<bool>? SelectionBatchState { get; set; }
    public bool Expanded { get => expanded; set { if (expanded == value) return; expanded = value; Notify(); } }
    // A running count keeps Selected O(1): a category can hold 100,000+ files and every change re-reads this property.
    private int selectedCount;
    public bool? Selected
    {
        get => Files.Count == 0 || selectedCount == 0 ? false : selectedCount == Files.Count ? true : null;
        set
        {
            if (selecting) return;
            selecting = true;
            SelectionBatchState?.Invoke(true);
            try { foreach (var file in Files) file.Selected = value == true; }
            finally { selecting = false; selectedCount = Files.Count(file => file.Selected); Notify(); SelectionBatchState?.Invoke(false); }
        }
    }
    public bool HasFiles => Files.Count > 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Replace(IEnumerable<CleanupItem> files)
    {
        foreach (var file in Files) file.PropertyChanged -= FileChanged;
        Files.Clear();
        foreach (var file in files) { file.SetTheme(light); Files.Add(file); file.PropertyChanged += FileChanged; }
        selectedCount = Files.Count(file => file.Selected);
        Notify(nameof(CountLabel)); Notify(nameof(TotalLabel)); Notify(nameof(Selected)); Notify(nameof(HasFiles));
    }
    private void FileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CleanupItem.Selected) || selecting) return;
        selectedCount += ((CleanupItem)sender!).Selected ? 1 : -1;
        Notify(nameof(Selected));
    }
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    // The single catalog of review categories, in display order. Only categories with detected items are shown.
    // Description is the technical wording (tooltip); Summary is one plain sentence for people who don't know computers well.
    internal sealed record Definition(string Id, string Title, SafetyLevel Safety, string Description, string Summary);
    internal static readonly Definition[] Definitions =
    [
        new("Temp", "Temp Files", SafetyLevel.Safe, "Old temporary files from Windows, Store apps and other applications; active, recent and protected files are skipped",
            "Scraps that Windows and apps leave behind while they work and never tidy up. Safe to remove."),
        new("Cache", "Browser & App Caches", SafetyLevel.Safe, "Rebuildable caches of browsers, chat, editor and launcher apps; the application must be closed",
            "Saved copies of web pages and app data that help things open quicker. They quietly pile up and refill by themselves."),
        new("DirectX", "Shader Cache", SafetyLevel.Safe, "Rebuildable DirectX, NVIDIA, AMD and Intel shader caches older than seven days",
            "Saved graphics data for games and apps. It is rebuilt automatically, so the next launch may take a moment longer."),
        new("Thumbnails", "Thumbnail Cache", SafetyLevel.Safe, "Explorer thumbnail and icon caches that Windows rebuilds; files in use are skipped",
            "The small preview pictures Windows keeps for your files and folders. It makes new ones whenever it needs them."),
        new("DevCache", "Developer & Creator Caches", SafetyLevel.Review, "NuGet, npm, pip, Yarn, IDE and Adobe media caches; rebuildable, but re-downloading or re-indexing takes time",
            "Downloads and work files kept by programming and design tools. Nothing of yours, but they download again the next time you use those tools."),
        new("Junk", "Windows Error Reports", SafetyLevel.Review, "Old Windows Error Reporting archives and queues; review before removing diagnostics",
            "Reports Windows saved when a program had a problem. Only useful if someone is helping you fix that problem."),
        new("Crash", "Crash Reports & Dumps", SafetyLevel.Review, "Old crash and memory dumps; may be useful for troubleshooting",
            "Snapshots saved when a program or Windows stopped working. They can be large and are only needed for troubleshooting."),
        new("SystemLogs", "Windows Logs", SafetyLevel.Review, "Old servicing, DISM and diagnostic-trace logs; useful only for troubleshooting, usually needs administrator rights",
            "Diary files where Windows notes its updates and repairs. Only useful when diagnosing a problem."),
        new("Update", "Windows Update Files", SafetyLevel.Review, "Downloaded update packages and update logs Windows can fetch again; usually needs administrator rights",
            "Installation files Windows already used for its updates. Windows can download them again if it ever needs them."),
        new("Delivery", "Delivery Optimization", SafetyLevel.Review, "Cached Windows and Store downloads that are rebuilt on demand; usually needs administrator rights",
            "Update and app downloads Windows keeps so it can share them with other PCs. They can be fetched again."),
        new("Setup", "Setup & Upgrade Logs", SafetyLevel.Review, "Old Windows setup and upgrade logs; only useful for diagnosing a failed upgrade",
            "Notes left behind when Windows was installed or upgraded. Only useful for finding out why an upgrade failed."),
        new("Installer", "Installer Leftovers", SafetyLevel.Review, "Extracted graphics-driver installers left at the drive root; usually needs administrator rights",
            "Folders a graphics-driver installer left behind after installing. Your installed driver is not affected."),
        new("Traces", "Usage Traces", SafetyLevel.Review, "Recent-file shortcuts that list what you opened lately; your files are not touched, only the history is removed",
            "The list of files you opened recently. Only the list is cleared; your files stay exactly where they are."),
        new("Shortcuts", "Broken Shortcuts", SafetyLevel.Review, "Old local shortcuts with a verified missing application and installation folder; opt-in",
            "Shortcuts that point to programs which are no longer installed, so they no longer work."),
        new("RecycleBin", "Recycle Bin", SafetyLevel.Caution, "Files you deleted earlier. Emptying is permanent and covers every drive; opt-in",
            "Files you deleted earlier. Emptying it removes them for good and cannot be undone."),
        new("Registry", "Registry Leftovers", SafetyLevel.Caution, "Opt-in missing current-user App Paths; exported before removal",
            "Leftover Windows settings entries from programs that were removed. A backup copy is saved first."),
    ];
    internal static ObservableCollection<ReviewCategory> Create() =>
        new(Definitions.Select(d => new ReviewCategory(d.Id, d.Title, d.Safety, d.Description, d.Summary)));
    internal static SafetyLevel SafetyOf(string id) => Definitions.FirstOrDefault(d => d.Id == id)?.Safety ?? SafetyLevel.Safe;
    internal static string CategoryFor(CleanupItem file) => CategoryFor(file.Kind, file.Group, file.CategoryId);
    internal static string CategoryFor(CleanupKind kind, string group, string? categoryId) => categoryId ?? kind switch
    {
        CleanupKind.Registry => "Registry", CleanupKind.Shortcut => "Shortcuts", CleanupKind.Junk => "Junk", CleanupKind.RecycleBin => "RecycleBin",
        _ => group switch { "DirectX cache" => "DirectX", "Crash reports" => "Crash", _ => kind == CleanupKind.Cache ? "Cache" : "Temp" }
    };
}
