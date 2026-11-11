using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hush;

// "Turn off Windows Update" and the restore point before big changes. Fakes stand in for services, scheduled tasks, policies and System Restore:
// nothing here ever changes a real Windows service, task, policy or restore point on this PC.
internal sealed class FakeRestorePoints : IRestorePoints
{
    internal RestorePointStatus Status = new(true, false, true);
    internal string? CreateError, EnableError = null;
    internal readonly List<string> Created = [], Enabled = [];
    public RestorePointStatus Probe() => Status;
    public string? Create(string description) { Created.Add(description); return CreateError; }
    public string? EnableProtection() { Enabled.Add("C:"); if (EnableError == null) Status = Status with { ProtectionOn = true }; return EnableError; }
}

internal sealed class FakeUpdateSystem : IWindowsUpdateSystem
{
    internal bool Admin = true;
    internal readonly Dictionary<string, PolicyValue> Policies = [];
    internal readonly Dictionary<string, int> Starts = new() { ["wuauserv"] = 3, ["UsoSvc"] = 2, ["WaaSMedicSvc"] = 3, ["BITS"] = 3 };
    internal readonly HashSet<string> Running = ["wuauserv", "UsoSvc"];
    internal readonly HashSet<string> Protected = ["WaaSMedicSvc"]; // Windows refuses to change it, like the real one
    internal readonly Dictionary<string, bool> Tasks = WindowsUpdateSwitch.Tasks.Take(5).ToDictionary(name => name, _ => true);
    internal readonly List<string> Writes = [];
    internal Action? OnFirstWrite;
    // What Windows can refuse (each with the reason a real tool prints): every policy write, every service change, every task change.
    internal bool RefusePolicies, RefuseServices, RefuseTasks;
    private bool wrote;
    public bool IsAdministrator => Admin;
    private static string K(string key, string name) => key + "|" + name;
    private void Note(string what) { if (!wrote) { wrote = true; OnFirstWrite?.Invoke(); } Writes.Add(what); }
    public PolicyValue ReadPolicy(string key, string name) => Policies.GetValueOrDefault(K(key, name), PolicyValue.Missing);
    public void WritePolicy(string key, string name, PolicyValue value) { if (RefusePolicies) throw new UnauthorizedAccessException("Access to the registry key is denied."); Note("policy " + name); if (value.Exists) Policies[K(key, name)] = value; else Policies.Remove(K(key, name)); }
    public int? ReadServiceStart(string service) => Starts.TryGetValue(service, out int start) ? start : null;
    public bool TryWriteServiceStart(string service, int start, out string error)
    {
        error = "";
        if (RefuseServices) { error = "Windows refused the change ([SC] ChangeServiceConfig FAILED 5: Access is denied.)"; return false; }
        if (Protected.Contains(service)) { error = "Windows protects this service and refused the change"; return false; }
        Note("service " + service); Starts[service] = start; return true;
    }
    public bool IsServiceRunning(string service) => Running.Contains(service);
    public void StopService(string service) { Note("stop " + service); Running.Remove(service); }
    public void StartService(string service) { Note("start " + service); Running.Add(service); }
    public bool? ReadTaskEnabled(string task) => Tasks.TryGetValue(task, out bool on) ? on : null;
    public bool TrySetTaskEnabled(string task, bool enabled, out string error)
    {
        error = "";
        if (RefuseTasks) { error = "ERROR: Access is denied.\r\n"; return false; }
        Note("task " + task); Tasks[task] = enabled; return true;
    }
}

internal static class WindowsUpdateChecks
{
    // Honest reports (owner, November 11, 2026: "Turn off Windows Update still doesn't work"): when Windows refuses, the report says why and a run that changed
    // nothing never leaves the red "Windows Update is off" line behind; the last result is also kept in a text file.
    internal static void RunReports(string fixture, Action<bool, string> check)
    {
        // 1. Windows refuses everything: nothing changed, so nothing is claimed.
        string refusedDirectory = Path.Combine(fixture, "update-switch-refused");
        var refused = new FakeUpdateSystem { RefusePolicies = true, RefuseServices = true, RefuseTasks = true };
        var none = new WindowsUpdateSwitch(refused, refusedDirectory);
        var report = none.TurnOff();
        string lastRun = File.Exists(none.LastRunPath) ? File.ReadAllText(none.LastRunPath) : "";
        check(!report.Done && report.Changed.Count == 0 && !none.IsOff && !File.Exists(Path.Combine(refusedDirectory, "windows-update-off.json")),
            "When Windows refuses every change, Hush says nothing was changed and leaves no 'Windows Update is off' state behind");
        check(report.Failed.Any(item => item.Contains("Access is denied")) && report.Failed.Any(item => item.Contains("wuauserv") && item.Contains("ChangeServiceConfig FAILED 5")) && report.Failed.Any(item => item.Contains("stayed enabled") && item.Contains("Access is denied")),
            "The report names why each part was refused (policy, service and task reasons), not only that it failed (" + string.Join(" | ", report.Failed) + ")");
        check(lastRun.Contains("nothing was changed") && lastRun.Contains("Access is denied") && lastRun.Contains("turn off"), "The result is also saved in a text file (windows-update-last-run.txt) that can be read afterwards");

        // 2. Windows refuses only the scheduled tasks (they usually need SYSTEM): the rest is off and the state is honest about it.
        string partialDirectory = Path.Combine(fixture, "update-switch-partial");
        var partial = new FakeUpdateSystem { RefuseTasks = true };
        var some = new WindowsUpdateSwitch(partial, partialDirectory);
        var partialReport = some.TurnOff();
        check(partialReport.Done && !partialReport.Complete && some.IsOff && partial.Starts["wuauserv"] == 4 && partialReport.Failed.Any(item => item.Contains("stayed enabled")) && File.ReadAllText(some.LastRunPath).Contains("partly done"),
            "When Windows refuses only some parts the rest is turned off, the state says so, and the refused parts are named");
        var back = some.TurnBackOn();
        check(back.Done && !some.IsOff && partial.Starts["wuauserv"] == 3 && File.ReadAllText(some.LastRunPath).Contains("turn back on"), "Turning it back on still restores what was changed and writes its own report");
        check(WindowsUpdateSwitch.Short("  \r\n[SC] OpenService FAILED 5:\r\n\r\nAccess is denied.  ") == "[SC] OpenService FAILED 5:" && WindowsUpdateSwitch.Short(null) == "" && WindowsUpdateSwitch.Short(new string('x', 300)).Length == 141,
            "A tool's answer is cut to its first line and a sensible length for the report");
    }

    internal static void RunEngine(string fixture, Action<bool, string> check)
    {
        string directory = Path.Combine(fixture, "update-switch");
        string history = Path.Combine(directory, "windows-update-off.json");
        var system = new FakeUpdateSystem { Admin = false };
        system.Policies[WindowsUpdateSwitch.Policies[0].Key + "|NoAutoUpdate"] = new(true, 0); // an original value that must come back
        var update = new WindowsUpdateSwitch(system, directory);

        bool refused = false;
        try { update.TurnOff(); } catch (IOException) { refused = true; }
        check(refused && system.Writes.Count == 0 && !update.IsOff && !File.Exists(history), "Turning Windows Update off without administrator rights changes nothing and saves nothing");

        system.Admin = true;
        bool savedBeforeFirstChange = false;
        system.OnFirstWrite = () => savedBeforeFirstChange = File.Exists(history) && File.ReadAllText(history).Contains("NoAutoUpdate");
        var off = update.TurnOff();
        check(savedBeforeFirstChange, "Every original value is saved to disk before the first change is made");
        check(update.IsOff && off.Changed.Any(item => item.Contains("wuauserv")) && system.Starts["wuauserv"] == 4 && system.Starts["UsoSvc"] == 4 && system.Starts["BITS"] == 4
            && !system.Running.Contains("wuauserv") && !system.Running.Contains("UsoSvc"), "Off means off: update services are disabled and stopped");
        check(WindowsUpdateSwitch.Policies.All(item => system.ReadPolicy(item.Key, item.Name) == new PolicyValue(true, item.Value)) && system.Tasks.Values.All(on => !on),
            "The Windows Update policies are set and the update scheduled tasks are disabled, each one verified after writing");
        check(!off.Complete && off.Failed.Any(item => item.Contains("WaaSMedicSvc") && item.Contains("protects")) && system.Starts["WaaSMedicSvc"] == 3
            && off.Text(true).Contains("Could not be changed") && off.Text(true).Contains("WaaSMedicSvc"), "A service Windows protects is reported exactly instead of being claimed as changed");

        // A second run keeps the very first originals; Hush's own values never become the 'before' state.
        update.TurnOff();
        var saved = File.ReadAllText(history);
        check(saved.Contains("\"Number\":0") && saved.Contains("\"Start\":3"), "Running it again keeps the original values saved the first time");

        system.Writes.Clear();
        var on = update.TurnBackOn();
        check(system.Starts["wuauserv"] == 3 && system.Starts["UsoSvc"] == 2 && system.Starts["BITS"] == 3 && system.Running.Contains("wuauserv") && system.Running.Contains("UsoSvc") && !system.Running.Contains("BITS"),
            "Turning it back on restores every start type and restarts the services that were running");
        check(system.ReadPolicy(WindowsUpdateSwitch.Policies[0].Key, "NoAutoUpdate") == new PolicyValue(true, 0)
            && WindowsUpdateSwitch.Policies.Skip(1).All(item => system.ReadPolicy(item.Key, item.Name) == PolicyValue.Missing) && system.Tasks.Values.All(enabled => enabled),
            "Undo puts back the original policy values (including a value that was absent) and re-enables the tasks");
        check(on.Complete && !update.IsOff && !File.Exists(history) && !system.Writes.Any(write => write == "service WaaSMedicSvc"), "A complete restore closes the history and never touches a service Hush could not change");

        // A damaged or foreign history file can never aim Hush at another value.
        Directory.CreateDirectory(directory);
        File.WriteAllText(history, "{ not json");
        bool damaged = false;
        try { update.TurnBackOn(); } catch (InvalidDataException) { damaged = true; } catch (JsonException) { damaged = true; }
        check(damaged && !update.IsOff, "A damaged history is refused with nothing written");
        File.Delete(history);
        update.TurnOff();
        File.WriteAllText(history, File.ReadAllText(history).Replace("NoAutoUpdate", "SomethingElse"));
        system.Writes.Clear();
        update.TurnBackOn();
        check(!system.Writes.Contains("policy SomethingElse") && system.ReadPolicy(WindowsUpdateSwitch.Policies[0].Key, "SomethingElse") == PolicyValue.Missing, "Only the fixed list of update values is ever restored, whatever a history file says");
        if (File.Exists(history)) File.Delete(history);

        // Restore point module: its face and the real thing's read-only probe.
        var fake = new FakeRestorePoints();
        check(fake.Probe().CanCreate && fake.Create("x") == null && fake.Created.Count == 1, "The restore point service is faked everywhere in the tests");
        var real = new WindowsRestorePoints().Probe();
        check(real != null, "The real System Restore probe only reads (it makes no restore point)");
    }

    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        string directory = Path.Combine(fixture, "update-ui");
        var system = new FakeUpdateSystem();
        var points = new FakeRestorePoints();
        var oldPoints = MainWindow.RestorePoints; MainWindow.RestorePoints = points;
        var window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "update-ui-preferences.json"), windowsUpdate: new WindowsUpdateSwitch(system, directory)) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var overlay = (FrameworkElement)window.FindName("WindowsSettingsOverlay");
            var consent = (CheckBox)window.FindName("WindowsConsentBox");
            var confirm = (Button)window.FindName("ConfirmWindowsSettingsButton");
            var banner = (FrameworkElement)window.FindName("UpdateOffBanner");
            string Text(string name) => ((TextBlock)window.FindName(name)).Text;
            Click("SettingsButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(200);
            check(banner.Visibility == Visibility.Collapsed && Text("UpdateSwitchSummary") == "On", "Windows Update is on: no red line, and the card says so");

            // Turn off: red consent box first, then a restore point, then the change.
            Click("TurnOffUpdateButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(Text("WindowsConfirmTitle") == "Turn off Windows Update?" && Text("WindowsConfirmDetail").Contains("stops getting security fixes") && consent.Visibility == Visibility.Visible
                && !confirm.IsEnabled && ((CheckBox)window.FindName("RestorePointBox")).IsChecked == true && ((CheckBox)window.FindName("AlsoUpdateOffBox")).Visibility == Visibility.Collapsed,
                "Turning Windows Update off asks first, names the security warning, offers a restore point and stays disabled until the consent box is ticked");
            check(consent.Tag is SolidColorBrush tag && tag.Color == ((SolidColorBrush)Application.Current.Resources["CleanTextBrush"]).Color, "The consent box uses the one Clean red");
            // The real-life case: no scan result on screen. The consent box (a Review-style checkbox whose enabled state used to follow "a scan result can be selected") was dead
            // then, so it could not be ticked and the "Turn off Windows Update" button stayed faded forever (owner report, November 11, 2026).
            check(!window.CanSelectCandidates && consent.IsEnabled && ((CheckBox)window.FindName("RestorePointBox")).IsEnabled,
                "With no scan results the consent box and the restore point box are still enabled, so the person can tick them and the button can be pressed");
            UiLayoutChecks.Layout(root, new Size(920, 820));
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots) { Directory.CreateDirectory(shots); UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "windows-update-confirm-dark.png")); }
            check(system.Writes.Count == 0 && points.Created.Count == 0, "Nothing is changed and no restore point is made before the answer");
            consent.IsChecked = true; consent.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check(confirm.IsEnabled, "Ticking the consent box enables the action");
            Click("ConfirmWindowsSettingsButton");
            UiLayoutChecks.PumpUntil(() => Text("WindowsConfirmTitle") == "Windows Update partly turned off");
            check(points.Created.Count == 1 && system.Starts["wuauserv"] == 4 && window.IsWindowsUpdateOff && Text("WindowsConfirmSummary").Contains("WaaSMedicSvc"),
                "A restore point is made first, Windows Update is turned off (the title says partly, since Windows refuses one service) and the result names what Windows would not change");
            UiLayoutChecks.Wait(350); UiLayoutChecks.Layout(root, new Size(920, 820));
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots2) UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots2, "windows-update-result-dark.png"));
            Click("CancelWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Collapsed);
            check(banner.Visibility == Visibility.Visible && ((SolidColorBrush)((TextBlock)window.FindName("UpdateOffText")).Foreground).Color == ((SolidColorBrush)Application.Current.Resources["CleanTextBrush"]).Color
                && Text("UpdateSwitchSummary").StartsWith("Off"), "While it is off a lasting red line shows at the top of Settings");

            // Back on with one click; no consent needed, no restore point offered.
            Click("UpdateBackOnButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(Text("WindowsConfirmTitle") == "Turn Windows Update back on?" && consent.Visibility == Visibility.Collapsed && confirm.IsEnabled && !window.WindowsOptionsShown, "Turning it back on needs no consent box and offers no restore point");
            Click("ConfirmWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => Text("WindowsConfirmTitle") == "Windows Update turned back on");
            check(system.Starts["wuauserv"] == 3 && !window.IsWindowUpdateOffForTests() && system.Running.Contains("wuauserv"), "One click puts every saved value back and restarts the services");
            Click("CancelWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Collapsed);
            check(banner.Visibility == Visibility.Collapsed, "The red line goes away when Windows Update is back on");

            // A restore point that cannot be made stops the change before anything is written.
            points.CreateError = "Windows did not create the restore point"; int writesBefore = system.Writes.Count;
            Click("TurnOffUpdateButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            consent.IsChecked = true; consent.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Click("ConfirmWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => Text("WindowsConfirmNote").Contains("restore point"));
            // 0.9: the popup STAYS OPEN with the reason (it used to close and tell the person to untick a box they could no longer reach).
            check(system.Writes.Count == writesBefore && !window.IsWindowsUpdateOff && overlay.Visibility == Visibility.Visible && ((CheckBox)window.FindName("RestorePointBox")).IsChecked == false
                && confirm.IsEnabled && Text("WindowsConfirmNote").Contains("Nothing was changed") && Text("WindowsConfirmNote").Contains("without one"),
                "If the restore point fails nothing is changed, the popup stays open and says why and how to continue without one");
            Click("CancelWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Collapsed);
            points.CreateError = null;

            // System Protection off: the box is unavailable, the turn-on box is offered and used first.
            points.Status = new(false, false, true);
            Click("TurnOffUpdateButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            var restoreBox = (CheckBox)window.FindName("RestorePointBox"); var protection = (CheckBox)window.FindName("EnableProtectionBox");
            UiLayoutChecks.PumpUntil(() => protection.Visibility == Visibility.Visible);
            check(!restoreBox.IsEnabled && restoreBox.IsChecked == false && protection.Visibility == Visibility.Visible && Text("RestorePointNote").Contains("System Protection is off"),
                "With System Protection off the popup says so and offers to turn it on");
            protection.IsChecked = true; consent.IsChecked = true; consent.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            int createdBefore = points.Created.Count;
            Click("ConfirmWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => Text("WindowsConfirmTitle") == "Windows Update partly turned off");
            check(points.Enabled.Count == 1 && points.Created.Count == createdBefore + 1 && window.IsWindowsUpdateOff, "Turning System Protection on first lets the restore point be made");
            Click("CancelWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Collapsed);

            // A restore point from the last 24 hours is relied on, with a note; restoring the system is never part of Hush.
            Click("UpdateBackOnButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Visible);
            Click("ConfirmWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => Text("WindowsConfirmTitle") == "Windows Update turned back on");
            Click("CancelWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Collapsed);
            points.Status = new(true, true, true); createdBefore = points.Created.Count;
            Click("TurnOffUpdateButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(Text("RestorePointNote").Contains("last 24 hours") && ((CheckBox)window.FindName("RestorePointBox")).IsChecked == true, "A recent restore point is explained instead of failing silently");
            Click("CancelWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => overlay.Visibility == Visibility.Collapsed);
            check(points.Created.Count == createdBefore, "Cancelling makes no restore point");
        }
        finally { window.Close(); MainWindow.RestorePoints = oldPoints; }
    }
}
