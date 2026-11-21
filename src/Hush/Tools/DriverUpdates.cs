using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Hush;

// Driver updates (owner, November 20, 2026: Tools > Software updates shows device sections, "like if there is a newer Bluetooth version driver, it will show
// that with something smart"; owner's picks: Programs plus one tab per device type, and Hush installs the ticked drivers itself). The drivers come from
// Windows Update (only updates of the Driver type, never Windows' own updates). Each one is sorted into a device type by its driver class and says which
// version is installed now (read from the device's own registry entry) and which one is new. Installing downloads and installs exactly the ticked ones
// through Windows Update, needs administrator mode, and says when a restart is needed. Behind an interface: tests use a fake, nothing real is installed.
internal sealed record DriverUpdate(string UpdateId, string Title, string Device, string Category, string Manufacturer, string NewVersion, string? InstalledVersion,
    DateTime? Released, long Bytes);

internal sealed record DriverInstallResult(int Installed, int Failed, bool RestartNeeded, IReadOnlyDictionary<string, string> Messages);

// Windows Update is turned off (Hush's own switch or anything else): there is nothing to search with.
internal sealed class WindowsUpdateOffException() : Exception("Windows Update is turned off");

// The driver search failed for another reason: its own error code is kept so the card can say it (owner, November 20, 2026: a PC found no Bluetooth driver
// and the card only said "cant be checked").
internal sealed class DriverSearchException(int code) : Exception($"Windows Update answered with error 0x{code:X8}") { internal int Code { get; } = code; }

internal interface IDriverUpdates
{
    Task<IReadOnlyList<DriverUpdate>> SearchAsync(CancellationToken token);
    Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdate> chosen, IProgress<string>? progress, CancellationToken token);
}

internal static class DriverCategories
{
    // The device tabs, in this order, after Programs. A tab shows only while it holds an update.
    internal static readonly string[] Order = ["Graphics", "Bluetooth", "Audio", "Network", "Other devices"];
    internal static string Singular(string category) => category switch
    {
        "Graphics" => "graphics", "Bluetooth" => "Bluetooth", "Audio" => "audio", "Network" => "network", _ => "device"
    };

    // Bluetooth is recognised by every signal there is (owner, November 20, 2026: a Bluetooth driver landed under Other devices or Graphics): the updates
    // class, the devices own class, a Bluetooth hardware id (BTH...), or the word in its title; only a real display driver beats it.
    internal static string Of(string driverClass, string title, string hardwareId = "", string deviceClass = "")
    {
        string text = (driverClass + " " + title).ToLowerInvariant();
        bool display = driverClass.Equals("Display", StringComparison.OrdinalIgnoreCase) || deviceClass.Equals("Display", StringComparison.OrdinalIgnoreCase);
        if (!display && (driverClass.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase) || deviceClass.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || IsBluetoothHardware(hardwareId) || text.Contains("bluetooth"))) return "Bluetooth";
        if (display || text.Contains("graphics") || text.Contains("display adapter")) return "Graphics";
        if (driverClass is "MEDIA" or "Media" or "AudioEndpoint" or "AudioProcessingObject" || text.Contains("audio") || text.Contains("sound")) return "Audio";
        if (driverClass.StartsWith("Net", StringComparison.OrdinalIgnoreCase) || text.Contains("wi-fi") || text.Contains("wireless lan") || text.Contains("ethernet")
            || text.Contains("network")) return "Network";
        return "Other devices";
    }

    internal static bool IsBluetoothHardware(string hardwareId) =>
        hardwareId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) || hardwareId.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) || hardwareId.Contains("BTHLE", StringComparison.OrdinalIgnoreCase);

    // The new version as Windows Update names it ("Intel - Bluetooth - 23.40.0.2"): the last dotted number in the title.
    internal static string VersionIn(string title) => Regex.Matches(title, @"\d+(?:\.\d+){1,3}").LastOrDefault()?.Value ?? "";
}

internal sealed class WindowsDriverUpdates : IDriverUpdates
{
    private const int ServiceDisabled = unchecked((int)0x80070422), UpdateServiceNotRunning = unchecked((int)0x8024A000);
    private object? session;                                   // the Windows Update session of the last search
    private readonly Dictionary<string, object> found = [];    // update id -> its Windows Update object, for installing exactly what was listed

    // Microsoft Update: the wider catalog Windows uses for optional drivers ("Receive updates for other Microsoft products"). Hush searches it only when it is
    // already turned on; it never turns it on by itself (that is a Windows setting).
    internal const string MicrosoftUpdateService = "7971f918-a847-4430-9279-4a52d1efe18d";
    private const int SearchDefault = 0, SearchWindowsUpdate = 2, SearchOthers = 3;

    // Owner's pick (November 20, 2026, "add optional + Microsoft Update"): the drivers come from every source Windows has, merged without duplicates: the
    // PC's own setting (Windows Update, or a company's update server), Windows Update asked directly (a company server often hides drivers), and Microsoft
    // Update when it is on. Optional drivers are included (the query does not filter them out); hidden ones stay hidden.
    public Task<IReadOnlyList<DriverUpdate>> SearchAsync(CancellationToken token) => RunAlone(() =>
    {
        found.Clear();
        dynamic updateSession = session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!)!;
        updateSession.ClientApplicationID = "Hush";
        const string query = "IsInstalled=0 and Type='Driver' and IsHidden=0";
        var results = new List<dynamic>();
        dynamic first = updateSession.CreateUpdateSearcher();
        try { results.Add(first.Search(query)); }
        catch (COMException ex) when (ex.HResult is ServiceDisabled or UpdateServiceNotRunning) { throw new WindowsUpdateOffException(); }
        catch (COMException ex) { throw new DriverSearchException(ex.HResult); }
        // The further sources are a bonus: one that fails (offline, not allowed by a policy) is skipped and the others still count.
        foreach (var (selection, service) in new[] { (SearchWindowsUpdate, ""), (SearchOthers, MicrosoftUpdateService) })
        {
            if (token.IsCancellationRequested) break;
            if (service.Length > 0 && !MicrosoftUpdateIsOn()) continue;
            try
            {
                dynamic searcher = updateSession.CreateUpdateSearcher();
                searcher.ServerSelection = selection;
                if (service.Length > 0) searcher.ServiceID = service;
                results.Add(searcher.Search(query));
            }
            catch (COMException) { }
        }
        var installed = InstalledDrivers.Read();
        var list = new List<DriverUpdate>();
        foreach (dynamic result in results)
        {
            int count = result.Updates.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic update = result.Updates.Item(i);
                string id = (string)update.Identity.UpdateID, title = (string)update.Title;
                if (found.ContainsKey(id)) continue;   // the same driver from two sources
                string driverClass = Text(() => update.DriverClass), hardware = Text(() => update.DriverHardwareID), model = Text(() => update.DriverModel);
                string maker = Text(() => update.DriverManufacturer);
                DateTime? released = null; try { released = (DateTime)update.DriverVerDate; } catch (Exception) { }
                long bytes = 0; try { bytes = (long)(decimal)update.MaxDownloadSize; } catch (Exception) { }
                installed.TryGetValue(hardware, out var device);
                string name = model.Length > 0 ? model : device?.Name ?? title;
                list.Add(new(id, title, name, DriverCategories.Of(driverClass, title + " " + model, hardware, device?.Class ?? ""), maker, DriverCategories.VersionIn(title), device?.Version, released, bytes));
                found[id] = update;
            }
        }
        return (IReadOnlyList<DriverUpdate>)list;
    }, token, TimeSpan.FromMinutes(4));

    // True when Microsoft Update is registered on this PC (read-only).
    private static bool MicrosoftUpdateIsOn()
    {
        try
        {
            dynamic manager = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.ServiceManager", throwOnError: true)!)!;
            foreach (dynamic service in manager.Services)
                if (string.Equals((string)service.ServiceID, MicrosoftUpdateService, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        return false;
    }

    public Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdate> chosen, IProgress<string>? progress, CancellationToken token) => RunAlone(() =>
    {
        if (session is null) throw new InvalidOperationException("Search first.");
        dynamic updateSession = session;
        dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", throwOnError: true)!)!;
        var order = new List<string>();
        foreach (var driver in chosen)
        {
            if (!found.TryGetValue(driver.UpdateId, out var item)) continue;   // only exactly what this search listed
            dynamic update = item;
            if (!(bool)update.EulaAccepted) update.AcceptEula();                  // the popup says the license is accepted for you
            collection.Add(update);
            order.Add(driver.UpdateId);
        }
        var messages = new Dictionary<string, string>();
        if (order.Count == 0) return new DriverInstallResult(0, 0, false, messages);
        progress?.Report($"Downloading {order.Count} driver{(order.Count == 1 ? "" : "s")}…");
        dynamic downloader = updateSession.CreateUpdateDownloader();
        downloader.Updates = collection;
        downloader.Download();
        token.ThrowIfCancellationRequested();
        progress?.Report($"Installing {order.Count} driver{(order.Count == 1 ? "" : "s")}…");
        dynamic installer = updateSession.CreateUpdateInstaller();
        installer.Updates = collection;
        dynamic result = installer.Install();
        int ok = 0, failed = 0;
        for (int i = 0; i < order.Count; i++)
        {
            int code = (int)result.GetUpdateResult(i).ResultCode;   // 2 succeeded, 3 succeeded with errors, 4 failed, 5 aborted
            bool good = code is 2 or 3;
            if (good) ok++; else failed++;
            messages[order[i]] = good ? "Installed" : code == 5 ? "Stopped" : "Not installed";
        }
        return new DriverInstallResult(ok, failed, (bool)result.RebootRequired, messages);
    }, token, TimeSpan.FromMinutes(30));

    private static string Text(Func<object?> read) { try { return read() as string ?? ""; } catch (Exception) { return ""; } }

    // Windows Update calls block; each runs on its own thread so the window never waits. A cancel stops waiting for it (the call itself finishes in the background).
    private static Task<T> RunAlone<T>(Func<T> work, CancellationToken token, TimeSpan limit)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A multithreaded apartment: the install uses the objects the search made on another thread, which only works across free threads (an STA's objects
        // would be stranded once its thread ends).
        var thread = new Thread(() => { try { done.TrySetResult(work()); } catch (Exception ex) { done.TrySetException(ex); } }) { IsBackground = true, Name = "Hush driver updates" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return done.Task.WaitAsync(limit, token);
    }
}

// The installed driver of each device, by hardware id: its name and driver version (read-only, from the device's registry entries; no administrator needed).
internal static class InstalledDrivers
{
    internal sealed record Device(string Name, string Version, string Class = "");

    internal static Dictionary<string, Device> Read(RegistryKey? root = null, TimeSpan? limit = null)
    {
        var devices = new Dictionary<string, Device>(StringComparer.OrdinalIgnoreCase);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var budget = limit ?? TimeSpan.FromSeconds(3);
        var machine = root ?? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        try
        {
            using var enumKey = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            using var classKey = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
            if (enumKey == null || classKey == null) return devices;
            foreach (string bus in enumKey.GetSubKeyNames())
            {
                using var busKey = Open(enumKey, bus); if (busKey == null) continue;
                foreach (string model in busKey.GetSubKeyNames())
                {
                    if (clock.Elapsed > budget) return devices;
                    using var modelKey = Open(busKey, model); if (modelKey == null) continue;
                    foreach (string instance in modelKey.GetSubKeyNames())
                    {
                        using var device = Open(modelKey, instance); if (device == null) continue;
                        if (device.GetValue("HardwareID") is not string[] ids || device.GetValue("Driver") is not string driver) continue;
                        using var driverKey = Open(classKey, driver);
                        if (driverKey?.GetValue("DriverVersion") is not string version) continue;
                        string name = Friendly(device.GetValue("FriendlyName") as string ?? device.GetValue("DeviceDesc") as string ?? model);
                        string deviceClass = device.GetValue("Class") as string ?? "";
                        foreach (string id in ids) devices.TryAdd(id, new Device(name, version, deviceClass));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        finally { if (root == null) machine.Dispose(); }
        return devices;
    }

    // "@oem12.inf,%desc%;Intel(R) Wireless Bluetooth(R)" -> "Intel(R) Wireless Bluetooth(R)"
    internal static string Friendly(string name) => name.Contains(';') ? name[(name.LastIndexOf(';') + 1)..] : name;

    private static RegistryKey? Open(RegistryKey parent, string name)
    {
        try { return parent.OpenSubKey(name); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
