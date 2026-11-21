using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Hush;

// One row of a Tools list. Rows notify only what changes (the tick).
internal abstract class ToolRow : INotifyPropertyChanged
{
    public abstract string Name { get; }
    public abstract string Detail { get; }
    public string Initial { get { char first = Name.FirstOrDefault(char.IsLetterOrDigit); return first == default ? "?" : char.ToUpperInvariant(first).ToString(); } }
    public string Topic { get; internal set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}

internal sealed class ProgramRow(InstalledProgram program) : ToolRow
{
    internal InstalledProgram Program { get; } = program;
    public override string Name => Program.Name;
    public override string Detail => Program.UninstallerMissing ? (Program.Detail.Length > 0 ? Program.Detail + " · " : "") + "its uninstaller is missing" : Program.Detail;
    public string ActionLabel => Program.UninstallerMissing ? "Remove leftovers" : "Uninstall";
    public string ActionName => ActionLabel + " " + Name;
    public string SelectName => "Select " + Name + " to uninstall with others";
    private bool isChecked;
    public bool Checked { get => isChecked; set { if (isChecked == value) return; isChecked = value; Changed(nameof(Checked)); } }

    // The Phase D row: who made it and when (or that its uninstaller is gone), its size and its share of the largest program as a bar.
    public string Meta => Program.UninstallerMissing ? "Uninstaller missing" + (Program.InstallLocation != null ? " · folder left behind" : "")
        : string.Join(" · ", new[] { Program.Publisher, Program.Installed is { } date ? "installed " + date.ToString("d MMM yyyy") : "" }.Where(part => part.Length > 0));
    public string SizeLabel => Program.Bytes > 0 ? InstalledProgram.FormatBytes(Program.Bytes) : "—";
    private double share;
    internal double Share { get => share; set { share = Math.Clamp(value, 0, 1); Changed(nameof(ShareStar)); Changed(nameof(RestStar)); } }
    public System.Windows.GridLength ShareStar => new(Math.Max(share, 0.001), System.Windows.GridUnitType.Star);
    public System.Windows.GridLength RestStar => new(Math.Max(1 - share, 0.001), System.Windows.GridUnitType.Star);
    // Its own icon, read on a worker thread after the list is shown (owner's pick: a letter tile in a soft color from its name until then, or when it has none).
    private System.Windows.Media.ImageSource? icon;
    public System.Windows.Media.ImageSource? Icon { get => icon; internal set { icon = value; Changed(nameof(Icon)); Changed(nameof(HasIcon)); } }
    public bool HasIcon => icon != null;
    public System.Windows.Media.Brush TileBrush => ProgramIcons.TileFor(Name);
    public double TileOpacity => Program.UninstallerMissing ? 0.5 : 1;   // a program that is mostly gone looks faded, as in the mockup
}

internal sealed class StoreAppRow(StoreApp app) : ToolRow
{
    internal StoreApp App { get; } = app;
    public override string Name => App.Name;
    public override string Detail => App.Detail;
    public string ActionName => "Uninstall " + Name;
    public System.Windows.Media.Brush TileBrush => ProgramIcons.TileFor(Name);
}

internal sealed class ExtensionRow(BrowserExtension extension) : ToolRow
{
    internal BrowserExtension Extension { get; } = extension;
    public override string Name => Extension.Name;
    public override string Detail => Extension.Detail;
    public bool Locked => Extension.ByPolicy;
    public string ActionLabel => Extension.Chromium ? "Remove in " + Extension.Browser : "Remove";
    public string ActionName => (Extension.Chromium ? "Open " + Extension.Browser + " to remove " : "Remove ") + Name;
    public System.Windows.Media.Brush TileBrush => ProgramIcons.TileFor(Name);
}

internal sealed class BackupRow(LeftoverBackup backup)
{
    internal LeftoverBackup Backup { get; } = backup;
    public string Name => Backup.Program;
    public string Detail => Backup.When.ToString("d MMM yyyy, HH:mm") + $" · {Backup.Items} item{(Backup.Items == 1 ? "" : "s")}" + (Backup.Bytes > 0 ? " · " + InstalledProgram.FormatBytes(Backup.Bytes) : "") + (Backup.Restored ? " · restored" : "");
    public string RestoreName => "Restore what was removed from " + Name;
    public string DeleteName => "Delete the backup of " + Name;
}

internal sealed class StepItem(string label) : INotifyPropertyChanged
{
    public string Label { get; } = label;
    public bool More { get; init; } = true;   // a short line joins it to the next step (not after the last)
    private bool lit, current;
    public bool Lit { get => lit; set { lit = value; PropertyChanged?.Invoke(this, new(nameof(Lit))); } }
    public bool Current { get => current; set { current = value; PropertyChanged?.Invoke(this, new(nameof(Current))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class LeftoverRow : INotifyPropertyChanged
{
    internal LeftoverRow(Leftover item, bool elevated, LeftoverGroupRow group)
    {
        Item = item; Group = group;
        CanCheck = elevated || !item.NeedsAdministrator;
        isChecked = item.Safe && CanCheck;
    }
    internal Leftover Item { get; }
    internal LeftoverGroupRow Group { get; }
    public bool CanCheck { get; }
    // A long path keeps its end (the part that names the program); the full path is in the tooltip.
    public string Title => Short(Item.Kind == LeftoverKind.RunValue ? Item.Path.Replace("|", " → ") : Item.Path);
    public string FullPath => Item.Path;
    private static string Short(string path)
    {
        if (path.Length <= 64) return path;
        var parts = path.Split('\\');
        string tail = string.Join("\\", parts.Skip(Math.Max(1, parts.Length - 3)));
        return parts[0] + "\\…\\" + tail;
    }
    public string Detail => Item.Why + (Item.Bytes > 0 ? " · " + InstalledProgram.FormatBytes(Item.Bytes) : "") + (CanCheck ? "" : " · needs administrator mode");
    private bool isChecked;
    public bool Checked { get => isChecked; set { if (isChecked == value || !CanCheck) return; isChecked = value; PropertyChanged?.Invoke(this, new(nameof(Checked))); Group.RowChanged(); } }
    internal void Set(bool value) { if (isChecked == value || !CanCheck) return; isChecked = value; PropertyChanged?.Invoke(this, new(nameof(Checked))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class LeftoverGroupRow : INotifyPropertyChanged
{
    internal LeftoverGroupRow(string name, IEnumerable<Leftover> items, bool elevated, Action changed)
    {
        Name = name; this.changed = changed;
        Rows = items.Select(item => new LeftoverRow(item, elevated, this)).ToList();
    }
    private readonly Action changed;
    public string Name { get; }
    public List<LeftoverRow> Rows { get; }
    public bool CanCheck => Rows.Any(row => row.CanCheck);
    public string SelectName => "Select all " + Name.ToLowerInvariant();
    public string Summary { get { long bytes = Rows.Sum(row => row.Item.Bytes); return $" · {Rows.Count}" + (bytes > 0 ? " · " + InstalledProgram.FormatBytes(bytes) : ""); } }
    private bool isOpen;
    public bool IsOpen { get => isOpen; set { isOpen = value; PropertyChanged?.Invoke(this, new(nameof(IsOpen))); } }
    // Ticked when every row that can be ticked is, empty when none is, mixed otherwise; ticking it ticks or clears the whole group.
    public bool? Checked
    {
        get { var usable = Rows.Where(row => row.CanCheck).ToList(); int on = usable.Count(row => row.Checked); return on == 0 ? false : on == usable.Count ? true : null; }
        set { bool target = value ?? false; foreach (var row in Rows) row.Set(target); RowChanged(); }
    }
    internal void RowChanged() { PropertyChanged?.Invoke(this, new(nameof(Checked))); changed(); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed record WipeDrive(string Root, string Label)
{
    public string ActionName => "Wipe the free space of " + Root;
}

// Tools (owner, November 19, 2026; Pro only). A page like Settings with four cards: Programs, Store apps, Browser extensions and Shredder. Every card reads its
// list when it is opened, never when the page opens.
public partial class MainWindow
{
    // Test seams: the test runner installs fakes and throw-away places for every window; the product uses the real ones.
    internal static Func<IProgramSystem> ProgramSystemFactory = () => new RegistryProgramSystem();
    internal static Func<LeftoverPlaces> LeftoverPlacesFactory = LeftoverPlaces.Default;
    internal static Func<IStoreAppSystem> StoreAppSystemFactory = () => new WindowsStoreAppSystem();
    internal static Func<IReadOnlyList<BrowserRoot>> BrowserRootsFactory = BrowserExtensionFinder.DefaultRoots;
    internal static Func<string, bool> BrowserIsRunning = BrowserExtensionFinder.BrowserRunning;
    internal static Action<string, string> ProgramStarter = (exe, arguments) =>
    {
        try { Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true })?.Dispose(); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    };
    internal static Func<IReadOnlyList<string>> ShredNeverFactory = Shredder.DefaultNever;
    internal static Func<IReadOnlyList<WipeDrive>> WipeDrivesFactory = () => DriveInfo.GetDrives()
        .Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
        .Select(drive => new WipeDrive(drive.RootDirectory.FullName, $"{drive.RootDirectory.FullName.TrimEnd('\\')} {drive.VolumeLabel}".Trim() + " · " + InstalledProgram.FormatBytes(drive.AvailableFreeSpace) + " free")).ToList();
    internal static Func<string, string> WipeFolderFor = Shredder.WipeFolder;
    internal static long? WipeLimitForTests;

    private string toolsDataFolder = CleanupEngine.DataDirectory;
    private bool toolsReturnToReview;
    private IProgramSystem? programSystem;
    private IProgramSystem Programs => programSystem ??= ProgramSystemFactory();
    private List<ProgramRow> programRows = [];
    private readonly ObservableCollection<string> shredItems = [];
    private CancellationTokenSource? shredCancellation;
    internal IReadOnlyList<ProgramRow> ProgramRows => programRows;
    internal ObservableCollection<string> ShredItems => shredItems;

    private bool ToolsElevated => AdminMode.Elevation.IsElevated;

    // Elevated: backups live in the administrator-only folder (an elevated restore must never read a list another program could have written).
    private string LeftoverBackupRoot => ToolsElevated && toolsDataFolder == CleanupEngine.DataDirectory
        ? AdminOnlyDirectory.Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hush", "Uninstall backups"))
        : Path.Combine(toolsDataFolder, "Uninstall backups");
    private LeftoverRemover Remover() => new(LeftoverBackupRoot, Path.Combine(toolsDataFolder, "Logs", "uninstall.jsonl"), LeftoverPlacesFactory(), ToolsElevated);

    private void Tools_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !IsProMode) return;
        // The Tools tab while a tool is open goes back to the Overview.
        if (ToolsPage.Visibility == Visibility.Visible) { if (!ToolsHomeShown) { ShowToolsHome(); Motion.Reveal(ToolFocusCard); } return; }
        SelectTab("Tools");
        toolsReturnToReview = ReviewPage.Visibility == Visibility.Visible;
        if (SettingsPage.Visibility == Visibility.Visible) { settingsVisit++; StopResourceIndicators(); }
        Motion.EnterPage(MainContent, () =>
        {
            ScanPage.Visibility = ReviewPage.Visibility = SettingsPage.Visibility = TweaksPage.Visibility = Visibility.Collapsed;
            ToolsPage.Visibility = Visibility.Visible;
        });
        ToolsBackButton.Focus();
        if (WipeDrivesList.ItemsSource == null) WipeDrivesList.ItemsSource = WipeDrivesFactory();
        ShredList.ItemsSource ??= shredItems;
        // Owner, November 20, 2026: Tools opens on the overview of every tool (nothing is read until a tool is opened).
        ShowToolsHome();
    }

    private void ToolsBack_Click(object sender, RoutedEventArgs e)
    {
        ShowReviewPage(toolsReturnToReview);
        ToolsButton.Focus();
    }

    // ----- Programs -----

    private void Programs_Expanded(object sender, RoutedEventArgs e) { if (ReferenceEquals(e.OriginalSource, sender) && programRows.Count == 0) _ = RefreshProgramsAsync(); }
    private void RefreshPrograms_Click(object sender, RoutedEventArgs e) => _ = RefreshProgramsAsync();

    private bool programsBusy;
    internal async Task RefreshProgramsAsync()
    {
        if (programsBusy) return;
        programsBusy = true;
        try
        {
            ProgramsWait.Begin("Reading your programs", 4, "Installed programs and their sizes");   // never blank while waiting
            var programs = await Task.Run(() => Programs.List());
            _ = ProgramsWait.FinishAsync();
            foreach (var row in programRows) row.PropertyChanged -= ProgramRowChanged;
            programRows = programs.Select(program => new ProgramRow(program)).ToList();
            foreach (var row in programRows) row.PropertyChanged += ProgramRowChanged;
            // Phase D (owner, November 20, 2026): ONE ordered list with a sort switch (largest first by default) instead of date topics, so the order is what the
            // person chose; each row shows its share of the largest program as a bar.
            long largest = Math.Max(1, programRows.Count == 0 ? 1 : programRows.Max(row => row.Program.Bytes));
            foreach (var row in programRows) row.Share = row.Program.Bytes / (double)largest;
            ShowProgramsSort();
            ApplyProgramsView();
            LoadProgramIcons(programRows);
            RefreshToolTiles();
            int missing = programRows.Count(row => row.Program.UninstallerMissing);
            ProgramsSummary.Text = programRows.Count == 0 ? "No installed programs were found" : $"{programRows.Count} program{(programRows.Count == 1 ? "" : "s")} installed" + (missing > 0 ? $" · {missing} with a missing uninstaller" : "");
            RefreshBackups();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { ProgramsWait.Cancel(); ProgramsSummary.Text = "The installed programs can't be read right now"; }
        finally { programsBusy = false; UpdateUninstallSelected(); }
    }

    private void ApplyProgramsView()
    {
        string search = ProgramsSearch.Text.Trim();
        var shown = search.Length == 0 ? programRows : programRows.Where(row => row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || row.Program.Publisher.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToList();
        ProgramsList.ItemsSource = SortPrograms(shown).ToList();
        ProgramsEmpty.Visibility = shown.Count == 0 && programRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Letter ranges split a long Store apps list (Software Updates no longer uses them: it groups by kind of program).
    private static string LetterRange(string name)
    {
        char first = name.Length > 0 ? char.ToUpperInvariant(name[0]) : '#';
        return first switch { >= 'A' and <= 'E' => "A–E", >= 'F' and <= 'K' => "F–K", >= 'L' and <= 'R' => "L–R", >= 'S' and <= 'Z' => "S–Z", _ => "0–9 and others" };
    }

    private void ProgramsSearch_Changed(object sender, TextChangedEventArgs e) { if (programRows.Count > 0) ApplyProgramsView(); }
    private void ProgramRowChanged(object? sender, PropertyChangedEventArgs e) => UpdateUninstallSelected();

    private void UpdateUninstallSelected()
    {
        int count = programRows.Count(row => row.Checked);
        UninstallSelectedButton.IsEnabled = count > 0 && !uninstallOpen;
        UninstallSelectedButton.Content = count > 1 ? $"Uninstall {count} programs" : "Uninstall selected";
    }

    private void UninstallProgram_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProgramRow row) OpenUninstall([row.Program]);
    }

    private void UninstallSelected_Click(object sender, RoutedEventArgs e)
    {
        var chosen = programRows.Where(row => row.Checked).Select(row => row.Program).ToList();
        if (chosen.Count > 0) OpenUninstall(chosen);
    }

    // ----- The uninstall popup: Prepare → Uninstaller → Leftovers → Review -----

    private enum UninstallPhase { Prepare, Running, WaitingForPerson, Searching, Review, Removing, Done }
    private UninstallPhase uninstallPhase;
    private bool uninstallOpen;
    private List<InstalledProgram> uninstallQueue = [];
    private LeftoverDepth leftoverDepth = LeftoverDepth.Moderate;
    private readonly List<StepItem> uninstallSteps = [new("Prepare"), new("Uninstaller"), new("Leftovers"), new("Review") { More = false }];
    private List<LeftoverGroupRow> leftoverGroups = [];
    private CancellationTokenSource? uninstallCancellation;
    private TaskCompletionSource<bool>? uninstallContinue;
    private IInputElement? uninstallReturnFocus;
    private string uninstallProgramNames = "";
    internal IReadOnlyList<LeftoverGroupRow> LeftoverGroupRows => leftoverGroups;
    internal bool UninstallPopupOpen => uninstallOpen;
    internal string UninstallStatusText => UninstallText.Text;

    internal void OpenUninstall(IReadOnlyList<InstalledProgram> programs)
    {
        if (uninstallOpen || busy || programs.Count == 0) return;
        uninstallOpen = true;
        uninstallQueue = programs.ToList();
        uninstallProgramNames = programs.Count == 1 ? programs[0].Name : $"{programs.Count} programs";
        leftoverGroups = [];
        UninstallSteps.ItemsSource ??= uninstallSteps;
        bool forced = programs.All(program => program.UninstallerMissing);
        UninstallTitle.Text = programs.Count == 1 ? (forced ? $"Remove what {programs[0].Name} left behind?" : $"Uninstall {programs[0].Name}?") : $"Uninstall {programs.Count} programs?";
        UninstallText.Text = programs.Count == 1
            ? (forced ? $"{programs[0].Name}'s own uninstaller is missing, so Hush only looks for what it left behind. You choose what goes; everything is backed up first."
                      : $"Hush runs {programs[0].Name}'s own uninstaller first (Windows may ask for permission). Then it looks for what it left behind, and you choose what goes. Everything is backed up first.")
            : "Hush runs each program's own uninstaller, one after another (Windows may ask for permission). Then it looks for what they left behind, and you choose what goes. Everything is backed up first.";
        SetDepth(LeftoverDepth.Moderate);
        UninstallDepthPanel.Visibility = Visibility.Visible;
        LeftoverScroll.Visibility = Visibility.Collapsed;
        SetUninstallPhase(UninstallPhase.Prepare, forced ? "Find leftovers" : "Uninstall", "Cancel");
        uninstallReturnFocus = Keyboard.FocusedElement;
        AppSurface.IsEnabled = false;
        SetBackdropBlur(7);
        UninstallOverlay.Visibility = Visibility.Visible;
        Motion.OpenPopup(UninstallOverlay);
        UninstallCancelButton.Focus();
        UpdateUninstallSelected();
    }

    private void SetUninstallPhase(UninstallPhase phase, string? action, string? cancel, bool danger = false)
    {
        uninstallPhase = phase;
        int step = phase switch { UninstallPhase.Prepare => 0, UninstallPhase.Running or UninstallPhase.WaitingForPerson => 1, UninstallPhase.Searching => 2, _ => 3 };
        for (int i = 0; i < uninstallSteps.Count; i++) { uninstallSteps[i].Lit = i <= step; uninstallSteps[i].Current = i == step; }
        UninstallActionButton.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
        if (action != null) UninstallActionButton.Content = action;
        UninstallActionButton.SetResourceReference(StyleProperty, danger ? "PopupDangerAction" : "PopupAction");   // Phase D popup buttons
        UninstallCancelButton.Visibility = cancel == null ? Visibility.Hidden : Visibility.Visible;
        if (cancel != null) UninstallCancelButton.Content = cancel;
        UninstallActionButton.IsEnabled = phase != UninstallPhase.Review || leftoverGroups.Any(group => group.Rows.Any(row => row.Checked));
    }

    private void SetDepth(LeftoverDepth depth)
    {
        leftoverDepth = depth;
        DepthSafeButton.Tag = depth == LeftoverDepth.Safe ? "On" : "Off";
        DepthModerateButton.Tag = depth == LeftoverDepth.Moderate ? "On" : "Off";
        DepthDeepButton.Tag = depth == LeftoverDepth.Deep ? "On" : "Off";
        UninstallDepthNote.Text = depth switch
        {
            LeftoverDepth.Safe => "Safe: its install folder, folders named exactly like it, its shortcuts and its entry in the programs list.",
            LeftoverDepth.Moderate => "Moderate: also its registry settings, its publisher's folder when nothing else of that publisher is left, and start-up entries pointing at it.",
            _ => "Deep: also folders and settings whose name only matches part of its name. More to read; none of the extra ones are ticked for you."
        };
    }

    private void Depth_Click(object sender, RoutedEventArgs e)
    {
        if (uninstallPhase != UninstallPhase.Prepare) return;
        SetDepth(ReferenceEquals(sender, DepthSafeButton) ? LeftoverDepth.Safe : ReferenceEquals(sender, DepthDeepButton) ? LeftoverDepth.Deep : LeftoverDepth.Moderate);
    }

    private async void UninstallAction_Click(object sender, RoutedEventArgs e)
    {
        switch (uninstallPhase)
        {
            case UninstallPhase.Prepare: await RunUninstallAsync(); break;
            case UninstallPhase.WaitingForPerson: uninstallContinue?.TrySetResult(true); break;
            case UninstallPhase.Review: await RemoveLeftoversAsync(); break;
            case UninstallPhase.Done: await CloseUninstall(); break;
        }
    }

    private async void UninstallCancel_Click(object sender, RoutedEventArgs e)
    {
        if (uninstallPhase is UninstallPhase.Removing) return;
        uninstallCancellation?.Cancel();
        uninstallContinue?.TrySetResult(false);
        await CloseUninstall();
    }

    private async Task RunUninstallAsync()
    {
        UninstallDepthPanel.Visibility = Visibility.Collapsed;
        uninstallCancellation = new CancellationTokenSource();
        var token = uninstallCancellation.Token;
        var found = new List<Leftover>();
        var notes = new List<string>();
        foreach (var program in uninstallQueue)
        {
            if (token.IsCancellationRequested || !uninstallOpen) return;
            bool forced = program.UninstallerMissing;
            if (!forced)
            {
                SetUninstallPhase(UninstallPhase.Running, null, "Cancel");
                UninstallText.Text = $"Waiting for {program.Name}'s own uninstaller to finish… Its window may be behind Hush.";
                var run = await Programs.RunUninstallerAsync(program, token);
                if (token.IsCancellationRequested || !uninstallOpen) return;
                if (!run.Started) { notes.Add($"{program.Name}: {run.Problem}"); continue; }
                if (await Task.Run(() => Programs.StillInstalled(program)))
                {
                    // Many uninstallers hand over to a copy of themselves and return at once: let the person say when its window is finished.
                    SetUninstallPhase(UninstallPhase.WaitingForPerson, "Continue", "Cancel");
                    UninstallText.Text = $"{program.Name}'s uninstaller may still be open in its own window. Finish it there, then press Continue.";
                    uninstallContinue = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!await uninstallContinue.Task || !uninstallOpen) return;
                    if (await Task.Run(() => Programs.StillInstalled(program)))
                    {
                        notes.Add($"{program.Name} is still installed (its uninstall may have been cancelled), so Hush did not look for its leftovers.");
                        continue;
                    }
                }
            }
            SetUninstallPhase(UninstallPhase.Searching, null, "Cancel");
            UninstallText.Text = $"Looking for what {program.Name} left behind…";
            var depth = leftoverDepth;
            var places = LeftoverPlacesFactory();
            try
            {
                found.AddRange(await Task.Run(() =>
                {
                    var remaining = Programs.List().Where(other => !other.Id.Equals(program.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                    return new LeftoverFinder(places).Find(program, depth, remaining, uninstallEntryLeft: forced && Programs.StillInstalled(program));
                }, token));
            }
            catch (OperationCanceledException) { return; }
        }
        if (!uninstallOpen) return;
        var distinct = found.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        string note = notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : "";
        _ = RefreshProgramsAsync();
        if (distinct.Count == 0)
        {
            UninstallText.Text = (uninstallQueue.Count == notes.Count ? "Nothing was uninstalled." : $"{uninstallProgramNames} left nothing behind that Hush can find.") + note;
            SetUninstallPhase(UninstallPhase.Done, "Done", null);
            return;
        }
        bool elevated = ToolsElevated;
        leftoverGroups = distinct.GroupBy(item => item.Group).OrderBy(group => group.Key == "Files and folders" ? 0 : group.Key == "Registry keys" ? 1 : 2)
            .Select(group => new LeftoverGroupRow(group.Key, group, elevated, LeftoverSelectionChanged)).ToList();
        LeftoverGroups.ItemsSource = leftoverGroups;
        LeftoverScroll.Visibility = Visibility.Visible;
        long bytes = distinct.Sum(item => item.Bytes);
        UninstallText.Text = $"Found {distinct.Count} item{(distinct.Count == 1 ? "" : "s")}" + (bytes > 0 ? $" ({InstalledProgram.FormatBytes(bytes)})" : "") + ". Only clear matches are ticked; read the rest before you tick them. Everything is backed up before it is removed." + note;
        SetUninstallPhase(UninstallPhase.Review, "Remove selected", "Keep them", danger: true);
    }

    private void LeftoverSelectionChanged()
    {
        if (uninstallPhase == UninstallPhase.Review) UninstallActionButton.IsEnabled = leftoverGroups.Any(group => group.Rows.Any(row => row.Checked));
    }

    private async Task RemoveLeftoversAsync()
    {
        var chosen = leftoverGroups.SelectMany(group => group.Rows).Where(row => row.Checked).Select(row => row.Item).ToList();
        if (chosen.Count == 0) return;
        SetUninstallPhase(UninstallPhase.Removing, null, null);
        LeftoverScroll.IsEnabled = false;
        UninstallText.Text = "Backing up, then removing what you chose…";
        string name = uninstallProgramNames;
        LeftoverReport report;
        try { var remover = Remover(); report = await Task.Run(() => remover.Remove(name, chosen)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report = new(0, 0, chosen.Count, 0, null, [ex.Message]); }
        LeftoverScroll.IsEnabled = true;
        LeftoverScroll.Visibility = Visibility.Collapsed;
        UninstallText.Text = report.Text + (report.Problems.Count > 0 ? "\n\n" + string.Join("\n", report.Problems.Take(4)) + (report.Problems.Count > 4 ? $"\n…and {report.Problems.Count - 4} more (see Logs\\uninstall.jsonl)." : "") : "");
        ProgramsStatus.Text = report.Text; ProgramsStatus.Visibility = Visibility.Visible;
        SetUninstallPhase(UninstallPhase.Done, "Done", null);
        RefreshBackups();
    }

    internal async Task CloseUninstall()
    {
        if (!uninstallOpen) return;
        uninstallOpen = false;
        UninstallOverlay.IsEnabled = false;
        SetBackdropBlur(0);
        await Motion.FadeOut(UninstallOverlay);
        UninstallOverlay.Visibility = Visibility.Collapsed;
        UninstallOverlay.IsEnabled = true;
        AppSurface.IsEnabled = true;
        LeftoverGroups.ItemsSource = null;
        if (uninstallReturnFocus != null) Keyboard.Focus(uninstallReturnFocus);
        foreach (var row in programRows) row.Checked = false;
        UpdateUninstallSelected();
    }

    // Test seam: drive the popup without clicking.
    internal Task PressUninstallAction() { UninstallAction_Click(this, new RoutedEventArgs()); return Task.CompletedTask; }

    // ----- Backups of removed leftovers -----

    private void RefreshBackups()
    {
        IReadOnlyList<LeftoverBackup> backups;
        try { backups = Remover().Backups(); } catch (IOException) { backups = []; }
        BackupsList.ItemsSource = backups.Select(backup => new BackupRow(backup)).ToList();
        BackupsCount.Text = $" · {backups.Count}";
        BackupsPanel.Visibility = backups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BackupRow row) return;
        if (!await AskConsent($"Restore what was removed from {row.Name}?", "Hush puts the backed-up files, registry settings and start-up entries back where they were. Files that exist again are left as they are. The program itself is not reinstalled.", "Restore", "Cancel")) return;
        LeftoverReport report;
        try { var remover = Remover(); report = await Task.Run(() => remover.Restore(row.Backup.Folder)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { report = new(0, 0, 1, 0, null, [ex.Message]); }
        ProgramsStatus.Text = report.Failed == 0 ? $"Restored {report.Removed} item{(report.Removed == 1 ? "" : "s")} for {row.Name}." : $"Restored {report.Removed}; {report.Failed} could not be restored: {string.Join("; ", report.Problems.Take(2))}";
        ProgramsStatus.Visibility = Visibility.Visible;
        RefreshBackups();
    }

    private async void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BackupRow row) return;
        if (!await AskConsent($"Delete the backup of {row.Name}?", "After this, what was removed cannot be restored any more.", "Delete backup", "Keep it")) return;
        try { Remover().DeleteBackup(row.Backup.Folder); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ProgramsStatus.Text = ex.Message; ProgramsStatus.Visibility = Visibility.Visible; }
        RefreshBackups();
    }

    // ----- Store apps -----

    private IStoreAppSystem? storeApps;
    private IStoreAppSystem StoreApps => storeApps ??= StoreAppSystemFactory();
    private bool storeBusy;
    internal IReadOnlyList<StoreAppRow> StoreAppRows { get; private set; } = [];

    private void StoreApps_Expanded(object sender, RoutedEventArgs e) { if (ReferenceEquals(e.OriginalSource, sender) && StoreAppRows.Count == 0) _ = RefreshStoreAppsAsync(); }
    private void RefreshStoreApps_Click(object sender, RoutedEventArgs e) => _ = RefreshStoreAppsAsync();

    internal async Task RefreshStoreAppsAsync()
    {
        if (storeBusy) return;
        storeBusy = true;
        try
        {
            StoreWait.Begin("Reading your Store apps", 4, "Apps installed from the Store");
            var apps = await Task.Run(() => StoreApps.List());
            _ = StoreWait.FinishAsync();
            var rows = apps.Select(app => new StoreAppRow(app)).ToList();
            var names = ListGrouping.Names(rows, row => row.App.Publisher.Length > 0 ? row.App.Publisher : "Other publishers", row => LetterRange(row.Name));
            for (int i = 0; i < rows.Count; i++) rows[i].Topic = names[i] == "" ? "" : "Store · " + names[i];
            StoreAppRows = rows;
            var view = new ListCollectionView(rows);
            if (rows.Any(row => row.Topic.Length > 0)) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ToolRow.Topic)));
            StoreAppsList.ItemsSource = view;
            StoreAppsSummary.Text = rows.Count == 0 ? "No Store apps you can remove were found" : $"{rows.Count} Store app{(rows.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        { StoreWait.Cancel(); StoreAppsSummary.Text = "The Store apps can't be read right now"; }
        finally { storeBusy = false; }
    }

    private async void UninstallStoreApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StoreAppRow row || storeBusy) return;
        if (!await AskConsent($"Uninstall {row.Name}?", $"{row.Name} is removed for your account, with its data. You can install it again from the Microsoft Store.", "Uninstall", "Cancel")) return;
        await StoreAction(row, "Uninstalled", () => StoreApps.UninstallAsync(row.App), refresh: true);
    }

    private async void ResetStoreApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StoreAppRow row || storeBusy) return;
        if (!await AskConsent($"Reset {row.Name}?", $"{row.Name} goes back to how it was when you installed it. Its settings, sign-ins and data are cleared; the app stays installed.", "Reset", "Cancel")) return;
        await StoreAction(row, "Reset", () => StoreApps.ResetAsync(row.App), refresh: false);
    }

    private async void EndStoreApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StoreAppRow row || storeBusy) return;
        if (!await AskConsent($"End {row.Name}?", $"{row.Name} is closed at once. Anything you have not saved in it is lost.", "End", "Cancel")) return;
        await StoreAction(row, "Ended", () => Task.Run(() => StoreApps.End(row.App)), refresh: false);
    }

    private async Task StoreAction(StoreAppRow row, string done, Func<Task<string?>> action, bool refresh)
    {
        storeBusy = true;
        string? problem;
        try { problem = await action(); } finally { storeBusy = false; }
        StoreAppsStatus.Text = problem == null ? $"{done} {row.Name}." : $"{row.Name}: {problem}";
        StoreAppsStatus.Visibility = Visibility.Visible;
        if (refresh && problem == null) await RefreshStoreAppsAsync();
    }

    // ----- Browser extensions -----

    internal IReadOnlyList<ExtensionRow> ExtensionRows { get; private set; } = [];
    private bool extensionsBusy;
    private List<TweaksTab> extensionTabs = [];
    private string openExtensionTab = "";
    internal IReadOnlyList<TweaksTab> ExtensionTabs => extensionTabs;
    internal string OpenExtensionTab => openExtensionTab;

    internal void ShowExtensionTab(string browser)
    {
        if (!extensionTabs.Any(tab => tab.Key == browser)) return;
        openExtensionTab = browser;
        ApplyExtensionView();
    }

    private void ExtensionTab_Select(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TweaksTab tab && tab.Key != openExtensionTab) { ShowExtensionTab(tab.Key); Motion.Reveal(ExtensionsList); }
    }

    // The open browser's extensions; headings (its profiles, or a long list split by the shared rule) only when there is more than one.
    private void ApplyExtensionView()
    {
        if (ExtensionsList.ItemsSource is not ListCollectionView view) return;
        foreach (var tab in extensionTabs) tab.Open = tab.Key == openExtensionTab;
        bool Shown(ExtensionRow row) => extensionTabs.Count <= 1 || row.Extension.Browser == openExtensionTab;
        bool grouped = ExtensionRows.Where(Shown).Select(row => row.Topic).Where(topic => topic.Length > 0).Distinct().Count() > 1;
        using (view.DeferRefresh())
        {
            view.Filter = row => row is ExtensionRow extension && Shown(extension);
            view.GroupDescriptions.Clear();
            if (grouped) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ToolRow.Topic)));
        }
    }
    private void Extensions_Expanded(object sender, RoutedEventArgs e) { if (ReferenceEquals(e.OriginalSource, sender) && ExtensionRows.Count == 0) _ = RefreshExtensionsAsync(); }
    private void RefreshExtensions_Click(object sender, RoutedEventArgs e) => _ = RefreshExtensionsAsync();

    internal async Task RefreshExtensionsAsync()
    {
        if (extensionsBusy) return;
        extensionsBusy = true;
        try
        {
            var roots = BrowserRootsFactory();
            ExtensionsWait.Begin("Reading your browser extensions", 4, "Every browser and profile on this PC");
            var found = await Task.Run(() => new BrowserExtensionFinder(roots).List());
            _ = ExtensionsWait.FinishAsync();
            var rows = found.Select(extension => new ExtensionRow(extension)).ToList();
            var names = ListGrouping.Names(rows, row => row.Extension.Browser + " extensions", row => row.Extension.Profile, flatLimit: 0, foldSingles: false);
            for (int i = 0; i < rows.Count; i++) rows[i].Topic = names[i];
            ExtensionRows = rows;
            // One icon tab per browser (owner, November 20, 2026: the Software Updates layout), in the order the browsers were found.
            extensionTabs = rows.Select(row => row.Extension.Browser).Distinct()
                .Select(browser => new TweaksTab(browser, browser) { IconGeometry = UpdateIcons.Of(ProgramKinds.Browsers), CountIsWaiting = false,
                    Count = rows.Count(row => row.Extension.Browser == browser).ToString() }).ToList();
            ExtensionBrowserTabs.ItemsSource = extensionTabs;
            ExtensionTabsBar.Visibility = extensionTabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (!extensionTabs.Any(tab => tab.Key == openExtensionTab)) openExtensionTab = extensionTabs.FirstOrDefault()?.Key ?? "";
            ExtensionsList.ItemsSource = new ListCollectionView(rows);
            ApplyExtensionView();
            ExtensionsSummary.Text = rows.Count == 0 ? "No browser extensions were found" : $"{rows.Count} extension{(rows.Count == 1 ? "" : "s")} in {rows.Select(row => row.Extension.Browser).Distinct().Count()} browser{(rows.Select(row => row.Extension.Browser).Distinct().Count() == 1 ? "" : "s")}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ExtensionsWait.Cancel(); ExtensionsSummary.Text = "The browser extensions can't be read right now"; }
        finally { extensionsBusy = false; }
    }

    private async void RemoveExtension_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ExtensionRow row) return;
        var extension = row.Extension;
        if (extension.Chromium)
        {
            // The browser's own page removes it for good (its settings are signed, so files removed behind its back can come back).
            var root = BrowserRootsFactory().FirstOrDefault(r => r.Browser == extension.Browser);
            if (root?.Exe is not { } exe) { ShowExtensionsStatus($"Hush could not find {extension.Browser}'s program. Open its Extensions page and remove {row.Name} there."); return; }
            string scheme = extension.Browser switch { "Edge" => "edge", "Brave" => "brave", "Vivaldi" => "vivaldi", "Opera" => "opera", _ => "chrome" };
            string profile = Path.GetFileName(extension.ProfilePath);
            ProgramStarter(exe, (extension.ProfilePath == root.Folder ? "" : $"--profile-directory=\"{profile}\" ") + $"{scheme}://extensions/?id={extension.Id}");
            ShowExtensionsStatus($"{extension.Browser} opened at {row.Name}. Press Remove there, then Refresh here.");
            return;
        }
        if (!await AskConsent($"Remove {row.Name} from {extension.Browser}?", $"Hush keeps a copy of its file, then removes it. {extension.Browser} forgets it the next time it starts. {extension.Browser} must be closed.", "Remove", "Cancel")) return;
        string backups = Path.Combine(toolsDataFolder, "Extension backups");
        string? problem = await Task.Run(() => BrowserExtensionFinder.Remove(extension, backups, BrowserIsRunning));
        ShowExtensionsStatus(problem == null ? $"Removed {row.Name} from {extension.Browser} (a copy is kept in Hush's Extension backups folder)." : $"{row.Name}: {problem}");
        if (problem == null) await RefreshExtensionsAsync();
    }

    private void ShowExtensionsStatus(string text) { ExtensionsStatus.Text = text; ExtensionsStatus.Visibility = Visibility.Visible; }

    // ----- Shredder -----

    private void Shredder_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        ShredList.ItemsSource ??= shredItems;
        WipeDrivesList.ItemsSource ??= WipeDrivesFactory();
    }

    internal void AddToShredList(IEnumerable<string> paths)
    {
        foreach (string path in paths) if (!shredItems.Contains(path, StringComparer.OrdinalIgnoreCase)) shredItems.Add(path);
        ShredButton.IsEnabled = shredItems.Count > 0 && shredCancellation == null;
    }

    private void ShredAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Choose files to shred" };
        if (dialog.ShowDialog(this) == true) AddToShredList(dialog.FileNames);
    }

    private void ShredAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "Choose folders to shred" };
        if (dialog.ShowDialog(this) == true) AddToShredList(dialog.FolderNames);
    }

    private void ShredRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) shredItems.Remove(path);
        ShredButton.IsEnabled = shredItems.Count > 0 && shredCancellation == null;
    }

    private async void Shred_Click(object sender, RoutedEventArgs e)
    {
        if (shredItems.Count == 0 || shredCancellation != null) return;
        var paths = shredItems.ToList();
        if (!await AskConsent($"Shred {paths.Count} item{(paths.Count == 1 ? "" : "s")}?", "Every file on the list (and in the folders on it) is overwritten and deleted. This cannot be undone: there is no backup and no Recycle Bin. On SSDs and USB sticks the drive may still keep old copies that no program can reach.", "Shred", "Cancel")) return;
        var never = ShredNeverFactory();
        await RunShredWork("Shredding", token => { var report = Shredder.Shred(paths, never, ShredProgress(), token); return $"Shredded {report.Files} file{(report.Files == 1 ? "" : "s")} ({InstalledProgram.FormatBytes(report.Bytes)})" + (report.Problems.Count > 0 ? $". {report.Problems.Count} left alone: " + string.Join("; ", report.Problems.Take(2)) : "."); });
        shredItems.Clear();
        ShredButton.IsEnabled = false;
    }

    private async void WipeDrive_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not WipeDrive drive || shredCancellation != null) return;
        if (!await AskConsent($"Wipe the free space of {drive.Root.TrimEnd('\\')}?", "Hush fills the drive's free space with zeros for a moment, then deletes that file again. Your files are not touched. It can take a long time, and while it runs the drive is full, so other programs may not be able to save. You can stop it at any time.", "Wipe free space", "Cancel")) return;
        string folder = WipeFolderFor(drive.Root);
        bool created = !Directory.Exists(folder);
        long? limit = WipeLimitForTests;
        await RunShredWork("Wiping free space", token =>
        {
            long written = Shredder.WipeFreeSpace(folder, ShredProgress(), token, limit);
            if (created) try { Directory.Delete(folder); } catch (IOException) { }
            return $"Overwrote {InstalledProgram.FormatBytes(written)} of free space on {drive.Root.TrimEnd('\\')}.";
        });
    }

    private IProgress<double> ShredProgress()
    {
        double last = -1;
        return new Progress<double>(value => { if (value - last < 0.01 && value < 1) return; last = value; ShredStatus.Text = $"{shredWorkName}… {value:P0}"; });
    }

    private string shredWorkName = "";
    private async Task RunShredWork(string name, Func<CancellationToken, string> work)
    {
        shredWorkName = name;
        shredCancellation = new CancellationTokenSource();
        var token = shredCancellation.Token;
        ShredButton.IsEnabled = false;
        ShredCancelButton.Visibility = Visibility.Visible;
        ShredStatus.Text = name + "…"; ShredStatus.Visibility = Visibility.Visible;
        string result;
        try { result = await Task.Run(() => work(token)); }
        catch (OperationCanceledException) { result = "Stopped. What was already done stays done."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result = ex.Message; }
        shredCancellation = null;
        ShredCancelButton.Visibility = Visibility.Collapsed;
        ShredStatus.Text = result;
        ShredButton.IsEnabled = shredItems.Count > 0;
    }

    private void ShredCancel_Click(object sender, RoutedEventArgs e) => shredCancellation?.Cancel();
}
