using System.IO;
using System.Windows;

namespace Hush;

// One line of the Scheduled tasks card (same row as a service): ON = the task is enabled, as Windows has it now; switching it OFF stages "turn it off".
internal sealed class TaskChoice(TaskCandidate candidate) : OffSwitchChoice
{
    internal TaskCandidate Candidate { get; } = candidate;
    public string Path => Candidate.Path;
    public override string Title => Candidate.Title;
    public override string HelpText => Candidate.Why + " Switch it off to disable it: Windows no longer runs it, and the task itself stays. Nothing changes until you review and confirm, and Undo turns it back on.";
    protected override string CurrentStatus => Candidate.Status;
    public override string LevelLabel => Candidate.Level switch { TaskLevel.Recommended => "Rarely needed", TaskLevel.Somewhat => "Read first", _ => "Left behind" };
    public override bool Amber => Candidate.Level != TaskLevel.Recommended;
}

// Settings > Scheduled tasks (0.9): the sibling of Settings > Services. All real work is in TaskTuning; this file only lists, asks, confirms and reports.
public partial class MainWindow
{
    private readonly TaskTuning taskTuning;
    // Test seam: the test runner points every window that is not given its own fake at an empty one, so no test ever reads or changes real tasks in the background.
    internal static Func<ITaskSystem> TaskSystemFactory = () => new RealTaskSystem();
    // Test seam: where the history (tasks-changed.json) is kept; null = the real "Windows settings" data folder. Tests point it at a fixture folder.
    internal static string? TaskHistoryDirectory;
    private List<TaskChoice> taskChoices = [];
    private bool tasksBusy;
    internal IReadOnlyList<TaskChoice> TaskChoices => taskChoices;
    internal TaskTuning Tasks => taskTuning;

    // The list is read when the card is opened (or refreshed), never when Settings opens.
    private void Tasks_Expanded(object sender, RoutedEventArgs e) { if (ReferenceEquals(e.OriginalSource, sender) && taskChoices.Count == 0) _ = RefreshTasksAsync(); }
    private void RefreshTasks_Click(object sender, RoutedEventArgs e) => _ = RefreshTasksAsync();

    // Reads what Hush would offer (read-only) and fills the card. Reading never changes anything and works without administrator rights.
    internal async Task RefreshTasksAsync()
    {
        if (tasksBusy) return;
        try
        {
            var candidates = await Task.Run(taskTuning.Scan);
            foreach (var old in taskChoices) old.PropertyChanged -= TaskChoiceChanged;
            // In order: the topics in their fixed order (left-behind tasks last), and inside a topic the rarely-needed ones first, then by name.
            taskChoices = candidates.Select(candidate => new TaskChoice(candidate))
                .OrderBy(choice => TaskTuning.TopicRank(choice.Candidate.Topic)).ThenBy(choice => choice.Candidate.Level).ThenBy(choice => choice.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(choice => choice.Path, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var choice in taskChoices) choice.PropertyChanged += TaskChoiceChanged;
            // A long list is split into sub-categories automatically (shared rule). Every sub-category starts closed, always.
            var names = ListGrouping.Names(taskChoices, choice => choice.Candidate.Topic, choice => choice.LevelLabel);
            for (int i = 0; i < taskChoices.Count; i++) taskChoices[i].Topic = names[i];
            var view = new System.Windows.Data.ListCollectionView(taskChoices);
            if (names.Any(name => name.Length > 0)) view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(TaskChoice.Topic)));
            TasksList.ItemsSource = view;
            int changed = taskTuning.ChangedCount;
            // Without administrator rights Windows hides some task files from Hush (the top folder cannot be listed), so the summary says more may show.
            TasksSummary.Text = (taskChoices.Count == 0 ? "Nothing to suggest on this PC" : $"{taskChoices.Count} task{(taskChoices.Count == 1 ? "" : "s")} you can turn off")
                + (changed > 0 ? $" · {changed} changed by Hush" : "") + (taskTuning.CanChange ? "" : " · more may show as administrator");
            TasksEmpty.Visibility = taskChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TasksUndoButton.Visibility = taskTuning.HasChanges ? Visibility.Visible : Visibility.Collapsed;
            UpdateTasksReview();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
        { TasksSummary.Text = "Scheduled tasks can't be read right now"; }
    }

    private void TaskChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateTasksReview();

    private void UpdateTasksReview()
    {
        int count = taskChoices.Count(choice => choice.Selected);
        TasksReviewButton.IsEnabled = count > 0 && !tasksBusy;
        TasksReviewButton.Content = count > 0 ? $"Review {count} change{(count == 1 ? "" : "s")}…" : "Review changes…";
    }

    private async void TasksReview_Click(object sender, RoutedEventArgs e)
    {
        var chosen = taskChoices.Where(choice => choice.Selected).ToList();
        if (chosen.Count == 0 || tasksBusy || busy) return;
        bool admin = taskTuning.CanChange;
        string lines = string.Join("\n", chosen.Select(choice => $"• {choice.Title}: {choice.Candidate.Why}"));
        string body = $"Hush will turn off {(chosen.Count == 1 ? "this scheduled task" : "these scheduled tasks")}, so Windows no longer runs {(chosen.Count == 1 ? "it" : "them")}. Nothing is deleted; Undo turns every one back on.\n\n{lines}"
            + (admin ? "" : "\n\nThis needs administrator rights. Hush restarts with Windows' own permission prompt and comes back to this card; nothing has been changed yet.");
        bool agreed = await AskConsent($"Turn off {chosen.Count} scheduled task{(chosen.Count == 1 ? "" : "s")}?", body, admin ? "Turn them off" : "Restart as administrator", "Cancel");
        if (!agreed) return;
        if (!admin)
        {
            if (await Task.Run(() => ElevatedLauncher("--tasks"))) { ExitForRestart(); return; }
            TasksStatus.Text = "Windows' permission prompt was declined or could not open, so nothing was changed.";
            TasksStatus.Visibility = Visibility.Visible;
            return;
        }
        await RunTasksAsync(() => taskTuning.Apply(chosen.Select(choice => choice.Path).ToList()), undo: false);
    }

    private async void TasksUndo_Click(object sender, RoutedEventArgs e)
    {
        if (tasksBusy || busy) return;
        if (!taskTuning.CanChange)
        {
            bool agreed = await AskConsent("Turn the scheduled tasks back on?", "Hush will turn every scheduled task it turned off back on. This needs administrator rights: Hush restarts with Windows' own permission prompt and comes back to this card.", "Restart as administrator", "Cancel");
            if (!agreed) return;
            if (await Task.Run(() => ElevatedLauncher("--tasks-undo"))) { ExitForRestart(); return; }
            TasksStatus.Text = "Windows' permission prompt was declined or could not open, so nothing was changed.";
            TasksStatus.Visibility = Visibility.Visible;
            return;
        }
        await RunTasksAsync(taskTuning.Undo, undo: true);
    }

    private async Task RunTasksAsync(Func<TaskReport> work, bool undo)
    {
        tasksBusy = true; UpdateTasksReview();
        TasksUndoButton.IsEnabled = RefreshTasksButton.IsEnabled = false;
        string message;
        try { message = (await Task.Run(work)).Text(undo); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { message = ex.Message; }
        tasksBusy = false;
        TasksUndoButton.IsEnabled = RefreshTasksButton.IsEnabled = true;
        TasksStatus.Text = message + (undo ? "" : " Undo turns them back on.");
        TasksStatus.Visibility = Visibility.Visible;
        await RefreshTasksAsync();
    }

    // Reached from Hush's own administrator restart: reopens Settings with the Scheduled tasks card open (the person still reviews and confirms).
    internal void ResumeTasks(bool undo)
    {
        if (busy) return;
        Settings_Click(this, new RoutedEventArgs());
        SettingsTasksSection.IsExpanded = true;
        _ = RefreshTasksAsync();
        if (undo) TasksUndo_Click(this, new RoutedEventArgs());
    }
}
