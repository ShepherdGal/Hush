using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hush;

// The Tools page (one screen, owner, November 21, 2026): the rail at the left lists the Overview and every tool, and the open entry fills the card beside it
// (its content scrolls inside). Tools opens on the Overview, which names a few things worth a look (owner's pick B). Preferences.LastTool is only remembered.
// The ten tools are the old
// cards (their Expanders, handlers and lazy reading are unchanged): showing one opens it, which is what reads its data, exactly as opening a card did.
public partial class MainWindow
{
    // The tools in the mockup's order: key (the Expander is "Tools" + key + "Section"), title, the tile's line when nothing is known, and a 20 px outline icon.
    private static readonly (string Key, string Title, string Line, string Icon)[] ToolCatalog =
    [
        ("Programs", "Programs", "Uninstall programs and what they leave behind", "M3,3.5 H8.5 V9 H3 Z M11.5,3.5 H17 V9 H11.5 Z M3,11 H8.5 V16.5 H3 Z M11.5,11 H17 V16.5 H11.5 Z"),
        ("Store", "Store apps", "Reset or remove apps from the Store", "M4,7 H16 L15,16.5 H5 Z M7.5,7 V5.5 A2.5,2.5 0 0 1 12.5,5.5 V7"),
        ("Extensions", "Browser extensions", "See and remove add-ons in every browser", "M8,3.5 H12 V6.5 A1.5,1.5 0 1 0 12,9.5 H16.5 V16.5 H3.5 V9.5 H8 A1.5,1.5 0 1 1 8,6.5 Z"),
        ("Updates", "Software Updates", "Newer versions of your programs and drivers", "M10,3.25 A6.75,6.75 0 1 1 9.99,3.25 M10,6.5 V13 M7.2,10.3 L10,13 L12.8,10.3"),
        ("Repair", "Device repair", "Fix Bluetooth that stopped working", "M6.5,6.5 L13.5,13.5 L10,17 V3 L13.5,6.5 L6.5,13.5"),
        ("Duplicates", "Duplicate files", "Duplicate and large files, removed to the Recycle Bin", "M6,6.5 H14.5 V16.5 H6 Z M3.5,13.5 V3.5 H12"),
        ("Registry", "Registry", "Issues, full backup and search", "M4,4 H9 V9 H4 Z M11,4 H16 V9 H11 Z M4,11 H9 V16 H4 Z M13.5,11 V16 M11,13.5 H16"),
        ("Shredder", "Shredder", "Overwrite files so they cannot come back", "M5,3.5 H12 L15,6.5 V11 H5 Z M6.5,13.5 V16.5 M10,13.5 V16.5 M13.5,13.5 V16.5"),
        ("Disk", "Disk check", "Health and a read-only file system check", "M3,5 H17 V15 H3 Z M6,12 H10"),
        ("RegistrySize", "Registry size", "How much space the registry could save", "M4,16.5 V9 M8,16.5 V5 M12,16.5 V11 M16,16.5 V7"),
    ];
    private static readonly Dictionary<string, Geometry> ToolIcons = ToolCatalog.ToDictionary(tool => tool.Key, tool => { var icon = Geometry.Parse(tool.Icon); icon.Freeze(); return icon; });

    private bool showingTool;
    internal string OpenTool { get; private set; } = "";
    // True while the Overview is the open entry (Tools opens on it).
    internal bool ToolsHomeShown => OpenTool == "Overview";

    // The rail: the Overview first, then every tool in the catalog's order.
    private readonly List<TweaksTab> toolRail =
    [
        new("Overview", "Overview") { RailIcon = OverviewIcon, Hint = "A few things worth a look right now", CountIsWaiting = false },
        .. ToolCatalog.Select(tool => new TweaksTab(tool.Key, tool.Title) { RailIcon = ToolIcons[tool.Key], Hint = tool.Line, CountIsWaiting = false }),
    ];
    internal IReadOnlyList<TweaksTab> ToolRail => toolRail;
    internal IReadOnlyList<Suggestion> ToolSuggestions => ToolsSuggestions.ItemsSource as IReadOnlyList<Suggestion> ?? [];

    internal void ShowToolsHome() => ShowTool("Overview", remember: false);

    // Owner's pick B: the Overview names at most three things worth a look, from what the tools already know (their own last results); nothing is read to
    // fill it. A tool with nothing to say gives way to a plain way to start.
    private void RefreshToolSuggestions()
    {
        var found = new List<Suggestion>();
        void Add(string key, string title, string line, string action = "Open") => found.Add(new(key, ToolIcons[key], title, line, action, () => { ShowTool(key); Motion.Reveal(ToolFocusCard); }));
        if (prefs.AppUpdatesCount is int updates && updates > 0) Add("Updates", $"{updates} update{(updates == 1 ? " is" : "s are")} ready", "Newer versions of your programs and drivers, from your last check");
        if (prefs.DiskHealth is string health && !health.Equals("good", StringComparison.OrdinalIgnoreCase)) Add("Disk", "Your disk health is worth a look", "Health in plain words and a read-only file system check");
        if (prefs.AppUpdatesCount is null) Add("Updates", "See if your programs have newer versions", "Programs and drivers, looked up only when you open it");
        if (prefs.DiskHealth is null) Add("Disk", "Your disk hasn't been checked yet", "Health first, then a read-only check of each drive");
        Add("Programs", "Remove a program cleanly", "Its own uninstaller first, then what it left behind, backed up");
        Add("Duplicates", "Find copies and big files", "They go to the Recycle Bin, so you can get them back");
        Add("Registry", "Back up the registry", "One full backup you can restore later, and a search");
        ToolsSuggestions.ItemsSource = found.Take(3).ToList();
    }

    private Expander ToolSection(string key) => (Expander)FindName("Tools" + key + "Section");
    private static string ToolKey(Expander section) => section.Name.Length > 12 ? section.Name[5..^7] : "";

    // Shows one tool in the focus card: the others are hidden (their data, once read, stays), its tile leaves the grid, the card scrolls to the top.
    internal void ShowTool(string key, bool remember = true)
    {
        bool overview = key == "Overview";
        if (!overview && !ToolCatalog.Any(tool => tool.Key == key)) key = "Programs";
        showingTool = true;
        try
        {
            foreach (var tool in ToolCatalog) ToolSection(tool.Key).Visibility = tool.Key == key ? Visibility.Visible : Visibility.Collapsed;
            if (!overview && !ToolSection(key).IsExpanded) ToolSection(key).IsExpanded = true;   // opening is what reads a tool's data (lazily, as before)
        }
        finally { showingTool = false; }
        OpenTool = key;
        ToolsOverview.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        if (overview) RefreshToolSuggestions();
        RefreshToolTiles();
        ToolsScroll.ScrollToTop();
        if (remember && !overview && prefs.LastTool != key) { prefs.LastTool = key; SavePreferences(); }
    }

    // Code (and the tests) may still open a tool by its Expander: that shows it in the card too.
    private void ToolSection_Expanded(object sender, RoutedEventArgs e)
    {
        if (showingTool || e.OriginalSource is not Expander section || !ReferenceEquals(section.Parent, ToolsSectionsPanel)) return;
        ShowTool(ToolKey(section));
    }

    // The rail's hints say what is known, from the tools' own last results (never read just to fill them); the open entry is marked.
    private void RefreshToolTiles()
    {
        ToolTiles.ItemsSource ??= toolRail;
        string Line(string key, string fallback) => key switch
        {
            "Updates" when prefs.AppUpdatesCount is int count => count == 0 ? "Everything was up to date at the last check" : $"{count} program{(count == 1 ? " has" : "s have")} a newer version",
            "Disk" when prefs.DiskHealth is string health => "Last check: " + health.ToLowerInvariant() switch { "good" => "healthy", "warning" => "worth a look", _ => "needs attention" },
            "Programs" when programRows.Count > 0 => $"{programRows.Count} programs installed",
            "Store" when StoreAppRows.Count > 0 => $"{StoreAppRows.Count} Store apps",
            "Extensions" when ExtensionRows.Count > 0 => $"{ExtensionRows.Count} extensions",
            _ => fallback
        };
        foreach (var entry in toolRail)
        {
            if (entry.Key != "Overview") entry.Hint = Line(entry.Key, ToolCatalog.First(tool => tool.Key == entry.Key).Line);
            entry.Open = entry.Key == OpenTool;
        }
    }

    // ----- Programs: the sort switch (owner's pick: largest first, with a switch to name or date installed) -----

    private string programsSort = "size";
    internal string ProgramsSort => programsSort;

    private void ProgramsSort_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string sort || sort == programsSort) return;
        programsSort = sort;
        ShowProgramsSort();
        if (programRows.Count > 0) ApplyProgramsView();
    }

    private void ShowProgramsSort()
    {
        var buttons = new[] { SortBySizeButton, SortByNameButton, SortByDateButton };
        foreach (var button in buttons)
            button.SetResourceReference(ForegroundProperty, (string)button.Tag == programsSort ? "TealTextBrush" : "MutedBrush");
        // The bar's pill springs to the chosen sort (the old one lets go first).
        foreach (var button in buttons) if ((string)button.Tag != programsSort) SegmentPanel.SetIsSelected(button, false);
        foreach (var button in buttons) if ((string)button.Tag == programsSort) SegmentPanel.SetIsSelected(button, true);
    }

    private IEnumerable<ProgramRow> SortPrograms(IEnumerable<ProgramRow> rows) => programsSort switch
    {
        "name" => rows.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase),
        "date" => rows.OrderByDescending(row => row.Program.Installed ?? DateTime.MinValue).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase),
        _ => rows.OrderByDescending(row => row.Program.Bytes).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
    };

    // Each program's own icon, read on a worker thread once the list is on screen; the rows show it as it arrives.
    private async void LoadProgramIcons(IReadOnlyList<ProgramRow> rows)
    {
        var wanted = rows.Where(row => row.Icon == null && !string.IsNullOrWhiteSpace(row.Program.DisplayIcon)).ToList();
        if (wanted.Count == 0) return;
        var loader = ProgramIcons.Loader;
        var icons = await Task.Run(() => wanted.Select(row => { try { return loader(row.Program.DisplayIcon!); } catch (Exception) { return null; } }).ToList());
        for (int i = 0; i < wanted.Count; i++) if (icons[i] != null) wanted[i].Icon = icons[i];
    }
}
