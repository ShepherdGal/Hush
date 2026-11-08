using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hush;

// An ObservableCollection that can swap its whole content with one Reset notification. Adding 100,000+ files one by one raised
// that many CollectionChanged events and froze the window; a scan result now replaces a category in a single step.
public sealed class BulkCollection<T> : ObservableCollection<T>
{
    internal void ReplaceAll(IReadOnlyCollection<T> values)
    {
        Items.Clear();
        if (Items is List<T> list) { list.Capacity = Math.Max(list.Capacity, values.Count); list.AddRange(values); }
        else foreach (var value in values) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class ReviewCategory(string id, string title, SafetyLevel safety, string description, string summary = "") : INotifyPropertyChanged
{
    // Plain-language sentence shown under the title; Description stays as the technical tooltip.
    public string Summary { get; } = summary;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public SafetyLevel Safety { get; } = safety;
    internal bool IsLight { get; private set; }
    public string TextAccent => SafetyPalette.Text(Safety, IsLight);
    internal void SetTheme(bool value)
    {
        IsLight = value; Notify(nameof(TextAccent));
        foreach (var file in Files) file.ThemeChanged();
    }
    public string SafetyLabel => SafetyPalette.Label(Safety);
    public string Description { get; } = description;
    public BulkCollection<CleanupItem> Files { get; } = [];
    public string CountLabel => $"{(Files.Count == 1 ? "1 item" : $"{Files.Count:N0} items")} · {SafetyLabel}";
    // Totals are computed once per scan result (Replace), never by looping the files in a property getter.
    public long TotalBytes { get; private set; }
    private bool unsized;
    public string TotalLabel => Id == "Registry" ? (Files.Count == 1 ? "1 entry" : $"{Files.Count:N0} entries")
        : CountedByItems.Contains(Id) ? (Files.Count == 1 ? "1 Item" : $"{Files.Count:N0} Items")
        : unsized ? "By Windows" : Sizes.Format(TotalBytes);
    private bool expanded, selecting;
    internal Action<bool>? SelectionBatchState { get; set; }
    // Raised for every single-item selection change so the window can update its totals from counters, not by scanning all files.
    internal event Action? SelectionTotalsChanged;
    public bool Expanded { get => expanded; set { if (expanded == value) return; expanded = value; Notify(); } }
    // Running counts keep Selected O(1): a category can hold hundreds of thousands of files and every change re-reads this property.
    public int SelectedCount { get; private set; }
    public long SelectedBytes { get; private set; }
    public bool? Selected
    {
        get => Files.Count == 0 || SelectedCount == 0 ? false : SelectedCount == Files.Count ? true : null;
        set
        {
            if (selecting) return;
            selecting = true;
            SelectionBatchState?.Invoke(true);
            try { bool on = value == true; foreach (var file in Files) file.Selected = on; }
            finally
            {
                selecting = false; RecountSelection(); Notify();
                SelectionBatchState?.Invoke(false); SelectionTotalsChanged?.Invoke();
            }
        }
    }
    public bool HasFiles => Files.Count > 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Replace(IReadOnlyCollection<CleanupItem> files)
    {
        foreach (var file in Files) file.Owner = null;
        foreach (var file in files) file.Owner = this;
        Files.ReplaceAll(files);
        TotalBytes = 0; bool anySized = false, anyTool = false;
        foreach (var file in files) { TotalBytes += file.Bytes; anySized |= file.Bytes > 0; anyTool |= file.Kind == CleanupKind.SystemTool; }
        unsized = anyTool && !anySized;
        RecountSelection();
        Notify(nameof(CountLabel)); Notify(nameof(TotalLabel)); Notify(nameof(Selected)); Notify(nameof(HasFiles));
    }
    private void RecountSelection()
    {
        int count = 0; long bytes = 0;
        foreach (var file in Files) if (file.Selected) { count++; bytes += file.Bytes; }
        SelectedCount = count; SelectedBytes = bytes;
    }
    internal void ItemSelectionChanged(CleanupItem item)
    {
        if (selecting) return;
        SelectedCount += item.Selected ? 1 : -1;
        SelectedBytes += item.Selected ? item.Bytes : -item.Bytes;
        Notify(nameof(Selected));
        SelectionTotalsChanged?.Invoke();
    }
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));

    // ----- Catalog -----
    // The single catalog of review categories, in display order, with the section (GroupId) each belongs to. The Review page lists them flat. Only categories with detected items are shown.
    // Description is the technical wording (tooltip); Summary is one plain sentence for people who don't know computers well.
    internal sealed record Definition(string Id, string Title, SafetyLevel Safety, string Description, string Summary, string GroupId);
    internal sealed record GroupDefinition(string Id, string Title, string Summary);
    internal static readonly GroupDefinition[] GroupDefinitions =
    [
        new("Recommended", "Recommended", "Everyday leftovers from Windows itself."),
        new("System", "Additional system items", "Larger or less common items Windows keeps."),
        new("Apps", "Application cleaning", "Caches kept by the browsers and apps installed on this PC."),
        new("Privacy", "Computer usage traces", "Records of what you opened and did. Cleared only when you pick them."),
    ];
    internal static readonly Definition[] Definitions =
    [
        // ----- Recommended -----
        new("Temp", "Temporary Files", SafetyLevel.Safe, "Old temporary files from Windows, Store apps and other applications; active, recent and protected files are skipped",
            "Scraps that Windows and apps leave behind while they work and never tidy up. Safe to remove.", "Recommended"),
        new("Thumbnails", "Thumbnail Cache", SafetyLevel.Safe, "Explorer thumbnail and icon caches that Windows rebuilds; files in use are skipped",
            "The small preview pictures Windows keeps for your files and folders. It makes new ones whenever it needs them.", "Recommended"),
        new("InternetCache", "Temporary Internet Files", SafetyLevel.Review, "Legacy Internet Explorer and WebView cache folders (IE, Content.IE5) only; Outlook and Word attachment copies are never touched",
            "Old copies of web pages saved by Windows components. They are downloaded again when needed.", "Recommended"),
        new("Junk", "Windows Error Reporting", SafetyLevel.Review, "Old Windows Error Reporting archives and queues; review before removing diagnostics",
            "Reports Windows saved when a program had a problem. Only useful if someone is helping you fix that problem.", "Recommended"),
        new("SystemLogs", "Windows Log Files", SafetyLevel.Review, "Old servicing, DISM, driver-install and diagnostic logs; useful only for troubleshooting, usually needs administrator rights. Security and event logs are never touched",
            "Diary files where Windows notes its updates and repairs. Only useful when diagnosing a problem.", "Recommended"),
        new("Update", "Windows Update Cleanup", SafetyLevel.Review, "Downloaded update packages and update and orchestrator logs Windows can fetch again; usually needs administrator rights",
            "Installation files Windows already used for its updates. Windows can download them again if it ever needs them.", "Recommended"),
        new("Delivery", "Delivery Optimization Files", SafetyLevel.Review, "Cached Windows and Store downloads that are rebuilt on demand; usually needs administrator rights",
            "Update and app downloads Windows keeps so it can share them with other PCs. They can be fetched again.", "Recommended"),
        new("Setup", "Windows Setup & Upgrade Logs", SafetyLevel.Review, "Old Windows setup and upgrade logs; only useful for diagnosing a failed upgrade",
            "Notes left behind when Windows was installed or upgraded. Only useful for finding out why an upgrade failed.", "Recommended"),
        new("Defender", "Microsoft Defender Antivirus", SafetyLevel.Review, "Defender's own diagnostic support logs only (MPLog, MpCmdRun and trace files); protection history, quarantine, scan history, definitions and settings are never touched; usually needs administrator rights",
            "Technical log files Microsoft Defender writes about its own work. Your protection, quarantine and history stay exactly as they are.", "Recommended"),
        // ----- Additional system items -----
        new("DirectX", "DirectX Shader Cache", SafetyLevel.Review, "Rebuildable DirectX shader caches older than seven days; the next game or app launch may take longer while they refill",
            "Saved graphics data for games and apps. It is rebuilt automatically, so the next launch may take a moment longer.", "System"),
        new("NvidiaShader", "NVIDIA Shader Cache", SafetyLevel.Review, "NVIDIA DXCache, GLCache and ComputeCache; rebuilt by the driver on demand",
            "Graphics data the NVIDIA driver saved for your games. It refills by itself, so games may stutter briefly the first time afterwards.", "System"),
        new("GpuShader", "AMD & Intel Shader Caches", SafetyLevel.Review, "AMD and Intel graphics driver shader caches; rebuilt on demand",
            "Graphics data your AMD or Intel driver saved for games and apps. It refills by itself.", "System"),
        new("Crash", "Old Crash Reports & Memory Dumps", SafetyLevel.Review, "Old crash and memory dumps; may be useful for troubleshooting",
            "Snapshots saved when a program or Windows stopped working. They can be large and are only needed for troubleshooting.", "System"),
        new("WindowsOld", "Previous Windows Installations", SafetyLevel.Advanced, "Windows.old, removed through Microsoft's Disk Cleanup handler. Irreversible: you can no longer go back to the previous Windows version",
            "The old copy of Windows kept after an upgrade so you could go back. Removing it frees a lot of space but you can no longer roll back.", "System"),
        new("ComponentStore", "Windows Update Component Cleanup", SafetyLevel.Advanced, "DISM /StartComponentCleanup: removes superseded component versions from the WinSxS store. Irreversible: installed updates can then no longer be uninstalled",
            "Windows removes old versions of its own parts that updates replaced. Windows decides what goes; installed updates can no longer be uninstalled.", "System"),
        new("RecycleBin", "Recycle Bin", SafetyLevel.Advanced, "Files you deleted earlier. Emptying is permanent and covers every drive; opt-in",
            "Files you deleted earlier. Emptying it removes them for good and cannot be undone.", "System"),
        new("Installer", "Old Installation Leftovers", SafetyLevel.Review, "Extracted graphics-driver installers at the drive root and old CHKDSK file fragments; usually needs administrator rights",
            "Folders an installer left behind after installing. Your installed programs and drivers are not affected.", "System"),
        new("Shortcuts", "Broken Shortcuts", SafetyLevel.Review, "Old local shortcuts with a verified missing application and installation folder; opt-in",
            "Shortcuts that point to programs which are no longer installed, so they no longer work.", "System"),
        new("Registry", "Registry Leftovers", SafetyLevel.Advanced, "Opt-in missing current-user App Paths; exported before removal",
            "Leftover Windows settings entries from programs that were removed. A backup copy is saved first.", "System"),
        // ----- Application cleaning -----
        new("BrowserEdge", "Microsoft Edge", SafetyLevel.Review, "Edge page, code and GPU caches; passwords, cookies and saved sessions are never included. Edge must be closed",
            "Saved copies of web pages that help Edge open sites quicker. Your passwords, logins and open tabs are not touched.", "Apps"),
        new("BrowserFirefox", "Mozilla Firefox", SafetyLevel.Review, "Firefox cache2, startup cache and thumbnails; passwords, cookies, bookmarks and sessions are never included. Firefox must be closed",
            "Saved copies of web pages that help Firefox open sites quicker. Your passwords, bookmarks and logins are not touched.", "Apps"),
        new("BrowserChrome", "Google Chrome", SafetyLevel.Review, "Chrome page, code and GPU caches; passwords, cookies and saved sessions are never included. Chrome must be closed",
            "Saved copies of web pages that help Chrome open sites quicker. Your passwords, logins and open tabs are not touched.", "Apps"),
        new("BrowserOther", "Other Browsers", SafetyLevel.Review, "Brave, Vivaldi, Opera and other Chromium browser caches; passwords, cookies and saved sessions are never included. The browser must be closed",
            "Saved copies of web pages kept by your other browsers. Passwords and logins are not touched.", "Apps"),
        new("BrowserLogs", "Browser Log Files", SafetyLevel.Review, "Crash reports and usage-metrics files kept by Edge, Chrome, Firefox and other browsers; passwords, cookies, history and sessions are never included. The browser must be closed",
            "Crash reports and small technical logs your browsers keep. Your passwords, logins and open tabs are not touched.", "Apps"),
        new("BrowserUpdates", "Browser Update Files", SafetyLevel.Review, "Downloaded update packages and update logs of Firefox; the installed browser is not affected. Firefox must be closed",
            "Update downloads Firefox already used. The browser itself stays as it is.", "Apps"),
        new("NvidiaApp", "NVIDIA Application Files", SafetyLevel.Review, "Driver installer downloads and driver log files kept by NVIDIA; the installed driver is not affected",
            "Old driver installers and small log files NVIDIA left behind. Your installed driver stays as it is.", "Apps"),
        new("DevCache", "NuGet & Developer Caches", SafetyLevel.Review, "NuGet, npm, pip, Poetry, Composer, Yarn, Go, Visual Studio, IDE and Adobe media caches; rebuildable, but re-downloading or re-indexing takes time",
            "Downloads and work files kept by programming and design tools. Nothing of yours, but they download again the next time you use those tools.", "Apps"),
        new("Cache", "Other Application Caches", SafetyLevel.Review, "Rebuildable caches of chat, editor, launcher and Store apps; the application must be closed",
            "Saved data that chat, editing and game apps keep to open quicker. They quietly pile up and refill by themselves.", "Apps"),
        // ----- Computer usage traces -----
        new("RecentAccess", "Windows Recent Access", SafetyLevel.Review, "Recent-file shortcuts in the Windows Recent folder; your files are not touched, only the history is removed. Jump lists and pinned items are never touched",
            "The list of files you opened lately. Only the list is cleared; your files stay exactly where they are.", "Privacy"),
        new("RecentDocs", "Recent Documents History", SafetyLevel.Review, "Office recent-document shortcuts; the documents themselves are not touched",
            "The lists Office keeps of the documents you used recently. The documents stay where they are.", "Privacy"),
        new("Clipboard", "Windows Clipboard History", SafetyLevel.Review, "Clears the Windows clipboard history through the Windows clipboard API; pinned clips are kept",
            "Things you copied earlier that Windows remembers (Win+V). Clips you pinned stay.", "Privacy"),
        new("EventTrace", "Windows Event Trace Logs", SafetyLevel.Review, "Old diagnostic .etl trace files; the Windows Event Log and security logs are never touched",
            "Technical traces Windows records about what it was doing. Security and event logs are left alone.", "Privacy"),
        new("UsageHistory", "Application Usage History", SafetyLevel.Review, "Usage logs of the .NET runtime that list which programs ran",
            "Small logs that record which programs you started.", "Privacy"),
        new("BrowserHistory", "Browser History", SafetyLevel.Review, "Chromium history, visited links and top sites. Bookmarks, passwords, cookies and saved sessions are never included; the browser must be closed",
            "The list of sites you visited. Bookmarks, passwords and logins are not touched.", "Privacy"),
        new("BrowserCookies", "Browser Cookies & Site Data", SafetyLevel.Advanced, "Chromium and Firefox cookie databases. You will be signed out of websites. Passwords and saved sessions are never included; the browser must be closed",
            "The small files websites use to remember you. Removing them signs you out of websites.", "Privacy"),
    ];
    // Chosen only in Settings > Browser data (with a red mark and a warning), never part of a normal scan or Quick Boost.
    internal static readonly HashSet<string> SettingsOnly = ["BrowserHistory", "BrowserCookies"];
    // Privacy traces are counted in items (a few hundred bytes each says nothing useful), as Microsoft PC Manager does.
    internal static readonly HashSet<string> CountedByItems = ["RecentAccess", "RecentDocs", "EventTrace", "UsageHistory", "Clipboard", "BrowserHistory", "BrowserCookies"];
    private static readonly Dictionary<string, SafetyLevel> safetyById = Definitions.ToDictionary(d => d.Id, d => d.Safety);
    internal static ObservableCollection<ReviewCategory> Create()
    {
        return new ObservableCollection<ReviewCategory>(Definitions.Select(d => new ReviewCategory(d.Id, d.Title, d.Safety, d.Description, d.Summary)));
    }
    internal static SafetyLevel SafetyOf(string id) => safetyById.TryGetValue(id, out var safety) ? safety : SafetyLevel.Safe;
    internal static string CategoryFor(CleanupItem file) => CategoryFor(file.Kind, file.Group, file.CategoryId);
    internal static string CategoryFor(CleanupKind kind, string group, string? categoryId) => categoryId ?? kind switch
    {
        CleanupKind.Registry => "Registry", CleanupKind.Shortcut => "Shortcuts", CleanupKind.Junk => "Junk", CleanupKind.RecycleBin => "RecycleBin",
        _ => group switch { "DirectX cache" => "DirectX", "Crash reports" => "Crash", _ => kind == CleanupKind.Cache ? "Cache" : "Temp" }
    };
}
