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
        Detail = $"Memory usage: {Sizes.Format(app.MemoryBytes)}" + (app.ProcessCount > 1 ? $" · {app.ProcessCount} processes" : "");
    }
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
    private List<ProcessRow> processRows = [];
    private readonly Dictionary<string, ImageSource?> processIcons = new(StringComparer.OrdinalIgnoreCase);
    // Tests point this at a fixture folder so the log is not written to the real one.
    internal static string? ProcessLogDirectory;
    internal IReadOnlyList<ProcessRow> ProcessRowsShown => processRows;

    private void Process_Expanded(object sender, RoutedEventArgs e) => RefreshProcesses(rebuildList: true);
    private void RefreshProcesses_Click(object sender, RoutedEventArgs e) => RefreshProcesses(rebuildList: true);

    // The summary line updates by itself while Settings is open; the list is rebuilt only when the card opens or Refresh is clicked,
    // so rows never jump under the pointer.
    internal async void RefreshProcesses(bool rebuildList)
    {
        if (processesLoading || endingProcess) return;
        processesLoading = true;
        try
        {
            var apps = await Task.Run(() => processManager.List());
            int count = apps.Count;
            ProcessSummary.Text = count == 0 ? "No apps with a window open" : $"In progress · {count} app{(count == 1 ? "" : "s")} · {Sizes.Format(apps.Sum(app => app.MemoryBytes))}";
            if (rebuildList || processRows.Count == 0 && SettingsProcessSection.IsExpanded)
            {
                processRows = apps.Select(app => new ProcessRow(app, IconFor(app.Path))).ToList();
                ProcessList.ItemsSource = processRows;
                ProcessEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { ProcessSummary.Text = "Apps can't be read right now"; }
        finally { processesLoading = false; }
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
        bool agreed = await AskConsent($"End {app.Name}?",
            $"Hush asks {app.Name} to close first, so it can offer to save your work. If it is still running after a few seconds, Hush ends it, and anything unsaved in it is lost."
            + $"\n\n{processes} using {Sizes.Format(app.MemoryBytes)} of memory will be closed. Nothing else is touched.", "End", "Keep it open");
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

    private static void WriteProcessLog(AppProcess app, EndOutcome outcome)
    {
        try
        {
            string directory = ProcessLogDirectory ?? Path.Combine(CleanupEngine.DataDirectory, "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "process-ends.jsonl"), JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, app = app.Name, path = app.Path, processes = app.ProcessCount, memoryBytes = app.MemoryBytes,
                closed = outcome.Closed, ended = outcome.Ended, stillRunning = outcome.StillRunning
            }) + Environment.NewLine);
        }
        catch { }
    }
}
