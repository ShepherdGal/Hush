using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Hush;

// Device repair, Bluetooth first (owner, November 20, 2026: "a very big Bluetooth drivers problem on that PC, build something to fix such issues"; owner's
// picks: diagnose, quick repair, reinstall driver, clean old devices, and force-clean devices that cannot be removed; the guided-steps screen). The diagnosis is
// read-only and needs no administrator rights; every fix asks first, needs administrator mode, is logged, and the diagnosis runs again after it. All PC calls
// go through IDeviceSystem: tests use a fake, nothing real is ever repaired on a test run.

// One Bluetooth device as Windows' device list shows it: the adapter (the radio chip, on USB or PCI) or a device paired with it (headphones, a mouse...).
internal sealed record BtDevice(string InstanceId, string Name, string Status, int Problem, bool Present, string DriverVersion, string DriverDate, string Provider)
{
    // The adapter is the chip itself; Windows' own virtual Bluetooth devices (BTH\MS_...) and the paired devices are not.
    public bool IsAdapter => Regex.IsMatch(InstanceId, @"^(USB|PCI|ACPI|UART|SDIO|BTHUSB)\\", RegexOptions.IgnoreCase);
    public bool IsPairedDevice => Regex.IsMatch(InstanceId, @"^(BTHENUM|BTHLE|BTHLEDEVICE)\\", RegexOptions.IgnoreCase);
    // The 12-digit Bluetooth address a paired device carries in its id ("..._A1B2C3D4E5F6" or "DEV_A1B2C3D4E5F6"); null when it has none.
    public string? Address => Regex.Match(InstanceId, @"(?:DEV_|_|&)([0-9A-F]{12})(?:[_\\&]|$)", RegexOptions.IgnoreCase) is { Success: true } match ? match.Groups[1].Value.ToLowerInvariant() : null;
    public bool HasProblem => Present && Problem != 0;
}

// A Bluetooth service and its state; OffByHush when Hush's Services card turned it off earlier.
internal sealed record BtService(string Name, string Title, int Start, bool Running, bool OffByHush)
{
    public bool Disabled => Start == 4;
}

internal sealed record BtDiagnosis(IReadOnlyList<BtDevice> Adapters, IReadOnlyList<BtDevice> OldDevices, bool? RadioOn, IReadOnlyList<BtService> Services)
{
    public bool AdapterMissing => Adapters.All(adapter => !adapter.Present);
    public IReadOnlyList<BtDevice> BrokenAdapters => Adapters.Where(adapter => adapter.HasProblem).ToList();
    public IReadOnlyList<BtService> OffServices => Services.Where(service => service.Disabled || (service.Name == "bthserv" && !service.Running)).ToList();
    public bool NeedsQuickRepair => OffServices.Count > 0 || BrokenAdapters.Count > 0 || RadioOn == false;
    public bool Healthy => !AdapterMissing && !NeedsQuickRepair;

    // What is wrong, in plain words (empty when nothing is).
    public IReadOnlyList<string> Problems
    {
        get
        {
            var problems = new List<string>();
            if (AdapterMissing) problems.Add(Adapters.Count == 0 ? "No Bluetooth adapter was found" : "The Bluetooth adapter is not connected");
            foreach (var adapter in BrokenAdapters) problems.Add($"{adapter.Name}: {DeviceRepair.ProblemText(adapter.Problem)} (error {adapter.Problem})");
            foreach (var service in OffServices) problems.Add(service.Title + (service.OffByHush ? " is off (turned off by Hush)" : service.Disabled ? " is turned off" : " is not running"));
            if (RadioOn == false) problems.Add("Bluetooth is switched off");
            return problems;
        }
    }
}

internal sealed record RepairResult(bool Ok, string Message);

// The PC calls behind Device repair (a fake in tests).
internal interface IDeviceSystem
{
    bool IsAdministrator { get; }
    Task<IReadOnlyList<BtDevice>> BluetoothDevicesAsync(CancellationToken token);
    Task<bool?> RadioOnAsync();
    Task<RepairResult> RestartDeviceAsync(string instanceId, CancellationToken token);
    Task<RepairResult> RemoveDeviceAsync(string instanceId, CancellationToken token);
    Task<RepairResult> ScanForDevicesAsync(CancellationToken token);
    RepairResult Unpair(string address);
    bool PairingExists(string address);
    RepairResult ExportPairing(string address, string file);
    RepairResult DeletePairing(string address);
}

internal sealed class DeviceRepair(IDeviceSystem pc, IServiceSystem services, ServiceTuning tuning, string backupFolder, string logPath)
{
    // The services Bluetooth needs and how Windows sets them by default (3 = start when needed).
    internal static readonly (string Name, string Title, int DefaultStart)[] BluetoothServices =
    [
        ("bthserv", "Bluetooth Support Service", 3), ("BthAvctpSvc", "Bluetooth AVCTP Service", 3), ("BTAGService", "Bluetooth Audio Gateway Service", 3),
    ];

    internal bool CanRepair => pc.IsAdministrator;

    // Windows' device problem codes in plain words (the ones Bluetooth adapters show).
    internal static string ProblemText(int code) => code switch
    {
        1 => "it is not set up correctly",
        3 => "its driver may be damaged",
        10 => "it could not start",
        12 => "it has no free resources",
        14 => "it needs a restart of Windows",
        18 => "its driver needs to be reinstalled",
        19 => "its settings are damaged",
        21 => "Windows is removing it",
        22 => "it is turned off in Device Manager",
        24 => "it is not working or not connected",
        28 => "no driver is installed",
        31 => "it is not working properly",
        39 => "its driver is damaged or missing",
        43 => "Windows stopped it because it reported problems",
        45 => "it is not connected",
        47 => "it was prepared for safe removal",
        52 => "its driver's signature could not be checked",
        _ => "it reports a problem"
    };

    internal async Task<BtDiagnosis> DiagnoseAsync(CancellationToken token)
    {
        var devices = await pc.BluetoothDevicesAsync(token);
        bool? radio = await pc.RadioOnAsync();
        var changed = tuning.ChangedNames;
        var state = BluetoothServices.Select(known =>
        {
            var info = services.Read(known.Name);
            return info == null ? null : new BtService(known.Name, known.Title, info.Start, info.Running, changed.Contains(known.Name));
        }).Where(service => service != null).Cast<BtService>().ToList();
        return new BtDiagnosis(devices.Where(device => device.IsAdapter).ToList(), devices.Where(device => device.IsPairedDevice && !device.Present).ToList(), radio, state);
    }

    // Step 2: the Bluetooth services back on (through Hush's own Undo when Hush turned them off, so its history stays right), started, and the adapter
    // restarted. No driver is changed.
    internal async Task<RepairResult> QuickRepairAsync(BtDiagnosis diagnosis, CancellationToken token)
    {
        var said = new List<string>();
        bool ok = true;
        var byHush = diagnosis.Services.Where(service => service.OffByHush).Select(service => service.Name).ToList();
        if (byHush.Count > 0)
        {
            var report = await Task.Run(() => tuning.Undo(byHush), token);
            said.Add(report.Text(undo: true).TrimEnd('.'));
        }
        foreach (var service in diagnosis.Services.Where(service => service.Disabled && !service.OffByHush))
        {
            var known = BluetoothServices.First(entry => entry.Name == service.Name);
            if (services.TrySetStart(service.Name, known.DefaultStart, false, out string error)) said.Add(service.Title + " turned back on");
            else { ok = false; said.Add(service.Title + " could not be turned on: " + error); }
        }
        try { services.Start("bthserv"); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        foreach (var adapter in diagnosis.Adapters.Where(adapter => adapter.Present))
        {
            var restart = await pc.RestartDeviceAsync(adapter.InstanceId, token);
            said.Add(restart.Ok ? adapter.Name + " restarted" : adapter.Name + " could not be restarted: " + restart.Message);
            ok &= restart.Ok;
        }
        Log("quick-repair", ok, said);
        return new RepairResult(ok, said.Count == 0 ? "Nothing needed repairing." : string.Join(". ", said) + ".");
    }

    // Step 3: the adapter is removed and Windows looks for devices again, which installs its driver afresh (Device Manager's Uninstall device + Scan for
    // changes). The driver package stays on the PC, so Windows has it at hand.
    internal async Task<RepairResult> ReinstallDriverAsync(BtDiagnosis diagnosis, CancellationToken token)
    {
        var said = new List<string>();
        bool ok = true;
        foreach (var adapter in diagnosis.Adapters)
        {
            var removed = await pc.RemoveDeviceAsync(adapter.InstanceId, token);
            said.Add(removed.Ok ? adapter.Name + " removed" : adapter.Name + " could not be removed: " + removed.Message);
            ok &= removed.Ok;
        }
        var scan = await pc.ScanForDevicesAsync(token);
        said.Add(scan.Ok ? "Windows looked for devices again and reinstalled what it found" : "Windows could not look for devices: " + scan.Message);
        ok &= scan.Ok;
        Log("reinstall-driver", ok, said);
        return new RepairResult(ok, string.Join(". ", said) + ".");
    }

    // Step 4: the chosen old devices are removed the normal way.
    internal async Task<RepairResult> RemoveOldDevicesAsync(IReadOnlyList<BtDevice> chosen, CancellationToken token)
    {
        int removed = 0; var refused = new List<string>();
        foreach (var device in chosen)
        {
            var result = await pc.RemoveDeviceAsync(device.InstanceId, token);
            if (result.Ok) removed++; else refused.Add(device.Name);
        }
        Log("remove-old-devices", refused.Count == 0, [$"{removed} removed", .. refused.Select(name => "refused: " + name)]);
        return new RepairResult(refused.Count == 0, $"Removed {removed} old device{(removed == 1 ? "" : "s")}"
            + (refused.Count > 0 ? $"; {refused.Count} refused ({string.Join(", ", refused)}): Force clean can remove them" : "") + ".");
    }

    // Step 5 (red): devices Windows refuses to remove. In order, stopping at the first that works: unpair through Windows' Bluetooth API, remove the device
    // with its children, and last the device's stored pairing in the registry, exported first to the administrator-only backup folder (the Bluetooth service is
    // stopped for that moment and started again).
    internal async Task<RepairResult> ForceCleanAsync(IReadOnlyList<BtDevice> chosen, CancellationToken token)
    {
        var said = new List<string>();
        bool ok = true, stopped = false;
        try
        {
            foreach (var device in chosen)
            {
                token.ThrowIfCancellationRequested();
                string? address = device.Address;
                if (address != null && pc.Unpair(address).Ok && !pc.PairingExists(address)) { said.Add(device.Name + " unpaired"); continue; }
                var removed = await pc.RemoveDeviceAsync(device.InstanceId, token);
                if (address == null || !pc.PairingExists(address))
                {
                    said.Add(removed.Ok ? device.Name + " removed" : device.Name + " could not be removed: " + removed.Message);
                    ok &= removed.Ok;
                    continue;
                }
                Directory.CreateDirectory(backupFolder);
                string file = Path.Combine(backupFolder, $"bluetooth-{address}-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
                var export = pc.ExportPairing(address, file);
                if (!export.Ok) { ok = false; said.Add(device.Name + ": its pairing was not backed up, so it was left alone (" + export.Message + ")"); continue; }
                if (!stopped) { try { services.Stop("bthserv"); stopped = true; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
                var deleted = pc.DeletePairing(address);
                said.Add(deleted.Ok ? device.Name + " force-removed (backup: " + Path.GetFileName(file) + ")" : device.Name + " could not be force-removed: " + deleted.Message);
                ok &= deleted.Ok;
            }
        }
        finally
        {
            if (stopped) { try { services.Start("bthserv"); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        }
        Log("force-clean", ok, said);
        return new RepairResult(ok, string.Join(". ", said) + ".");
    }

    private void Log(string step, bool ok, IEnumerable<string> lines)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, JsonSerializer.Serialize(new { at = DateTime.Now, step, ok, lines }) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

// The real PC: Windows' device list through PowerShell's PnpDevice commands (read-only), pnputil for restart/remove/scan, Windows' Bluetooth API to unpair,
// and the BTHPORT registry key for a pairing Windows will not let go of.
internal sealed class RealDeviceSystem : IDeviceSystem
{
    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);
    private const string Pairings = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters";

    public bool IsAdministrator => WindowsSystemTools.IsElevated();

    public async Task<IReadOnlyList<BtDevice>> BluetoothDevicesAsync(CancellationToken token)
    {
        const string script =
            "$all = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.Class -eq 'Bluetooth' -or $_.InstanceId -like 'BTHENUM\\*' -or $_.InstanceId -like 'BTHLE\\*' -or $_.InstanceId -like 'BTHLEDEVICE\\*' });" +
            "$present = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | ForEach-Object InstanceId);" +
            "$all | ForEach-Object { $p = @(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_ProblemCode,DEVPKEY_Device_DriverVersion,DEVPKEY_Device_DriverDate,DEVPKEY_Device_DriverProvider -ErrorAction SilentlyContinue);" +
            "[pscustomobject]@{ Id = $_.InstanceId; Name = \"$($_.FriendlyName)\"; Status = \"$($_.Status)\"; Present = ($present -contains $_.InstanceId);" +
            "Problem = [int]($p | Where-Object KeyName -eq 'DEVPKEY_Device_ProblemCode').Data; Version = \"$(($p | Where-Object KeyName -eq 'DEVPKEY_Device_DriverVersion').Data)\";" +
            "Date = \"$(($p | Where-Object KeyName -eq 'DEVPKEY_Device_DriverDate').Data)\"; Provider = \"$(($p | Where-Object KeyName -eq 'DEVPKEY_Device_DriverProvider').Data)\" } } | ConvertTo-Json -Compress";
        var (code, output) = await WindowsDiskCheck.Hidden(Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"", token, TimeSpan.FromSeconds(90));
        if (code != 0 || output.Trim().Length == 0) return [];
        return Parse(output);
    }

    // PowerShell's JSON: one object, or an array of them.
    internal static IReadOnlyList<BtDevice> Parse(string json)
    {
        var list = new List<BtDevice>();
        try
        {
            using var document = JsonDocument.Parse(json.Trim());
            var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToList() : [document.RootElement];
            foreach (var item in items)
            {
                string S(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                int problem = item.TryGetProperty("Problem", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                bool present = item.TryGetProperty("Present", out var pr) && pr.ValueKind == JsonValueKind.True;
                string id = S("Id"); if (id.Length == 0) continue;
                string date = DateTime.TryParse(S("Date"), out var when) ? when.ToString("d MMM yyyy") : "";
                list.Add(new BtDevice(id, S("Name").Length > 0 ? S("Name") : "Unnamed Bluetooth device", S("Status"), problem, present, S("Version"), date, S("Provider")));
            }
        }
        catch (JsonException) { }
        return list;
    }

    public async Task<bool?> RadioOnAsync()
    {
        try
        {
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync().AsTask();
            var bluetooth = radios.FirstOrDefault(radio => radio.Kind == Windows.Devices.Radios.RadioKind.Bluetooth);
            return bluetooth == null ? null : bluetooth.State == Windows.Devices.Radios.RadioState.On;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    private static async Task<RepairResult> PnpUtil(string arguments, CancellationToken token)
    {
        var (code, output) = await WindowsDiskCheck.Hidden(Path.Combine(System32, "pnputil.exe"), arguments, token, TimeSpan.FromMinutes(2));
        string last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return new RepairResult(code is 0 or 3010, code == int.MinValue ? "it took too long" : last.Length > 0 ? last : $"pnputil ended with code {code}");
    }

    public Task<RepairResult> RestartDeviceAsync(string instanceId, CancellationToken token) => PnpUtil($"/restart-device \"{instanceId}\"", token);
    public Task<RepairResult> RemoveDeviceAsync(string instanceId, CancellationToken token) => PnpUtil($"/remove-device \"{instanceId}\" /subtree", token);
    public Task<RepairResult> ScanForDevicesAsync(CancellationToken token) => PnpUtil("/scan-devices", token);

    [DllImport("bthprops.cpl", SetLastError = true)] private static extern int BluetoothRemoveDevice(ref ulong address);

    public RepairResult Unpair(string address)
    {
        try
        {
            ulong value = Convert.ToUInt64(address, 16);
            int code = BluetoothRemoveDevice(ref value);
            return new RepairResult(code == 0, code == 0 ? "unpaired" : $"Windows answered {code}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or FormatException) { return new RepairResult(false, ex.Message); }
    }

    public bool PairingExists(string address)
    {
        using var key = Registry.LocalMachine.OpenSubKey(Pairings + @"\Devices\" + address);
        return key != null;
    }

    public RepairResult ExportPairing(string address, string file)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(System32, "reg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("export"); start.ArgumentList.Add(@"HKLM\" + Pairings + @"\Devices\" + address); start.ArgumentList.Add(file); start.ArgumentList.Add("/y");
        using var process = System.Diagnostics.Process.Start(start);
        if (process == null) return new RepairResult(false, "reg.exe did not start");
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000)) { try { process.Kill(); } catch (InvalidOperationException) { } return new RepairResult(false, "the backup took too long"); }
        return new RepairResult(process.ExitCode == 0 && File.Exists(file), process.ExitCode == 0 ? "backed up" : error.Trim());
    }

    public RepairResult DeletePairing(string address)
    {
        try
        {
            using var devices = Registry.LocalMachine.OpenSubKey(Pairings + @"\Devices", writable: true);
            devices?.DeleteSubKeyTree(address, throwOnMissingSubKey: false);
            // The pairing key the adapter keeps for the device (Keys\<adapter>\<device>).
            using var keys = Registry.LocalMachine.OpenSubKey(Pairings + @"\Keys", writable: true);
            if (keys != null)
                foreach (string adapter in keys.GetSubKeyNames())
                {
                    using var adapterKey = keys.OpenSubKey(adapter, writable: true);
                    if (adapterKey?.GetValue(address) != null) adapterKey.DeleteValue(address, throwOnMissingValue: false);
                }
            return new RepairResult(!PairingExists(address), "removed");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return new RepairResult(false, ex.Message); }
    }
}
