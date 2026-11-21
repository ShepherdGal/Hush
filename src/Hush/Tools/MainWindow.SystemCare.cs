using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace Hush;

// One update waiting for the person (owner, November 20, 2026: "something that lets people understand there is an update waiting"): a "New" chip on the right
// and an Update button of its own on every line, a soft accent fill and edge while it waits; once it is installed the chip says Updated and the button goes.
internal abstract class UpdateRow : ToolRow
{
    private string? result;
    private bool isChecked, busy, succeeded;
    // What happened to this one (null = nothing yet). Succeeded marks a finished install; a failed one keeps its Update button so it can be tried again.
    internal string? Result { get => result; set { result = value; Changed(nameof(Detail)); ChangedState(); } }
    internal bool Succeeded { get => succeeded; set { succeeded = value; ChangedState(); } }
    internal bool Busy { get => busy; set { if (busy == value) return; busy = value; Changed(nameof(CanUpdate)); } }
    public bool Checked { get => isChecked; set { if (isChecked == value) return; isChecked = value; Changed(nameof(Checked)); } }
    public bool CanUpdate => !succeeded && !busy;
    public Visibility NewVisibility => result == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DoneVisibility => succeeded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UpdateVisibility => succeeded ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WaitingVisibility => succeeded ? Visibility.Collapsed : Visibility.Visible;   // the soft fill and the accent edge
    public string UpdateName => "Update " + Name;
    public string SelectName => "Select " + Name + " to update with others";
    public abstract bool IsDriver { get; }
    public virtual System.Windows.Media.Geometry? Icon => null;
    public System.Windows.Media.Brush TileBrush => ProgramIcons.TileFor(Name);
    // A program's own icon from its installed entry (null = the letter tile stays; drivers show their device icon instead).
    private System.Windows.Media.ImageSource? programIcon;
    public System.Windows.Media.ImageSource? ProgramIcon { get => programIcon; internal set { programIcon = value; Changed(nameof(ProgramIcon)); Changed(nameof(HasProgramIcon)); } }
    public bool HasProgramIcon => programIcon != null;
    // The row's progress bar (EasedProgress): idle, queued, working, done or failed. Programs take about 40 s, a driver batch about 90 s (expected, not measured).
    private string fill = "idle";
    public string FillState { get => fill; internal set { if (fill == value) return; fill = value; Changed(nameof(FillState)); } }
    public double ExpectedSeconds => IsDriver ? 90 : 40;
    private void ChangedState()
    {
        Changed(nameof(CanUpdate)); Changed(nameof(NewVisibility)); Changed(nameof(DoneVisibility)); Changed(nameof(UpdateVisibility)); Changed(nameof(WaitingVisibility));
    }
}

internal sealed class AppUpdateRow(AppUpdate update) : UpdateRow
{
    internal AppUpdate Update { get; } = update;
    public override string Name => Update.Name;
    public override bool IsDriver => false;
    // What kind of program it is (Browsers, Chat and meetings, ...), for the Kind view of the Programs tab.
    public string Kind { get; } = ProgramKinds.Of(update.Name, update.Id);
    public int KindOrder => ProgramKinds.OrderOf(Kind);
    // How much room the program takes now, matched by name from the installed programs (0 = not known); the update's own download size is not known before it starts.
    private long installedBytes;
    public long InstalledBytes { get => installedBytes; internal set { installedBytes = value; Changed(nameof(Detail)); } }
    public override string Detail => (Update.Version.Length > 0 ? Update.Version + " → " : "") + Update.Available
        + (installedBytes > 0 ? " · takes " + InstalledProgram.FormatBytes(installedBytes) : "") + (Result != null ? " · " + Result : "");
}

// One driver update: the device, the version you have and the new one, who makes it, when it came out, its size, and how its install went.
internal sealed class DriverUpdateRow(DriverUpdate driver) : UpdateRow
{
    internal DriverUpdate Driver { get; } = driver;
    public override string Name => Driver.Device;
    public override bool IsDriver => true;
    public override System.Windows.Media.Geometry? Icon => UpdateIcons.Of(Driver.Category);   // the Bluetooth rune for Bluetooth, a monitor for graphics ...
    public override string Detail
    {
        get
        {
            var parts = new List<string>
            {
                Driver.NewVersion.Length == 0 ? "A newer driver" : Driver.InstalledVersion is string have && have != Driver.NewVersion ? $"{have} → {Driver.NewVersion}" : "New version " + Driver.NewVersion
            };
            if (Driver.Manufacturer.Length > 0) parts.Add(Driver.Manufacturer);
            if (Driver.Released is DateTime released) parts.Add("released " + released.ToString("d MMM yyyy"));
            if (Driver.Bytes > 0) parts.Add(InstalledProgram.FormatBytes(Driver.Bytes));
            if (Result != null) parts.Add(Result);
            return string.Join(" · ", parts);
        }
    }
}

// One file in the duplicate/large list. In a duplicate set it can never be ticked when every other copy already is (one copy always stays).
internal sealed class FoundFileRow(FoundFile file, List<FoundFileRow>? set) : ToolRow
{
    internal FoundFile File { get; } = file;
    internal List<FoundFileRow>? Set { get; } = set;
    public override string Name => File.Path;
    public override string Detail => InstalledProgram.FormatBytes(File.Bytes) + " · changed " + File.Modified.ToString("d MMM yyyy");
    private bool isChecked;
    public bool Checked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            if (value && Set != null && Set.Count(row => row.isChecked) >= Set.Count - 1) { Changed(nameof(Checked)); return; } // the last copy stays
            isChecked = value; Changed(nameof(Checked));
        }
    }
}

internal sealed record DiskDrive(string Root, string Label, long TotalBytes = 0, long FreeBytes = 0)
{
    public string CheckName => "Check drive " + Root.TrimEnd('\\');
}

// One place to look in (Duplicate and large files): a usual folder or one the person added; ticked = searched.
internal sealed class PlaceChip(string name, string path) : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    private bool isChecked;
    public bool Checked { get => isChecked; set { if (isChecked == value) return; isChecked = value; PropertyChanged?.Invoke(this, new(nameof(Checked))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

// One drive card of Disk check (owner, November 20, 2026, mockup 4A): a ring showing how full it is, its name and free space, a state chip, its own Check
// button, and an eased bar while Windows reads it.
internal sealed class DiskDriveRow(DiskDrive drive) : System.ComponentModel.INotifyPropertyChanged
{
    internal DiskDrive Drive { get; } = drive;
    public string Name => Drive.Label;
    public string CheckName => Drive.CheckName;
    private double Used => Drive.TotalBytes > 0 ? 100.0 * (Drive.TotalBytes - Drive.FreeBytes) / Drive.TotalBytes : 0;
    public System.Windows.Media.Geometry UsedRing => ProgressRing.Create(Used);
    public string UsedText => Drive.TotalBytes > 0 ? $"{Used:0}%" : "—";
    public string FreeText => Drive.TotalBytes > 0 ? $"{InstalledProgram.FormatBytes(Drive.FreeBytes)} free of {InstalledProgram.FormatBytes(Drive.TotalBytes)}" : "Size not known";
    private string state = "idle", line = "";
    // idle, checking, good, problems, failed
    public string State { get => state; set { state = value; foreach (string name in new[] { nameof(State), nameof(Chip), nameof(ChipVisible), nameof(FillState), nameof(CanCheck) }) Changed(name); } }
    public string Line { get => line; set { line = value; Changed(nameof(Line)); Changed(nameof(LineVisible)); } }
    public Visibility LineVisible => line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Chip => state switch { "checking" => "Checking", "good" => "No problems", "problems" => "Problems found", "failed" => "Not checked", _ => "" };
    public Visibility ChipVisible => Chip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string FillState => state switch { "checking" => "working", "good" or "problems" => "done", "failed" => "failed", _ => "idle" };
    private bool busy;
    internal bool Busy { get => busy; set { busy = value; Changed(nameof(CanCheck)); } }
    public bool CanCheck => !busy && state != "checking";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}

// Tools > Software updates, Duplicate and large files, Disk check, Registry size (owner, November 19, 2026; Pro only).
public partial class MainWindow
{
    internal static Func<ISoftwareUpdater> SoftwareUpdaterFactory = () => new WingetUpdater();
    internal static Func<IDiskCheck> DiskCheckFactory = () => new WindowsDiskCheck();
    internal static Func<string, IRegistryCompactor> RegistryCompactorFactory = folder => new RegistryCompactor(folder);
    internal static Func<string, bool>? RecycleForTests;
    internal static Func<IReadOnlyList<DiskDrive>> DiskDrivesFactory = () => DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
        .Select(drive => new DiskDrive(drive.RootDirectory.FullName, $"{(drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : "Local disk")} ({drive.RootDirectory.FullName.TrimEnd('\\')})", drive.TotalSize, drive.AvailableFreeSpace)).ToList();

    // ----- Software updates: Programs and one tab per device type (owner, November 20, 2026) -----
    // Programs come from Windows' app installer, drivers from Windows Update (only driver updates). Both are read when the tool is opened, side by side; each
    // tab fills in as its answer arrives. A device tab shows only while it holds an update. The summary line says what is new in plain words ("a newer
    // Bluetooth driver (22.150 → 23.40)"), each row says the version you have and the new one. Nothing installs before Update and its confirmation.
    internal static Func<IDriverUpdates> DriverUpdatesFactory = () => new WindowsDriverUpdates();
    private ISoftwareUpdater? softwareUpdater;
    private ISoftwareUpdater Updater => softwareUpdater ??= SoftwareUpdaterFactory();
    private IDriverUpdates? driverUpdates;
    private IDriverUpdates Drivers => driverUpdates ??= DriverUpdatesFactory();
    private List<AppUpdateRow> updateRows = [];
    private List<DriverUpdateRow> driverRows = [];
    private CancellationTokenSource? updatesCancellation;
    private readonly System.Collections.ObjectModel.ObservableCollection<TweaksTab> updateTabs = [new("Programs", "Programs") { IconGeometry = UpdateIcons.Of("Programs") }];
    private string openUpdatesTab = "Programs";
    // The Programs tab's switch (owner, November 20, 2026: "by kind of program, in a switch like Size | Name"): Kind groups the updates under Browsers, Chat and
    // meetings ...; Size puts the programs that take the most room first; Name is A to Z. It replaces the old A–E, F–K letter groups.
    private string updatesSort = "kind";
    internal string UpdatesSort => updatesSort;
    private string? programsNote, driversNote;   // why a side could not be checked (null = checked)
    private bool programsChecked, driversChecked, driversNeedUpdateOn;
    internal IReadOnlyList<AppUpdateRow> AppUpdateRows => updateRows;
    internal IReadOnlyList<DriverUpdateRow> DriverUpdateRows => driverRows;
    internal IReadOnlyList<TweaksTab> UpdateTabs => updateTabs;
    internal string OpenUpdatesTab => openUpdatesTab;

    private void Updates_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        UpdateTabsList.ItemsSource ??= updateTabs;
        if (!programsChecked && !driversChecked && updatesCancellation == null) _ = RefreshAppUpdatesAsync();
    }
    private void RefreshUpdates_Click(object sender, RoutedEventArgs e) => _ = RefreshAppUpdatesAsync();
    private void UpdatesStop_Click(object sender, RoutedEventArgs e) => updatesCancellation?.Cancel();
    private void UpdateTab_Select(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is TweaksTab tab) { ShowUpdatesTab(tab.Key); Motion.Reveal(UpdatesList); } }

    internal async Task RefreshAppUpdatesAsync()
    {
        if (updatesCancellation != null) return;
        UpdateTabsList.ItemsSource ??= updateTabs;
        updatesCancellation = new CancellationTokenSource();
        var token = updatesCancellation.Token;
        programsChecked = driversChecked = driversNeedUpdateOn = false; programsNote = driversNote = null;
        SetUpdatesSummary("Looking for program and driver updates…", "", "MutedBrush");
        UpdatesStatus.Visibility = Visibility.Collapsed; DriversUpdateOnButton.Visibility = Visibility.Collapsed;
        UpdatesStopButton.Visibility = Visibility.Visible; RefreshUpdatesButton.IsEnabled = false;
        // Never blank while waiting (owner, November 20, 2026, mockup B): the named steps tick off as each answer arrives, over one easing bar.
        UpdatesWait.Begin("Looking for updates", 60, "Your programs", "Drivers from Windows Update");
        // Both questions go out at once; each side fills its tabs when its answer arrives.
        var programs = Updater.Available ? Updater.ListAsync(token) : null;
        var drivers = Drivers.SearchAsync(token);
        try
        {
            if (programs == null) programsNote = "Windows' app installer is not on this PC, so programs can't be checked. Install \"App Installer\" from the Microsoft Store to use this.";
            else
            {
                try
                {
                    var list = await programs;
                    foreach (var old in updateRows) old.PropertyChanged -= UpdateRowChanged;
                    updateRows = list.Select(update => new AppUpdateRow(update)).OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                    foreach (var row in updateRows) row.PropertyChanged += UpdateRowChanged;
                    if (updateRows.Count > 0)
                    {
                        // How much room each program takes now and its own icon (owner, November 21, 2026: "apps still don't have their real icons"), read once on a
                        // worker thread and matched by name from the installed programs; a program that can't be matched keeps its letter tile.
                        var installed = Programs;
                        var names = updateRows.Select(row => row.Name).ToList();
                        var loader = ProgramIcons.Loader;
                        var found = await Task.Run(() => MatchInstalled(installed, names, loader));
                        foreach (var row in updateRows)
                            if (found.TryGetValue(row.Name, out var match)) { row.InstalledBytes = match.Bytes; row.ProgramIcon = match.Icon; }
                    }
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { programsNote = "Programs can't be checked right now: " + ex.Message; }
            }
            programsChecked = programsNote == null;
            if (!programsChecked) ClearRows(ref updateRows);   // a check that failed leaves no old rows behind
            UpdatesWait.Done(0, programsNote != null ? "Could not be checked" : updateRows.Count == 0 ? "All up to date" : $"{updateRows.Count} update{(updateRows.Count == 1 ? "" : "s")} found");
            UpdatesWait.Now(1);
            ShowUpdatesResults();
            try
            {
                var list = await drivers;
                foreach (var old in driverRows) old.PropertyChanged -= UpdateRowChanged;
                driverRows = list.Select(driver => new DriverUpdateRow(driver)).OrderBy(row => Array.IndexOf(DriverCategories.Order, row.Driver.Category)).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                foreach (var row in driverRows) row.PropertyChanged += UpdateRowChanged;
                driversChecked = true;
            }
            catch (WindowsUpdateOffException) { driversNote = "Windows Update is off, so Hush can't look for driver updates."; driversNeedUpdateOn = true; ClearRows(ref driverRows); }
            catch (TimeoutException) { driversNote = "Windows Update took too long to answer about drivers. Try Check again later."; ClearRows(ref driverRows); }
            catch (DriverSearchException ex) { driversNote = $"Driver updates can't be checked right now (Windows Update error 0x{ex.Code:X8})."; ClearRows(ref driverRows); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or IOException)
            { driversNote = "Driver updates can't be checked right now" + (ex is System.Runtime.InteropServices.COMException com ? $" (error 0x{com.HResult:X8})." : "."); ClearRows(ref driverRows); }
        }
        catch (OperationCanceledException) { SetUpdatesSummary("Stopped", "", "MutedBrush"); UpdatesWait.Cancel(); }
        finally { updatesCancellation = null; UpdatesStopButton.Visibility = Visibility.Collapsed; RefreshUpdatesButton.IsEnabled = true; UpdateUpdateButton(); }
        if (!token.IsCancellationRequested)
        {
            UpdatesWait.Done(1, driversNote != null ? "Could not be checked" : driverRows.Count == 0 ? "No newer drivers" : $"{driverRows.Count} driver update{(driverRows.Count == 1 ? "" : "s")} found");
            ShowUpdatesResults();
            await UpdatesWait.FinishAsync();
        }
    }

    // Owner, November 21, 2026 ("Split"): Bluetooth driver updates stay here as one more device tab, but the Bluetooth health check lives only in Device repair;
    // Software Updates no longer looks at Bluetooth or links there.

    // Each update's installed program, by name: the same name first, else one name that starts with the other ("Mozilla Firefox" and "Mozilla Firefox (x64
    // en-US)"), comparing letters and digits only and never on fewer than four of them. Returns its size and its own icon (null when it has none).
    internal static Dictionary<string, (long Bytes, System.Windows.Media.ImageSource? Icon)> MatchInstalled(IProgramSystem system, IReadOnlyList<string> names, Func<string, System.Windows.Media.ImageSource?> loader)
    {
        var result = new Dictionary<string, (long, System.Windows.Media.ImageSource?)>(StringComparer.CurrentCultureIgnoreCase);
        IReadOnlyList<InstalledProgram> programs;
        try { programs = system.List(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return result; }   // letter tiles, names order
        static string Key(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var keyed = programs.Select(program => (Key: Key(program.Name), Program: program)).Where(entry => entry.Key.Length > 0).ToList();
        foreach (string name in names)
        {
            string key = Key(name);
            if (key.Length < 4 || result.ContainsKey(name)) continue;
            var candidates = keyed.Where(entry => entry.Key == key).ToList();
            if (candidates.Count == 0) candidates = keyed.Where(entry => Math.Min(entry.Key.Length, key.Length) >= 4 && (entry.Key.StartsWith(key) || key.StartsWith(entry.Key))).ToList();
            if (candidates.Count == 0) continue;
            var best = candidates.MaxBy(entry => entry.Program.Bytes).Program;
            System.Windows.Media.ImageSource? icon = null;
            foreach (var candidate in candidates.Select(entry => entry.Program).Prepend(best))
            {
                if (string.IsNullOrWhiteSpace(candidate.DisplayIcon)) continue;
                try { icon = loader(candidate.DisplayIcon!); } catch (Exception) { icon = null; }
                if (icon != null) break;
            }
            result[name] = (best.Bytes, icon);
        }
        return result;
    }

    // Old rows can't be installed once a new check has started (the search that found them is gone), so a side that failed shows none.
    private void ClearRows<T>(ref List<T> rows) where T : ToolRow { foreach (var row in rows) row.PropertyChanged -= UpdateRowChanged; rows = []; }

    // Tabs, the open tab's rows, the smart summary and Home's tile, from what is known now.
    private void ShowUpdatesResults()
    {
        var wanted = new List<(string Key, int Count)> { ("Programs", updateRows.Count) };
        foreach (string category in DriverCategories.Order)
        {
            int count = driverRows.Count(row => row.Driver.Category == category);
            if (count > 0) wanted.Add((category, count));
        }
        if (!updateTabs.Select(tab => tab.Key).SequenceEqual(wanted.Select(item => item.Key)))
        {
            updateTabs.Clear();
            foreach (var (key, _) in wanted) updateTabs.Add(new TweaksTab(key, key) { IconGeometry = UpdateIcons.Of(key) });
        }
        foreach (var tab in updateTabs) tab.Count = wanted.Single(item => item.Key == tab.Key).Count is var n && (n > 0 || tab.Key != "Programs" || programsChecked) ? n.ToString() : "";
        if (!updateTabs.Any(tab => tab.Key == openUpdatesTab)) openUpdatesTab = "Programs";
        ShowUpdatesTab(openUpdatesTab);
        ShowUpdatesSummary();
        var notes = new[] { programsNote, driversNote }.Where(note => note != null).ToList();
        if (notes.Count > 0) ShowUpdatesStatus(string.Join(" ", notes)); else UpdatesStatus.Visibility = Visibility.Collapsed;
        DriversUpdateOnButton.Visibility = driversNeedUpdateOn ? Visibility.Visible : Visibility.Collapsed;
        if (programsChecked || driversChecked) RecordAppUpdates(updateRows.Count + driverRows.Count);   // Home's Updates tile
    }

    internal void ShowUpdatesTab(string key)
    {
        openUpdatesTab = key;
        foreach (var tab in updateTabs) tab.Open = tab.Key == key;
        if (key == "Programs")
        {
            var view = new System.Windows.Data.ListCollectionView(updateRows);
            if (updatesSort == "size")
            {
                view.SortDescriptions.Add(new(nameof(AppUpdateRow.InstalledBytes), System.ComponentModel.ListSortDirection.Descending));
                view.SortDescriptions.Add(new(nameof(ToolRow.Name), System.ComponentModel.ListSortDirection.Ascending));
            }
            else if (updatesSort == "name") view.SortDescriptions.Add(new(nameof(ToolRow.Name), System.ComponentModel.ListSortDirection.Ascending));
            else
            {
                view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(AppUpdateRow.Kind)));
                view.SortDescriptions.Add(new(nameof(AppUpdateRow.KindOrder), System.ComponentModel.ListSortDirection.Ascending));
                view.SortDescriptions.Add(new(nameof(ToolRow.Name), System.ComponentModel.ListSortDirection.Ascending));
            }
            UpdatesList.ItemsSource = view;
            UpdatesEmpty.Text = programsChecked && updateRows.Count == 0 ? "Your programs are up to date." : "";
        }
        else
        {
            UpdatesList.ItemsSource = driverRows.Where(row => row.Driver.Category == key).ToList();
            UpdatesEmpty.Text = "";
        }
        UpdatesEmpty.Visibility = UpdatesEmpty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesSortBar.Visibility = key == "Programs" && updateRows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesActionBar.Visibility = OpenTabRows().Any() ? Visibility.Visible : Visibility.Collapsed;
        ShowUpdatesSort();
        UpdateSelectAllButton();
    }

    private void UpdatesSort_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string sort || sort == updatesSort) return;
        updatesSort = sort;
        ShowUpdatesTab(openUpdatesTab);
        Motion.Reveal(UpdatesList);
    }

    // The switch's pill springs to the chosen view (the old one lets go first), as on the Programs tool.
    private void ShowUpdatesSort()
    {
        var buttons = new[] { UpdatesSortKindButton, UpdatesSortSizeButton, UpdatesSortNameButton };
        foreach (var button in buttons) button.SetResourceReference(ForegroundProperty, (string)button.Tag == updatesSort ? "TealTextBrush" : "MutedBrush");
        foreach (var button in buttons) if ((string)button.Tag != updatesSort) SegmentPanel.SetIsSelected(button, false);
        foreach (var button in buttons) if ((string)button.Tag == updatesSort) SegmentPanel.SetIsSelected(button, true);
    }

    // The rows of the open tab: what Select all ticks.
    private IEnumerable<UpdateRow> OpenTabRows() => openUpdatesTab == "Programs" ? updateRows : driverRows.Where(row => row.Driver.Category == openUpdatesTab);

    private void SelectAllUpdates_Click(object sender, RoutedEventArgs e)
    {
        var rows = OpenTabRows().Where(row => !row.Succeeded).ToList();
        bool tick = rows.Any(row => !row.Checked);
        foreach (var row in rows) row.Checked = tick;
    }

    private void UpdateSelectAllButton()
    {
        var rows = OpenTabRows().Where(row => !row.Succeeded).ToList();
        SelectAllUpdatesButton.Visibility = rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllUpdatesButton.Content = rows.Count > 0 && rows.All(row => row.Checked) ? "Clear" : "Select all";
    }

    // "5 updates ready" in the accent color, then only a short split in muted words: "3 programs, 2 drivers" (owner, November 20, 2026: the first thing the card
    // says is that something is waiting; the longer line with device names and versions was "too cluttered"; the tabs show the rest). Up to date reads in green.
    private void ShowUpdatesSummary()
    {
        var (headline, detail, ready) = UpdatesSummaryParts();
        SetUpdatesSummary(headline, detail.Length > 0 ? " · " + detail : "", ready ? "TealTextBrush" : headline.Contains("up to date") || headline.StartsWith("Everything") ? "GoodBrush" : "MutedBrush");
    }

    private void SetUpdatesSummary(string headline, string detail, string headlineBrush)
    {
        UpdatesHeadline.Text = headline; UpdatesDetail.Text = detail;
        UpdatesHeadline.FontWeight = headlineBrush == "MutedBrush" ? FontWeights.Normal : FontWeights.SemiBold;
        UpdatesHeadline.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, headlineBrush);
    }

    // What the card's summary line shows right now (the headline and the muted rest).
    internal string UpdatesSummaryShown => UpdatesHeadline.Text + UpdatesDetail.Text;
    internal string SmartUpdatesSummary() { var (headline, detail, _) = UpdatesSummaryParts(); return headline + (detail.Length > 0 ? " · " + detail : ""); }

    private (string Headline, string Detail, bool Ready) UpdatesSummaryParts()
    {
        // Only what still waits counts: an update that was just installed is no longer "ready".
        var waitingDrivers = driverRows.Where(row => !row.Succeeded).ToList();
        int waitingPrograms = updateRows.Count(row => !row.Succeeded), total = waitingDrivers.Count + waitingPrograms;
        var parts = new List<string>();
        if (waitingPrograms > 0) parts.Add($"{waitingPrograms} program{(waitingPrograms == 1 ? "" : "s")}");
        if (waitingDrivers.Count > 0) parts.Add($"{waitingDrivers.Count} driver{(waitingDrivers.Count == 1 ? "" : "s")}");
        if (total > 0) return ($"{total} update{(total == 1 ? "" : "s")} ready", string.Join(", ", parts), true);
        if (driverRows.Count + updateRows.Count > 0) return ("Everything you chose is updated", "", false);
        if (programsChecked && driversChecked) return ("Your programs and drivers are up to date", "", false);
        if (programsChecked && updatesCancellation != null) return ("Your programs are up to date", "looking for driver updates…", false);
        if (programsChecked) return ("Your programs are up to date", "", false);
        if (driversChecked) return ("Your drivers are up to date", "", false);
        return (updatesCancellation != null ? "Looking for program and driver updates…" : "Updates can't be checked right now", "", false);
    }

    private void DriversUpdateOn_Click(object sender, RoutedEventArgs e) => UpdateBackOn_Click(sender, e);

    private void UpdateRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(AppUpdateRow.Checked)) { UpdateUpdateButton(); UpdateSelectAllButton(); } }
    private void UpdateUpdateButton()
    {
        int count = updateRows.Count(row => row.Checked && !row.Succeeded) + driverRows.Count(row => row.Checked && !row.Succeeded);
        UpdateSelectedButton.IsEnabled = count > 0 && updatesCancellation == null;
        UpdateSelectedButton.Content = count > 1 ? $"Update {count}" : "Update selected";
    }

    private async void UpdateSelected_Click(object sender, RoutedEventArgs e) =>
        await RunUpdatesAsync(updateRows.Where(row => row.Checked && !row.Succeeded).ToList(), driverRows.Where(row => row.Checked && !row.Succeeded).ToList());

    // The Update button on one line (owner, November 20, 2026): the same confirmation and the same install, for that one update.
    private async void UpdateRow_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag)
        {
            case AppUpdateRow program: await RunUpdatesAsync([program], []); break;
            case DriverUpdateRow driver: await RunUpdatesAsync([], [driver]); break;
        }
    }

    private void SetUpdatesBusy(bool busy) { foreach (var row in updateRows) row.Busy = busy; foreach (var row in driverRows) row.Busy = busy; }

    internal async Task RunUpdatesAsync(List<AppUpdateRow> programs, List<DriverUpdateRow> drivers)
    {
        if (programs.Count + drivers.Count == 0 || updatesCancellation != null) return;
        // Installing drivers needs administrator mode; say so before anything happens.
        if (drivers.Count > 0 && !ToolsElevated)
        {
            if (!await AskConsent("Restart as administrator?", "Installing drivers needs administrator mode. Hush restarts with Windows' own permission prompt; open Software Updates again after the restart and tick the drivers.\n\nNothing has been installed.", "Restart as administrator", "Cancel")) return;
            if (await Task.Run(() => ElevatedLauncher(""))) { ExitForRestart(); return; }
            ShowUpdatesStatus("Windows' permission prompt was declined or could not open, so nothing was installed.");
            return;
        }
        string What(int p, int d) => string.Join(" and ", new[] { p > 0 ? $"{p} program{(p == 1 ? "" : "s")}" : "", d > 0 ? $"{d} driver{(d == 1 ? "" : "s")}" : "" }.Where(part => part.Length > 0));
        var body = new List<string>();
        if (programs.Count > 0) body.Add("Programs update one by one through Windows' app installer, quietly, from each publisher's official source. Close them first; an open program may not update.");
        if (drivers.Count > 0) body.Add("Drivers come from Windows Update for your own devices. A device can switch off for a moment while its driver installs, and Windows may need a restart to finish.");
        body.Add("The publisher's license is accepted for you.");
        if (!await AskConsent($"Update {What(programs.Count, drivers.Count)}?", string.Join("\n\n", body), "Update", "Cancel")) return;
        updatesCancellation = new CancellationTokenSource();
        var token = updatesCancellation.Token;
        UpdatesStopButton.Visibility = Visibility.Visible; RefreshUpdatesButton.IsEnabled = false; SetUpdatesBusy(true); UpdateUpdateButton();
        int ok = 0, failed = 0, driversOk = 0, driversFailed = 0; bool restart = false;
        try
        {
            // Every chosen line shows its bar: an empty track while it waits its turn, then an eased fill while it downloads and installs (owner, November 20, 2026).
            foreach (var row in programs.Cast<UpdateRow>().Concat(drivers)) { row.Result = "waiting…"; row.FillState = "queued"; }
            foreach (var row in programs)
            {
                if (token.IsCancellationRequested) break;
                row.Result = "downloading and updating…"; row.FillState = "working";
                ShowUpdatesStatus($"Updating {row.Name}… ({ok + failed + 1} of {programs.Count})");
                var (success, message) = await Updater.UpdateAsync(row.Update, token);
                row.FillState = success ? "done" : "failed";
                row.Result = message; row.Checked = false; row.Succeeded = success;
                if (success) ok++; else failed++;
            }
            if (drivers.Count > 0 && !token.IsCancellationRequested)
            {
                foreach (var row in drivers) { row.Result = "downloading and installing…"; row.FillState = "working"; }
                var progress = new Progress<string>(ShowUpdatesStatus);
                var result = await Drivers.InstallAsync(drivers.Select(row => row.Driver).ToList(), progress, token);
                foreach (var row in drivers)
                {
                    row.Result = result.Messages.TryGetValue(row.Driver.UpdateId, out var outcome) ? outcome : "Not installed";
                    row.Checked = false; row.Succeeded = row.Result.StartsWith("Installed", StringComparison.Ordinal);
                    row.FillState = row.Succeeded ? "done" : "failed";
                }
                driversOk = result.Installed; driversFailed = result.Failed + drivers.Count - result.Installed - result.Failed; restart = result.RestartNeeded;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException or WindowsUpdateOffException)
        { foreach (var row in drivers.Where(row => row.FillState is "queued" or "working")) row.Result = "Not installed"; driversFailed = drivers.Count - driversOk; }
        finally
        {
            // Anything that never got its turn (stopped) or was cut off loses its bar and says so.
            foreach (var row in programs.Cast<UpdateRow>().Concat(drivers).Where(row => row.FillState is "queued" or "working"))
            {
                row.FillState = row.FillState == "working" ? "failed" : "idle";
                if (row.Result is "waiting…" or "downloading and updating…" or "downloading and installing…") row.Result = token.IsCancellationRequested ? "stopped" : "Not installed";
            }
            updatesCancellation = null; UpdatesStopButton.Visibility = Visibility.Collapsed; RefreshUpdatesButton.IsEnabled = true;
            SetUpdatesBusy(false); UpdateUpdateButton(); UpdateSelectAllButton(); ShowUpdatesSummary();
        }
        var said = new List<string>();
        if (programs.Count > 0) said.Add($"Updated {ok} program{(ok == 1 ? "" : "s")}" + (failed > 0 ? $"; {failed} not updated" : ""));
        if (drivers.Count > 0) said.Add($"Installed {driversOk} driver{(driversOk == 1 ? "" : "s")}" + (driversFailed > 0 ? $"; {driversFailed} not installed" : ""));
        ShowUpdatesStatus(string.Join(". ", said) + (failed + driversFailed > 0 ? " (the reason is on each line)" : "") + (token.IsCancellationRequested ? "; stopped" : "") + "."
            + (restart ? " Restart Windows to finish installing the drivers." : ""));
    }

    private void ShowUpdatesStatus(string text) { UpdatesStatus.Text = text; UpdatesStatus.Visibility = Visibility.Visible; }

    // ----- Duplicate and large files -----

    private readonly ObservableCollection<string> duplicateFolders = [];
    private List<FoundFileRow> foundRows = [];
    private CancellationTokenSource? duplicateCancellation;
    internal ObservableCollection<string> DuplicateFolders => duplicateFolders;
    internal IReadOnlyList<FoundFileRow> FoundFileRows => foundRows;
    internal static long DuplicateMinimumBytes = FileFinder.DefaultMinimumDuplicate, LargeFileBytes = FileFinder.DefaultLargeFile;

    // Owner, November 20, 2026 (mockup 5A, "pages look empty and not understood"): a friendly start: what it does in one line, the usual places as one-click
    // chips (Downloads and Desktop ticked to begin with: searching only reads), any other folder as its own chip, and "Find in N places".
    internal static Func<IReadOnlyList<(string Name, string Path, bool Ticked)>> DuplicatePlacesFactory = () =>
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = new List<(string, string, bool)>
        {
            ("Downloads", Path.Combine(home, "Downloads"), true), ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), true),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), false), ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), false),
            ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), false), ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), false),
        };
        return places.Where(place => place.Item2.Length > 0 && Directory.Exists(place.Item2)).ToList();
    };
    private readonly ObservableCollection<PlaceChip> placeChips = [];
    internal IReadOnlyList<PlaceChip> PlaceChips => placeChips;

    private void Duplicates_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || placeChips.Count > 0) return;
        DuplicateFoldersList.ItemsSource = placeChips;
        foreach (var (name, path, ticked) in DuplicatePlacesFactory()) AddPlaceChip(name, path, ticked);
        RefreshDuplicateStart();
    }

    private void AddPlaceChip(string name, string path, bool ticked)
    {
        var existing = placeChips.FirstOrDefault(chip => chip.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { existing.Checked = existing.Checked || ticked; return; }
        var chip = new PlaceChip(name, path) { Checked = ticked };
        chip.PropertyChanged += (_, _) => { SyncDuplicateFolders(); RefreshDuplicateStart(); };
        placeChips.Add(chip);
        SyncDuplicateFolders();
    }

    private void SyncDuplicateFolders()
    {
        var wanted = placeChips.Where(chip => chip.Checked).Select(chip => chip.Path).ToList();
        foreach (string gone in duplicateFolders.Where(folder => !wanted.Contains(folder, StringComparer.OrdinalIgnoreCase)).ToList()) duplicateFolders.Remove(gone);
        foreach (string add in wanted) if (!duplicateFolders.Contains(add, StringComparer.OrdinalIgnoreCase)) duplicateFolders.Add(add);
    }

    private void RefreshDuplicateStart()
    {
        int count = duplicateFolders.Count;
        DuplicateSearchButton.IsEnabled = count > 0 && duplicateCancellation == null;
        DuplicateSearchButton.Content = count == 0 ? "Choose a place first" : $"Find in {count} place{(count == 1 ? "" : "s")}";
    }

    internal void AddDuplicateFolders(IEnumerable<string> folders)
    {
        DuplicateFoldersList.ItemsSource ??= placeChips;
        foreach (string folder in folders) AddPlaceChip(Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } name ? name : folder, folder, true);
        RefreshDuplicateStart();
    }

    private void DuplicateAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "Choose folders or drives to look in" };
        if (dialog.ShowDialog(this) == true) AddDuplicateFolders(dialog.FolderNames);
    }


    private void DuplicateStop_Click(object sender, RoutedEventArgs e) => duplicateCancellation?.Cancel();
    private void DuplicateSearch_Click(object sender, RoutedEventArgs e) => _ = FindDuplicatesAsync();

    internal async Task FindDuplicatesAsync()
    {
        if (duplicateCancellation != null || duplicateFolders.Count == 0) return;
        duplicateCancellation = new CancellationTokenSource();
        var token = duplicateCancellation.Token;
        DuplicateSearchButton.IsEnabled = false; DuplicateStopButton.Visibility = Visibility.Visible;
        var folders = duplicateFolders.ToList();
        // Never blank while it works (owner, November 20, 2026): the steps tick off with what the search reports.
        DuplicatesWait.Begin("Looking for copies and big files", 30, "Listing the files", "Comparing files of the same size", "Done");
        DuplicateSummaryTiles.Visibility = Visibility.Collapsed;
        var progress = new Progress<string>(text =>
        {
            DuplicatesSummary.Text = text;
            if (text.StartsWith("Comparing", StringComparison.Ordinal)) DuplicatesWait.Now(1, text); else DuplicatesWait.Now(0, text);
        });
        long minimum = DuplicateMinimumBytes, large = LargeFileBytes;
        FileSearchResult result;
        try { result = await Task.Run(() => FileFinder.Search(folders, minimum, large, progress, token)); }
        finally { duplicateCancellation = null; DuplicateStopButton.Visibility = Visibility.Collapsed; RefreshDuplicateStart(); }
        _ = DuplicatesWait.FinishAsync();
        foreach (var old in foundRows) old.PropertyChanged -= FoundRowChanged;
        var rows = new List<FoundFileRow>();
        foreach (var set in result.Duplicates.Take(300))
        {
            var members = new List<FoundFileRow>();
            foreach (var file in set.Files) members.Add(new FoundFileRow(file, members) { Topic = $"Copies of {Path.GetFileName(set.Files[0].Path)} · {set.Files.Count} × {InstalledProgram.FormatBytes(set.Bytes)}" });
            rows.AddRange(members);
        }
        rows.AddRange(result.Large.Select(file => new FoundFileRow(file, null) { Topic = $"Largest files (over {InstalledProgram.FormatBytes(large)})" }));
        foundRows = rows;
        foreach (var row in foundRows) row.PropertyChanged += FoundRowChanged;
        var view = new System.Windows.Data.ListCollectionView(foundRows);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ToolRow.Topic)));
        DuplicateResultsList.ItemsSource = view;
        long wasted = result.Duplicates.Sum(set => set.Wasted);
        DuplicatesSummary.Text = $"{result.FilesSeen:N0} files looked at · {result.Duplicates.Count:N0} set{(result.Duplicates.Count == 1 ? "" : "s")} of copies" + (wasted > 0 ? $" ({InstalledProgram.FormatBytes(wasted)} in extra copies)" : "") + $" · {result.Large.Count} large files" + (result.Stopped ? " · stopped early" : "");
        DuplicateRecycleButton.Visibility = foundRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // A summary first (mockup 5A): what is in extra copies, how many big files, and what could be freed at most.
        long largeBytes = result.Large.Sum(file => file.Bytes);
        DuplicateCopiesValue.Text = InstalledProgram.FormatBytes(wasted); DuplicateCopiesLabel.Text = $"in {result.Duplicates.Sum(set => set.Files.Count - 1):N0} extra copies";
        DuplicateLargeValue.Text = result.Large.Count.ToString("N0"); DuplicateLargeLabel.Text = $"files over {InstalledProgram.FormatBytes(large)}";
        DuplicateFreeValue.Text = InstalledProgram.FormatBytes(wasted + largeBytes); DuplicateFreeLabel.Text = "you could free at most";
        DuplicateSummaryTiles.Visibility = Visibility.Visible;
        DuplicatesEmptyResult.Visibility = foundRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRecycleButton();
    }

    private void FoundRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateRecycleButton();
    private void UpdateRecycleButton()
    {
        var chosen = foundRows.Where(row => row.Checked).ToList();
        DuplicateRecycleButton.IsEnabled = chosen.Count > 0;
        DuplicateRecycleButton.Content = chosen.Count > 0 ? $"Move {chosen.Count} to Recycle Bin ({InstalledProgram.FormatBytes(chosen.Sum(row => row.File.Bytes))})" : "Move to Recycle Bin";
    }

    private async void DuplicateRecycle_Click(object sender, RoutedEventArgs e)
    {
        var chosen = foundRows.Where(row => row.Checked).ToList();
        if (chosen.Count == 0) return;
        if (!await AskConsent($"Move {chosen.Count} file{(chosen.Count == 1 ? "" : "s")} to the Recycle Bin?", "They go to the Recycle Bin, so you can restore them from there. One copy of every duplicate always stays where it is.", "Move to Recycle Bin", "Cancel")) return;
        var paths = chosen.Select(row => row.File.Path).ToList();
        var recycle = RecycleForTests;
        var (moved, problems) = await Task.Run(() => FileFinder.Recycle(paths, recycle));
        foreach (var row in chosen) row.PropertyChanged -= FoundRowChanged;
        foundRows = foundRows.Except(chosen.Where(row => !File.Exists(row.File.Path))).ToList();
        // A set with only one copy left is no longer a set of copies.
        foundRows = foundRows.Where(row => row.Set == null || row.Set.Count(member => foundRows.Contains(member)) > 1).ToList();
        foreach (var row in foundRows.Where(row => row.Set != null)) row.Set!.RemoveAll(member => !foundRows.Contains(member));
        foreach (var row in foundRows) row.Checked = false;
        var view = new System.Windows.Data.ListCollectionView(foundRows);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ToolRow.Topic)));
        DuplicateResultsList.ItemsSource = view;
        DuplicatesStatus.Text = $"Moved {moved} file{(moved == 1 ? "" : "s")} to the Recycle Bin." + (problems.Count > 0 ? " Not moved: " + string.Join("; ", problems.Take(3)) : "");
        DuplicatesStatus.Visibility = Visibility.Visible;
        UpdateRecycleButton();
    }

    // ----- Disk check -----

    private IDiskCheck? diskCheck;
    private IDiskCheck Disks => diskCheck ??= DiskCheckFactory();
    private bool diskBusy;
    private string? diskToRepair;

    private List<DiskDriveRow> diskRows = [];
    internal IReadOnlyList<DiskDriveRow> DiskRows => diskRows;

    // Opening Disk check (owner, November 20, 2026, mockup 4A): the drives as cards straight away, and the drives' health read with the waiting steps (never
    // blank); health is said in friendly words with a chip.
    private async void Disk_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (diskRows.Count == 0) { diskRows = DiskDrivesFactory().Select(drive => new DiskDriveRow(drive)).ToList(); DiskDrivesList.ItemsSource = diskRows; }
        if (DiskHealthText.Text.Length > 0) return;
        DiskWait.Begin("Reading drive health", 5, "Asking Windows how your drives are doing");
        var health = await Disks.HealthAsync();
        await DiskWait.FinishAsync();
        bool allGood = health.Count > 0 && health.All(drive => drive.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase));
        DiskHealthText.Text = health.Count == 0 ? "Windows did not report drive health."
            : allGood ? (health.Count == 1 ? $"{health[0].Name} ({health[0].Media}) is Healthy" : $"All {health.Count} drives are Healthy")
            : string.Join(" · ", health.Select(drive => $"{drive.Name}: {drive.Health}"));
        DiskHealthChip.Text = health.Count == 0 ? "Unknown" : allGood ? "Healthy" : "Worth a look";
        DiskHealthChipFill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, allGood ? "GoodBrush" : "MeterWarnBrush");
        DiskHealthChip.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, allGood ? "GoodBrush" : "MeterWarnBrush");
        DiskHealthRow.Visibility = Visibility.Visible;
        RecordDiskHealth(health);   // Home's Disk health tile
    }

    private async void DiskCheck_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DiskDriveRow row || diskBusy) return;
        var drive = row.Drive;
        diskBusy = true; DiskRepairButton.Visibility = Visibility.Collapsed;
        foreach (var other in diskRows) other.Busy = true;
        row.State = "checking"; row.Line = "Reading the file system; nothing on the drive is changed";
        DiskWait.Begin($"Checking {drive.Label}", 120, "Reading the file system (read-only)");
        DiskCheckResult result;
        try { result = await Disks.CheckAsync(drive.Root, CancellationToken.None); }
        finally { diskBusy = false; foreach (var other in diskRows) other.Busy = false; }
        if (result.Ran) await DiskWait.FinishAsync(); else DiskWait.Cancel();
        row.State = !result.Ran ? "failed" : result.Problems ? "problems" : "good";
        row.Line = result.Summary;
        ShowDiskStatus($"{drive.Label} · {result.Summary}");
        DiskSummary.Text = result.Ran ? (result.Problems ? $"Problems found on {drive.Root.TrimEnd('\\')}" : $"{drive.Root.TrimEnd('\\')} has no problems") : DiskSummary.Text;
        diskToRepair = result.Problems ? drive.Root : null;
        DiskRepairButton.Visibility = result.Problems ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DiskRepair_Click(object sender, RoutedEventArgs e)
    {
        if (diskToRepair is not { } drive) return;
        if (!await AskConsent($"Repair {drive.TrimEnd('\\')} at the next restart?", "Windows checks and repairs the drive while the PC starts, before anything uses it. The next start takes longer; do not turn the PC off while it runs.", "Repair at next restart", "Cancel")) return;
        string? problem = await Disks.ScheduleRepairAsync(drive);
        ShowDiskStatus(problem ?? $"Done. Windows repairs {drive.TrimEnd('\\')} the next time the PC starts.");
        if (problem == null) DiskRepairButton.Visibility = Visibility.Collapsed;
    }

    private void ShowDiskStatus(string text) { DiskStatus.Text = text; DiskStatus.Visibility = Visibility.Visible; }

    // ----- Registry size -----

    private async void RegistryMeasure_Click(object sender, RoutedEventArgs e)
    {
        if (!ToolsElevated) { RegistrySizeText.Text = "Measuring needs administrator mode (Windows only lets administrators save a copy of the registry)."; RegistrySizeText.Visibility = Visibility.Visible; return; }
        RegistryMeasureButton.IsEnabled = false;
        RegistrySizeText.Text = "Measuring…"; RegistrySizeText.Visibility = Visibility.Visible;
        try
        {
            var sizes = await RegistryCompactorFactory(Path.Combine(toolsDataFolder, "Registry measure")).MeasureAsync(CancellationToken.None);
            long saving = sizes.Sum(size => size.Saving);
            RegistrySizeText.Text = sizes.Count == 0 ? "Windows did not let Hush measure the registry." : string.Join("\n", sizes.Select(size => $"{size.Name}: {InstalledProgram.FormatBytes(size.Current)} now, {InstalledProgram.FormatBytes(size.Compact)} packed")) +
                $"\n\nA compaction would save about {InstalledProgram.FormatBytes(saving)}. That is too little to notice, so Hush leaves the registry files as they are.";
            RegistrySizeSummary.Text = sizes.Count == 0 ? RegistrySizeSummary.Text : $"Could save about {InstalledProgram.FormatBytes(saving)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RegistrySizeText.Text = "Measuring failed: " + ex.Message; }
        finally { RegistryMeasureButton.IsEnabled = true; RegistryWait.Cancel(); RegistrySizeText.Visibility = Visibility.Visible; }
    }
}
