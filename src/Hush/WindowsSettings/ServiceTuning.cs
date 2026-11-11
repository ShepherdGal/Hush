using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Hush;

// Settings > Services (0.9, user request): finds Windows services that start with Windows but that most people do not need, and services left behind by a program
// that was removed (their program file no longer exists). Hush never claims this "speeds up" the PC: it shows what each one uses right now and lets the person
// turn it off (Disabled, so Windows no longer starts it; Undo puts the original start type back).
// Rules, kept like every other system change in Hush: a curated list only (nothing outside it can be changed, whatever the UI sends), no security / update /
// network / driver service is ever on it, every original start type is saved to disk BEFORE the first change, the result is read back and reported exactly,
// Undo puts every original back, and each change is logged. Everything that touches Windows sits behind IServiceSystem, so tests never change a real service.
internal sealed record ServiceInfo(string Name, string Display, int Type, int Start, bool Delayed, string ImagePath, bool Running, int Pid);

internal interface IServiceSystem
{
    bool IsAdministrator { get; }
    // Every Windows service on this PC. Read-only; works without administrator rights.
    IReadOnlyList<ServiceInfo> List();
    // False only when the file is certainly missing (a drive that is absent or removable counts as "cannot tell", so it is never reported missing).
    bool FileExists(string path);
    long MemoryOf(int pid);
    ServiceInfo? Read(string name);
    bool TrySetStart(string name, int start, bool delayed, out string error);
    void Stop(string name);
    void Start(string name);
}

internal enum ServiceLevel { Recommended, Somewhat, LeftBehind }

// One service Hush offers. TargetStart is what Hush would set: 4 = Disabled, for catalog services and leftovers alike (reversible with Undo).
internal sealed record ServiceCandidate(string Name, string Title, string Why, ServiceLevel Level, bool Running, long MemoryBytes, int OriginalStart, bool OriginalDelayed, int TargetStart)
{
    internal string Topic => ServiceTuning.TopicFor(Name, Level);
    internal string StartLabel => OriginalStart switch { 2 => OriginalDelayed ? "Starts with Windows (delayed)" : "Starts with Windows", 3 => "Starts only when needed", 4 => "Disabled", _ => "System" };
    // True for a service Hush turned off earlier (it is Disabled now): the card still lists it, with its switch ON, so it can be turned back on.
    internal bool ChangedByHush { get; init; }
    internal string Status => ChangedByHush ? "Turned off by Hush · was " + StartLabel : StartLabel + " · " + (Running ? "Running" + (MemoryBytes > 0 ? " · " + Sizes.Format(MemoryBytes) : "") : "Not running");
}

internal sealed record ServiceReport(IReadOnlyList<string> Changed, IReadOnlyList<string> Failed, IReadOnlyList<string> StillRunning)
{
    internal string Text(bool undo)
    {
        string verb = undo ? "put back" : "changed";
        string text = Changed.Count == 0 ? "Nothing was changed." : $"{Changed.Count} service{(Changed.Count == 1 ? "" : "s")} {verb} and checked: {string.Join(", ", Changed)}.";
        if (StillRunning.Count > 0) text += $" {string.Join(", ", StillRunning)} could not be stopped right now; {(undo ? "it keeps running" : "it stops at the next restart")}.";
        if (Failed.Count > 0) text += $"\n\nCould not be changed: {string.Join("; ", Failed)}.";
        return text;
    }
}

internal sealed class ServiceTuning(IServiceSystem system, string directory)
{
    private const int Automatic = 2, Manual = 3, Disabled = 4;

    // The curated list. Names are Windows' own service names. None of these is a security, update, network, sign-in, driver or storage service
    // (a test checks that against the names Hush must never touch). Level: Recommended = rarely used on a home PC; Somewhat = useful to some people, read first.
    internal static readonly (string Name, string Title, string Why, ServiceLevel Level)[] Catalog =
    [
        ("Fax", "Fax", "Sends and receives faxes. Almost nobody still uses it.", ServiceLevel.Recommended),
        ("MapsBroker", "Downloaded Maps Manager", "Keeps offline maps up to date for the Maps app. Not needed if you do not use offline maps.", ServiceLevel.Recommended),
        ("RetailDemo", "Retail Demo Service", "Only meant for demo PCs in stores.", ServiceLevel.Recommended),
        ("WMPNetworkSvc", "Windows Media Player Network Sharing", "Shares your media library with other devices on your network.", ServiceLevel.Recommended),
        ("lltdsvc", "Link-Layer Topology Discovery Mapper", "Draws the \"network map\". Rarely used.", ServiceLevel.Recommended),
        ("RemoteRegistry", "Remote Registry", "Lets other computers read this PC's registry. Off is safer.", ServiceLevel.Recommended),
        ("AJRouter", "AllJoyn Router Service", "Connects some smart-home gadgets. Rarely used.", ServiceLevel.Recommended),
        ("icssvc", "Windows Mobile Hotspot Service", "Shares this PC's connection as a hotspot. Only needed if you use that.", ServiceLevel.Recommended),
        ("XblAuthManager", "Xbox Live Auth Manager", "Xbox sign-in for Store games. Not needed if you do not use Xbox features.", ServiceLevel.Recommended),
        ("XblGameSave", "Xbox Live Game Save", "Syncs Xbox game saves. Not needed if you do not use Xbox features.", ServiceLevel.Recommended),
        ("XboxGipSvc", "Xbox Accessory Management Service", "For Xbox controllers and accessories. Wired and Bluetooth gamepads keep working in most games.", ServiceLevel.Recommended),
        ("XboxNetApiSvc", "Xbox Live Networking Service", "Xbox network features. Not needed if you do not use them.", ServiceLevel.Recommended),
        ("wisvc", "Windows Insider Service", "Only for PCs on Windows Insider builds.", ServiceLevel.Recommended),
        ("SEMgrSvc", "Payments and NFC/SE Manager", "For tap-to-pay hardware. Most PCs do not have any.", ServiceLevel.Recommended),
        ("CscService", "Offline Files", "Keeps copies of network files for offline use. Rarely used at home.", ServiceLevel.Recommended),
        ("diagnosticshub.standardcollector.service", "Diagnostics Hub Standard Collector", "A developer tool for collecting diagnostics.", ServiceLevel.Recommended),
        ("DiagTrack", "Connected User Experiences and Telemetry", "Sends diagnostic data to Microsoft. Turning it down means Windows reports less.", ServiceLevel.Somewhat),
        ("SysMain", "SysMain (Superfetch)", "Preloads apps you use often. Little help on a fast SSD; on a hard drive some apps may start a little slower.", ServiceLevel.Somewhat),
        ("WSearch", "Windows Search", "Builds the search index. Without it, searching still works but is slower.", ServiceLevel.Somewhat),
        ("WerSvc", "Windows Error Reporting", "Sends crash reports. Without it, you no longer get \"report this problem\" prompts.", ServiceLevel.Somewhat),
        ("Spooler", "Print Spooler", "Needed for printing. Only change it if you never print.", ServiceLevel.Somewhat),
        ("TabletInputService", "Touch Keyboard and Handwriting Panel Service", "Needed for the touch keyboard and pen handwriting. Keep it on a touchscreen or pen PC.", ServiceLevel.Somewhat),
        ("SSDPSRV", "SSDP Discovery", "Finds devices for casting and DLNA. Casting to a TV may stop finding it.", ServiceLevel.Somewhat),
        ("upnphost", "UPnP Device Host", "Lets this PC act as a UPnP device. Rarely used.", ServiceLevel.Somewhat),
        ("TrkWks", "Distributed Link Tracking Client", "Keeps shortcuts working when files move between network drives. Rarely needed.", ServiceLevel.Somewhat),
        ("WpcMonSvc", "Parental Controls", "Needed only if you use Microsoft Family Safety on this PC.", ServiceLevel.Somewhat),
        ("gupdate", "Google Update Service", "Checks for Chrome and Google app updates in the background. Chrome still updates when you open it.", ServiceLevel.Somewhat),
        ("gupdatem", "Google Update Service (machine-wide)", "Checks for Chrome and Google app updates in the background. Chrome still updates when you open it.", ServiceLevel.Somewhat),
        ("edgeupdate", "Microsoft Edge Update Service", "Checks for Edge updates in the background. Edge still updates when you open it.", ServiceLevel.Somewhat),
        ("edgeupdatem", "Microsoft Edge Update Service (machine-wide)", "Checks for Edge updates in the background. Edge still updates when you open it.", ServiceLevel.Somewhat),
        ("AdobeARMservice", "Adobe Acrobat Update Service", "Checks for Adobe Reader and Acrobat updates in the background.", ServiceLevel.Somewhat),
        ("MozillaMaintenance", "Mozilla Maintenance Service", "Helps Firefox install updates without extra prompts. Firefox still updates.", ServiceLevel.Somewhat),
        // ---- more services that most home PCs do not need (the well-known "safe to turn off" lists; each exists only on some PCs and is listed only if present) ----
        ("GamingServices", "Gaming Services", "The helper behind Game Pass and Microsoft Store games. Keep it if you install games from the Microsoft Store or Xbox app.", ServiceLevel.Somewhat),
        ("GamingServicesNet", "Gaming Services (network)", "The network half of Gaming Services for Microsoft Store games. Keep it if you use Game Pass.", ServiceLevel.Somewhat),
        ("dmwappushservice", "WAP Push Message Routing Service", "Routes device-management push messages. Not needed on a home PC.", ServiceLevel.Recommended),
        ("PcaSvc", "Program Compatibility Assistant", "Offers compatibility fixes for old programs. Without it, old programs get no automatic suggestions.", ServiceLevel.Somewhat),
        ("WdiServiceHost", "Diagnostic Service Host", "Runs the built-in Windows troubleshooters. Without it, Troubleshoot cannot run.", ServiceLevel.Somewhat),
        ("WdiSystemHost", "Diagnostic System Host", "Runs the built-in Windows troubleshooters. Without it, Troubleshoot cannot run.", ServiceLevel.Somewhat),
        ("PhoneSvc", "Phone Service", "Lets Windows make calls through a paired phone. Not needed without that.", ServiceLevel.Recommended),
        ("SmsRouter", "Microsoft Windows SMS Router Service", "Routes text messages. Not needed on most PCs.", ServiceLevel.Recommended),
        ("TapiSrv", "Telephony", "For dial-up modems and some old VPN connections. Not needed on most PCs.", ServiceLevel.Somewhat),
        ("WFDSConMgrSvc", "Wi-Fi Direct Services Connection Manager", "Connects to nearby Wi-Fi Direct devices (some wireless displays and printers).", ServiceLevel.Recommended),
        ("SensrSvc", "Sensor Monitoring Service", "Watches hardware sensors such as ambient light. Not needed on a desktop PC.", ServiceLevel.Recommended),
        ("SensorDataService", "Sensor Data Service", "Delivers sensor data (rotation, light). Keep it on a tablet or 2-in-1.", ServiceLevel.Somewhat),
        ("SensorService", "Sensor Service", "Manages sensors such as auto-rotate and auto-brightness. Keep it on a tablet or 2-in-1.", ServiceLevel.Somewhat),
        ("RemoteAccess", "Routing and Remote Access", "Turns this PC into a router or VPN server. Not needed on a home PC.", ServiceLevel.Recommended),
        ("WinRM", "Windows Remote Management", "Lets administrators run commands on this PC remotely. Not needed at home.", ServiceLevel.Somewhat),
        ("FDResPub", "Function Discovery Resource Publication", "Makes this PC visible in network folders. Turning it off hides this PC from other PCs' Network view.", ServiceLevel.Somewhat),
        ("fdPHost", "Function Discovery Provider Host", "Finds network devices and printers. Network printer discovery may stop working.", ServiceLevel.Somewhat),
        ("p2pimsvc", "Peer Networking Identity Manager", "Old peer-to-peer networking. Not used by current programs.", ServiceLevel.Recommended),
        ("p2psvc", "Peer Networking Grouping", "Old peer-to-peer networking. Not used by current programs.", ServiceLevel.Recommended),
        ("PNRPsvc", "Peer Name Resolution Protocol", "Old peer-to-peer name lookup. Not used by current programs.", ServiceLevel.Recommended),
        ("PNRPAutoReg", "PNRP Machine Name Publication Service", "Old peer-to-peer name publishing. Not used by current programs.", ServiceLevel.Recommended),
        ("NetTcpPortSharing", "Net.Tcp Port Sharing Service", "Lets several .NET programs share one network port. Only developers need it.", ServiceLevel.Recommended),
        ("WebClient", "WebClient", "Lets Windows open files on WebDAV web folders. Rarely used; off is also safer.", ServiceLevel.Recommended),
        ("PrintNotify", "Printer Extensions and Notifications", "Shows printer pop-ups and extra printer apps. Basic printing still works.", ServiceLevel.Somewhat),
        ("stisvc", "Windows Image Acquisition (WIA)", "Needed to scan from a scanner or import from some cameras. Keep it if you scan.", ServiceLevel.Somewhat),
        ("PushToInstall", "Windows PushToInstall Service", "Lets the Microsoft Store install apps you start from another device.", ServiceLevel.Recommended),
        ("WalletService", "WalletService", "Supports the old Windows Wallet app. Not needed.", ServiceLevel.Recommended),
        ("SCardSvr", "Smart Card", "For smart-card readers used to sign in at work. Not needed on a home PC.", ServiceLevel.Recommended),
        ("ScDeviceEnum", "Smart Card Device Enumeration Service", "Finds smart-card readers. Not needed without them.", ServiceLevel.Recommended),
        ("SCPolicySvc", "Smart Card Removal Policy", "Locks the PC when a smart card is removed. Not needed without one.", ServiceLevel.Recommended),
        ("CertPropSvc", "Certificate Propagation", "Copies smart-card certificates. Not needed without a smart card.", ServiceLevel.Somewhat),
        ("WbioSrvc", "Windows Biometric Service", "Needed for fingerprint and face sign-in (Windows Hello). Keep it if you use those.", ServiceLevel.Somewhat),
        ("bthserv", "Bluetooth Support Service", "Needed for Bluetooth devices. Keep it if you use Bluetooth headphones, mice or keyboards.", ServiceLevel.Somewhat),
        ("BthAvctpSvc", "AVCTP Service", "Lets Bluetooth audio devices control playback. Keep it if you use Bluetooth audio.", ServiceLevel.Somewhat),
        ("ALG", "Application Layer Gateway Service", "Helps old programs through Internet Connection Sharing. Rarely used.", ServiceLevel.Recommended),
        ("CDPSvc", "Connected Devices Platform Service", "Nearby sharing, Phone Link and clipboard sync between devices. Keep it if you use those.", ServiceLevel.Somewhat),
        ("SharedRealitySvc", "Spatial Data Service", "Used by mixed-reality headsets. Not needed without one.", ServiceLevel.Recommended),
        ("perceptionsimulation", "Windows Perception Simulation Service", "Used by mixed-reality headsets. Not needed without one.", ServiceLevel.Recommended),
        ("spectrum", "Windows Perception Service", "Used by mixed-reality headsets. Not needed without one.", ServiceLevel.Recommended),
        ("MixedRealityOpenXRSvc", "Windows Mixed Reality OpenXR Service", "Used by mixed-reality headsets. Not needed without one.", ServiceLevel.Recommended),
        ("VacSvc", "Volumetric Audio Compositor Service", "Spatial audio for mixed-reality headsets. Not needed without one.", ServiceLevel.Recommended),
        ("NvTelemetryContainer", "NVIDIA Telemetry Container", "Sends usage data from the NVIDIA driver. The driver works without it.", ServiceLevel.Recommended),
        ("TieringEngineService", "Storage Tiers Management", "Moves files between fast and slow drives in Storage Spaces. Not needed on a normal PC.", ServiceLevel.Recommended),
        ("SNMPTRAP", "SNMP Trap", "Receives network-monitoring messages. Not needed at home.", ServiceLevel.Recommended),
        ("lmhosts", "TCP/IP NetBIOS Helper", "Name lookup for very old Windows networks. Rarely needed.", ServiceLevel.Somewhat),
        ("MicrosoftEdgeElevationService", "Microsoft Edge Elevation Service", "Helps Edge update itself with extra rights. Edge still updates when you open it.", ServiceLevel.Somewhat),
        ("GoogleChromeElevationService", "Google Chrome Elevation Service", "Helps Chrome update itself with extra rights. Chrome still updates when you open it.", ServiceLevel.Somewhat),
        ("brave", "Brave Update Service", "Checks for Brave updates in the background. Brave still updates when you open it.", ServiceLevel.Somewhat),
        ("bravem", "Brave Update Service (machine-wide)", "Checks for Brave updates in the background. Brave still updates when you open it.", ServiceLevel.Somewhat),
        ("dbupdate", "Dropbox Update Service", "Checks for Dropbox updates in the background. Dropbox still updates itself.", ServiceLevel.Somewhat),
        ("dbupdatem", "Dropbox Update Service (machine-wide)", "Checks for Dropbox updates in the background. Dropbox still updates itself.", ServiceLevel.Somewhat),
    ];


    // The sub-category (topic) of every catalog entry, shown as a collapsible heading on the Services card. Names differ from the Windows-choice topics on
    // purpose: the Settings page remembers which headings are open by name. A leftover whose program is gone has its own topic.
    internal const string LeftBehindTopic = "Left behind by removed programs";
    private static readonly Dictionary<string, string[]> TopicMembers = new()
    {
        ["Xbox and gaming"] = ["XblAuthManager", "XblGameSave", "XboxGipSvc", "XboxNetApiSvc", "GamingServices", "GamingServicesNet"],
        ["Diagnostics and error reports"] = ["DiagTrack", "dmwappushservice", "diagnosticshub.standardcollector.service", "WerSvc", "PcaSvc", "WdiServiceHost", "WdiSystemHost", "NvTelemetryContainer"],
        ["Phone, SMS and nearby devices"] = ["PhoneSvc", "SmsRouter", "TapiSrv", "icssvc", "WFDSConMgrSvc", "CDPSvc", "AJRouter"],
        ["Sensors and mixed reality"] = ["SensrSvc", "SensorDataService", "SensorService", "SharedRealitySvc", "perceptionsimulation", "spectrum", "MixedRealityOpenXRSvc", "VacSvc"],
        ["Network discovery and sharing"] = ["lltdsvc", "SSDPSRV", "upnphost", "FDResPub", "fdPHost", "lmhosts", "WMPNetworkSvc", "TrkWks", "CscService", "WebClient", "NetTcpPortSharing", "ALG", "SNMPTRAP"],
        ["Remote access and old peer-to-peer"] = ["RemoteRegistry", "RemoteAccess", "WinRM", "p2pimsvc", "p2psvc", "PNRPsvc", "PNRPAutoReg"],
        ["Printing and scanning"] = ["Spooler", "PrintNotify", "stisvc", "Fax"],
        ["Cards, Bluetooth and sign-in devices"] = ["SCardSvr", "ScDeviceEnum", "SCPolicySvc", "CertPropSvc", "WbioSrvc", "bthserv", "BthAvctpSvc", "TabletInputService"],
        ["Store, maps and Windows extras"] = ["MapsBroker", "RetailDemo", "wisvc", "SEMgrSvc", "WalletService", "PushToInstall", "TieringEngineService", "WpcMonSvc"],
        ["Search and performance helpers"] = ["SysMain", "WSearch"],
        ["App updaters"] = ["gupdate", "gupdatem", "edgeupdate", "edgeupdatem", "MicrosoftEdgeElevationService", "GoogleChromeElevationService", "AdobeARMservice", "MozillaMaintenance", "brave", "bravem", "dbupdate", "dbupdatem"],
    };
    private static readonly Dictionary<string, string> topicByName = TopicMembers.SelectMany(pair => pair.Value.Select(name => (name, topic: pair.Key))).ToDictionary(pair => pair.name, pair => pair.topic, StringComparer.OrdinalIgnoreCase);
    // The order the topics are shown in on the card: the order they are declared above, then anything else, then the left-behind services last.
    private static readonly string[] TopicOrder = TopicMembers.Keys.ToArray();
    internal static int TopicRank(string topic)
    {
        if (topic == LeftBehindTopic) return 1000;
        if (topic == ListGrouping.Other) return 999;
        int index = Array.IndexOf(TopicOrder, topic);
        return index >= 0 ? index : 998;
    }
    internal static string TopicFor(string name, ServiceLevel level) => level == ServiceLevel.LeftBehind ? LeftBehindTopic : topicByName.GetValueOrDefault(name, ListGrouping.Other);
    // Names that are never offered and never changed, even if a leftover's key somehow carried one (defense in depth; the catalog above avoids them anyway).
    internal static readonly HashSet<string> NeverTouch = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "Sense", "WdNisSvc", "WdFilter", "SecurityHealthService", "wscsvc", "mpssvc", "BFE", "EventLog", "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "lsass",
        "Dhcp", "Dnscache", "nsi", "NlaSvc", "netprofm", "LanmanWorkstation", "LanmanServer", "Winmgmt", "CryptSvc", "TrustedInstaller", "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS",
        "TermService", "ProfSvc", "UserManager", "StateRepository", "PlugPlay", "Power", "Schedule", "Themes", "AudioSrv", "Audiosrv", "AudioEndpointBuilder", "ShellHWDetection", "VaultSvc",
        "EventSystem", "SystemEventsBroker", "TimeBrokerSvc", "gpsvc", "iphlpsvc", "BrokerInfrastructure", "CoreMessagingRegistrar", "Wcmsvc", "WlanSvc", "Netman", "NcbService"
    };

    private static bool IsProgramService(int type) => (type & 0x30) != 0; // own process or shared process; kernel and file-system drivers are never listed

    private sealed record SavedService(string Name, int Start, bool Delayed, bool WasRunning);
    private sealed record History(int Schema, List<SavedService> Services);
    private readonly string folder = Path.GetFullPath(directory);
    private string HistoryPath => Path.Combine(folder, "services-changed.json");

    internal bool CanChange => system.IsAdministrator;
    // True while Hush has changed services that have not been put back.
    internal bool HasChanges { get { try { return Load()?.Services.Count > 0; } catch { return false; } } }
    internal int ChangedCount { get { try { return Load()?.Services.Count ?? 0; } catch { return 0; } } }

    // What Hush would offer right now. Read-only. A service already set to Manual or Disabled is not offered: there is nothing left to do for it.
    internal IReadOnlyList<ServiceCandidate> Scan()
    {
        var found = system.List().Where(info => IsProgramService(info.Type) && !NeverTouch.Contains(info.Name)).ToDictionary(info => info.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<ServiceCandidate>();
        foreach (var (name, title, why, level) in Catalog)
        {
            if (!found.TryGetValue(name, out var info) || info.Start is not (Automatic or Manual)) continue; // not on this PC, or already disabled: nothing left to do
            result.Add(new(name, title, why, level, info.Running, info.Running && info.Pid > 0 ? system.MemoryOf(info.Pid) : 0, info.Start, info.Delayed, Disabled));
        }
        // Leftovers: a program service that starts with Windows but whose program file is gone. It can only fail to start; Disabled stops the error and is reversible.
        foreach (var info in found.Values.OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (info.Start != Automatic || Catalog.Any(item => item.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase))) continue;
            string? file = ExecutablePath(info.ImagePath);
            if (file == null || !IsOutsideWindows(file) || system.FileExists(file)) continue;
            string title = info.Display.Length == 0 || info.Display.StartsWith('@') ? info.Name : info.Display;
            result.Add(new(info.Name, title, $"Left behind by a program that was removed: its file ({Path.GetFileName(file)}) no longer exists, so this service can only fail to start.",
                ServiceLevel.LeftBehind, false, 0, info.Start, info.Delayed, Disabled));
        }
        // Running ones first (they are the ones using the PC right now), then by start type (Automatic before Manual), then by how safe they are.
        return result.OrderByDescending(candidate => candidate.Running).ThenBy(candidate => candidate.OriginalStart).ThenBy(candidate => candidate.Level).ToList();
    }

    // The services Hush turned off earlier and that are still disabled (read from the saved history, so Undo can put exactly these back). Read-only.
    internal IReadOnlyList<ServiceCandidate> ScanChanged()
    {
        var result = new List<ServiceCandidate>();
        History? history;
        try { history = Load(); } catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { return result; }
        if (history == null) return result;
        foreach (var saved in history.Services)
        {
            var now = system.Read(saved.Name);
            if (now == null || now.Start != Disabled) continue; // gone, or someone changed it since: nothing to show
            var entry = Catalog.FirstOrDefault(item => item.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));
            bool known = entry.Name != null;
            string title = known ? entry.Title : now.Display.Length == 0 || now.Display.StartsWith('@') ? now.Name : now.Display;
            string why = known ? entry.Why : "Left behind by a program that was removed: its file no longer exists, so this service could only fail to start.";
            result.Add(new(saved.Name, title, why, known ? entry.Level : ServiceLevel.LeftBehind, false, 0, saved.Start, saved.Delayed, Disabled) { ChangedByHush = true });
        }
        return result.OrderBy(candidate => candidate.Level).ThenBy(candidate => candidate.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // The program file named by a service's ImagePath (quoted or not, arguments dropped, variables expanded); null when it is not an ordinary local file path.
    internal static string? ExecutablePath(string imagePath)
    {
        string text = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        string path;
        if (text.StartsWith('"')) { int end = text.IndexOf('"', 1); if (end <= 1) return null; path = text[1..end]; }
        else { int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (exe <= 0) return null; path = text[..(exe + 4)]; }
        return path.Length > 3 && Regex.IsMatch(path, @"^[A-Za-z]:\\") && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    internal static bool IsOutsideWindows(string file)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !file.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    internal ServiceReport Apply(IReadOnlyCollection<string> names)
    {
        if (!system.IsAdministrator) throw new IOException("Administrator rights are needed to change services. Nothing was changed.");
        // Only what Scan offers right now can be changed, whatever names arrive: an unknown or protected name is ignored.
        var offered = Scan().ToDictionary(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase);
        var chosen = names.Distinct(StringComparer.OrdinalIgnoreCase).Where(offered.ContainsKey).Select(name => offered[name]).ToList();
        var changed = new List<string>(); var failed = new List<string>(); var running = new List<string>();
        if (chosen.Count == 0) return new(changed, failed, running);
        var history = Load() ?? new History(1, []);
        var savedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // originals this call saved (a service changed earlier keeps its older original)
        foreach (var candidate in chosen)
            if (!history.Services.Any(saved => saved.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)))
            {
                history.Services.Add(new(candidate.Name, candidate.OriginalStart, candidate.OriginalDelayed, candidate.Running)); // the ORIGINAL, kept until it is put back
                savedNow.Add(candidate.Name);
            }
        Save(history); // saved BEFORE the first change
        foreach (var candidate in chosen)
        {
            if (!system.TrySetStart(candidate.Name, candidate.TargetStart, false, out string error)) { failed.Add($"{candidate.Title} ({error})"); Forget(history, savedNow, candidate.Name); continue; }
            var after = system.Read(candidate.Name);
            if (after == null || after.Start != candidate.TargetStart) { failed.Add($"{candidate.Title} did not keep its new setting"); Forget(history, savedNow, candidate.Name); continue; }
            changed.Add(candidate.Title);
            Log(candidate.TargetStart == Disabled ? "disabled" : "manual", candidate);
            if (!candidate.Running) continue;
            try { system.Stop(candidate.Name); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or IOException) { running.Add(candidate.Title); }
        }
        if (history.Services.Count == 0) { try { File.Delete(HistoryPath); } catch (IOException) { } } else Save(history);
        return new(changed, failed, running);
    }

    // A service whose change did not happen has nothing to put back, so the original saved for it a moment ago is dropped again.
    private static void Forget(History history, HashSet<string> savedNow, string name)
    {
        if (savedNow.Contains(name)) history.Services.RemoveAll(saved => saved.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // only = null puts every changed service back; otherwise only the named ones (the person switched them back on one by one).
    internal ServiceReport Undo(IReadOnlyCollection<string>? only = null)
    {
        if (!system.IsAdministrator) throw new IOException("Administrator rights are needed to put services back. Nothing was changed.");
        var dropped = new List<string>();
        var history = Load(dropped) ?? throw new IOException("Hush has not changed any service, so there is nothing to put back.");
        var changed = new List<string>(); var failed = new List<string>(); var running = new List<string>();
        if (only == null) foreach (string name in dropped) failed.Add($"{name} (left as it is: Hush could not confirm that it changed this service)");
        foreach (var saved in history.Services.ToList())
        {
            if (only != null && !only.Contains(saved.Name, StringComparer.OrdinalIgnoreCase)) continue;
            var now = system.Read(saved.Name);
            if (now == null) { history.Services.Remove(saved); continue; } // the service no longer exists
            string title = Catalog.FirstOrDefault(item => item.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase)).Title ?? saved.Name;
            if (now.Start != saved.Start || now.Delayed != saved.Delayed)
            {
                if (!system.TrySetStart(saved.Name, saved.Start, saved.Delayed, out string error)) { failed.Add($"{title} ({error})"); continue; }
                var after = system.Read(saved.Name);
                if (after == null || after.Start != saved.Start) { failed.Add($"{title} did not return to its old setting"); continue; }
            }
            changed.Add(title);
            history.Services.Remove(saved);
            if (saved.WasRunning)
            {
                try { system.Start(saved.Name); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or IOException) { running.Add(title); }
            }
        }
        if (history.Services.Count == 0) { try { File.Delete(HistoryPath); } catch (IOException) { } } else Save(history);
        return new(changed, failed, running);
    }

    // rejected (optional) receives the names of entries that were dropped because Hush cannot confirm it could ever have changed them.
    private History? Load(List<string>? rejected = null)
    {
        if (!File.Exists(HistoryPath)) return null;
        if (!CleanupEngine.SafePath(HistoryPath, folder) || new FileInfo(HistoryPath).Length > 65536) throw new InvalidDataException("The services history is unsafe or damaged. Nothing was changed.");
        var history = JsonSerializer.Deserialize<History>(File.ReadAllText(HistoryPath)) ?? throw new InvalidDataException("The services history is damaged.");
        if (history.Schema != 1 || history.Services == null) throw new InvalidDataException("The services history is damaged.");
        // The history sits in a folder the person's own programs can write to, and Undo runs with administrator rights, so the file must never be able to
        // aim Hush at another service (0.9 audit). Only start types Hush could have saved (Automatic, Manual, Disabled) are kept, and only names Hush could
        // have offered: a catalog service, or a leftover whose program file is, right now, still missing. Everything else is dropped and reported.
        history.Services.RemoveAll(saved => string.IsNullOrWhiteSpace(saved.Name) || NeverTouch.Contains(saved.Name) || saved.Start is < Automatic or > Disabled);
        foreach (var saved in history.Services.ToList())
        {
            if (Catalog.Any(item => item.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase)) || IsVerifiedLeftover(saved.Name)) continue;
            history.Services.Remove(saved);
            rejected?.Add(saved.Name);
        }
        return history;
    }

    // The same test Scan uses for leftovers, minus the start type (Hush has already set it to Disabled): a program service whose file is outside Windows and missing.
    private bool IsVerifiedLeftover(string name)
    {
        var info = system.Read(name);
        if (info == null || !IsProgramService(info.Type) || NeverTouch.Contains(info.Name)) return false;
        string? file = ExecutablePath(info.ImagePath);
        return file != null && IsOutsideWindows(file) && !system.FileExists(file);
    }

    private void Save(History history)
    {
        Directory.CreateDirectory(folder);
        if (!CleanupEngine.SafePath(folder, folder)) throw new IOException("The services history location is unsafe.");
        string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, history); file.Flush(true); }
            File.Move(temporary, HistoryPath, overwrite: true);
        }
        finally { if (File.Exists(temporary) && CleanupEngine.SafePath(temporary, folder)) File.Delete(temporary); }
    }

    private void Log(string action, ServiceCandidate candidate)
    {
        try
        {
            string logs = Path.Combine(folder, "..", "Logs");
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "services.jsonl"), JsonSerializer.Serialize(new
            { utc = DateTime.UtcNow, action, service = candidate.Name, title = candidate.Title, wasStart = candidate.OriginalStart, wasRunning = candidate.Running, nowStart = candidate.TargetStart }) + Environment.NewLine);
        }
        catch { /* the log is a courtesy; the saved history is what Undo relies on */ }
    }
}

// The real Windows. Services are read from the registry (no administrator rights needed) and one sc.exe call for what is running; changes go through
// sc.exe (Windows' own service control tool), never by editing the registry. Never used by tests.
internal sealed class RealServiceSystem : IServiceSystem
{
    public bool IsAdministrator => WindowsSystemTools.IsElevated();

    public IReadOnlyList<ServiceInfo> List()
    {
        var running = RunningServices();
        var result = new List<ServiceInfo>();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var services = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services == null) return result;
        foreach (string name in services.GetSubKeyNames())
        {
            running.TryGetValue(name, out var state);
            var info = ReadKey(services, name, state.Running, state.Pid);
            if (info != null) result.Add(info);
        }
        return result;
    }

    private static ServiceInfo? ReadKey(RegistryKey services, string name, bool running, int pid)
    {
        try
        {
            using var key = services.OpenSubKey(name);
            if (key == null || key.GetValue("Type") is not int type || key.GetValue("Start") is not int start) return null;
            string image = key.GetValue("ImagePath") as string ?? "";
            bool delayed = key.GetValue("DelayedAutostart") is int d && d == 1;
            return new(name, key.GetValue("DisplayName") as string ?? "", type, start, delayed, image, running, pid);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; /* not readable: not ours to list */ }
    }

    // One call for everything: STATE 4 = running, plus the process id. The numbers read the same in every Windows language.
    private static Dictionary<string, (bool Running, int Pid)> RunningServices()
    {
        var result = new Dictionary<string, (bool, int)>(StringComparer.OrdinalIgnoreCase);
        var (code, output) = Run("sc.exe", "queryex", "type=", "service", "state=", "all");
        if (code != 0) return result;
        ParseQueryEx(output, result);
        return result;
    }

    private static void ParseQueryEx(string output, Dictionary<string, (bool Running, int Pid)> result)
    {
        foreach (string block in Regex.Split(output, @"\r?\n\r?\n"))
        {
            var name = Regex.Match(block, @"SERVICE_NAME:\s*(\S+)"); var state = Regex.Match(block, @"STATE\s*:\s*(\d+)"); var pid = Regex.Match(block, @"PID\s*:\s*(\d+)");
            if (name.Success && state.Success) result[name.Groups[1].Value] = (state.Groups[1].Value == "4", pid.Success ? int.Parse(pid.Groups[1].Value) : 0);
        }
    }

    public bool FileExists(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (root == null) return true;
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return true; // cannot tell: never call it missing
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }

    public long MemoryOf(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.WorkingSet64; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }

    // One service only: its own registry key plus "sc queryex <name>" (no walk of the whole services tree, no full list).
    public ServiceInfo? Read(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/']) >= 0) return null;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var services = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services == null) return null;
        var info = ReadKey(services, name, false, 0);
        if (info == null) return null;
        var state = new Dictionary<string, (bool Running, int Pid)>(StringComparer.OrdinalIgnoreCase);
        try { var (code, output) = Run("sc.exe", "queryex", name); if (code == 0) ParseQueryEx(output, state); }
        catch (IOException) { /* state unknown: reported as not running, as List does when sc fails */ }
        return state.TryGetValue(name, out var found) ? info with { Running = found.Running, Pid = found.Pid } : info;
    }

    public bool TrySetStart(string name, int start, bool delayed, out string error)
    {
        error = "";
        string kind = start switch { 2 => delayed ? "delayed-auto" : "auto", 3 => "demand", 4 => "disabled", _ => "" };
        if (kind.Length == 0) { error = "unknown start type"; return false; }
        try
        {
            var (code, output) = Run("sc.exe", "config", name, "start=", kind);
            if (code != 0) error = code == 5 ? "Windows refused the change" : output.Trim().Split('\n')[0].Trim();
            return code == 0;
        }
        catch (IOException ex) { error = ex.Message; return false; }
    }

    private static int? State(string name)
    {
        var (code, output) = Run("sc.exe", "query", name);
        var match = Regex.Match(output, @"STATE\s*:\s*(\d+)");
        return code == 0 && match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static void Wait(string name, int state)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15)) { if (State(name) == state) return; Thread.Sleep(300); }
        throw new TimeoutException(name + " did not reach the expected state in time.");
    }

    // A non-zero answer from sc.exe (running dependents, not stoppable, access denied) is reported at once instead of waiting 15 s for a state that will not come.
    // 1062 = not started and 1056 = already running: those are the wanted state already.
    public void Stop(string name)
    {
        if (State(name) is 1 or null) return;
        var (code, output) = Run("sc.exe", "stop", name);
        if (code != 0 && code != 1062) throw new InvalidOperationException(Short(output, "Windows would not stop " + name + " (code " + code + ")."));
        Wait(name, 1);
    }

    public void Start(string name)
    {
        if (State(name) == 4) return;
        var (code, output) = Run("sc.exe", "start", name);
        if (code != 0 && code != 1056) throw new InvalidOperationException(Short(output, "Windows would not start " + name + " (code " + code + ")."));
        Wait(name, 4);
    }

    private static string Short(string output, string fallback)
    {
        string line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => !l.StartsWith("[SC]", StringComparison.Ordinal)) ?? "";
        return line.Length is > 0 and < 160 ? line : fallback;
    }

    // Also used by RealTaskSystem for schtasks.exe: arguments through ArgumentList, output read asynchronously, a 30 s limit and a kill on timeout.
    internal static (int Code, string Output) Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows did not start " + file + ".");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { try { process.Kill(); } catch { } throw new IOException(file + " did not answer."); }
        return (process.ExitCode, output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
    }
}
