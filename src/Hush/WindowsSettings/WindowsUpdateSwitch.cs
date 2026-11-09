using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Hush;

// "Turn off Windows Update" (0.9, user direction, a red / Advanced choice). Off means completely off: the Windows Update policies, the start types of the update
// services and the Update Orchestrator scheduled tasks. Every original value is saved to disk BEFORE anything changes, Undo puts every one back and restarts the
// services, and the report says exactly what was and was not changed and verified (never a claim that was not checked).
// Everything that touches Windows sits behind IWindowsUpdateSystem, so tests use a fake and never change a real service, task or policy.
internal interface IWindowsUpdateSystem
{
    bool IsAdministrator { get; }
    PolicyValue ReadPolicy(string key, string name);
    void WritePolicy(string key, string name, PolicyValue value);
    int? ReadServiceStart(string service);
    // False (with the reason) when Windows refuses, which it does for WaaSMedicSvc: it is protected by TrustedInstaller.
    bool TryWriteServiceStart(string service, int start, out string error);
    bool IsServiceRunning(string service);
    void StopService(string service);
    void StartService(string service);
    bool? ReadTaskEnabled(string task);
    bool TrySetTaskEnabled(string task, bool enabled, out string error);
}

internal sealed record UpdateOffReport(bool Done, IReadOnlyList<string> Changed, IReadOnlyList<string> Failed)
{
    internal bool Complete => Failed.Count == 0;
    // One plain paragraph for the result popup: what changed and was verified, and what could not be changed.
    internal string Text(bool turningOff)
    {
        string verb = turningOff ? "turned off" : "restored";
        string text = Changed.Count == 0 ? $"Nothing was {verb}." : $"{Changed.Count} part{(Changed.Count == 1 ? " was" : "s were")} {verb} and checked: {string.Join(", ", Changed)}.";
        if (Failed.Count > 0) text += $"\n\nCould not be changed: {string.Join("; ", Failed)}.";
        return text;
    }
}

internal sealed class WindowsUpdateSwitch(IWindowsUpdateSystem system, string directory)
{
    private const string Policy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", PolicyAu = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    // Fixed lists only: nothing here comes from a file or the UI.
    internal static readonly (string Key, string Name, int Value)[] Policies =
    [
        (PolicyAu, "NoAutoUpdate", 1), (PolicyAu, "AUOptions", 1), (Policy, "DisableWindowsUpdateAccess", 1), (Policy, "SetDisableUXWUAccess", 1)
    ];
    internal static readonly string[] Services = ["wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS"];
    internal static readonly string[] Tasks =
    [
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan", @"\Microsoft\Windows\UpdateOrchestrator\Schedule Work", @"\Microsoft\Windows\UpdateOrchestrator\UpdateModelTask",
        @"\Microsoft\Windows\UpdateOrchestrator\USO_UxBroker", @"\Microsoft\Windows\UpdateOrchestrator\Reboot_AC", @"\Microsoft\Windows\UpdateOrchestrator\Reboot_Battery",
        @"\Microsoft\Windows\WindowsUpdate\Scheduled Start"
    ];
    private const int Disabled = 4;

    private sealed record SavedPolicy(string Key, string Name, bool Exists, int Number, bool IsDword);
    private sealed record SavedService(string Name, int? Start, bool WasRunning);
    private sealed record SavedTask(string Name, bool? Enabled);
    private sealed record Backup(int Schema, bool Active, List<SavedPolicy> Policies, List<SavedService> Services, List<SavedTask> Tasks);
    private readonly string historyPath = Path.Combine(Path.GetFullPath(directory), "windows-update-off.json");
    private readonly string folder = Path.GetFullPath(directory);

    // True while Hush's own change is in place (saved history says so). The red line in Settings follows this.
    internal bool IsOff
    {
        get { try { return Load()?.Active == true; } catch { return false; } }
    }

    internal bool CanChange => system.IsAdministrator;

    internal UpdateOffReport TurnOff()
    {
        if (!system.IsAdministrator) throw new IOException("Administrator rights are needed to turn off Windows Update. Nothing was changed.");
        var existing = Load();
        // Originals are saved once and kept while it is off, so a second run can never replace the real "before" values with Hush's own.
        var backup = existing is { Active: true } ? existing : new Backup(1,
            Active: false,
            Policies.Select(item => { var value = system.ReadPolicy(item.Key, item.Name); return new SavedPolicy(item.Key, item.Name, value.Exists, value.Number, value.IsDword); }).ToList(),
            Services.Select(name => new SavedService(name, system.ReadServiceStart(name), system.IsServiceRunning(name))).ToList(),
            Tasks.Select(name => new SavedTask(name, system.ReadTaskEnabled(name))).ToList());
        if (backup.Policies.Any(saved => saved.Exists && !saved.IsDword)) throw new IOException("A Windows Update policy has an unusual value type that Hush could not restore exactly. Nothing was changed.");
        Save(backup with { Active = true }); // saved BEFORE the first change
        var changed = new List<string>(); var failed = new List<string>();
        try
        {
            foreach (var (key, name, value) in Policies)
            {
                try
                {
                    system.WritePolicy(key, name, new(true, value));
                    if (system.ReadPolicy(key, name) == new PolicyValue(true, value)) changed.Add("policy " + name); else failed.Add("policy " + name + " did not keep its value");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { failed.Add("policy " + name + " (" + ex.Message + ")"); }
            }
            foreach (string name in Services)
            {
                if (system.ReadServiceStart(name) == null) continue; // this edition has no such service
                if (!system.TryWriteServiceStart(name, Disabled, out string error)) { failed.Add(name + " (" + error + ")"); continue; }
                if (system.ReadServiceStart(name) != Disabled) { failed.Add(name + " did not stay disabled"); continue; }
                changed.Add(name + " disabled");
                if (name != "WaaSMedicSvc") { try { system.StopService(name); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { /* stays disabled: it will not start again */ } }
            }
            int tasksOff = 0, tasksFailed = 0;
            foreach (string task in Tasks)
            {
                if (system.ReadTaskEnabled(task) == null) continue;
                if (system.TrySetTaskEnabled(task, false, out _) && system.ReadTaskEnabled(task) == false) tasksOff++; else tasksFailed++;
            }
            if (tasksOff > 0) changed.Add($"{tasksOff} update task{(tasksOff == 1 ? "" : "s")} disabled");
            if (tasksFailed > 0) failed.Add($"{tasksFailed} update task{(tasksFailed == 1 ? "" : "s")} stayed enabled");
        }
        catch { Save(backup with { Active = true }); throw; }
        return new(true, changed, failed);
    }

    internal UpdateOffReport TurnBackOn()
    {
        if (!system.IsAdministrator) throw new IOException("Administrator rights are needed to turn Windows Update back on. Nothing was changed.");
        var backup = Load() ?? throw new IOException("Hush has not turned Windows Update off, so there is nothing to restore.");
        var changed = new List<string>(); var failed = new List<string>();
        foreach (var saved in backup.Policies)
        {
            if (!Policies.Any(item => item.Key == saved.Key && item.Name == saved.Name)) continue; // a damaged file can never aim Hush at another value
            var original = saved.Exists ? new PolicyValue(true, saved.Number) : PolicyValue.Missing;
            try
            {
                system.WritePolicy(saved.Key, saved.Name, original);
                if (system.ReadPolicy(saved.Key, saved.Name) == original) changed.Add("policy " + saved.Name); else failed.Add("policy " + saved.Name + " did not return to its old value");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { failed.Add("policy " + saved.Name + " (" + ex.Message + ")"); }
        }
        foreach (var saved in backup.Services)
        {
            if (!Services.Contains(saved.Name) || saved.Start == null) continue;
            // A service that still has its old start type (one Windows would not let Hush disable) is left alone: nothing to put back.
            if (system.ReadServiceStart(saved.Name) == saved.Start) continue;
            if (!system.TryWriteServiceStart(saved.Name, saved.Start.Value, out string error)) { failed.Add(saved.Name + " (" + error + ")"); continue; }
            if (system.ReadServiceStart(saved.Name) != saved.Start) { failed.Add(saved.Name + " did not return to its old start type"); continue; }
            changed.Add(saved.Name + " restored");
            if (saved.WasRunning && saved.Name != "WaaSMedicSvc") { try { system.StartService(saved.Name); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { failed.Add(saved.Name + " could not be restarted (it starts again on its own or at the next restart)"); } }
        }
        int tasksOn = 0, tasksFailed = 0;
        foreach (var saved in backup.Tasks)
        {
            if (!Tasks.Contains(saved.Name) || saved.Enabled == null) continue;
            if (system.TrySetTaskEnabled(saved.Name, saved.Enabled.Value, out _) && system.ReadTaskEnabled(saved.Name) == saved.Enabled) tasksOn++; else tasksFailed++;
        }
        if (tasksOn > 0) changed.Add($"{tasksOn} update task{(tasksOn == 1 ? "" : "s")} restored");
        if (tasksFailed > 0) failed.Add($"{tasksFailed} update task{(tasksFailed == 1 ? "" : "s")} could not be restored");
        // The history is closed only when everything came back; otherwise it stays so a second try (or Undo later) still knows the originals.
        if (failed.Count == 0) { try { File.Delete(historyPath); } catch (IOException) { } }
        else Save(backup with { Active = true });
        return new(true, changed, failed);
    }

    private Backup? Load()
    {
        if (!File.Exists(historyPath)) return null;
        if (!CleanupEngine.SafePath(historyPath, folder) || new FileInfo(historyPath).Length > 65536) throw new InvalidDataException("The Windows Update history is unsafe or damaged. Nothing was changed.");
        var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(historyPath)) ?? throw new InvalidDataException("The Windows Update history is damaged.");
        if (backup.Schema != 1 || backup.Policies == null || backup.Services == null || backup.Tasks == null) throw new InvalidDataException("The Windows Update history is damaged.");
        return backup;
    }

    private void Save(Backup backup)
    {
        Directory.CreateDirectory(folder);
        if (!CleanupEngine.SafePath(folder, folder)) throw new IOException("The Windows Update history location is unsafe.");
        string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, backup); file.Flush(true); }
            File.Move(temporary, historyPath, overwrite: true);
        }
        finally { if (File.Exists(temporary) && CleanupEngine.SafePath(temporary, folder)) File.Delete(temporary); }
    }
}

// The real Windows: HKLM policy values, service start types (their Start value under CurrentControlSet\Services, which needs administrator rights) and
// scheduled tasks through schtasks.exe. Never used by tests.
internal sealed class RealWindowsUpdateSystem : IWindowsUpdateSystem
{
    public bool IsAdministrator => WindowsSystemTools.IsElevated();

    public PolicyValue ReadPolicy(string key, string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var open = root.OpenSubKey(key);
        object? value = open?.GetValue(name);
        return value == null ? PolicyValue.Missing : value is int number && open!.GetValueKind(name) == RegistryValueKind.DWord ? new(true, number) : new(true, 0, false);
    }

    public void WritePolicy(string key, string name, PolicyValue value)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (value.Exists) { using var open = root.CreateSubKey(key, writable: true); open.SetValue(name, value.Number, RegistryValueKind.DWord); }
        else { using var open = root.OpenSubKey(key, writable: true); open?.DeleteValue(name, throwOnMissingValue: false); }
    }

    private static string ServiceKey(string service) => @"SYSTEM\CurrentControlSet\Services\" + service;

    public int? ReadServiceStart(string service)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var open = root.OpenSubKey(ServiceKey(service));
        return open?.GetValue("Start") is int start ? start : null;
    }

    public bool TryWriteServiceStart(string service, int start, out string error)
    {
        error = "";
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var open = root.OpenSubKey(ServiceKey(service), writable: true);
            if (open == null) { error = "not found"; return false; }
            open.SetValue("Start", start, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { error = service == "WaaSMedicSvc" ? "Windows protects this service and refused the change" : "Windows refused the change"; return false; }
    }

    // Service state through sc.exe (its state code is a number, so it reads the same in every language): 4 = running, 1 = stopped.
    private static int? ServiceState(string service)
    {
        var (code, output) = Run("sc.exe", "query", service);
        var match = System.Text.RegularExpressions.Regex.Match(output, @"STATE\s*:\s*(\d+)");
        return code == 0 && match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    public bool IsServiceRunning(string service) { try { return ServiceState(service) == 4; } catch (IOException) { return false; } }

    private static void WaitForState(string service, int state)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20)) { if (ServiceState(service) == state) return; Thread.Sleep(300); }
        throw new TimeoutException(service + " did not reach the expected state in time.");
    }

    public void StopService(string service)
    {
        if (ServiceState(service) is 1 or null) return;
        Run("sc.exe", "stop", service);
        WaitForState(service, 1);
    }

    public void StartService(string service)
    {
        if (ServiceState(service) == 4) return;
        Run("sc.exe", "start", service);
        WaitForState(service, 4);
    }

    private static (int Code, string Output) Schtasks(params string[] arguments) => Run("schtasks.exe", arguments);

    private static (int Code, string Output) Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows did not start the task scheduler.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { try { process.Kill(); } catch { } throw new IOException(file + " did not answer."); }
        return (process.ExitCode, output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
    }

    // The task definition's own <Enabled> flag (language independent); a task with no such element is enabled.
    public bool? ReadTaskEnabled(string task)
    {
        try
        {
            var (code, output) = Schtasks("/Query", "/TN", task, "/XML");
            if (code != 0) return null;
            var document = XDocument.Parse(output.Trim());
            var enabled = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Enabled" && element.Parent?.Name.LocalName == "Settings");
            return enabled == null || !string.Equals(enabled.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { return null; }
    }

    public bool TrySetTaskEnabled(string task, bool enabled, out string error)
    {
        error = "";
        try
        {
            var (code, output) = Schtasks("/Change", "/TN", task, enabled ? "/ENABLE" : "/DISABLE");
            if (code != 0) error = output.Trim();
            return code == 0;
        }
        catch (IOException ex) { error = ex.Message; return false; }
    }
}
