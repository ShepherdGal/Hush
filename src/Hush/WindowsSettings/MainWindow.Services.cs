using System.ComponentModel;
using System.IO;
using System.Windows;

namespace Hush;

// One line of the Services card: a switch, the service's plain name with a "?" explanation, what it uses right now, and its level.
// The switch shows the service's state the way Windows has it: ON = the service is enabled (as it is now); the person switches it OFF to disable it. A switch
// that is OFF is a change waiting for "Review changes": nothing is written until that is confirmed. Selected means "will be disabled" (= switch OFF).
internal sealed class ServiceChoice(ServiceCandidate candidate) : OffSwitchChoice
{
    internal ServiceCandidate Candidate { get; } = candidate;
    public string Name => Candidate.Name;
    public override string Title => Candidate.Title;
    public override string HelpText => Candidate.Why + " Switch it off to disable it: Windows no longer starts it. Nothing changes until you review and confirm, and Undo puts it back.";
    protected override string CurrentStatus => Candidate.Status;
    public override string LevelLabel => Candidate.Level switch { ServiceLevel.Recommended => "Rarely needed", ServiceLevel.Somewhat => "Read first", _ => "Left behind" };
    public override bool Amber => Candidate.Level != ServiceLevel.Recommended;
}

// The shared line behind the Services and Scheduled tasks cards (one row template, ServiceTemplate): ON = enabled as Windows has it now; switching it OFF
// stages a change ("Will be turned off after you review"). Selected means "will be turned off" (= switch OFF).
internal abstract class OffSwitchChoice : INotifyPropertyChanged
{
    public abstract string Title { get; }
    public abstract string HelpText { get; }
    public abstract string LevelLabel { get; }
    public abstract bool Amber { get; }
    protected abstract string CurrentStatus { get; }
    public string HelpName => "More about " + Title;
    // What the item is doing now, or what is about to happen to it.
    public string Status => selected ? "Will be turned off after you review · " + CurrentStatus : CurrentStatus;
    // The sub-category heading this line sits under; empty while the whole list is short enough to stay flat (see ListGrouping).
    public string Topic { get; internal set; } = "";
    private bool selected;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); PropertyChanged?.Invoke(this, new(nameof(On))); PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    // The switch itself: ON while the item stays enabled.
    public bool On { get => !selected; set => Selected = !value; }
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
            var candidates = await Task.Run(serviceTuning.Scan);
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
            ServicesSummary.Text = (serviceChoices.Count == 0 ? "Nothing to suggest on this PC" : $"{serviceChoices.Count} service{(serviceChoices.Count == 1 ? "" : "s")} you can turn off · {serviceChoices.Count(choice => choice.Candidate.Running)} running now") + (changed > 0 ? $" · {changed} changed by Hush" : "");
            ServicesEmpty.Visibility = serviceChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ServicesUndoButton.Visibility = serviceTuning.HasChanges ? Visibility.Visible : Visibility.Collapsed;
            UpdateServicesReview();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
        { ServicesSummary.Text = "Services can't be read right now"; }
    }

    private void ServiceChoiceChanged(object? sender, PropertyChangedEventArgs e) => UpdateServicesReview();

    private void UpdateServicesReview()
    {
        int count = serviceChoices.Count(choice => choice.Selected);
        ServicesReviewButton.IsEnabled = count > 0 && !servicesBusy;
        ServicesReviewButton.Content = count > 0 ? $"Review {count} change{(count == 1 ? "" : "s")}…" : "Review changes…";
    }

    private async void ServicesReview_Click(object sender, RoutedEventArgs e)
    {
        var chosen = serviceChoices.Where(choice => choice.Selected).ToList();
        if (chosen.Count == 0 || servicesBusy || busy) return;
        bool admin = serviceTuning.CanChange;
        string lines = string.Join("\n", chosen.Select(choice => $"• {choice.Title}: {choice.Candidate.Why}"));
        string body = $"Hush will disable {(chosen.Count == 1 ? "this service" : "these services")}, so Windows no longer starts {(chosen.Count == 1 ? "it" : "them")}. Running ones are stopped now. Nothing is uninstalled; Undo puts every original setting back.\n\n{lines}"
            + (admin ? "" : "\n\nThis needs administrator rights. Hush restarts with Windows' own permission prompt and comes back to this card; nothing has been changed yet.");
        bool agreed = await AskConsent($"Change {chosen.Count} service{(chosen.Count == 1 ? "" : "s")}?", body, admin ? "Change them" : "Restart as administrator", "Cancel");
        if (!agreed) return;
        if (!admin)
        {
            if (await Task.Run(() => ElevatedLauncher("--services"))) { ExitForRestart(); return; }
            ServicesStatus.Text = "Windows' permission prompt was declined or could not open, so nothing was changed.";
            ServicesStatus.Visibility = Visibility.Visible;
            return;
        }
        await RunServicesAsync(() => serviceTuning.Apply(chosen.Select(choice => choice.Name).ToList()), undo: false);
    }

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
        await RunServicesAsync(serviceTuning.Undo, undo: true);
    }

    private async Task RunServicesAsync(Func<ServiceReport> work, bool undo)
    {
        servicesBusy = true; UpdateServicesReview();
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
