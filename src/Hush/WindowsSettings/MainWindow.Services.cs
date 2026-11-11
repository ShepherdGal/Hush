using System.ComponentModel;
using System.IO;
using System.Windows;

namespace Hush;

// One line of the Services card: a switch, the service's plain name with a "?" explanation, what it uses right now, and its level.
// The switch works like O&O ShutUp10++: the person switches it ON to disable the service. A switch that is ON is a change waiting for "Review changes":
// nothing is written until that is confirmed. Selected means "will be disabled" (= switch ON).
internal sealed class ServiceChoice(ServiceCandidate candidate) : OffSwitchChoice
{
    internal ServiceCandidate Candidate { get; } = candidate;
    public string Name => Candidate.Name;
    public override string Title => Candidate.Title;
    public override bool WasOff => Candidate.ChangedByHush;
    public override string HelpText => Candidate.Why + (WasOff ? " Hush turned this off earlier. Switch it off to put it back as it was." : " Switch it on to disable it: Windows no longer starts it.")
        + " Nothing changes until you review and confirm, and Undo puts it back.";
    protected override string CurrentStatus => Candidate.Status;
    public override string LevelLabel => Candidate.Level switch { ServiceLevel.Recommended => "Rarely needed", ServiceLevel.Somewhat => "Read first", _ => "Left behind" };
    public override bool Amber => Candidate.Level != ServiceLevel.Recommended;
}

// The shared line behind the Services and Scheduled tasks cards (one row template, ServiceTemplate), laid out like O&O ShutUp10++: switching it ON means
// "turn this off" and stages a change ("Will be turned off after you review"). Selected means "will be turned off" (= switch ON).
internal abstract class OffSwitchChoice : INotifyPropertyChanged
{
    public abstract string Title { get; }
    public abstract string HelpText { get; }
    public abstract string LevelLabel { get; }
    public abstract bool Amber { get; }
    // True for a line Hush turned off earlier: its switch starts ON (it is off, as Hush left it) and switching it OFF stages "turn it back on".
    public abstract bool WasOff { get; }
    protected abstract string CurrentStatus { get; }
    public string HelpName => "More about " + Title;
    // What the item is doing now, or what is about to happen to it.
    public string Status => selected ? (WasOff ? "Will be turned back on after you review · " : "Will be turned off after you review · ") + CurrentStatus : CurrentStatus;
    // The sub-category heading this line sits under; empty while the whole list is short enough to stay flat (see ListGrouping).
    public string Topic { get; internal set; } = "";
    private bool selected;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); PropertyChanged?.Invoke(this, new(nameof(On))); PropertyChanged?.Invoke(this, new(nameof(Status))); } }
        // The switch is ON when the thing is (or will be) turned off: a line Hush offers starts OFF, a line Hush already turned off starts ON.
    public bool On { get => selected != WasOff; set => Selected = value != WasOff; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

// Settings > Services (0.9): lists services that start with Windows but that most people do not need, and services left behind by removed programs.
// All real work is in ServiceTuning; this file only lists, asks, confirms and reports.
public partial class MainWindow
{
    private readonly ServiceTuning serviceTuning;
    // Test seam: the test runner points every window that is not given its own fake at an empty one, so no test ever runs sc.exe in the background.
    internal static Func<IServiceSystem> ServiceSystemFactory = () => new RealServiceSystem();
    private List<ServiceChoice> serviceChoices = [];
    private bool servicesBusy;
    internal IReadOnlyList<ServiceChoice> ServiceChoices => serviceChoices;
    internal ServiceTuning Services => serviceTuning;

    private void Services_Expanded(object sender, RoutedEventArgs e) { if (serviceChoices.Count == 0) _ = RefreshServicesAsync(); }
    private void RefreshServices_Click(object sender, RoutedEventArgs e) => _ = RefreshServicesAsync();

    // Reads what Hush would offer (read-only) and fills the card. Reading never changes anything and works without administrator rights.
    internal async Task RefreshServicesAsync()
    {
        if (servicesBusy) return;
        try
        {
            // What Hush offers, plus what it already turned off earlier (those lines start ON and can be switched back OFF).
            var candidates = await Task.Run(() => serviceTuning.Scan().Concat(serviceTuning.ScanChanged()).ToList());
            foreach (var old in serviceChoices) old.PropertyChanged -= ServiceChoiceChanged;
            // In order: the topics in their fixed order (left-behind services last), and inside a topic the rarely-needed ones before the read-first ones, then by name.
            serviceChoices = candidates.Select(candidate => new ServiceChoice(candidate))
                .OrderBy(choice => ServiceTuning.TopicRank(choice.Candidate.Topic)).ThenBy(choice => choice.Candidate.Level).ThenBy(choice => choice.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var choice in serviceChoices) choice.PropertyChanged += ServiceChoiceChanged;
            // A long list is split into sub-categories automatically (shared rule). Every sub-category starts closed, always: opening a card never opens anything inside it.
            var names = ListGrouping.Names(serviceChoices, choice => choice.Candidate.Topic, choice => choice.LevelLabel);
            for (int i = 0; i < serviceChoices.Count; i++) serviceChoices[i].Topic = names[i];
            var view = new System.Windows.Data.ListCollectionView(serviceChoices);
            if (names.Any(name => name.Length > 0)) view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ServiceChoice.Topic)));
            ServicesList.ItemsSource = view;
            int changed = serviceTuning.ChangedCount;
            int offered = serviceChoices.Count(choice => !choice.WasOff);
            ServicesSummary.Text = (serviceChoices.Count == 0 ? "Nothing to suggest on this PC" : offered == 0 ? "Nothing left to turn off" : $"{offered} service{(offered == 1 ? "" : "s")} you can turn off · {serviceChoices.Count(choice => choice.Candidate.Running)} running now") + (changed > 0 ? $" · {changed} changed by Hush" : "");
            ServicesEmpty.Visibility = serviceChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ServicesUndoButton.Visibility = serviceTuning.HasChanges ? Visibility.Visible : Visibility.Collapsed;
            UpdateReviewButton();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
        { ServicesSummary.Text = "Services can't be read right now"; }
    }

    // ----- The Services card's Actions menu: like the one for the Windows choices, every action only switches the matching services ON and opens the one
    // review for everything (nothing is written before it is confirmed). -----
    private void ServicesActions_Click(object sender, RoutedEventArgs e) => ServicesActionsPopup.IsOpen = !ServicesActionsPopup.IsOpen;

    private void StageServices(Func<ServiceChoice, bool> include)
    {
        ServicesActionsPopup.IsOpen = false;
        if (servicesBusy || busy) return;
        foreach (var choice in serviceChoices) if (include(choice)) choice.Selected = true;
        if (!serviceChoices.Any(choice => choice.Selected))
        { ServicesStatus.Text = "Nothing to turn off: no service in the list matches that choice."; ServicesStatus.Visibility = Visibility.Visible; return; }
        ReviewChanges_Click(this, new RoutedEventArgs());
    }
    private void ServicesActionRare_Click(object sender, RoutedEventArgs e) => StageServices(choice => choice.Candidate.Level == ServiceLevel.Recommended);
    private void ServicesActionReadFirst_Click(object sender, RoutedEventArgs e) => StageServices(choice => choice.Candidate.Level is ServiceLevel.Recommended or ServiceLevel.Somewhat);
    private void ServicesActionAll_Click(object sender, RoutedEventArgs e) => StageServices(_ => true);
    private void ServicesActionClear_Click(object sender, RoutedEventArgs e)
    {
        ServicesActionsPopup.IsOpen = false;
        foreach (var choice in serviceChoices) choice.Selected = false;
        ServicesStatus.Visibility = Visibility.Collapsed;
    }
    private void ServicesActionUndo_Click(object sender, RoutedEventArgs e)
    {
        ServicesActionsPopup.IsOpen = false;
        if (serviceTuning.HasChanges) ServicesUndo_Click(sender, e);
        else { ServicesStatus.Text = "Nothing to undo: Hush has not changed any service."; ServicesStatus.Visibility = Visibility.Visible; }
    }

    private void ServiceChoiceChanged(object? sender, PropertyChangedEventArgs e) => UpdateReviewButton();

    private async void ServicesUndo_Click(object sender, RoutedEventArgs e)
    {
        if (servicesBusy || busy) return;
        if (!serviceTuning.CanChange)
        {
            bool agreed = await AskConsent("Put the services back?", "Hush will put every service it changed back to its original setting and start the ones that were running. This needs administrator rights: Hush restarts with Windows' own permission prompt and comes back to this card.", "Restart as administrator", "Cancel");
            if (!agreed) return;
            if (await Task.Run(() => ElevatedLauncher("--services-undo"))) { ExitForRestart(); return; }
            ServicesStatus.Text = "Windows' permission prompt was declined or could not open, so nothing was changed.";
            ServicesStatus.Visibility = Visibility.Visible;
            return;
        }
        await RunServicesAsync(() => serviceTuning.Undo(), undo: true);
    }

    private async Task RunServicesAsync(Func<ServiceReport> work, bool undo)
    {
        servicesBusy = true; UpdateReviewButton();
        ServicesUndoButton.IsEnabled = RefreshServicesButton.IsEnabled = false;
        string message;
        try { message = (await Task.Run(work)).Text(undo); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { message = ex.Message; }
        servicesBusy = false;
        ServicesUndoButton.IsEnabled = RefreshServicesButton.IsEnabled = true;
        ServicesStatus.Text = message + (undo ? "" : " Undo puts them back.");
        ServicesStatus.Visibility = Visibility.Visible;
        await RefreshServicesAsync();
    }

    // Reached from Hush's own administrator restart: reopens Settings with the Services card open (the person still reviews and confirms).
    internal void ResumeServices(bool undo)
    {
        if (busy) return;
        Settings_Click(this, new RoutedEventArgs());
        SettingsServicesSection.IsExpanded = true;
        _ = RefreshServicesAsync();
        if (undo) ServicesUndo_Click(this, new RoutedEventArgs());
    }
}
