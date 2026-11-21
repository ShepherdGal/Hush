using System.IO;
using System.Windows;

namespace Hush;

// One line of the Startup card, on the shared row (ServiceTemplate) and with the shared meaning of the switch: ON = it does not start with Windows (or
// will not, after you review). A line that is already off (Hush or Task Manager turned it off) starts ON; switching it OFF stages "start with Windows again".
internal sealed class StartupItemChoice(StartupCandidate candidate) : OffSwitchChoice
{
    internal StartupCandidate Candidate { get; } = candidate;
    public string Id => Candidate.Id;
    public override string Title => Candidate.Title;
    public override bool WasOff => !Candidate.StartsNow && !Candidate.Locked;
    public override bool CanEdit => !Candidate.Locked;
    public override string HelpText => Candidate.Locked
        ? $"{Candidate.LockedNote}. Hush shows it so you know it runs, but never changes it. It runs: {Candidate.Command}"
        : $"It runs: {Candidate.Command}\n\n"
          + Candidate.Where switch
          {
              StartupWhere.Task => "A scheduled task that runs when you sign in or when Windows starts. Turning it off disables the task; nothing is deleted.",
              StartupWhere.Service => "A service of another program that starts with Windows. Turning it off sets it to start only when needed, so the program can still start it when you open it.",
              StartupWhere.StoreApp => "A Store app's own startup switch (the same one Task Manager shows). Turning it off only stops the app starting when you sign in.",
              _ => "Turning it off works like Task Manager's Startup apps: the program stays installed and its entry stays as it is; it just no longer starts with Windows."
          }
          + (Candidate.FileMissing ? " Its program file is missing, so it can only fail to start." : "")
          + " Nothing changes until you review and confirm, and Undo puts it back.";
    protected override string CurrentStatus => Candidate.Status;
    // Phase C (owner, November 19, 2026): Hush's own list of things most PCs do not need at sign-in (updaters, launchers, chat and helper apps that start
    // themselves when opened). Only a mark: nothing is switched for the person.
    private static readonly string[] SuggestedWords = ["update", "updater", "helper", "launcher", "assistant", "tray", "agent", "notifier", "discord", "spotify", "steam", "epicgames", "teams", "skype", "zoom", "dropbox", "adobe", "ccxprocess", "acrotray", "itunes", "cortana", "edge", "googledrive", "gamebar", "xbox"];
    internal bool Suggested => !Candidate.Locked && Candidate.StartsNow && Candidate.Where is not StartupWhere.Service
        && SuggestedWords.Any(word => Candidate.Title.Replace(" ", "").Contains(word, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(Candidate.Command.Trim('"').Split('"')[0]).Contains(word, StringComparison.OrdinalIgnoreCase));
    public override string LevelLabel => Suggested ? "Suggested" : Candidate.Where switch
    {
        StartupWhere.Registry or StartupWhere.Folder or StartupWhere.StoreApp => Candidate.FileMissing ? "Left behind" : "Startup app",
        StartupWhere.Task => "Task",
        StartupWhere.Service => "Service",
        _ => "Locked"
    };
    // Green: a Run/folder app (the Task Manager switch). Amber: a task or a service (read first), a left-behind entry, or a locked one.
    public override bool Amber => !Suggested && (Candidate.Where is not (StartupWhere.Registry or StartupWhere.Folder or StartupWhere.StoreApp) || Candidate.FileMissing);
}

// Settings > Startup (owner, November 14, 2026; Pro only): everything that starts with Windows. All real work is in StartupTuning; this file only lists and
// reports. Changes go through the one Review changes button with the Services and Scheduled tasks cards.
public partial class MainWindow
{
    private readonly StartupTuning startupTuning;
    private List<StartupItemChoice> startupChoices = [];
    private bool startupBusy;
    // Test seam: where the locked sources are read (null = the real registry).
    internal static Func<StartupLockedPlaces>? StartupLockedPlacesDefault;
    internal IReadOnlyList<StartupItemChoice> StartupChoices => startupChoices;
    internal StartupTuning Startup => startupTuning;

    // The list is read when the card is opened (or refreshed), never when Settings opens.
    private void Startup_Expanded(object sender, RoutedEventArgs e) { if (ReferenceEquals(e.OriginalSource, sender) && startupChoices.Count == 0) _ = RefreshStartupAsync(); }
    private void RefreshStartup_Click(object sender, RoutedEventArgs e) => _ = RefreshStartupAsync();

    internal async Task RefreshStartupAsync()
    {
        if (startupBusy) return;
        try
        {
            var candidates = await Task.Run(() => startupTuning.Scan().ToList());
            foreach (var old in startupChoices) old.PropertyChanged -= StartupChoiceChanged;
            // In order: the five sources in their fixed order; inside each, what starts now first (the ones to look at), then by name.
            startupChoices = candidates.Select(candidate => new StartupItemChoice(candidate))
                .OrderBy(choice => Array.IndexOf(StartupTuning.TopicOrder, choice.Candidate.Topic)).ThenByDescending(choice => choice.Candidate.StartsNow)
                .ThenBy(choice => choice.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var choice in startupChoices) choice.PropertyChanged += StartupChoiceChanged;
            // Each line keeps its source heading (it says where it starts from); a long source splits itself (shared rule). The tabs below pick the source.
            var names = ListGrouping.Names(startupChoices, choice => choice.Candidate.Topic, choice => choice.Candidate.StartsNow ? "starts now" : "turned off", flatLimit: 0, foldSingles: false);
            for (int i = 0; i < startupChoices.Count; i++) startupChoices[i].Topic = names[i];
            // The sources as icon tabs (owner, November 20, 2026: the Software Updates layout): only sources that hold something; registry and folder apps share
            // the Apps tab (Task Manager shows them as one list) under their own two headings.
            startupTabs = StartupTabKinds.Where(kind => startupChoices.Any(choice => StartupTabOf(choice.Candidate.Topic) == kind.Key))
                .Select(kind => new TweaksTab(kind.Key, kind.Label) { IconGeometry = UpdateIcons.Of(kind.Icon), CountIsWaiting = false,
                    Count = startupChoices.Count(choice => StartupTabOf(choice.Candidate.Topic) == kind.Key).ToString() }).ToList();
            StartupSourceTabs.ItemsSource = startupTabs;
            StartupTabsBar.Visibility = startupTabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (!startupTabs.Any(tab => tab.Key == openStartupTab)) openStartupTab = startupTabs.FirstOrDefault()?.Key ?? "";
            StartupList.ItemsSource = new System.Windows.Data.ListCollectionView(startupChoices);
            ApplyStartupView();
            int starting = startupChoices.Count(choice => choice.Candidate.StartsNow && !choice.Candidate.Locked);
            SetStartupItemCount(starting);   // Home's Startup items tile follows the card
            int changed = startupTuning.ChangedCount;
            StartupSummary.Text = (startupChoices.Count == 0 ? "Nothing starts with Windows from the places Hush checks" : $"{starting} of {startupChoices.Count} start with Windows")
                + (changed > 0 ? $" · {changed} turned off by Hush" : "") + (startupTuning.CanChangeAll ? "" : " · more tasks may show as administrator");
            StartupEmpty.Visibility = startupChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StartupUndoButton.Visibility = startupTuning.HasChanges ? Visibility.Visible : Visibility.Collapsed;
            UpdateReviewButton();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
        { StartupSummary.Text = "What starts with Windows can't be read right now"; }
    }

    private void StartupChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateReviewButton();

    // ----- The source tabs -----
    private static readonly (string Key, string Label, string Icon)[] StartupTabKinds =
        [("Apps", "Apps", "Programs"), ("Store", "Store apps", "Store"), ("Tasks", "Tasks", "Tasks"), ("Services", "Services", "Services"), ("Locked", "Locked", "Locked")];
    private List<TweaksTab> startupTabs = [];
    private string openStartupTab = "";
    internal IReadOnlyList<TweaksTab> StartupTabs => startupTabs;
    internal string OpenStartupTab => openStartupTab;

    internal static string StartupTabOf(string topic) => Array.IndexOf(StartupTuning.TopicOrder, topic) switch
    {
        0 or 1 => "Apps", 2 => "Store", 3 => "Tasks", 4 => "Services", _ => "Locked"
    };

    internal void ShowStartupTab(string key)
    {
        if (!startupTabs.Any(tab => tab.Key == key)) return;
        openStartupTab = key;
        ApplyStartupView();
    }

    private void StartupTab_Select(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TweaksTab tab && tab.Key != openStartupTab) { ShowStartupTab(tab.Key); Motion.Reveal(StartupList); }
    }

    // What the list shows: the open source (or, while the Tweaks search has text, matches from every source), under headings only when they say something
    // (the Apps tab's two places, or a long source split by the shared rule; a tab whose rows share one heading is a plain list).
    private void ApplyStartupView()
    {
        if (StartupList.ItemsSource is not System.Windows.Data.ListCollectionView view) return;
        string text = settingsFilter;
        bool searching = text.Length > 0;
        foreach (var tab in startupTabs) tab.Open = !searching && tab.Key == openStartupTab;
        StartupTabsBar.Visibility = !searching && startupTabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        bool Shown(StartupItemChoice choice) => searching
            ? choice.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || choice.Topic.Contains(text, StringComparison.OrdinalIgnoreCase)
            : StartupTabOf(choice.Candidate.Topic) == openStartupTab;
        bool grouped = searching || startupChoices.Where(Shown).Select(choice => choice.Topic).Distinct().Count() > 1;
        using (view.DeferRefresh())
        {
            view.Filter = row => row is StartupItemChoice choice && Shown(choice);
            view.GroupDescriptions.Clear();
            if (grouped) view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(StartupItemChoice.Topic)));
        }
    }

    private async void StartupUndo_Click(object sender, RoutedEventArgs e)
    {
        if (startupBusy || busy) return;
        if (!startupTuning.CanChangeAll)
        {
            bool agreed = await AskConsent("Let everything start with Windows again?", "Hush will put back everything it stopped from starting with Windows. This can need administrator rights: Hush restarts with Windows' own permission prompt and comes back to this card.", "Restart as administrator", "Cancel");
            if (!agreed) return;
            if (await Task.Run(() => ElevatedLauncher(StartupUndoArgument))) { ExitForRestart(); return; }
            StartupStatus.Text = "Windows' permission prompt was declined or could not open, so nothing was changed.";
            StartupStatus.Visibility = Visibility.Visible;
            return;
        }
        startupBusy = true; UpdateReviewButton();
        string message;
        try { message = (await Task.Run(() => startupTuning.Undo())).Text(on: true); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { message = ex.Message; }
        startupBusy = false;
        StartupStatus.Text = message; StartupStatus.Visibility = Visibility.Visible;
        await RefreshStartupAsync();
    }

    internal const string StartupUndoArgument = "--startup-undo";

    // Reached from Hush's own administrator restart for Undo: reopens the card and asks again.
    internal async void ResumeStartupUndo()
    {
        if (busy) return;
        Settings_Click(this, new RoutedEventArgs());
        SettingsStartupSection.IsExpanded = true;
        await RefreshStartupAsync();
        StartupUndo_Click(this, new RoutedEventArgs());
    }
}
