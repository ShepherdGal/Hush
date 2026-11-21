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

// A sub-heading row inside a category's list (owner, November 11, 2026): when one card holds files of several apps or locations ("User temp" and "Windows temp",
// several apps' caches) each app gets its own heading with its count and size, so the list reads as groups.
// Per-app rows (owner, November 14, 2026: "checkbox + collapse"): the heading has its own checkbox (select or clear that app's files,
// mixed when partly selected) and starts closed with a chevron; its files show when it is opened. The selected count is a running count kept by the category
// (never a loop over the files in a getter).
public sealed class ReviewSubHeading(ReviewCategory category, string title, List<CleanupItem> files, long bytes) : INotifyPropertyChanged
{
    public ReviewCategory Category { get; } = category;
    public string Title { get; } = title;
    internal List<CleanupItem> Files { get; } = files;
    public string Detail { get; } = $"{(files.Count == 1 ? "1 item" : $"{files.Count:N0} items")} · {Sizes.Format(bytes)}";
    public string SelectName => "Select all of " + Title;
    internal int SelectedCount { get; set; }
    private bool open;
    public bool IsOpen { get => open; set { if (open == value) return; open = value; Changed(); Category.ShowRows(); } }
    public bool? Selected
    {
        get => SelectedCount == 0 ? false : SelectedCount == Files.Count ? true : null;
        set => Category.SelectGroup(this, value == true);
    }
    public string SelectionAccent => Category.TextAccent;
    internal void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    public event PropertyChangedEventHandler? PropertyChanged;
}

// Picks the template of a row in a category's list: a file or a sub-heading.
public sealed class ReviewRowTemplateSelector : System.Windows.Controls.DataTemplateSelector
{
    public System.Windows.DataTemplate? File { get; set; }
    public System.Windows.DataTemplate? Heading { get; set; }
    public override System.Windows.DataTemplate? SelectTemplate(object item, System.Windows.DependencyObject container) => item is ReviewSubHeading ? Heading : File;
}

public sealed class ReviewCategory(string id, string title, SafetyLevel safety, string description, string summary = "") : INotifyPropertyChanged
{
    // Plain-language sentence shown under the title; Description stays as the technical tooltip.
    public string Summary { get; } = summary;
    public string Id { get; } = id;
    // Position in the catalog (Definitions order inside its risk level); the tie-breaker when two categories hold the same amount.
    public int CatalogOrder { get; internal set; }
    public string Title { get; } = title;
    public SafetyLevel Safety { get; } = safety;
    internal bool IsLight { get; private set; }
    public string TextAccent => SafetyPalette.Text(Safety, IsLight);
    internal void SetTheme(bool value)
    {
        IsLight = value; Notify(nameof(TextAccent));
        foreach (var heading in headings.Values) heading.Changed(nameof(ReviewSubHeading.SelectionAccent));
        foreach (var file in Files) file.ThemeChanged();
    }
    public string SafetyLabel => SafetyPalette.Label(Safety);
    public string Description { get; } = description;
    public BulkCollection<CleanupItem> Files { get; } = [];
    // What the list shows: the files, with a sub-heading in front of each app's files when the card holds more than one (built once per scan result); the files of
    // a closed sub-heading are left out (ShowRows), so a card with several apps opens as a short list of apps.
    public BulkCollection<object> Rows { get; } = [];
    private List<object> allRows = [];
    private Dictionary<string, ReviewSubHeading> headings = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyCollection<ReviewSubHeading> SubHeadings => headings.Values;
    public string CountLabel => $"{(Files.Count == 1 ? "1 item" : $"{Files.Count:N0} items")} · {SafetyLabel}";
    // Totals are computed once per scan result (Replace), never by looping the files in a property getter.
    public long TotalBytes { get; private set; }
    private bool unsized;
    public string TotalLabel => RegistryIds.Contains(Id) ? (Files.Count == 1 ? "1 entry" : $"{Files.Count:N0} entries")
        : CountedByItems.Contains(Id) ? (Files.Count == 1 ? "1 Item" : $"{Files.Count:N0} Items")
        : unsized ? "—" : Sizes.Format(TotalBytes);
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
    // Some of its items need administrator mode (the all-users registry rows): the card shows a lock and "Restart as administrator" (owner, November 19, 2026).
    public bool HasLocked { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Replace(IReadOnlyCollection<CleanupItem> files)
    {
        foreach (var file in Files) file.Owner = null;
        foreach (var file in files) file.Owner = this;
        Files.ReplaceAll(files);
        allRows = BuildRows(files);
        ShowRows();
        TotalBytes = 0; bool anySized = false, anyTool = false;
        bool anyLocked = false;
        foreach (var file in files) { TotalBytes += file.Bytes; anySized |= file.Bytes > 0; anyTool |= file.Kind == CleanupKind.SystemTool; anyLocked |= file.Locked; }
        unsized = anyTool && !anySized;
        HasLocked = anyLocked;
        RecountSelection();
        Notify(nameof(CountLabel)); Notify(nameof(TotalBytes)); Notify(nameof(TotalLabel)); Notify(nameof(Selected)); Notify(nameof(HasFiles)); Notify(nameof(HasLocked));
    }
    private List<object> BuildRows(IReadOnlyCollection<CleanupItem> files)
    {
        var rows = new List<object>(files.Count + 4);
        var groups = new Dictionary<string, (List<CleanupItem> Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!groups.TryGetValue(file.Group, out var group)) groups[file.Group] = group = ([], 0);
            group.Files.Add(file);
            groups[file.Group] = (group.Files, group.Bytes + file.Bytes);
        }
        headings = new(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var file in files)
        {
            if (groups.Count > 1 && !string.Equals(current, file.Group, StringComparison.OrdinalIgnoreCase))
            {
                current = file.Group;
                var group = groups[file.Group];
                var heading = new ReviewSubHeading(this, file.Group, group.Files, group.Bytes);
                if (!headings.ContainsKey(file.Group)) headings[file.Group] = heading;
                rows.Add(heading);
            }
            rows.Add(file);
        }
        return rows;
    }

    // The visible rows: every sub-heading, and the files of the open ones (all files when the card holds a single app). One Reset per change.
    internal void ShowRows()
    {
        if (headings.Count == 0) { Rows.ReplaceAll(allRows); return; }
        var visible = new List<object>(allRows.Count);
        ReviewSubHeading? owner = null;
        foreach (var row in allRows)
        {
            if (row is ReviewSubHeading heading) { owner = heading; visible.Add(heading); }
            else if (owner?.IsOpen != false) visible.Add(row);
        }
        Rows.ReplaceAll(visible);
    }

    // The sub-heading's checkbox: selects or clears that app's files in one batch (like the card's own checkbox).
    internal void SelectGroup(ReviewSubHeading heading, bool on)
    {
        if (selecting) return;
        selecting = true;
        SelectionBatchState?.Invoke(true);
        try { foreach (var file in heading.Files) file.Selected = on; }
        finally
        {
            selecting = false; RecountSelection(); Notify(nameof(Selected));
            SelectionBatchState?.Invoke(false); SelectionTotalsChanged?.Invoke();
        }
    }

    private void RecountSelection()
    {
        int count = 0; long bytes = 0;
        foreach (var heading in headings.Values) heading.SelectedCount = 0;
        foreach (var file in Files)
            if (file.Selected)
            {
                count++; bytes += file.Bytes;
                if (headings.Count > 0 && headings.TryGetValue(file.Group, out var heading)) heading.SelectedCount++;
            }
        SelectedCount = count; SelectedBytes = bytes;
        foreach (var heading in headings.Values) heading.Changed(nameof(ReviewSubHeading.Selected));
    }
    internal void ItemSelectionChanged(CleanupItem item)
    {
        if (selecting) return;
        SelectedCount += item.Selected ? 1 : -1;
        SelectedBytes += item.Selected ? item.Bytes : -item.Bytes;
        if (headings.Count > 0 && headings.TryGetValue(item.Group, out var heading)) { heading.SelectedCount += item.Selected ? 1 : -1; heading.Changed(nameof(ReviewSubHeading.Selected)); }
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
        new("WindowsOld", "Previous Windows Installations", SafetyLevel.Advanced, "Windows.old, removed by Hush itself (administrator mode; Hush takes ownership of the old files). Includes the old Users folder. Irreversible: you can no longer go back to the previous Windows version",
            "The old copy of Windows kept after an upgrade so you could go back, including the old Users folder. Removing it frees a lot of space but you can no longer roll back.", "System"),
        new("RecycleBin", "Recycle Bin", SafetyLevel.Advanced, "Files you deleted earlier. Emptying is permanent and covers every drive; opt-in",
            "Files you deleted earlier. Emptying it removes them for good and cannot be undone.", "System"),
        new("Installer", "Old Installation Leftovers", SafetyLevel.Review, "Extracted graphics-driver installers at the drive root and old CHKDSK file fragments; usually needs administrator rights",
            "Folders an installer left behind after installing. Your installed programs and drivers are not affected.", "System"),
        new("Prefetch", "Prefetch Files", SafetyLevel.Review, "Windows' prefetch hints (.pf files in the Prefetch folder) about which programs you start; Windows rebuilds them and the next launch of each program can be a little slower; usually needs administrator rights",
            "Notes Windows keeps about the programs you open, to start them a little faster. Windows makes new ones as you go.", "System"),
        new("Shortcuts", "Broken Shortcuts", SafetyLevel.Review, "Old local shortcuts with a verified missing application and installation folder; opt-in",
            "Shortcuts that point to programs which are no longer installed, so they no longer work.", "System"),
        new("Registry", "Registry Leftovers", SafetyLevel.Advanced, "Opt-in missing current-user App Paths; exported before removal",
            "Leftover Windows settings entries from programs that were removed. A backup copy is saved first.", "System"),
        // ----- Registry issues (Pro, Advanced; owner, November 14, 2026). Only entries whose file is certainly gone; a .reg backup first. -----
        new("RegMuiCache", "Program Names of Removed Programs", SafetyLevel.Advanced, "Windows' cache of program names (MuiCache) for programs whose file no longer exists; exported before removal",
            "Names Windows remembered for programs that are no longer on this PC. A backup copy is saved first.", "System"),
        new("RegSounds", "Sounds That Point to Missing Files", SafetyLevel.Advanced, "Sound events whose sound file no longer exists; the event becomes silent; exported before removal",
            "Sound settings that point to sound files which are gone, so they cannot play anyway. A backup copy is saved first.", "System"),
        new("RegComClasses", "ActiveX and COM Leftovers", SafetyLevel.Advanced, "This account's ActiveX/COM classes whose program file and folder no longer exist; exported before removal",
            "Registrations left by programs that were removed: their files and folders are gone. A backup copy is saved first.", "System"),
        new("RegFileTypes", "File Types of Removed Programs", SafetyLevel.Advanced, "This account's file types whose program type no longer exists anywhere; exported before removal",
            "File types that point to a program that is no longer registered. A backup copy is saved first.", "System"),
        // Phase B (owner, November 19, 2026): more registry issues, in this account and for all users (those need administrator mode; locked without it).
        new("RegUninstall", "Uninstall Entries of Removed Programs", SafetyLevel.Advanced, "Entries in the installed-programs list whose uninstaller and install folder no longer exist (Windows Installer entries are never included); exported before removal",
            "Programs listed as installed whose files are gone, so they cannot be uninstalled anyway. A backup copy is saved first.", "System"),
        new("RegSharedDlls", "Shared DLLs That No Longer Exist", SafetyLevel.Advanced, "Windows' shared-DLL counter entries for files that no longer exist; exported before removal",
            "A list Windows keeps of shared program files; these entries name files that are gone. A backup copy is saved first.", "System"),
        new("RegInstallerFolders", "Installer Folders That No Longer Exist", SafetyLevel.Advanced, "Windows Installer's record of folders that no longer exist; exported before removal",
            "Folders Windows Installer remembers that are no longer on this PC. A backup copy is saved first.", "System"),
        new("RegContextMenu", "Right-Click Commands of Removed Programs", SafetyLevel.Advanced, "Right-click menu commands for files, folders, drives or the desktop whose program file no longer exists; exported before removal",
            "Right-click menu entries that would only show an error: the program they start is gone. A backup copy is saved first.", "System"),
        new("RegStartupMissing", "Startup Entries Whose Program Is Gone", SafetyLevel.Advanced, "Run entries (this account and all users) whose program file no longer exists; exported before removal",
            "Things Windows tries to start when you sign in, but their program is gone. A backup copy is saved first.", "System"),
        new("RegEmptyKeys", "Empty Program Keys", SafetyLevel.Advanced, "Keys directly under this account's Software key that hold nothing at all (never Microsoft, Classes or Policies); recorded before removal",
            "Empty settings folders left by programs. They hold nothing. A record is saved first.", "System"),
        // Phase C (owner, November 19, 2026): the font cache and Explorer's own caches (Explorer forgets folder views, start menu order, tray icons and usage history).
        new("FontCache", "Windows Font Cache", SafetyLevel.Advanced, "Windows' font cache files (FontCache\\*.dat); Windows rebuilds them and the first start after can be slower. Files the font service is using are skipped",
            "A list Windows keeps to show fonts faster. Windows builds it again by itself.", "System"),
        new("ExplorerCaches", "File Explorer Caches", SafetyLevel.Advanced, "Folder view settings (ShellBags), the Start menu order, past tray icons and the program usage history (UserAssist); exported before removal. Explorer restart needed",
            "What File Explorer remembers about folders you opened, how you arranged menus and which programs you used. Folder views go back to their defaults. A backup copy is saved first.", "System"),
        new("CertCache", "Certificate Download Cache", SafetyLevel.Advanced, "Revocation lists and certificates Windows downloaded to check signatures (CryptnetUrlCache); downloaded again when needed. The certificate store is never touched",
            "Copies Windows downloaded to check that programs and websites are genuine. They are downloaded again when needed.", "System"),
        // ----- Application cleaning -----
        new("BrowserEdge", "Microsoft Edge", SafetyLevel.Review, "Edge page, code and GPU caches; passwords, cookies and saved sessions are never included. Edge must be closed",
            "Saved copies of web pages that help Edge open sites quicker. Your passwords, logins and open tabs are not touched.", "Apps"),
        new("BrowserFirefox", "Mozilla Firefox", SafetyLevel.Review, "Firefox cache2, startup cache and thumbnails; passwords, cookies, bookmarks and sessions are never included. Firefox must be closed",
            "Saved copies of web pages that help Firefox open sites quicker. Your passwords, bookmarks and logins are not touched.", "Apps"),
        new("BrowserChrome", "Google Chrome", SafetyLevel.Review, "Chrome page, code and GPU caches; passwords, cookies and saved sessions are never included. Chrome must be closed",
            "Saved copies of web pages that help Chrome open sites quicker. Your passwords, logins and open tabs are not touched.", "Apps"),
        new("BrowserLogs", "Browser Log Files", SafetyLevel.Review, "Crash reports and usage-metrics files kept by Edge, Chrome, Firefox and other browsers; passwords, cookies, history and sessions are never included. The browser must be closed",
            "Crash reports and small technical logs your browsers keep. Your passwords, logins and open tabs are not touched.", "Apps"),
        new("BrowserUpdates", "Browser Update Files", SafetyLevel.Review, "Downloaded update packages and update logs of Firefox; the installed browser is not affected. Firefox must be closed",
            "Update downloads Firefox already used. The browser itself stays as it is.", "Apps"),
        new("NvidiaApp", "NVIDIA Application Files", SafetyLevel.Review, "Driver installer downloads and driver log files kept by NVIDIA; the installed driver is not affected",
            "Old driver installers and small log files NVIDIA left behind. Your installed driver stays as it is.", "Apps"),
        new("DevCache", "NuGet & Developer Caches", SafetyLevel.Review, "NuGet, npm, pip, Poetry, Composer, Yarn, Go, Visual Studio, IDE and Adobe media caches; rebuildable, but re-downloading or re-indexing takes time",
            "Downloads and work files kept by programming and design tools. Nothing of yours, but they download again the next time you use those tools.", "Apps"),
        new("NuGetPackages", "NuGet Packages", SafetyLevel.Review, "The restored NuGet packages in your .nuget packages folder; the next build downloads them again, and offline builds fail until then",
            "Libraries your programming tools downloaded. They download again at the next build, so builds without internet fail until then.", "Apps"),
        // Red / Advanced (owner request, 0.9): big downloads and installed-app files of other programs. Nothing personal, but removing them has real consequences,
        // so they are never ticked by default and need the consent box.
        // One red card per app (owner, November 11, 2026: the single long card was split so each app reads on its own).
        new("DownloadsUnity", "Unity Asset Store Downloads", SafetyLevel.Advanced, "Unity Asset Store packages you downloaded (the Asset Store-5.x folder); they come back only from your account, and only while Unity and Unity Hub are closed; the app downloads or reinstalls what it needs, but it can take long and it may not start until then",
            "Big downloads and installed files of Unity. Removing them frees a lot of space, but Unity must download or reinstall them before you can use it again.", "Apps"),
        new("DownloadsAutodesk", "Autodesk Fusion 360 App Files", SafetyLevel.Advanced, "Fusion 360's web-deployed app files (webdeploy); the app downloads or reinstalls what it needs, but it can take long and it may not start until then",
            "Big downloads and installed files of Fusion 360. Removing them frees a lot of space, but Fusion 360 must download or reinstall them before you can use it again.", "Apps"),
        new("DownloadsCapCut", "CapCut App Files", SafetyLevel.Advanced, "CapCut's installed app versions (its Apps folder); the app downloads or reinstalls what it needs, but it can take long and it may not start until then",
            "Big downloads and installed files of CapCut. Removing them frees a lot of space, but CapCut must download or reinstall them before you can use it again.", "Apps"),
        new("DownloadsCodex", "Codex Runtimes", SafetyLevel.Advanced, "The runtimes Codex downloaded (.cache\\codex-runtimes); the app downloads or reinstalls what it needs, but it can take long and it may not start until then",
            "Big downloads and installed files of Codex. Removing them frees a lot of space, but Codex must download or reinstall them before you can use it again.", "Apps"),
        new("DownloadsZed", "Zed Downloaded Tools", SafetyLevel.Advanced, "The tools Zed downloaded (its copilot and node folders); the app downloads or reinstalls what it needs, but it can take long and it may not start until then",
            "Big downloads and installed files of Zed. Removing them frees a lot of space, but Zed must download or reinstall them before you can use it again.", "Apps"),
        new("DownloadsGitHub", "GitHub Desktop Update Packages", SafetyLevel.Advanced, "GitHub Desktop's downloaded update packages; the app downloads or reinstalls what it needs, but it can take long and it may not start until then",
            "Big downloads and installed files of GitHub Desktop. Removing them frees a lot of space, but GitHub Desktop must download or reinstall them before you can use it again.", "Apps"),
        new("DevHomeCaches", "Developer Caches in Your Home Folder", SafetyLevel.Review, "Gradle, Cargo and Bun download caches in your user folder (.gradle, .cargo, .bun); the tools download what they need again, and offline builds fail until then",
            "Downloads that programming tools keep in your user folder. They download again at the next build, so builds without internet fail until then.", "Apps"),
        new("AppLogs", "Application Log Files", SafetyLevel.Review, "Old *.log files in apps' own Logs folders inside your AppData; the apps write new ones; useful only when diagnosing a problem",
            "Diary files that apps keep about what they did. New ones are written as you use them.", "Apps"),
        new("AppTemp", "Application Temporary Files", SafetyLevel.Review, "Old files in apps' own Temp or tmp folders inside your AppData; files in use are skipped",
            "Scratch files that apps leave in their own temporary folders and rarely tidy up.", "Apps"),
        new("Cache", "Other Application Caches", SafetyLevel.Review, "Rebuildable caches of chat, editor, launcher and Store apps; the application must be closed",
            "Saved data that chat, editing and game apps keep to open quicker. They quietly pile up and refill by themselves.", "Apps"),
        // ----- Computer usage traces -----
        new("RecentAccess", "Windows Recent Access", SafetyLevel.Review, "Recent-file shortcuts in the Windows Recent folder; your files are not touched, only the history is removed. Jump lists and pinned items are never touched",
            "The list of files you opened lately. Only the list is cleared; your files stay exactly where they are.", "Privacy"),
        new("RecentDocs", "Recent Documents History", SafetyLevel.Review, "Office recent-document shortcuts; the documents themselves are not touched",
            "The lists Office keeps of the documents you used recently. The documents stay where they are.", "Privacy"),
        new("ExplorerHistory", "Explorer and Dialog History", SafetyLevel.Advanced, "File Explorer's Run box, address bar and search history, the Open/Save dialog history, Registry Editor's last key and recent consoles; exported before removal. Your files are not touched",
            "The lists Windows keeps of what you typed, searched and opened in File Explorer and file dialogs. A backup copy is saved first.", "Privacy"),
        new("AppRecentLists", "App Recent-File Lists", SafetyLevel.Advanced, "Recent-file lists of Paint, WordPad, Windows Media Player, WinRAR, 7-Zip and Media Player Classic; exported before removal. The files are not touched",
            "The lists some apps keep of the files you opened lately. Only the lists are cleared. A backup copy is saved first.", "Privacy"),
        new("Clipboard", "Windows Clipboard History", SafetyLevel.Review, "Clears the Windows clipboard history through the Windows clipboard API; pinned clips are kept",
            "Things you copied earlier that Windows remembers (Win+V). Clips you pinned stay.", "Privacy"),
        new("DnsCache", "DNS Cache", SafetyLevel.Review, "Clears the names of websites Windows looked up (DnsFlushResolverCache); Windows looks them up again as you browse",
            "Website names Windows remembered. It finds them again as you browse.", "Privacy"),
        new("EventTrace", "Windows Event Trace Logs", SafetyLevel.Review, "Old diagnostic .etl trace files; the Windows Event Log and security logs are never touched",
            "Technical traces Windows records about what it was doing. Security and event logs are left alone.", "Privacy"),
        new("UsageHistory", "Application Usage History", SafetyLevel.Review, "Usage logs of the .NET runtime that list which programs ran",
            "Small logs that record which programs you started.", "Privacy"),
        new("BrowserHistory", "Browser History", SafetyLevel.Review, "Chromium history, visited links and top sites. Bookmarks, passwords, cookies and saved sessions are never included; the browser must be closed",
            "The list of sites you visited. Bookmarks, passwords and logins are not touched.", "Privacy"),
        new("BrowserCookies", "Browser Cookies & Site Data", SafetyLevel.Advanced, "Chromium and Firefox cookie databases. You will be signed out of websites. Passwords and saved sessions are never included; the browser must be closed",
            "The small files websites use to remember you. Removing them signs you out of websites.", "Privacy"),
        // Last on purpose (0.9): people with many tabs open are least likely to tick it by accident.
        new("BrowserOther", "Other Browsers", SafetyLevel.Review, "Brave, Vivaldi, Opera and other Chromium browser caches; passwords, cookies and saved sessions are never included. The browser must be closed",
            "Saved copies of web pages kept by your other browsers. Passwords and logins are not touched.", "Apps"),
    ];
    // Chosen only in Settings > Browser data (with a red mark and a warning), never part of a normal scan or Quick Boost.
    internal static readonly HashSet<string> SettingsOnly = ["BrowserHistory", "BrowserCookies"];
    // Privacy traces are counted in items (a few hundred bytes each says nothing useful).
    internal static readonly HashSet<string> CountedByItems = ["RecentAccess", "RecentDocs", "EventTrace", "UsageHistory", "Clipboard", "BrowserHistory", "BrowserCookies", "ExplorerHistory", "AppRecentLists", "ExplorerCaches", "DnsCache"];
    // Categories whose rows are registry entries (counted as entries, sized "REGISTRY").
    internal static readonly HashSet<string> RegistryIds = ["Registry", "ExplorerHistory", "AppRecentLists", "ExplorerCaches", "RegMuiCache", "RegSounds", "RegComClasses", "RegFileTypes", "RegUninstall", "RegSharedDlls", "RegInstallerFolders", "RegContextMenu", "RegStartupMissing", "RegEmptyKeys"];
    private static readonly Dictionary<string, SafetyLevel> safetyById = Definitions.ToDictionary(d => d.Id, d => d.Safety);
    // The order of the files inside a category (owner, November 11, 2026): files from the same folder stay together, the folder holding the most comes first, and
    // inside a folder the largest file comes first. Done once per scan result (one sort, one pass for the folder totals), never per file in a getter.
    internal static List<CleanupItem> OrderBySizeAndLocation(List<CleanupItem> files)
    {
        if (files.Count < 2) return files;
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var groupTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string key = file.Group + "\n" + (file.Folder ?? "");
            totals[key] = totals.GetValueOrDefault(key) + file.Bytes;
            groupTotals[file.Group] = groupTotals.GetValueOrDefault(file.Group) + file.Bytes;
        }
        var keyed = new (long GroupTotal, string Group, long Total, string Folder, CleanupItem Item)[files.Count];
        for (int index = 0; index < keyed.Length; index++)
        {
            string folder = files[index].Folder ?? "", group = files[index].Group;
            keyed[index] = (groupTotals[group], group, totals[group + "\n" + folder], folder, files[index]);
        }
        Array.Sort(keyed, (a, b) =>
        {
            int byGroupTotal = b.GroupTotal.CompareTo(a.GroupTotal); if (byGroupTotal != 0) return byGroupTotal;
            int byGroup = string.Compare(a.Group, b.Group, StringComparison.OrdinalIgnoreCase); if (byGroup != 0) return byGroup;
            int byTotal = b.Total.CompareTo(a.Total); if (byTotal != 0) return byTotal;
            int byFolder = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase); if (byFolder != 0) return byFolder;
            int bySize = b.Item.Bytes.CompareTo(a.Item.Bytes); if (bySize != 0) return bySize;
            return string.Compare(a.Item.FileName ?? a.Item.Path, b.Item.FileName ?? b.Item.Path, StringComparison.OrdinalIgnoreCase);
        });
        var ordered = new List<CleanupItem>(keyed.Length);
        foreach (var entry in keyed) ordered.Add(entry.Item);
        return ordered;
    }

    // The Advanced "large downloads and installed app files" cards, one per app (they used to be one card, AppDownloads).
    internal static readonly string[] AppDownloadIds = ["DownloadsUnity", "DownloadsAutodesk", "DownloadsCapCut", "DownloadsCodex", "DownloadsZed", "DownloadsGitHub"];

    internal static ObservableCollection<ReviewCategory> Create()
    {
        // Review page order: Safe (green) first, then Review (orange), then Advanced (red); catalog order is kept inside each level (OrderBy is stable).
        return new ObservableCollection<ReviewCategory>(Definitions.OrderBy(d => d.Safety).Select((d, index) => new ReviewCategory(d.Id, d.Title, d.Safety, d.Description, d.Summary) { CatalogOrder = index }));
    }
    internal static SafetyLevel SafetyOf(string id) => safetyById.TryGetValue(id, out var safety) ? safety : SafetyLevel.Safe;
    internal static string CategoryFor(CleanupItem file) => CategoryFor(file.Kind, file.Group, file.CategoryId);
    internal static string CategoryFor(CleanupKind kind, string group, string? categoryId) => categoryId ?? kind switch
    {
        CleanupKind.Registry => "Registry", CleanupKind.Shortcut => "Shortcuts", CleanupKind.Junk => "Junk", CleanupKind.RecycleBin => "RecycleBin",
        _ => group switch { "DirectX cache" => "DirectX", "Crash reports" => "Crash", _ => kind == CleanupKind.Cache ? "Cache" : "Temp" }
    };
}

// One of the three color groups on the Review page (Green Safe, Amber Review, Red Advanced): what it holds, in numbers the page can show at a glance.
public sealed class ReviewGroupSummary(SafetyLevel level) : INotifyPropertyChanged
{
    internal SafetyLevel Level { get; } = level;
    // The person sees "Risky" for the red level (owner, November 19, 2026: Safe / Review / Risky); the code name stays SafetyLevel.Advanced.
    public string Title => Level switch { SafetyLevel.Safe => "Safe", SafetyLevel.Review => "Review", _ => "Risky" };
    public string Summary => Level switch
    {
        SafetyLevel.Safe => "Temporary files and caches that Windows rebuilds. Recommended.",
        SafetyLevel.Review => "Browser, app and system leftovers. You choose what to clean.",
        _ => "Cannot be undone, or touches recovery data. Needs your explicit consent."
    };
    public int Count { get; private set; }
    public int SelectedCount { get; private set; }
    public long Bytes { get; private set; }
    public int Categories { get; private set; }
    public bool HasItems => Count > 0;
    // The Clean page (Phase D): "2 categories · 4,108 items" and the group's share of everything found, drawn as a size bar (two star columns, no layout code).
    public string DetailLabel => $"{(Categories == 1 ? "1 category" : $"{Categories:N0} categories")} · {(Count == 1 ? "1 item" : $"{Count:N0} items")}";
    public double Share { get; private set; }
    public System.Windows.GridLength ShareStar => new(Math.Max(Share, 0.001), System.Windows.GridUnitType.Star);
    public System.Windows.GridLength RestStar => new(Math.Max(1 - Share, 0.001), System.Windows.GridUnitType.Star);
    // A soft divider shows above every group except the first one that is listed (the bar above the list has its own).
    private bool dividerAbove;
    public bool DividerAbove { get => dividerAbove; internal set { if (dividerAbove == value) return; dividerAbove = value; PropertyChanged?.Invoke(this, new(nameof(DividerAbove))); } }
    // The group's checkbox: ticked when everything in it is selected, mixed when only part of it is, empty when nothing is.
    public bool? SelectionState => Count == 0 || SelectedCount == 0 ? false : SelectedCount == Count ? true : null;
    public string CountLabel => $"{(Count == 1 ? "1 item" : $"{Count:N0} items")} · {SelectedCount:N0} selected";
    public string TotalLabel => Bytes > 0 ? Sizes.Format(Bytes) : "—";
    public string AutomationName => $"{Title} group: {CountLabel}";
    private bool light;
    public System.Windows.Media.Brush Accent => (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(SafetyPalette.Text(Level, light))!;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Update(IReadOnlyList<ReviewCategory> categories, bool isLight, long allBytes = 0)
    {
        Count = categories.Sum(category => category.Files.Count);
        Categories = categories.Count;
        SelectedCount = categories.Sum(category => category.SelectedCount);
        Bytes = categories.Sum(category => category.TotalBytes);
        Share = allBytes > 0 ? Math.Clamp((double)Bytes / allBytes, 0, 1) : 0;
        light = isLight;
        PropertyChanged?.Invoke(this, new(string.Empty));
    }
}
