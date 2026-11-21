using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Hush;

// One sub-tab of the Tweaks page: its name, how many rows it holds, and whether something in it is staged.
internal sealed class TweaksTab(string key, string label) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    private string count = "";
    private bool staged, open;
    public string Count { get => count; set { if (count == value) return; count = value; Changed(nameof(Count)); Changed(nameof(BadgeVisible)); Changed(nameof(HasUpdates)); Changed(nameof(SpokenName)); } }
    // Software Updates' tabs (owner, November 20, 2026): an icon of their own and a count badge that is in the accent color while something waits.
    public System.Windows.Media.Geometry? IconGeometry { get; init; }
    // As an entry of the left rail (Tools and Tweaks, owner, November 21, 2026): its icon on the 20 px grid of the Settings index, and a one-line hint.
    public System.Windows.Media.Geometry? RailIcon { get; init; }
    private string hint = "";
    public string Hint { get => hint; set { if (hint == value) return; hint = value; Changed(nameof(Hint)); } }
    public Visibility BadgeVisible => count.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    // Software Updates' counts are things that wait, so their badges are accent whenever they are not zero. Elsewhere (Tweaks, Startup, Browser extensions) a
    // count is only how many rows a tab holds: its badge is accent on the open tab only, so one bar never shouts with five accent badges.
    public bool CountIsWaiting { get; init; } = true;
    public bool HasUpdates => count.Length > 0 && count != "0" && (CountIsWaiting || open);
    public bool Staged { get => staged; set { if (staged == value) return; staged = value; Changed(nameof(StagedDot)); Changed(nameof(SpokenName)); } }
    public bool Open { get => open; set { if (open == value) return; open = value; Changed(nameof(Open)); Changed(nameof(Ink)); Changed(nameof(HasUpdates)); } }
    public Visibility StagedDot => staged ? Visibility.Visible : Visibility.Collapsed;
    public Brush Ink => (Brush)Application.Current.FindResource(open ? "HeadingBrush" : "MutedBrush");
    public string SpokenName => Label + (count.Length > 0 ? ", " + count.Replace("/", " of ") : "") + (staged ? ", has staged changes" : "");
    internal void ThemeChanged() => Changed(nameof(Ink));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}

// The Tweaks page (one screen, owner, November 21, 2026; earlier picks of November 20, 2026). The rail at the left holds the Overview and five entries that show
// the old Settings cards as sections of one card: Privacy (Privacy & telemetry, More impact and the red Windows Update), Windows tweaks, Services, Tasks and
// Startup. Tweaks opens on the Overview (a few things worth a look, owner's pick B). Opening Tweaks reads every list quietly in the background, one after another
// (read-only), so the counts and suggestions fill in and switching entries is instant; one search shows matches from every entry; the bar under the card holds the
// open entry's Actions and the one Review button (on the Overview only while something is staged). Preferences.LastTweaksTab is only remembered. Everything a
// card did before (reading, staging, the one Review button, Undo) is unchanged.
public partial class MainWindow
{
    private readonly List<TweaksTab> tweaksTabs =
    [
        new("Overview", "Overview") { RailIcon = OverviewIcon, Hint = "A few things worth a look right now", CountIsWaiting = false },
        new("Privacy", "Privacy") { IconGeometry = UpdateIcons.Of("Privacy"), RailIcon = RailGlyph(UpdateIcons.Of("Privacy")), Hint = "Stop Windows sending data about how you use it", CountIsWaiting = false },
        new("Tweaks", "Windows tweaks") { IconGeometry = UpdateIcons.Of("Tweaks"), RailIcon = RailGlyph(UpdateIcons.Of("Tweaks")), Hint = "Quieter menus and taskbar, fewer ads and tips", CountIsWaiting = false },
        new("Services", "Services") { IconGeometry = UpdateIcons.Of("Services"), RailIcon = RailGlyph(UpdateIcons.Of("Services")), Hint = "Background parts of Windows you may not need", CountIsWaiting = false },
        new("Tasks", "Tasks") { IconGeometry = UpdateIcons.Of("Tasks"), RailIcon = RailGlyph(UpdateIcons.Of("Tasks")), Hint = "Scheduled jobs: telemetry and app updaters", CountIsWaiting = false },
        new("Startup", "Startup") { IconGeometry = UpdateIcons.Of("Startup"), RailIcon = RailGlyph(UpdateIcons.Of("Startup")), Hint = "Everything that starts with Windows", CountIsWaiting = false },
    ];
    internal IReadOnlyList<TweaksTab> TweaksTabsShown => tweaksTabs;
    internal IReadOnlyList<Suggestion> TweakSuggestions => TweaksSuggestions.ItemsSource as IReadOnlyList<Suggestion> ?? [];
    internal string OpenTweaksTab { get; private set; } = "";
    private bool showingTweaksTab, tweaksReadOnce;

    private Expander[] SectionsOf(string key) => key switch
    {
        "Overview" => [],
        "Privacy" => [SettingsPrivacySection, SettingsImpactSection, SettingsUpdateSection],
        "Tweaks" => [SettingsTweaksSection],
        "Services" => [SettingsServicesSection],
        "Tasks" => [SettingsTasksSection],
        _ => [SettingsStartupSection]
    };
    private Expander[] AllTweakSections => [SettingsPrivacySection, SettingsImpactSection, SettingsUpdateSection, SettingsTweaksSection, SettingsServicesSection, SettingsTasksSection, SettingsStartupSection];
    private string TabOf(FrameworkElement? section) => tweaksTabs.FirstOrDefault(tab => SectionsOf(tab.Key).Contains(section))?.Key ?? "Privacy";

    private void InitializeTweaksPage()
    {
        TweaksTabs.ItemsSource = tweaksTabs;
        // A list read again gets a new view: the search follows it.
        foreach (var list in new ItemsControl[] { ServicesList, TasksList, StartupList, TweaksList })
            DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ItemsControl)).AddValueChanged(list, (_, _) => { if (settingsFilter.Length > 0) ApplyTweaksSearch(); });
        // Code (and the tests) may still open one of the moved cards directly: that opens Tweaks at its tab.
        TweaksSectionsPanel.AddHandler(Expander.ExpandedEvent, new RoutedEventHandler((_, e) =>
        {
            if (showingTweaksTab || e.OriginalSource is not Expander section || !AllTweakSections.Contains(section)) return;
            if (TweaksPage.Visibility != Visibility.Visible) OpenTweaksPage(TabOf(section));
            else if (settingsFilter.Length == 0 && TabOf(section) != OpenTweaksTab) ShowTweaksTab(TabOf(section));
        }));
    }

    // Opens the Tweaks page at an entry (the Overview unless one is named).
    internal void OpenTweaksPage(string? tab = null)
    {
        if (busy || !IsProMode) return;
        SelectTab("Tweaks");
        LeaveSettings();
        if (TweaksPage.Visibility != Visibility.Visible)
        {
            Motion.EnterPage(MainContent, () =>
            {
                ScanPage.Visibility = ReviewPage.Visibility = SettingsPage.Visibility = ToolsPage.Visibility = Visibility.Collapsed;
                TweaksPage.Visibility = Visibility.Visible;
            });
            RefreshWindowsUpdateState();
        }
        ShowTweaksTab(tab ?? "Overview");
        if (!tweaksReadOnce) { tweaksReadOnce = true; ReadTweaksQuietly(); }
    }

    // Shows one sub-tab: its sections are visible and opened (opening is what reads a list that is not read yet), the bar shows its Actions.
    internal void ShowTweaksTab(string key, bool remember = true)
    {
        if (!tweaksTabs.Any(tab => tab.Key == key)) key = "Privacy";
        if (settingsFilter.Length > 0) SettingsSearch.Text = "";   // choosing a tab ends a search
        OpenTweaksTab = key;
        showingTweaksTab = true;
        try
        {
            var shown = SectionsOf(key);
            foreach (var section in AllTweakSections) section.Visibility = shown.Contains(section) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var section in shown) if (!section.IsExpanded) section.IsExpanded = true;
        }
        finally { showingTweaksTab = false; }
        if (key is "Privacy" or "Overview" && adjustmentChoices.Count == 0) LoadWindowsSettings();
        ActionsButton.Visibility = key == "Privacy" ? Visibility.Visible : Visibility.Collapsed;
        ServicesActionsButton.Visibility = key == "Services" ? Visibility.Visible : Visibility.Collapsed;
        TabActionsButton.Visibility = key is "Tweaks" or "Tasks" or "Startup" ? Visibility.Visible : Visibility.Collapsed;
        TabActionUndoButton.Visibility = key == "Tweaks" ? Visibility.Collapsed : Visibility.Visible;   // a tweak is undone by switching it off
        RefreshWindowsSettingsButton.Visibility = key == "Overview" ? Visibility.Collapsed : Visibility.Visible;
        CloseActionMenus();
        TweaksNoMatch.Visibility = Visibility.Collapsed;
        TweaksOverview.Visibility = key == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tab in tweaksTabs) tab.Open = tab.Key == key;
        TweaksScroll.ScrollToTop();
        if (remember && key != "Overview" && prefs.LastTweaksTab != key) { prefs.LastTweaksTab = key; SavePreferences(); }
        RefreshTweaksTabs();
    }

    // The one click handler of the shared icon tabs (IconTabTemplate): the bar the tab sits in decides what opens.
    private void IconTab_Select(object sender, RoutedEventArgs e)
    {
        for (DependencyObject? parent = sender as DependencyObject; parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (ReferenceEquals(parent, UpdateTabsList)) { UpdateTab_Select(sender, e); return; }
            if (ReferenceEquals(parent, StartupSourceTabs)) { StartupTab_Select(sender, e); return; }
            if (ReferenceEquals(parent, ExtensionBrowserTabs)) { ExtensionTab_Select(sender, e); return; }
        }
    }

    // Owner's pick: opening Tweaks reads every list one after another in the background (read-only), so the counts fill in. Only what is not read yet.
    private async void ReadTweaksQuietly()
    {
        try
        {
            if (adjustmentChoices.Count == 0) LoadWindowsSettings();
            RefreshTweaksTabs();
            if (tweakChoices.Count == 0) RefreshTweaks();
            RefreshTweaksTabs();
            if (serviceChoices.Count == 0) await RefreshServicesAsync();
            RefreshTweaksTabs();
            if (taskChoices.Count == 0) await RefreshTasksAsync();
            RefreshTweaksTabs();
            if (startupChoices.Count == 0) await RefreshStartupAsync();
            RefreshTweaksTabs();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException) { RefreshTweaksTabs(); }
    }

    // The tabs' counts and dots, and the bar's "Staged: 1 Privacy · 2 Services" (called whenever the Review button is updated).
    internal void RefreshTweaksTabs()
    {
        // Owner, November 20, 2026: a badge says what is active out of the total ("50/77"), not only the total: privacy protections applied, tweaks on,
        // services and tasks still enabled, startup items that start with Windows.
        (int Active, int Count, int Staged) Of(string key) => key switch
        {
            "Privacy" => (adjustmentChoices.Count(choice => choice.Baseline.Enabled), adjustmentChoices.Count, PendingWindows),
            "Tweaks" => (tweakChoices.Count(choice => choice.State.On), tweakChoices.Count, tweakChoices.Count(choice => choice.Selected)),
            "Services" => (serviceChoices.Count(choice => !choice.WasOff), serviceChoices.Count, serviceChoices.Count(choice => choice.Selected)),
            "Tasks" => (taskChoices.Count(choice => !choice.WasOff), taskChoices.Count, taskChoices.Count(choice => choice.Selected)),
            _ => (startupChoices.Count(choice => choice.Candidate.StartsNow), startupChoices.Count, startupChoices.Count(choice => choice.Selected))
        };
        var staged = new List<string>();
        foreach (var tab in tweaksTabs)
        {
            if (tab.Key == "Overview") continue;
            var (active, count, pending) = Of(tab.Key);
            tab.Count = count > 0 ? $"{active:N0}/{count:N0}" : "";
            tab.Staged = pending > 0;
            if (pending > 0) staged.Add($"{pending} {tab.Label}");
        }
        TweaksStagedSummary.Text = staged.Count == 0 ? "" : "Staged: " + string.Join(" · ", staged);
        bool overview = OpenTweaksTab == "Overview" && settingsFilter.Length == 0;
        // On the Overview the bar shows only while something is staged (owner's pick, November 21, 2026).
        TweaksFooterBar.Visibility = overview && staged.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (overview) RefreshTweakSuggestions();
    }

    private static readonly Geometry ReviewIcon = Frozen("M4.5,10.5 L8.3,14.2 L15.5,6");

    // Owner's pick B: at most three things worth a look, from the lists already read (Tweaks reads them quietly when it opens); nothing is read to fill it, and
    // a list with nothing to say gives way to a plain way to start. Only counts are named: never a speed claim.
    private void RefreshTweakSuggestions()
    {
        var found = new List<Suggestion>();
        void Add(string key, string title, string line) => found.Add(new(key, RailGlyph(UpdateIcons.Of(key)), title, line, "Open", () => { ShowTweaksTab(key); Motion.Reveal(TweaksCard); }));
        static string Plural(int count, string one, string many) => count == 1 ? one : many;
        int pending = PendingChanges;
        if (pending > 0) found.Add(new("Review", ReviewIcon, $"{pending} {Plural(pending, "change is", "changes are")} waiting for review", "Nothing changes until you confirm, and Undo puts it back", "Review", () => ReviewChanges_Click(this, new RoutedEventArgs())));
        int privacy = adjustmentChoices.Count(choice => !choice.Advanced && choice.CanEdit && !choice.Baseline.Enabled && !choice.Selected);
        if (privacy > 0) Add("Privacy", $"{privacy} recommended privacy {Plural(privacy, "setting is", "settings are")} not applied", "Windows can still send data about how you use it");
        int startup = startupChoices.Count(choice => choice.Suggested && !choice.Selected);
        if (startup > 0) Add("Startup", $"{startup} {Plural(startup, "app starts", "apps start")} with Windows that you may not need", "They open by themselves every time you sign in");
        int services = serviceChoices.Count(choice => choice.Candidate.Level == ServiceLevel.Recommended && choice.Candidate.Running && !choice.WasOff && !choice.Selected);
        if (services > 0) Add("Services", $"{services} rarely needed {Plural(services, "service is", "services are")} running", "Each one is saved first, so Undo puts it back");
        int tasks = taskChoices.Count(choice => choice.Candidate.Level == TaskLevel.Recommended && !choice.WasOff && !choice.Selected);
        if (tasks > 0) Add("Tasks", $"{tasks} rarely needed scheduled {Plural(tasks, "task is", "tasks are")} on", "Telemetry jobs and app updaters that run on their own");
        Add("Tweaks", "Make Windows quieter", "Fewer ads, tips and suggestions, and a tidier right-click menu");
        Add("Privacy", "Choose what Windows may send", "Telemetry and activity history, one switch each");
        TweaksSuggestions.ItemsSource = found.DistinctBy(suggestion => suggestion.Key).Take(3).ToList();
    }

    // ----- One search for every tab (owner's pick: while typing, matches from all five tabs, each under its own heading) -----

    private Predicate<object>? OffSwitchFilter()
    {
        string text = settingsFilter;
        if (text.Length == 0) return null;
        return row => row is OffSwitchChoice choice && (choice.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || choice.Topic.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyTweaksSearch()
    {
        foreach (var list in new ItemsControl[] { ServicesList, TasksList, TweaksList })
            if (list.ItemsSource is ListCollectionView view) view.Filter = OffSwitchFilter();
        ApplyStartupView();   // Startup also filters by its open source tab (a search looks in every source)
        if (settingsFilter.Length == 0) { ShowTweaksTab(OpenTweaksTab.Length > 0 ? OpenTweaksTab : "Overview", remember: false); return; }
        TweaksOverview.Visibility = Visibility.Collapsed;   // a search shows matches only
        TweaksFooterBar.Visibility = Visibility.Visible;
        static bool Any(ItemsControl list) => list.ItemsSource is ListCollectionView view && view.Count > 0;
        var matches = new Dictionary<Expander, bool>
        {
            [SettingsPrivacySection] = Any(PrivacyAdjustments), [SettingsImpactSection] = Any(AdvancedAdjustments),
            [SettingsUpdateSection] = "Turn off Windows Update".Contains(settingsFilter, StringComparison.OrdinalIgnoreCase),
            [SettingsTweaksSection] = Any(TweaksList), [SettingsServicesSection] = Any(ServicesList), [SettingsTasksSection] = Any(TasksList), [SettingsStartupSection] = Any(StartupList)
        };
        showingTweaksTab = true;
        try { foreach (var (section, any) in matches) { section.Visibility = any ? Visibility.Visible : Visibility.Collapsed; if (any && !section.IsExpanded) section.IsExpanded = true; } }
        finally { showingTweaksTab = false; }
        TweaksNoMatch.Visibility = matches.Values.Any(any => any) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var tab in tweaksTabs) tab.Open = false;   // results come from every tab: no tab is underlined while searching
        TweaksScroll.ScrollToTop();
    }

    // ----- The bar's Actions for Windows tweaks, Tasks and Startup (owner's pick: the open tab's own actions; these have no levels) -----

    private void TabActions_Click(object sender, RoutedEventArgs e) => TabActionsPopup.IsOpen = !TabActionsPopup.IsOpen;

    private void TabActionClear_Click(object sender, RoutedEventArgs e)
    {
        TabActionsPopup.IsOpen = false;
        IEnumerable<OffSwitchChoice> rows = OpenTweaksTab switch { "Tweaks" => tweakChoices, "Tasks" => taskChoices, _ => startupChoices };
        foreach (var row in rows.ToList()) row.Selected = false;   // nothing staged any more; what Windows has is untouched
        UpdateReviewButton();
    }

    private void TabActionUndo_Click(object sender, RoutedEventArgs e)
    {
        TabActionsPopup.IsOpen = false;
        if (OpenTweaksTab == "Tasks") TasksUndo_Click(sender, e);
        else if (OpenTweaksTab == "Startup") StartupUndo_Click(sender, e);
    }
}
