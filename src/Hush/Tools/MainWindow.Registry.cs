using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Hush;

internal sealed class RegistryBackupRow(RegistryBackup backup)
{
    internal RegistryBackup Backup { get; } = backup;
    public string Name => Backup.When.ToString("d MMM yyyy, HH:mm") + (Backup.Label.Length > 0 ? " · " + Backup.Label : "");
    public string Detail => InstalledProgram.FormatBytes(Backup.Bytes) + " · " + string.Join(", ", Backup.Parts).ToLowerInvariant();
    public string RestoreName => "Restore the registry backup of " + Name;
    public string DeleteName => "Delete the registry backup of " + Name;
}

internal sealed class RegistryFindRow(RegistryFind find, bool elevated) : ToolRow
{
    internal RegistryFind Find { get; } = find;
    public override string Name => Find.Path;
    public bool CanCheck => !Find.Protected && (!Find.AllUsers || elevated);
    public override string Detail => (Find.Match switch { RegistryMatch.KeyName => "Key name matches", RegistryMatch.ValueName => "Value name matches", _ => "Value matches" })
        + (Find.Preview.Length > 0 ? ": " + Find.Preview : "") + (Find.Protected ? " · Windows needs it, so it cannot be deleted" : Find.AllUsers && !elevated ? " · needs administrator mode" : "");
    private bool isChecked;
    public bool Checked { get => isChecked; set { if (isChecked == value || (value && !CanCheck)) return; isChecked = value; Changed(nameof(Checked)); } }
}

// Tools > Registry (owner, November 19, 2026; Pro only): full backups (also made before each registry clean unless turned off) and registry search.
public partial class MainWindow
{
    // Test seam: what a full backup exports (the test runner points it at one small key; the product exports this account's registry, and all users' software
    // when elevated).
    internal static Func<bool, IReadOnlyList<RegistryBackupPart>> RegistryBackupPartsFactory = RegistryBackupService.DefaultParts;
    internal static IReadOnlyList<string> RegistrySearchRoots = RegistrySearch.DefaultRoots;
    private List<RegistryFindRow> registryFinds = [];
    private CancellationTokenSource? registrySearchCancellation;
    internal IReadOnlyList<RegistryFindRow> RegistryFindRows => registryFinds;

    private string RegistryBackupFolder => ToolsElevated && toolsDataFolder == CleanupEngine.DataDirectory
        ? AdminOnlyDirectory.Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hush", "Registry full backups"))
        : Path.Combine(toolsDataFolder, "Registry full backups");
    private RegistryBackupService RegistryBackups() => new(RegistryBackupFolder, RegistryBackupPartsFactory(ToolsElevated));

    // Hooked into the engine: runs once before the first registry entry of a clean is touched. Null = done or turned off; otherwise why it failed.
    private string? BackUpRegistryBeforeClean()
    {
        if (!prefs.RegistryBackupBeforeClean) return null;
        try { RegistryBackups().Create("before a registry clean"); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return ex.Message; }
    }

    private void RegistryCard_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        RegistryAutoBackupSwitch.IsChecked = prefs.RegistryBackupBeforeClean;
        RefreshRegistryBackups();
    }

    internal void RefreshRegistryBackups()
    {
        IReadOnlyList<RegistryBackup> list;
        try { list = RegistryBackups().List(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { list = []; }
        RegistryBackupsList.ItemsSource = list.Select(backup => new RegistryBackupRow(backup)).ToList();
        RegistrySummary.Text = list.Count == 0 ? "No full backup yet" : $"{list.Count} full backup{(list.Count == 1 ? "" : "s")} · newest {list[0].When:d MMM, HH:mm}";
    }

    private void RegistryAutoBackup_Click(object sender, RoutedEventArgs e)
    {
        prefs.RegistryBackupBeforeClean = RegistryAutoBackupSwitch.IsChecked == true;
        SavePreferences();
    }

    private bool registryBackupBusy;
    private async void RegistryBackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (registryBackupBusy) return;
        registryBackupBusy = true; RegistryBackupNowButton.IsEnabled = false;
        ShowRegistryBackupStatus("Backing up the registry… This can take a minute.");
        try
        {
            var service = RegistryBackups();
            var made = await Task.Run(() => service.Create("made by you"));
            ShowRegistryBackupStatus($"Backed up ({InstalledProgram.FormatBytes(made.Bytes)}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { ShowRegistryBackupStatus("The backup could not be made: " + ex.Message); }
        finally { registryBackupBusy = false; RegistryBackupNowButton.IsEnabled = true; RefreshRegistryBackups(); }
    }

    private async void RegistryRestore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RegistryBackupRow row || registryBackupBusy) return;
        if (!await AskConsent($"Restore the registry from {row.Name}?", "Hush imports the saved copy back with Windows' own import. Values it holds are put back as they were then; keys made since then stay (an import never deletes). Restart the PC afterwards. Parts for all users need administrator mode.", "Restore", "Cancel")) return;
        registryBackupBusy = true;
        ShowRegistryBackupStatus("Restoring… This can take a minute.");
        var service = RegistryBackups();
        string? problem = await Task.Run(() => service.Restore(row.Backup.File));
        registryBackupBusy = false;
        ShowRegistryBackupStatus(problem ?? "Restored. Restart the PC so every program reads the restored settings.");
    }

    private async void RegistryBackupDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RegistryBackupRow row || registryBackupBusy) return;
        if (!await AskConsent($"Delete the backup of {row.Name}?", "It can no longer be restored after this.", "Delete backup", "Keep it")) return;
        try { RegistryBackups().Delete(row.Backup.File); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowRegistryBackupStatus(ex.Message); }
        RefreshRegistryBackups();
    }

    private void ShowRegistryBackupStatus(string text) { RegistryBackupStatus.Text = text; RegistryBackupStatus.Visibility = Visibility.Visible; }

    // ----- Search -----

    private void RegistrySearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; _ = SearchRegistryAsync(); } }
    private void RegistrySearch_Click(object sender, RoutedEventArgs e) => _ = SearchRegistryAsync();
    private void RegistrySearchStop_Click(object sender, RoutedEventArgs e) => registrySearchCancellation?.Cancel();

    internal async Task SearchRegistryAsync()
    {
        string text = RegistrySearchBox.Text.Trim();
        if (registrySearchCancellation != null) return;
        if (text.Length < 3) { ShowRegistrySearchStatus("Type at least 3 letters."); return; }
        bool keys = RegistrySearchKeys.IsChecked == true, values = RegistrySearchValues.IsChecked == true, data = RegistrySearchData.IsChecked == true;
        if (!keys && !values && !data) { ShowRegistrySearchStatus("Choose at least one of key names, value names or text values."); return; }
        registrySearchCancellation = new CancellationTokenSource();
        var token = registrySearchCancellation.Token;
        RegistrySearchButton.IsEnabled = false; RegistrySearchStopButton.Visibility = Visibility.Visible;
        ShowRegistrySearchStatus("Searching…");
        var roots = RegistrySearchRoots;
        List<RegistryFind> finds;
        try { finds = await Task.Run(() => RegistrySearch.Search(text, roots, keys, values, data, token)); }
        finally { registrySearchCancellation = null; RegistrySearchButton.IsEnabled = true; RegistrySearchStopButton.Visibility = Visibility.Collapsed; }
        bool elevated = ToolsElevated;
        foreach (var old in registryFinds) old.PropertyChanged -= RegistryFindChanged;
        registryFinds = finds.Select(find => new RegistryFindRow(find, elevated)).ToList();
        foreach (var row in registryFinds) row.PropertyChanged += RegistryFindChanged;
        var names = ListGrouping.Names(registryFinds, row => row.Find.Match switch { RegistryMatch.KeyName => "Matching key names", RegistryMatch.ValueName => "Matching value names", _ => "Matching values" },
            row => row.Find.AllUsers ? "all users" : "this account");
        for (int i = 0; i < registryFinds.Count; i++) registryFinds[i].Topic = names[i];
        var view = new System.Windows.Data.ListCollectionView(registryFinds);
        if (registryFinds.Any(row => row.Topic.Length > 0)) view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ToolRow.Topic)));
        RegistryFindsList.ItemsSource = view;
        ShowRegistrySearchStatus(finds.Count == 0 ? $"Nothing in the registry matches \"{text}\"." : $"{finds.Count:N0} match{(finds.Count == 1 ? "" : "es")}" + (finds.Count >= RegistrySearch.MaxFinds ? " (the first 2,000; search for something more exact)" : "") + (token.IsCancellationRequested ? " (stopped early)" : "") + ". Tick what you want to delete.");
        RegistryDeleteButton.Visibility = finds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRegistryDelete();
    }

    private void RegistryFindChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateRegistryDelete();

    private void UpdateRegistryDelete()
    {
        int count = registryFinds.Count(row => row.Checked);
        RegistryDeleteButton.IsEnabled = count > 0;
        RegistryDeleteButton.Content = count > 1 ? $"Delete {count} entries" : "Delete selected";
    }

    private async void RegistryDelete_Click(object sender, RoutedEventArgs e)
    {
        var chosen = registryFinds.Where(row => row.Checked).ToList();
        if (chosen.Count == 0) return;
        if (!await AskConsent($"Delete {chosen.Count} registry entr{(chosen.Count == 1 ? "y" : "ies")}?", "Each one is exported first (to Hush's Registry backups folder; double-clicking an export puts it back), then deleted. A deleted key takes everything under it. A wrong deletion can make a program or Windows misbehave.", "Delete", "Cancel")) return;
        string backups = Path.Combine(toolsDataFolder, "Registry backups");
        bool elevated = ToolsElevated;
        var finds = chosen.Select(row => row.Find).ToList();
        var (deleted, problems) = await Task.Run(() => RegistrySearch.Delete(finds, backups, elevated));
        foreach (var row in chosen) row.PropertyChanged -= RegistryFindChanged;
        registryFinds = registryFinds.Except(chosen).ToList();
        var view = new System.Windows.Data.ListCollectionView(registryFinds);
        if (registryFinds.Any(row => row.Topic.Length > 0)) view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ToolRow.Topic)));
        RegistryFindsList.ItemsSource = view;
        ShowRegistrySearchStatus($"Deleted {deleted} entr{(deleted == 1 ? "y" : "ies")}; each was exported first." + (problems.Count > 0 ? " Left alone: " + string.Join("; ", problems.Take(3)) + (problems.Count > 3 ? "…" : "") : ""));
        UpdateRegistryDelete();
    }

    private void ShowRegistrySearchStatus(string text) { RegistrySearchStatus.Text = text; RegistrySearchStatus.Visibility = Visibility.Visible; }
}
