using System.ComponentModel;
using System.IO;
using System.Windows;

namespace Hush;

// One step of Device repair's guided list (owner's pick B, November 20, 2026): a number (a check mark once done), a title, one line that says what it does
// or what happened, a chip with its state, and an eased bar while it runs.
internal sealed class RepairStep(string key, int number, string title, string line, bool risky = false) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public bool Risky { get; } = risky;
    private string line = line, state = "pending";
    public string Line { get => line; set { if (line == value) return; line = value; Changed(nameof(Line)); } }
    // pending, current, working, done, failed, skipped, notneeded
    public string State
    {
        get => state;
        set { if (state == value) return; state = value; foreach (var name in new[] { nameof(State), nameof(NumberText), nameof(Chip), nameof(IsCurrent), nameof(FillState), nameof(ChipVisible) }) Changed(name); }
    }
    public string NumberText => state == "done" ? "✓" : number.ToString();
    public bool IsCurrent => state is "current" or "working";
    public string Chip => state switch { "working" => "Working", "done" => "Done", "failed" => "Did not help", "skipped" => "Skipped", "notneeded" => "Not needed", "current" => Risky ? "Risky" : "Next", _ => Risky ? "Optional" : "" };
    public Visibility ChipVisible => Chip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string FillState => state switch { "working" => "working", "done" => "done", "failed" => "failed", _ => "idle" };
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}

// An old Bluetooth device (a pairing left behind): the person ticks which to remove; nothing is ticked for them.
internal sealed class RepairDeviceRow(BtDevice device) : INotifyPropertyChanged
{
    internal BtDevice Device { get; } = device;
    public string Name => Device.Name;
    public string Detail => (Device.Address is string address ? "Address " + string.Join(":", Enumerable.Range(0, 6).Select(i => address.Substring(i * 2, 2).ToUpperInvariant())) + " · " : "") + "not connected";
    private bool isChecked;
    public bool Checked { get => isChecked; set { if (isChecked == value) return; isChecked = value; PropertyChanged?.Invoke(this, new(nameof(Checked))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

// Tools > Device repair: Bluetooth (owner, November 20, 2026). Opening it runs the read-only check; then the steps run one after another, gentlest first,
// each asking first, and the check runs again after each so the next step is only offered while something is still wrong.
public partial class MainWindow
{
    internal static Func<IDeviceSystem> DeviceSystemFactory = () => new RealDeviceSystem();
    private IDeviceSystem? deviceSystem;
    private IServiceSystem? repairServices;
    private readonly List<RepairStep> repairSteps =
    [
        new("check", 1, "Check Bluetooth", "Looks at the adapter, its driver, Bluetooth's services and old devices"),
        new("quick", 2, "Quick repair", "Turns Bluetooth's services back on and restarts the adapter; the driver stays as it is"),
        new("reinstall", 3, "Reinstall driver", "Removes the adapter so Windows installs its driver again"),
        new("clean", 4, "Clean old devices", "Removes old devices you choose (pairings that are no longer used)"),
        new("force", 5, "Force clean stuck devices", "For old devices Windows refuses to remove; their pairing is backed up first", risky: true),
    ];
    private List<RepairDeviceRow> repairDevices = [];
    private BtDiagnosis? btDiagnosis;
    private bool repairBusy;
    internal IReadOnlyList<RepairStep> RepairSteps => repairSteps;
    internal IReadOnlyList<RepairDeviceRow> RepairDevices => repairDevices;
    internal BtDiagnosis? BluetoothDiagnosis => btDiagnosis;

    private IDeviceSystem DeviceSystem => deviceSystem ??= DeviceSystemFactory();
    private DeviceRepair Repair => new(DeviceSystem, repairServices ?? ServiceSystemFactory(), serviceTuning,
        DeviceSystem.IsAdministrator && toolsDataFolder == CleanupEngine.DataDirectory
            ? AdminOnlyDirectory.Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hush", "Device repair backups"))
            : Path.Combine(toolsDataFolder, "Device repair backups"),
        Path.Combine(toolsDataFolder, "Logs", "device-repair.jsonl"));

    private void Repair_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        RepairStepsList.ItemsSource ??= repairSteps;
        if (repairSteps[0].State == "pending" && !repairBusy) _ = CheckBluetoothAsync();
    }

    private void RepairCheckAgain_Click(object sender, RoutedEventArgs e) { foreach (var step in repairSteps) step.State = "pending"; _ = CheckBluetoothAsync(); }

    // Step 1 (read-only): what is wrong, then which step comes next.
    internal async Task CheckBluetoothAsync()
    {
        if (repairBusy) return;
        repairBusy = true; SetRepairButtons();
        var check = repairSteps[0];
        check.State = "working"; check.Line = "Looking at the adapter, its driver and Bluetooth's services…";
        RepairSummary.Text = "Checking Bluetooth…";
        try
        {
            btDiagnosis = await Repair.DiagnoseAsync(CancellationToken.None);
            check.State = "done";
            var problems = btDiagnosis.Problems;
            check.Line = problems.Count == 0 ? "No problem found" : string.Join(" · ", problems);
            repairDevices = btDiagnosis.OldDevices.Select(device => new RepairDeviceRow(device)).ToList();
            foreach (var row in repairDevices) row.PropertyChanged += (_, _) => SetRepairButtons();
            RepairDevicesList.ItemsSource = repairDevices;
            int found = problems.Count;
            RepairSummary.Text = found == 0 && repairDevices.Count == 0 ? "Bluetooth looks healthy"
                : found == 0 ? $"Bluetooth works · {repairDevices.Count} old device{(repairDevices.Count == 1 ? "" : "s")} you can clean up"
                : $"{found} problem{(found == 1 ? "" : "s")} found" + (repairDevices.Count > 0 ? $" · {repairDevices.Count} old device{(repairDevices.Count == 1 ? "" : "s")}" : "");
            AdvanceRepair();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            check.State = "failed"; check.Line = "Bluetooth could not be checked: " + ex.Message;
            RepairSummary.Text = "Bluetooth can't be checked right now";
        }
        finally { repairBusy = false; SetRepairButtons(); }
    }

    // The next step that applies becomes current; steps that are no longer needed say so.
    private void AdvanceRepair()
    {
        if (btDiagnosis is not { } diagnosis) return;
        bool Applies(RepairStep step) => step.Key switch
        {
            "quick" => diagnosis.NeedsQuickRepair,
            "reinstall" => diagnosis.BrokenAdapters.Count > 0 || diagnosis.AdapterMissing,
            "clean" or "force" => repairDevices.Count > 0,
            _ => false
        };
        RepairStep? next = null;
        foreach (var step in repairSteps.Skip(1))
        {
            if (step.State is "done" or "failed" or "skipped") continue;
            if (!Applies(step)) { step.State = "notneeded"; continue; }
            if (next == null) { next = step; step.State = "current"; } else step.State = "pending";
        }
        RepairDevicesPanel.Visibility = next?.Key is "clean" or "force" && repairDevices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetRepairButtons();
    }

    private RepairStep? CurrentRepairStep => repairSteps.FirstOrDefault(step => step.State is "current" or "working");

    private void SetRepairButtons()
    {
        var step = CurrentRepairStep;
        int ticked = repairDevices.Count(row => row.Checked);
        RepairContinueButton.Visibility = RepairSkipButton.Visibility = step == null ? Visibility.Collapsed : Visibility.Visible;
        RepairContinueButton.Content = step?.Key switch { "clean" => ticked > 0 ? $"Remove {ticked}" : "Remove", "force" => ticked > 0 ? $"Force clean {ticked}" : "Force clean", _ => "Continue" };
        RepairContinueButton.Style = (Style)FindResource(step?.Risky == true ? "DangerButton" : "AccentOutlineButton");
        RepairContinueButton.IsEnabled = !repairBusy && step != null && (step.Key is not ("clean" or "force") || ticked > 0);
        RepairSkipButton.IsEnabled = RepairCheckButton.IsEnabled = !repairBusy;
    }

    private void RepairSkip_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRepairStep is not { } step || repairBusy) return;
        step.State = "skipped";
        AdvanceRepair();
    }

    private async void RepairContinue_Click(object sender, RoutedEventArgs e) => await RunRepairStepAsync();

    internal async Task RunRepairStepAsync()
    {
        if (CurrentRepairStep is not { } step || repairBusy || btDiagnosis is not { } diagnosis) return;
        if (!DeviceSystem.IsAdministrator)
        {
            if (!await AskConsent("Restart as administrator?", "Repairing Bluetooth needs administrator mode. Hush restarts with Windows' own permission prompt; open Device repair again after the restart.\n\nNothing has been changed.", "Restart as administrator", "Cancel")) return;
            if (await Task.Run(() => ElevatedLauncher(""))) { ExitForRestart(); return; }
            ShowRepairStatus("Windows' permission prompt was declined or could not open, so nothing was changed.");
            return;
        }
        var chosen = repairDevices.Where(row => row.Checked).Select(row => row.Device).ToList();
        var (title, body, action) = step.Key switch
        {
            "quick" => ("Run a quick repair?", "Hush turns Bluetooth's services back on (the ones it turned off earlier are put back exactly) and restarts the Bluetooth adapter. Bluetooth devices disconnect for a moment. No driver is changed.", "Repair"),
            "reinstall" => ("Reinstall the Bluetooth driver?", "Hush removes the Bluetooth adapter from Windows' device list and asks Windows to look for devices again, which installs its driver afresh (the driver stays on this PC). Bluetooth is off for a moment; you may need to pair some devices again. Windows may ask for a restart.", "Reinstall"),
            "clean" => ($"Remove {chosen.Count} old device{(chosen.Count == 1 ? "" : "s")}?", "The devices you ticked are removed from Windows' device list. Pair them again if you still use them.", "Remove"),
            _ => ($"Force clean {chosen.Count} stuck device{(chosen.Count == 1 ? "" : "s")}?", "For devices Windows refuses to remove: Hush unpairs them, removes them with everything under them, and as a last step deletes their stored pairing from the registry. Each pairing is backed up first. Bluetooth stops for a moment. Use this only for devices you no longer use.", "Force clean")
        };
        if (!await AskConsent(title, body, action, "Cancel")) return;
        repairBusy = true; step.State = "working"; SetRepairButtons();
        RepairResult result;
        try
        {
            result = step.Key switch
            {
                "quick" => await Repair.QuickRepairAsync(diagnosis, CancellationToken.None),
                "reinstall" => await Repair.ReinstallDriverAsync(diagnosis, CancellationToken.None),
                "clean" => await Repair.RemoveOldDevicesAsync(chosen, CancellationToken.None),
                _ => await Repair.ForceCleanAsync(chosen, CancellationToken.None)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { result = new RepairResult(false, ex.Message); }
        repairBusy = false;
        ShowRepairStatus(result.Message);
        // Check again: the step counts as done only when what it was for is now fine.
        var before = step;
        before.State = "done";
        await CheckBluetoothAsync();
        bool helped = before.Key switch
        {
            "quick" => btDiagnosis?.NeedsQuickRepair == false,
            "reinstall" => btDiagnosis is { AdapterMissing: false } now && now.BrokenAdapters.Count == 0,
            _ => chosen.All(device => btDiagnosis?.OldDevices.All(old => old.InstanceId != device.InstanceId) == true)
        };
        before.State = helped && result.Ok ? "done" : "failed";
        before.Line = result.Message;
        AdvanceRepair();
    }

    private void ShowRepairStatus(string text) { RepairStatus.Text = text; RepairStatus.Visibility = Visibility.Visible; }
}
