using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hush;

// One line of the Process list: the app's icon and name, how much memory its processes use together, and its End button.
public sealed class ProcessRow : INotifyPropertyChanged
{
    internal ProcessRow(AppProcess app, ImageSource? icon)
    {
        App = app; Icon = icon;
        string usage = $"{Sizes.Format(app.MemoryBytes)}" + (app.ProcessCount > 1 ? $" · {app.ProcessCount} processes" : "");
        Detail = app.Kind == ProcessKind.Window ? "Memory usage: " + usage : $"{app.Reason} · {usage}";
        Topic = app.Kind switch { ProcessKind.LeftBehind => LeftBehindTopic, ProcessKind.NoWindow => NoWindowTopic, _ => "" };
    }
    // Topic headings of the background list (names unique across Settings: the page remembers open headings by name).
    internal const string LeftBehindTopic = "Left behind by closed apps", NoWindowTopic = "Running without a window";
    public string Topic { get; internal set; }
    internal AppProcess App { get; }
    public string Name => App.Name;
    public string Detail { get; }
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon != null;
    public string Initial => Name.Length == 0 ? "?" : Name[..1].ToUpperInvariant();
    public string EndLabel => "End " + App.Name;
    private bool canEnd = true;
    public bool NeedsAdministrator => App.NeedsAdministrator;
    public bool CanEnd { get => canEnd; set { if (canEnd == value) return; canEnd = value; PropertyChanged?.Invoke(this, new(nameof(CanEnd))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

// Settings > Process (next to Info): how many apps are open right now and, when the card is opened, each one with an End button.
// Ending always asks first, asks the app to close before anything stronger, and is logged. Only apps with a visible window are listed.
public partial class MainWindow
{
    private IProcessManager processManager = new WindowsProcessManager();
    private bool processesLoading, endingProcess;
    private List<ProcessRow> processRows = [], backgroundRows = [];
    private readonly Dictionary<string, ImageSource?> processIcons = new(StringComparer.OrdinalIgnoreCase);
    // Tests point this at a fixture folder so the log is not written to the real one.
    internal static string? ProcessLogDirectory;
    internal IReadOnlyList<ProcessRow> ProcessRowsShown => processRows;
    internal IReadOnlyList<ProcessRow> BackgroundRowsShown => backgroundRows;

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e) => RefreshProcesses(rebuildList: true);

    // The summary line updates by itself while Settings is open; the list is rebuilt only when the card opens or Refresh is clicked,
    // so rows never jump under the pointer.
    internal async void RefreshProcesses(bool rebuildList)
    {
        if (processesLoading || endingProcess) return;
        processesLoading = true;
        try
        {
            // The background programs are read only while the card is open and the list is being rebuilt (never by the summary's own refresh).
            bool readBackground = rebuildList && SettingsProcessSection.IsExpanded;
            var (apps, background) = await Task.Run(() => (processManager.List(), readBackground ? processManager.ListBackground() : null));
            int count = apps.Count;
            ProcessSummary.Text = count == 0 ? "No apps with a window open" : $"In progress · {count} app{(count == 1 ? "" : "s")} · {Sizes.Format(apps.Sum(app => app.MemoryBytes))}";
            if (rebuildList || processRows.Count == 0 && SettingsProcessSection.IsExpanded)
            {
                processRows = apps.Select(app => new ProcessRow(app, IconFor(app.Path))).ToList();
                ProcessList.ItemsSource = processRows;
                ProcessEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (background != null) ShowBackground(background);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { ProcessSummary.Text = "Apps can't be read right now"; }
        finally { processesLoading = false; }
    }

    // Leftovers first, then the rest; each kind under its own closed topic heading (a long topic splits itself, the shared rule).
    private void ShowBackground(IReadOnlyList<AppProcess> background)
    {
        backgroundRows = background.OrderBy(app => app.Kind).ThenByDescending(app => app.MemoryBytes).Select(app => new ProcessRow(app, IconFor(app.Path))).ToList();
        var names = ListGrouping.Names(backgroundRows, row => row.Topic, row => row.App.NeedsAdministrator ? "needs administrator" : "can end", flatLimit: 0, foldSingles: false);
        for (int i = 0; i < backgroundRows.Count; i++) backgroundRows[i].Topic = names[i];
        var view = new System.Windows.Data.ListCollectionView(backgroundRows);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ProcessRow.Topic)));
        BackgroundProcessList.ItemsSource = view;
        var left = backgroundRows.Where(row => row.App.Kind == ProcessKind.LeftBehind).ToList();
        int other = backgroundRows.Count - left.Count;
        BackgroundProcessSummary.Text = backgroundRows.Count == 0 ? "Nothing else of yours is running without a window"
            : (left.Count > 0 ? $"{left.Count} left behind by closed apps ({Sizes.Format(left.Sum(row => row.App.MemoryBytes))})" : "Nothing left behind by closed apps")
              + (other > 0 ? $" · {other} more without a window" : "");
        BackgroundProcessesPanel.Visibility = Visibility.Visible;
        int endable = left.Count(row => !row.App.NeedsAdministrator);
        EndLeftBehindButton.Visibility = endable > 1 ? Visibility.Visible : Visibility.Collapsed;
        EndLeftBehindText.Text = $"End all left behind ({endable})";
    }

    private ImageSource? IconFor(string path)
    {
        if (processIcons.TryGetValue(path, out var known)) return known;
        ImageSource? image = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                image = source;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or NotSupportedException) { }
        return processIcons[path] = image;
    }

    private async void EndProcess_Click(object sender, RoutedEventArgs e)
    {
        if (endingProcess || busy || sender is not Button { Tag: ProcessRow row }) return;
        var app = row.App;
        string processes = app.ProcessCount == 1 ? "1 process" : $"{app.ProcessCount} processes";
        string how = app.Kind switch
        {
            ProcessKind.LeftBehind => $"{app.Name} was left running after its app closed ({app.Reason.ToLowerInvariant()}). It has no window, so Hush ends it at once. The app starts it again when you open it.",
            ProcessKind.NoWindow => $"{app.Name} has no window, so it cannot be asked to close: Hush ends it at once. If it is busy (a download, a sync, a command), that work stops.",
            _ => $"Hush asks {app.Name} to close first, so it can offer to save your work. If it is still running after a few seconds, Hush ends it, and anything unsaved in it is lost."
        };
        bool agreed = await AskConsent($"End {app.Name}?", how
            + $"\n\n{processes} using {Sizes.Format(app.MemoryBytes)} of memory will be closed. Nothing else is touched.", "End", "Keep it running");
        if (!agreed) return;
        endingProcess = true;
        row.CanEnd = false;
        ProcessNote.Text = $"Ending {app.Name}…";
        ProcessNote.Visibility = Visibility.Visible;
        try
        {
            var outcome = await Task.Run(() => processManager.End(app, CancellationToken.None));
            WriteProcessLog(app, outcome);
            ProcessNote.Text = outcome.StillRunning > 0
                ? $"{app.Name} is still running; it may be waiting for you to save. Answer it, then try again."
                : outcome.Closed + outcome.Ended == 0 ? $"{app.Name} had already closed."
                : $"{app.Name} was closed. It was using {Sizes.Format(app.MemoryBytes)}; that memory is free again unless another program takes it.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { ProcessNote.Text = $"{app.Name} could not be closed from here."; row.CanEnd = true; }
        finally { endingProcess = false; }
        RefreshProcesses(rebuildList: true);
    }

    // Ends everything closed apps left behind, after one confirmation (programs that still work without a window are never part of it).
    private async void EndLeftBehind_Click(object sender, RoutedEventArgs e)
    {
        if (endingProcess || busy) return;
        var left = backgroundRows.Where(row => row.App.Kind == ProcessKind.LeftBehind && !row.App.NeedsAdministrator && row.CanEnd).ToList();
        if (left.Count == 0) return;
        long memory = left.Sum(row => row.App.MemoryBytes);
        int count = left.Sum(row => row.App.ProcessCount);
        bool agreed = await AskConsent("End what closed apps left behind?",
            $"These helper processes kept running after their app closed: {string.Join(", ", left.Select(row => row.App.Name))}. They have no window and do nothing useful on their own; "
            + $"each app starts them again when you open it.\n\n{count} process{(count == 1 ? "" : "es")} using {Sizes.Format(memory)} of memory will be ended. Programs that still run without a window are not touched.",
            "End all", "Keep them");
        if (!agreed) return;
        endingProcess = true;
        foreach (var row in left) row.CanEnd = false;
        ProcessNote.Text = "Ending what closed apps left behind…";
        ProcessNote.Visibility = Visibility.Visible;
        int done = 0, still = 0;
        try
        {
            foreach (var row in left)
            {
                var outcome = await Task.Run(() => processManager.End(row.App, CancellationToken.None));
                WriteProcessLog(row.App, outcome);
                done += outcome.Closed + outcome.Ended; still += outcome.StillRunning;
            }
            ProcessNote.Text = still > 0 ? $"{done} process{(done == 1 ? "" : "es")} ended; {still} could not be ended from here."
                : done == 0 ? "They had already closed." : $"{done} process{(done == 1 ? "" : "es")} ended. They were using {Sizes.Format(memory)}; that memory is free again unless another program takes it.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { ProcessNote.Text = "Some of them could not be ended from here."; }
        finally { endingProcess = false; }
        RefreshProcesses(rebuildList: true);
    }

    private static void WriteProcessLog(AppProcess app, EndOutcome outcome)
    {
        try
        {
            string directory = ProcessLogDirectory ?? Path.Combine(CleanupEngine.DataDirectory, "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "process-ends.jsonl"), JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, app = app.Name, path = app.Path, kind = app.Kind.ToString(), processes = app.ProcessCount, memoryBytes = app.MemoryBytes,
                closed = outcome.Closed, ended = outcome.Ended, stillRunning = outcome.StillRunning
            }) + Environment.NewLine);
        }
        catch { }
    }
}
