using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Hush;

internal sealed class CleanupEngine
{
    private const string AppPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hush");
    // Targets (and the delete-time lookup of the approved target for an item) are built on first use: the default list
    // looks at the disk, which must not slow window startup, and the lookup is never a per-file scan of the list.
    private Lazy<IReadOnlyList<CleanupTarget>> targetSource;
    private Lazy<Dictionary<string, Approved>> approvedSource;
    private readonly bool defaultTargets;
    // Temporary files (0.9, user direction): files of any age are found. The safety that stays: a file in use is always skipped (exclusive-handle delete)
    // and every file is checked again at delete time.
    private IReadOnlyList<CleanupTarget> targets => targetSource.Value;
    private Dictionary<string, Approved> approved => approvedSource.Value;
    private readonly bool includeRegistry;
    private readonly IRecycleBin? recycleBin;
    private readonly ISystemTools? systemTools;
    private readonly string dataDirectory;
    private readonly ProcessGate processes = new();

    // A target the engine will delete from, with its wildcards parsed once instead of once per file.
    private sealed record Approved(CleanupTarget Target, string[]? Patterns);

    public CleanupEngine() : this(new Lazy<IReadOnlyList<CleanupTarget>>(DefaultTargets), true, null, new ShellRecycleBin(), new WindowsSystemTools(), defaultTargets: true) { }
    internal CleanupEngine(IReadOnlyList<CleanupTarget> targets, bool includeRegistry = false, string? dataDirectory = null, IRecycleBin? recycleBin = null, ISystemTools? systemTools = null)
        : this(new Lazy<IReadOnlyList<CleanupTarget>>(targets), includeRegistry, dataDirectory, recycleBin, systemTools) { }
    private CleanupEngine(Lazy<IReadOnlyList<CleanupTarget>> targetSource, bool includeRegistry, string? dataDirectory, IRecycleBin? recycleBin, ISystemTools? systemTools, bool defaultTargets = false)
    {
        this.defaultTargets = defaultTargets;
        this.targetSource = targetSource;
        this.includeRegistry = includeRegistry;
        this.recycleBin = recycleBin;
        this.systemTools = systemTools;
        this.dataDirectory = dataDirectory ?? DataDirectory;
        approvedSource = NewApprovedSource();
    }

    private Lazy<Dictionary<string, Approved>> NewApprovedSource() => new(() =>
    {
        var lookup = new Dictionary<string, Approved>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targetSource.Value)
        {
            try { lookup.TryAdd(ApprovalKey(Path.GetFullPath(target.Root), target.Kind, target.Group), new(target, Patterns(target.Pattern))); }
            catch (ArgumentException) { } // An unresolvable location can never be scanned or cleaned.
        }
        return lookup;
    });

    // True when this process is elevated as a different Windows account than the person signed in (see AdminMode). Read once per target build.
    internal static bool UserLocationsSkipped => !AdminMode.Elevation.IsSignedInUser;

    private static string ApprovalKey(string root, CleanupKind kind, string group) => $"{root}|{kind}|{group}";

    // Every default location, its risk category and its rules live here and nowhere else.
    // Locations marked DeletableOnly are system-protected: a file is listed only if this user can open it for deletion right now.
    // Folders that do not exist (application not installed) are never added, so a scan only visits what is really there.
    internal static List<CleanupTarget> DefaultTargets()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? Path.GetPathRoot(windows) ?? "C:").TrimEnd('\\') + "\\";
        static TimeSpan Days(int days) => TimeSpan.FromDays(days);
        const string dumps = "*.dmp;*.hdmp;*.mdmp";
        var list = new List<CleanupTarget>
        {
            // ----- Recommended -----
            new(Path.GetTempPath(), "User temp", CleanupKind.Temp, TimeSpan.Zero, Category: "Temp"),
            new(Path.Combine(windows, "Temp"), "Windows temp", CleanupKind.Temp, TimeSpan.Zero, Category: "Temp"),
            // Explorer keeps these open, so only unlocked files are listed.
            new(Path.Combine(local, "Microsoft", "Windows", "Explorer"), "Thumbnail cache", CleanupKind.Cache, Days(1),
                "thumbcache_*.db;iconcache_*.db", Recursive: false, Category: "Thumbnails", DeletableOnly: true),
            // Windows Error Reporting archives/queues (per user, then machine-wide).
            new(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"), "Windows diagnostics", CleanupKind.Junk, Days(30), "*.wer;" + dumps, Category: "Junk"),
            new(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"), "Windows diagnostics", CleanupKind.Junk, Days(30), "*.wer;" + dumps, Category: "Junk"),
            new(Path.Combine(common, "Microsoft", "Windows", "WER", "ReportArchive"), "Windows diagnostics", CleanupKind.Junk, Days(30), "*.wer;" + dumps, Category: "Junk", DeletableOnly: true),
            new(Path.Combine(common, "Microsoft", "Windows", "WER", "ReportQueue"), "Windows diagnostics", CleanupKind.Junk, Days(30), "*.wer;" + dumps, Category: "Junk", DeletableOnly: true),
            // Scratch space Windows Error Reporting uses while it packages a report.
            new(Path.Combine(local, "Microsoft", "Windows", "WER", "Temp"), "Windows diagnostics", CleanupKind.Temp, Days(7), Category: "Junk"),
            new(Path.Combine(common, "Microsoft", "Windows", "WER", "Temp"), "Windows diagnostics", CleanupKind.Temp, Days(7), Category: "Junk", DeletableOnly: true),
            // Windows Update downloads, update logs, Delivery Optimization and setup logs (Review, opt-in, usually need administrator rights).
            // While Windows is installing an update (the servicing worker is running) its downloads are in use: the location is skipped with a note, never forced.
            new(Path.Combine(windows, "SoftwareDistribution", "Download"), "Windows Update downloads", CleanupKind.Temp, Days(14), Processes: ["TiWorker", "TrustedInstaller"], Category: "Update", DeletableOnly: true),
            new(Path.Combine(windows, "Logs", "WindowsUpdate"), "Windows Update logs", CleanupKind.Temp, Days(14), "*.etl", Category: "Update", DeletableOnly: true),
            new(Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
                "Delivery Optimization cache", CleanupKind.Cache, Days(7), Category: "Delivery", DeletableOnly: true),
            new(Path.Combine(windows, "SoftwareDistribution", "DeliveryOptimization"), "Delivery Optimization cache", CleanupKind.Cache, Days(7), Category: "Delivery", DeletableOnly: true),
            new(Path.Combine(common, "USOShared", "Logs"), "Update Orchestrator logs", CleanupKind.Temp, Days(14), "*.etl;*.log", Category: "Update", DeletableOnly: true),
            new(Path.Combine(windows, "Panther"), "Windows setup logs", CleanupKind.Temp, Days(30), "*.log;*.etl", Category: "Setup", DeletableOnly: true),
            // Windows servicing and DISM logs (Review, opt-in, protected). The Event Log, security logs and boot logs are never listed.
            new(Path.Combine(windows, "Logs", "CBS"), "Windows servicing logs", CleanupKind.Temp, Days(30), "*.log;*.cab", Category: "SystemLogs", DeletableOnly: true),
            new(Path.Combine(windows, "Logs", "DISM"), "Windows servicing logs", CleanupKind.Temp, Days(30), "*.log", Category: "SystemLogs", DeletableOnly: true),
            new(Path.Combine(windows, "Logs", "MoSetup"), "Windows servicing logs", CleanupKind.Temp, Days(30), "*.log;*.etl", Category: "SystemLogs", DeletableOnly: true),
            new(Path.Combine(windows, "Logs", "waasmedic"), "Windows servicing logs", CleanupKind.Temp, Days(30), "*.etl;*.log", Category: "SystemLogs", DeletableOnly: true),
            new(Path.Combine(windows, "Logs", "NetSetup"), "Windows servicing logs", CleanupKind.Temp, Days(30), "*.log;*.etl", Category: "SystemLogs", DeletableOnly: true),
            new(Path.Combine(windows, "Logs", "DPX"), "Windows servicing logs", CleanupKind.Temp, Days(30), "*.log", Category: "SystemLogs", DeletableOnly: true),
            // Driver-install history (setupapi.dev.log and friends) is only useful for troubleshooting a driver install.
            new(Path.Combine(windows, "INF"), "Windows driver-install logs", CleanupKind.Temp, Days(30), "setupapi*.log", Recursive: false, Category: "SystemLogs", DeletableOnly: true),
            new(Path.Combine(windows, "Debug"), "Windows debug logs", CleanupKind.Temp, Days(30), "*.log", Recursive: false, Category: "SystemLogs", DeletableOnly: true),
            // Microsoft Defender: only its own diagnostic support logs. Never Scans, Quarantine, History, Definition Updates or Platform.
            new(Path.Combine(common, "Microsoft", "Windows Defender", "Support"), "Defender diagnostic logs", CleanupKind.Temp, Days(7),
                "MPLog-*.log;MpCmdRun*.log;MpWppTracing-*.etl;MpDetection*.log", Recursive: false, Category: "Defender", DeletableOnly: true),
            // ----- Additional system items -----
            // Crash reports and memory dumps (never anything but dump files).
            new(Path.Combine(local, "CrashDumps"), "Crash reports", CleanupKind.Temp, Days(14), "*.dmp", Category: "Crash"),
            new(Path.Combine(windows, "Minidump"), "Crash reports", CleanupKind.Temp, Days(14), "*.dmp", Category: "Crash", DeletableOnly: true),
            new(Path.Combine(windows, "LiveKernelReports"), "Crash reports", CleanupKind.Temp, Days(14), "*.dmp", Category: "Crash", DeletableOnly: true),
            new(windows, "Crash reports", CleanupKind.Temp, Days(14), "MEMORY.DMP", Recursive: false, Category: "Crash", DeletableOnly: true),
            // Leftover installer extractions at the drive root (Review, opt-in, protected).
            new(Path.Combine(systemDrive, "NVIDIA"), "Driver installer leftovers", CleanupKind.Temp, Days(30), Category: "Installer", DeletableOnly: true),
            new(Path.Combine(systemDrive, "AMD"), "Driver installer leftovers", CleanupKind.Temp, Days(30), Category: "Installer", DeletableOnly: true),
            // Shortcuts whose application and install folder are verified missing.
            new(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Broken shortcuts", CleanupKind.Shortcut, Days(7), "*.lnk", Recursive: false, Category: "Shortcuts"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Broken shortcuts", CleanupKind.Shortcut, Days(7), "*.lnk", Category: "Shortcuts"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Broken shortcuts", CleanupKind.Shortcut, Days(7), "*.lnk", Recursive: false, Category: "Shortcuts", DeletableOnly: true),
            new(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Broken shortcuts", CleanupKind.Shortcut, Days(7), "*.lnk", Category: "Shortcuts", DeletableOnly: true),
            // ----- Computer usage traces -----
            // Only the shortcuts Windows and Office keep to list recent files. The files themselves, and jump lists, are never touched.
            new(Path.Combine(roaming, "Microsoft", "Windows", "Recent"), "Recent item shortcuts", CleanupKind.Temp, TimeSpan.Zero, "*.lnk", Recursive: false, Category: "RecentAccess"),
            new(Path.Combine(roaming, "Microsoft", "Office", "Recent"), "Office recent documents", CleanupKind.Temp, TimeSpan.Zero, "*.lnk", Recursive: false, Category: "RecentDocs"),
            // Diagnostic trace files only; never the Windows Event Log (.evtx) and never security logs.
            new(Path.Combine(common, "Microsoft", "Diagnosis", "ETLLogs"), "Windows diagnostic traces", CleanupKind.Temp, Days(1), "*.etl", Category: "EventTrace", DeletableOnly: true),
            new(Path.Combine(windows, "Logs", "SIH"), "Windows update trace logs", CleanupKind.Temp, Days(1), "*.etl", Category: "EventTrace", DeletableOnly: true),
        };
        // Adds one target per named sub-folder of baseDir that exists today. The application must be closed (processes) for its cache.
        void AddFolders(string category, string group, CleanupKind kind, int days, string baseDir, string[] subfolders, string[]? processes = null,
            string? pattern = null, bool deletableOnly = false, bool recursive = true)
        {
            if (!Directory.Exists(baseDir)) return;
            foreach (var sub in subfolders)
            {
                string folder = Path.Combine(baseDir, sub);
                if (Directory.Exists(folder)) list.Add(new(folder, group, kind, Days(days), pattern, processes, recursive, category, deletableOnly));
            }
        }
        List<string> Folders(string parent, string pattern = "*")
        {
            try { return Directory.Exists(parent) ? Directory.EnumerateDirectories(parent, pattern).ToList() : []; }
            catch (Exception ex) when (IsFileError(ex)) { return []; }
        }
        // Legacy Internet Explorer / WebView cache folders only. INetCache also holds Outlook and Word attachment copies (Content.Outlook,
        // Content.Word) and, on some PCs, whole application data folders, so it is never cleaned as a whole.
        AddFolders("InternetCache", "Temporary Internet Files", CleanupKind.Cache, 7, Path.Combine(local, "Microsoft", "Windows", "INetCache"),
            ["IE", "Content.IE5", Path.Combine("Low", "Content.IE5")]);
        // Old CHKDSK fragments at the drive root (found.000 ...).
        foreach (var folder in Folders(systemDrive, "found.*"))
            list.Add(new(folder, "Old CHKDSK files", CleanupKind.Temp, Days(30), "*.chk", Category: "Installer", DeletableOnly: true));
        // Prefetch: Windows' own hints about which programs you start. It rebuilds them (the next launch of each program can be a little slower),
        // so it is Review, never Safe. System-protected: listed only when this user may delete the file.
        string prefetch = Path.Combine(windows, "Prefetch");
        if (Directory.Exists(prefetch))
            list.Add(new(prefetch, "Prefetch files", CleanupKind.Cache, TimeSpan.Zero, "*.pf", Recursive: false, Category: "Prefetch", DeletableOnly: true));
        // Shader caches are rebuilt by the graphics driver on demand.
        AddFolders("DirectX", "DirectX cache", CleanupKind.Cache, 7, local, ["D3DSCache"]);
        AddFolders("DirectX", "DirectX cache", CleanupKind.Cache, 7, Path.Combine(local, "Microsoft"), ["DirectX Shader Cache"]);
        AddFolders("NvidiaShader", "NVIDIA shader cache", CleanupKind.Cache, 7, Path.Combine(local, "NVIDIA"), ["DXCache", "GLCache", "ComputeCache", "OptixCache"]);
        AddFolders("NvidiaShader", "NVIDIA shader cache", CleanupKind.Cache, 7, Path.Combine(local, "NVIDIA Corporation"), ["NV_Cache"]);
        AddFolders("NvidiaShader", "NVIDIA shader cache", CleanupKind.Cache, 7, Path.Combine(common, "NVIDIA Corporation"), ["NV_Cache"], deletableOnly: true);
        AddFolders("GpuShader", "AMD shader cache", CleanupKind.Cache, 7, Path.Combine(local, "AMD"), ["DxCache", "DxcCache", "GLCache", "VkCache"]);
        AddFolders("GpuShader", "Intel shader cache", CleanupKind.Cache, 7, Path.Combine(local, "Intel"), ["ShaderCache"]);
        // Windows apps (Store/MSIX) keep their own temporary state and web cache inside each package folder.
        foreach (var package in Folders(Path.Combine(local, "Packages")))
        {
            AddFolders("Temp", "Windows app temp files", CleanupKind.Temp, 3, package, ["TempState", Path.Combine("AC", "Temp")]);
            AddFolders("Cache", "Windows app cache", CleanupKind.Cache, 7, package, [Path.Combine("AC", "INetCache")]);
        }
        AddFolders("Cache", "Microsoft Store cache", CleanupKind.Cache, 7, Path.Combine(local, "Packages", "Microsoft.WindowsStore_8wekyb3d8bbwe"), ["LocalCache"], ["WinStore.App"]);
        // CapCut keeps effect, template and AI caches in User Data\Cache and downloads them again when needed; drafts and exports live elsewhere.
        AddFolders("Cache", "CapCut cache", CleanupKind.Cache, 7, Path.Combine(local, "CapCut", "User Data"), ["Cache"], ["CapCut"]);
        // Office keeps downloaded add-ins (web extensions) in Wef and fetches them again. OfficeFileCache and UnsavedFiles are never touched.
        AddFolders("Cache", "Office add-in cache", CleanupKind.Cache, 7, Path.Combine(local, "Microsoft", "Office", "16.0"), ["Wef"],
            ["WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE", "MSACCESS", "VISIO", "MSPUB"]);
        // Installers that apps downloaded for their next update (electron-updater style: installer.exe and a pending folder inside <app>-updater).
        // The app downloads the update again if it still needs it. Only these exact files and the pending folder, only when that layout is present.
        foreach (var updater in Folders(local, "*-updater").Concat(Folders(local, "*.update")))
        {
            string name = Path.GetFileName(updater), pending = Path.Combine(updater, "pending");
            bool hasPending = Directory.Exists(pending);
            if (!hasPending && !File.Exists(Path.Combine(updater, "installer.exe"))) continue;
            list.Add(new(updater, $"Update downloads ({name})", CleanupKind.Cache, Days(7), "installer.exe;current.blockmap", Recursive: false, Category: "Cache"));
            if (hasPending) list.Add(new(pending, $"Update downloads ({name} pending)", CleanupKind.Cache, Days(7), null, Category: "Cache"));
        }
        AddFolders("Cache", "Remote Desktop cache", CleanupKind.Cache, 7, Path.Combine(local, "Microsoft", "Terminal Server Client"), ["Cache"], pattern: "*.bmc;*.bin");
        // Chromium browsers. Per profile: rebuildable caches (Review), history files (Review) and the cookie database (Advanced). Passwords
        // ("Login Data"), bookmarks, saved sessions, autofill and extensions are never listed. Group names stay stable (Brave-Browser) for audit logs.
        const string chromiumHistory = "History;History-journal;Visited Links;Top Sites;Top Sites-journal;Network Action Predictor;Network Action Predictor-journal;Shortcuts;Shortcuts-journal";
        foreach (var browser in new[] { (Path.Combine("Google", "Chrome", "User Data"), "Chrome", "chrome", "BrowserChrome"),
            (Path.Combine("Microsoft", "Edge", "User Data"), "Edge", "msedge", "BrowserEdge"),
            (Path.Combine("BraveSoftware", "Brave-Browser", "User Data"), "Brave-Browser", "brave", "BrowserOther"),
            (Path.Combine("Vivaldi", "User Data"), "Vivaldi", "vivaldi", "BrowserOther") })
        {
            string userData = Path.Combine(local, browser.Item1);
            if (!Directory.Exists(userData) || !SafePath(userData, userData)) continue;
            AddFolders(browser.Item4, browser.Item2 + " cache", CleanupKind.Cache, 7, userData, ["GrShaderCache", "ShaderCache", "DawnCache", "GraphiteDawnCache"], [browser.Item3]);
            // Component and extension installer downloads that the browser fetches again when it needs them.
            AddFolders(browser.Item4, browser.Item2 + " cache", CleanupKind.Cache, 14, userData, ["component_crx_cache", "extensions_crx_cache"], [browser.Item3]);
            // Crash reports and usage-metrics files (the "log files" of a Chromium browser).
            AddFolders("BrowserLogs", browser.Item2 + " crash reports", CleanupKind.Temp, 14, Path.Combine(userData, "Crashpad"), ["reports"], [browser.Item3], "*.dmp;*.meta");
            AddFolders("BrowserLogs", browser.Item2 + " usage metrics", CleanupKind.Temp, 14, userData, ["BrowserMetrics"], [browser.Item3], "*.pma");
            foreach (var profile in Folders(userData))
            {
                string name = Path.GetFileName(profile);
                if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                AddFolders(browser.Item4, browser.Item2 + " cache", CleanupKind.Cache, 7, profile,
                    ["Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", "Media Cache"], [browser.Item3]);
                list.Add(new(profile, browser.Item2 + " history", CleanupKind.Junk, TimeSpan.Zero, chromiumHistory, [browser.Item3], false, "BrowserHistory"));
                AddFolders("BrowserCookies", browser.Item2 + " cookies", CleanupKind.Junk, 0, profile, ["Network"], [browser.Item3], "Cookies;Cookies-journal", recursive: false);
            }
        }
        foreach (var opera in new[] { "Opera Stable", "Opera GX Stable" })
        {
            string root = Path.Combine(local, "Opera Software", opera);
            AddFolders("BrowserOther", "Opera cache", CleanupKind.Cache, 7, root, ["Cache", "Code Cache", "GPUCache"], ["opera"]);
            AddFolders("BrowserOther", "Opera cache", CleanupKind.Cache, 7, Path.Combine(root, "Default"), ["Cache", "Code Cache", "GPUCache"], ["opera"]);
        }
        foreach (var profile in Folders(Path.Combine(local, "Mozilla", "Firefox", "Profiles")))
            AddFolders("BrowserFirefox", "Firefox cache", CleanupKind.Cache, 7, profile, ["cache2", "startupCache", "thumbnails", "jumpListCache", "OfflineCache"], ["firefox"]);
        foreach (var profile in Folders(Path.Combine(roaming, "Mozilla", "Firefox", "Profiles")))
            if (Directory.Exists(profile))
                list.Add(new(profile, "Firefox cookies", CleanupKind.Junk, TimeSpan.Zero, "cookies.sqlite;cookies.sqlite-wal;cookies.sqlite-shm", ["firefox"], false, "BrowserCookies"));
        // Firefox crash reports (the dump and its description) in the roaming profile, only while Firefox is closed.
        AddFolders("BrowserLogs", "Firefox crash reports", CleanupKind.Temp, 14, Path.Combine(roaming, "Mozilla", "Firefox"), ["Crash Reports"], ["firefox"], "*.dmp;*.extra;*.txt");
        // NVIDIA app: driver installers it downloaded (the installed driver is not affected).
        AddFolders("NvidiaApp", "NVIDIA installer downloads", CleanupKind.Cache, 30, Path.Combine(common, "NVIDIA Corporation"), ["Downloader"], deletableOnly: true);
        AddFolders("NvidiaApp", "NVIDIA driver logs", CleanupKind.Temp, 14, Path.Combine(common, "NVIDIA Corporation"), ["umdlogs"], pattern: "*.log;*.txt", deletableOnly: true, recursive: false);
        // Firefox keeps downloaded update packages per installation; only the package and log files, only while Firefox is closed.
        foreach (var updates in Folders(Path.Combine(local, "Mozilla", "updates")))
            AddFolders("BrowserUpdates", "Firefox update files", CleanupKind.Cache, 7, updates, ["updates"], ["firefox"], "*.mar;*.log;*.bak");
        // Chat, editor and launcher apps built on web engines: their caches rebuild on next start.
        AddFolders("Cache", "Discord cache", CleanupKind.Cache, 7, Path.Combine(roaming, "discord"), ["Cache", "Code Cache", "GPUCache"], ["discord"]);
        AddFolders("Cache", "Slack cache", CleanupKind.Cache, 7, Path.Combine(roaming, "Slack"), ["Cache", "Code Cache", "GPUCache"], ["slack"]);
        AddFolders("Cache", "Teams cache", CleanupKind.Cache, 7, Path.Combine(roaming, "Microsoft", "Teams"), ["Cache", "blob_storage", "GPUCache", "tmp", "Code Cache"], ["teams"]);
        AddFolders("Cache", "VS Code cache", CleanupKind.Cache, 7, Path.Combine(roaming, "Code"), ["Cache", "CachedData", "CachedExtensionVSIXs", "Code Cache", "GPUCache"], ["code"]);
        AddFolders("Cache", "Steam web cache", CleanupKind.Cache, 7, Path.Combine(local, "Steam"), ["htmlcache"], ["steam", "steamwebhelper"]);
        foreach (var web in Folders(Path.Combine(local, "EpicGamesLauncher", "Saved"), "webcache*"))
            list.Add(new(web, "Epic Games Launcher web cache", CleanupKind.Cache, Days(7), null, ["EpicGamesLauncher", "EpicWebHelper"], true, "Cache"));
        // Logs and caches of other common programs (all rebuilt or rewritten by the program itself).
        AddFolders("Cache", "Zoom logs", CleanupKind.Temp, 14, Path.Combine(roaming, "Zoom"), ["logs"], ["zoom"], "*.log;*.txt");
        AddFolders("Cache", "Epic Games Launcher logs", CleanupKind.Temp, 14, Path.Combine(local, "EpicGamesLauncher", "Saved"), ["Logs"], ["EpicGamesLauncher"], "*.log");
        AddFolders("Cache", "Windows Package Manager logs", CleanupKind.Temp, 14, Path.Combine(local, "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState"), ["DiagOutputDir"], pattern: "*.log");
        AddFolders("Cache", "Java download cache", CleanupKind.Cache, 30, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Sun", "Java", "Deployment"), ["cache"], ["java", "javaw"]);
        // Developer and creator caches: rebuildable, but re-downloading or re-indexing costs time, so they are Review (opt-in).
        AddFolders("DevCache", "NuGet cache", CleanupKind.Cache, 14, Path.Combine(local, "NuGet"), ["v3-cache", "plugins-cache", "Cache"]);
        // The restored packages themselves (0.9): re-downloaded on the next build, so an offline build fails until then. Its own Review row.
        AddFolders("NuGetPackages", "NuGet packages", CleanupKind.Cache, 0, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget"), ["packages"]);
        AddFolders("DevCache", "npm cache", CleanupKind.Cache, 14, local, ["npm-cache"]);
        AddFolders("DevCache", "pip cache", CleanupKind.Cache, 14, Path.Combine(local, "pip"), ["Cache"]);
        AddFolders("DevCache", "Yarn cache", CleanupKind.Cache, 14, Path.Combine(local, "Yarn"), ["Cache"]);
        AddFolders("DevCache", "Composer cache", CleanupKind.Cache, 14, Path.Combine(local, "Composer"), ["cache"]);
        AddFolders("DevCache", "Poetry cache", CleanupKind.Cache, 14, Path.Combine(local, "pypoetry"), ["Cache"]);
        AddFolders("DevCache", "Go build cache", CleanupKind.Cache, 14, local, ["go-build"]);
        foreach (var ide in Folders(Path.Combine(local, "JetBrains")))
            AddFolders("DevCache", "JetBrains caches", CleanupKind.Cache, 14, ide, ["caches"]);
        foreach (var studio in Folders(Path.Combine(local, "Google")).Where(path => Path.GetFileName(path).StartsWith("AndroidStudio", StringComparison.Ordinal)))
            AddFolders("DevCache", "Android Studio caches", CleanupKind.Cache, 14, studio, ["caches"]);
        foreach (var version in Folders(Path.Combine(local, "Microsoft", "VisualStudio")))
            AddFolders("DevCache", "Visual Studio caches", CleanupKind.Cache, 14, version, ["ComponentModelCache"], ["devenv"]);
        AddFolders("DevCache", "Adobe media cache", CleanupKind.Cache, 14, Path.Combine(roaming, "Adobe", "Common"),
            ["Media Cache Files", "Media Cache", "Peak Files"], ["Adobe Premiere Pro", "AfterFX", "Adobe Media Encoder", "Audition"]);
        // Application usage logs written by the .NET runtime (which programs ran); the programs themselves are untouched.
        // More developer and creator caches found on a real PC (all rebuilt by the tools themselves).
        AddFolders("DevCache", "uv cache", CleanupKind.Cache, 14, Path.Combine(local, "uv"), ["cache"]);
        AddFolders("DevCache", "node-gyp cache", CleanupKind.Cache, 14, Path.Combine(local, "node-gyp"), ["Cache"]);
        AddFolders("DevCache", "Adobe analyzer cache", CleanupKind.Cache, 14, Path.Combine(roaming, "Adobe", "Common"), ["Analyzer Cache Files"],
            ["Adobe Premiere Pro", "AfterFX", "Adobe Media Encoder", "Audition"]);
        AddFolders("Cache", "Squirrel installer temp", CleanupKind.Temp, 14, local, ["SquirrelTemp"]);
        list.Add(new(Path.Combine(common, "Microsoft", "EdgeUpdate", "Log"), "Edge updater logs", CleanupKind.Temp, Days(14), "*.log;*.log.*", Category: "SystemLogs", DeletableOnly: true));
        AddFolders("UsageHistory", ".NET usage logs", CleanupKind.Temp, 0, Path.Combine(local, "Microsoft"), ["CLR_v4.0\\UsageLogs", "CLR_v4.0_32\\UsageLogs"], pattern: "*.log", recursive: false);
        // Apps without a hand-written rule: Electron/WebView2/CEF caches, Firefox-family caches, per-app logs, temp and crash dumps (see AppDiscovery).
        // Locations the rules above already own are not added twice. The walk is bounded, so it cannot slow the first scan noticeably.
        list.AddRange(AppDiscovery.Discover(local, roaming, Path.GetTempPath(), list.Select(target => target.Root).ToList()));
        // Administrator mode, different account: when Hush runs as an administrator who is not the signed-in person (a standard user typed an admin's
        // password), every "user" folder above is the administrator's profile, not the person's. Those locations are left out; system locations stay.
        if (UserLocationsSkipped)
        {
            string profile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            list.RemoveAll(target => { try { return UnderRoot(Path.GetFullPath(target.Root), profile); } catch (ArgumentException) { return true; } });
        }
        return list;
    }

    private readonly record struct Raw(string? Name, long Length, long Modified, long Created, bool IsDirectory);

    private static bool Matches(string[]? patterns, ReadOnlySpan<char> name)
    {
        if (patterns == null) return true;
        foreach (string pattern in patterns)
            if (FileSystemName.MatchesSimpleExpression(pattern, name)) return true;
        return false;
    }

    private static string[]? Patterns(string? pattern) => pattern?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Normalize(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    // Where two targets overlap, the most specific one owns the files: an outer target never enters the root of an inner one, and targets
    // with the same root share one "already counted" set. Nothing is remembered for targets that overlap nothing, so a scan of millions
    // of files holds no per-path set.
    private static (HashSet<string>?[] Skip, HashSet<string>?[] Seen) PlanOverlaps(IReadOnlyList<CleanupTarget> list)
    {
        var skip = new HashSet<string>?[list.Count];
        var seen = new HashSet<string>?[list.Count];
        string?[] roots = new string?[list.Count];
        for (int i = 0; i < list.Count; i++) { try { roots[i] = Normalize(list[i].Root); } catch (ArgumentException) { } }
        var shared = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Count; i++)
        {
            if (roots[i] == null) continue;
            for (int j = 0; j < list.Count; j++)
            {
                if (i == j || roots[j] == null) continue;
                if (string.Equals(roots[i], roots[j], StringComparison.OrdinalIgnoreCase))
                { if (!shared.TryGetValue(roots[i]!, out var set)) shared[roots[i]!] = set = new(StringComparer.OrdinalIgnoreCase); seen[i] = set; }
                else if (roots[j]!.StartsWith(roots[i] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    (skip[i] ??= new(StringComparer.OrdinalIgnoreCase)).Add(roots[j]!);
            }
        }
        return (skip, seen);
    }

    // One location's results. A location runs on its own worker so that one call that never returns (a stuck network path, a security
    // product holding a file open, a dead drive) cannot freeze the scan: the coordinator watches the heartbeat and abandons the location.
    private sealed class TargetRun
    {
        internal readonly List<CleanupItem> Items = [];
        internal int Examined, Inaccessible, Protected;
        internal long Files, Bytes;
        internal bool Busy;
        internal volatile bool Abandoned;
        private long heartbeat = Environment.TickCount64;
        internal void Beat() => Volatile.Write(ref heartbeat, Environment.TickCount64);
        internal long LastBeat => Volatile.Read(ref heartbeat);
    }

    internal ScanResult Scan(IProgress<ScanProgress>? progress, CancellationToken token, ScanOptions? options = null)
    {
        options ??= new();
        using var background = options.Background ? LowResourceMode.BackgroundScope() : null;
        progress?.Report(new("Preparing scan", 0, 0, 0, 0));
        // A normal scan leaves out the Settings-only categories (browser history and cookies); they are scanned only when Settings asks for them.
        IReadOnlyList<CleanupTarget> active = options.Include == null
            ? targets.Where(target => !ReviewCategory.SettingsOnly.Contains(target.Category ?? "")).ToList()
            : targets.Where(options.Include).ToList();
        List<CleanupItem> found = [];
        int inaccessible = 0, busy = 0, examined = 0, stalled = 0, protectedFiles = 0;
        long fileCount = 0, bytes = 0;
        bool countOnly = options.CountOnly;
        bool extras = !countOnly && options.Include == null;
        int stages = Math.Max(1, active.Count + (extras && includeRegistry ? 1 : 0) + (extras && recycleBin != null ? 1 : 0) + (extras && systemTools != null ? 1 : 0));
        var (skipRoots, seenSets) = PlanOverlaps(active);
        // One enumeration pass per directory returns size, times and attributes, so no file is stat'ed twice.
        // Reparse points and system files are never returned; hidden files are (the default would skip them).
        var enumeration = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        for (int targetIndex = 0; targetIndex < active.Count; targetIndex++)
        {
            if (token.IsCancellationRequested) break;
            var target = active[targetIndex];
            double start = (double)targetIndex / stages;
            double end = (double)(targetIndex + 1) / stages;
            progress?.Report(new(target.Group, examined, found.Count, bytes, start));
            var run = new TargetRun();
            var locationClock = Stopwatch.StartNew();
            int baseExamined = examined, baseFound = found.Count;
            long baseBytes = bytes;
            var skipSet = skipRoots[targetIndex];
            var seenSet = seenSets[targetIndex];
            var worker = Task.Factory.StartNew(() =>
            {
                using var scope = options.Background ? LowResourceMode.BackgroundScope() : null;
                ScanTarget(target, start, end, skipSet, seenSet, options, enumeration, progress, token, run, baseExamined, baseFound, baseBytes);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            bool skipped = false;
            try
            {
                while (!worker.Wait(150))
                {
                    if (token.IsCancellationRequested)
                    {
                        // Stop answers at once: give the worker a moment to hand back what it has, then leave it behind.
                        if (!worker.Wait(1500)) { run.Abandoned = true; skipped = true; }
                        break;
                    }
                    if (options.StallMilliseconds > 0 && Environment.TickCount64 - run.LastBeat > options.StallMilliseconds)
                    {
                        run.Abandoned = true; skipped = true; stalled++;
                        LogScanIssue($"Skipped a location that stopped responding for {options.StallMilliseconds / 1000.0:0.#} s: {target.Group} ({target.Root})");
                        break;
                    }
                }
            }
            catch (AggregateException failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerException ?? failure).Throw(); }
            if (!skipped)
            {
                found.AddRange(run.Items); examined += run.Examined; inaccessible += run.Inaccessible; protectedFiles += run.Protected; fileCount += run.Files; bytes += run.Bytes;
                if (run.Busy) busy++;
            }
            if (!token.IsCancellationRequested) progress?.Report(new(target.Group, examined, found.Count, bytes, end));
            // A location that finished but took long is noted, so "why did it sit at one percentage?" can be answered from the log.
            if (!skipped && locationClock.ElapsedMilliseconds >= options.SlowNoteMilliseconds)
                LogScanIssue($"Slow location: {target.Group} ({target.Root}) took {locationClock.Elapsed.TotalSeconds:0.#} s and looked at {run.Examined:N0} entries ({run.Files:N0} listed)");
        }
        int nextStage = active.Count;
        // The three extra steps ask Windows itself (registry, shell, Disk Cleanup). Each is guarded so a stuck answer cannot hold the scan forever.
        if (extras && includeRegistry && !token.IsCancellationRequested)
        {
            progress?.Report(new("App registry leftovers", examined, found.Count, bytes, (double)nextStage++ / stages));
            if (!Guarded("App registry leftovers", options.StepTimeoutMilliseconds, options.Background, token, list => ScanRegistry(list, token), found)) stalled++;
        }
        if (extras && recycleBin != null && !token.IsCancellationRequested)
        {
            progress?.Report(new("Recycle Bin", examined, found.Count, bytes, (double)nextStage++ / stages));
            if (!Guarded("Recycle Bin", options.StepTimeoutMilliseconds, options.Background, token, ScanRecycleBin, found)) stalled++;
        }
        if (extras && systemTools != null && !token.IsCancellationRequested)
        {
            progress?.Report(new("Windows cleanup tools", examined, found.Count, bytes, (double)nextStage++ / stages));
            if (!Guarded("Windows cleanup tools", options.StepTimeoutMilliseconds * 3, options.Background, token, list => ScanSystemTools(list, token), found)) stalled++;
        }
        if (!token.IsCancellationRequested) progress?.Report(new("Complete", examined, found.Count, bytes, 1,
            RegistryEntries: found.Count(item => item.Kind is CleanupKind.Registry or CleanupKind.RecycleBin or CleanupKind.SystemTool)));
        return new(found.OrderByDescending(x => x.Bytes).ToList(), inaccessible, busy, token.IsCancellationRequested, fileCount, bytes, stalled, protectedFiles);
    }

    // Runs one extra scan step on its own thread with a time limit. Whatever it found is added only if it finished in time.
    private bool Guarded(string name, int timeoutMilliseconds, bool background, CancellationToken token, Action<List<CleanupItem>> step, List<CleanupItem> found)
    {
        var result = new List<CleanupItem>();
        var task = Task.Factory.StartNew(() =>
        {
            using var scope = background ? LowResourceMode.BackgroundScope() : null; // low-resource scans stay at background priority here too
            step(result);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var clock = Stopwatch.StartNew();
        try
        {
            while (!task.Wait(150))
            {
                if (token.IsCancellationRequested) return true;
                if (timeoutMilliseconds > 0 && clock.ElapsedMilliseconds > timeoutMilliseconds)
                { LogScanIssue($"Skipped a Windows step that did not answer in time: {name}"); return false; }
            }
        }
        catch (AggregateException failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerException ?? failure).Throw(); }
        found.AddRange(result);
        return true;
    }

    // A short local note, so "why did Hush skip that?" has an answer. Written under this engine's data directory. Never throws.
    private void LogScanIssue(string text)
    {
        try
        {
            string directory = Path.Combine(dataDirectory, "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "scan-issues.log"), $"{DateTime.UtcNow:O}  {text}{Environment.NewLine}");
        }
        catch { }
    }

    // The scan of one location. Runs on a worker; every counter it touches belongs to `run`, which the coordinator merges only if the
    // location finished (or was stopped cleanly). It calls run.Beat() with every entry so a location that is merely big is never mistaken for a stuck one.
    private void ScanTarget(CleanupTarget target, double start, double end, HashSet<string>? skipSet, HashSet<string>? seenSet, ScanOptions options,
        EnumerationOptions enumeration, IProgress<ScanProgress>? progress, CancellationToken token, TargetRun run, int baseExamined, int baseFound, long baseBytes)
    {
        options.OnTargetStart?.Invoke(target);
        if (run.Abandoned) return;
        if (!Directory.Exists(target.Root) || !SafePath(target.Root, target.Root)) return;
        if (BrowserRunning(target, fresh: true) || AppOpen(target)) { run.Busy = true; return; }
        bool countOnly = options.CountOnly;
        string fullRoot = Path.GetFullPath(target.Root);
        string[]? patterns = Patterns(target.Pattern);
        long cutoff = (DateTime.UtcNow - target.MinimumAge).Ticks;
        string[]? excludedNames = target.ExcludeFolders;
        bool selected = SelectedByDefault(target);
        // Only locations that need the full path of every file (protected probe, shortcut check, duplicate guard) pay for building it.
        bool needPath = target.DeletableOnly || target.Kind == CleanupKind.Shortcut || seenSet != null;
        bool wantName = !countOnly || needPath;
        int seenInTarget = 0;
        bool firstReported = false;
        var reportClock = Stopwatch.StartNew();
        // Discovery has no known total, so the target's share of the gauge fills on a saturating curve as files are found.
        double Fraction() => start + (end - start) * seenInTarget / (seenInTarget + 4000.0);
        void Report(string label)
        {
            if (run.Abandoned) return; // a location that was given up on must not push stale counts or names into the UI
            progress?.Report(new(label, baseExamined + run.Examined, baseFound + run.Items.Count, baseBytes + run.Bytes, Fraction()));
            reportClock.Restart();
        }
        Stack<string> pending = new();
        pending.Push(target.Root);
        while (pending.Count > 0 && !token.IsCancellationRequested && !run.Abandoned)
        {
            string directory = pending.Pop();
            run.Beat();
            if (!directory.StartsWith(Path.TrimEndingDirectorySeparator(target.Root), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var entries = new FileSystemEnumerable<Raw>(directory,
                    (ref FileSystemEntry entry) => entry.IsDirectory
                        ? new Raw(entry.FileName.ToString(), 0, 0, 0, true)
                        : new Raw(wantName ? entry.FileName.ToString() : null, entry.Length, entry.LastWriteTimeUtc.UtcTicks, entry.CreationTimeUtc.UtcTicks, false), enumeration)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    {
                        run.Beat(); // also counts entries the pattern filters out, so a huge folder of other files still shows life
                        if (entry.IsDirectory)
                        {
                            if (!target.Recursive) return false;
                            if (excludedNames != null)
                                foreach (string excluded in excludedNames)
                                    if (entry.FileName.Equals(excluded, StringComparison.OrdinalIgnoreCase)) return false;
                            return true;
                        }
                        return Matches(patterns, entry.FileName) && entry.LastWriteTimeUtc.UtcTicks < cutoff && entry.CreationTimeUtc.UtcTicks < cutoff;
                    }
                };
                foreach (var raw in entries)
                {
                    if (token.IsCancellationRequested || run.Abandoned) break;
                    run.Examined++; seenInTarget++;
                    if (raw.IsDirectory)
                    {
                        string sub = Path.Combine(directory, raw.Name!);
                        if (skipSet == null || !skipSet.Contains(sub)) pending.Push(sub);
                        continue;
                    }
                    try
                    {
                        string? full = null;
                        if (needPath)
                        {
                            full = Path.Combine(directory, raw.Name!);
                            if (seenSet != null)
                            {
                                if (run.Abandoned) break; // an abandoned worker must never touch a set that later locations share
                                if (!seenSet.Add(full)) continue;
                            }
                            if (target.DeletableOnly)
                            {
                                run.Beat();
                                bool deletable = CanDelete(full);
                                run.Beat();
                                if (!deletable) { run.Protected++; continue; }
                            }
                        }
                        string? shortcutTarget = null, shortcutHash = null;
                        if (target.Kind == CleanupKind.Shortcut)
                        {
                            shortcutTarget = ShortcutProbe.MissingApplication(full!);
                            if (shortcutTarget == null) continue;
                            using var shortcut = new FileStream(full!, FileMode.Open, FileAccess.Read, FileShare.Read);
                            shortcutHash = Convert.ToHexString(SHA256.HashData(shortcut));
                        }
                        run.Files++; run.Bytes += raw.Length;
                        if (!countOnly)
                            run.Items.Add(new CleanupItem { Folder = directory, FileName = raw.Name, Root = fullRoot, Group = target.Group,
                                Kind = target.Kind, CategoryId = target.Category, Bytes = raw.Length, ModifiedTicks = raw.Modified,
                                CreatedTicks = raw.Created, ShortcutTarget = shortcutTarget, ShortcutHash = shortcutHash, Selected = selected });
                    }
                    catch (Exception ex) when (IsFileError(ex)) { run.Inaccessible++; }
                    // Report the first file of a location right away, then from inside a huge flat folder too, not only between folders.
                    if (!firstReported) { firstReported = true; Report("Discovering " + target.Group.ToLowerInvariant()); }
                    else if ((seenInTarget & 127) == 0 && reportClock.ElapsedMilliseconds >= options.ReportIntervalMs)
                        Report("Discovering " + target.Group.ToLowerInvariant());
                }
            }
            catch (Exception ex) when (IsFileError(ex)) { if (!target.DeletableOnly) run.Inaccessible++; }
            if (reportClock.ElapsedMilliseconds >= options.ReportIntervalMs) Report("Discovering " + target.Group.ToLowerInvariant());
        }
    }

    // Only the Safe category is selected by default; Review, Advanced and the Recycle Bin are always opt-in.
    private static bool SelectedByDefault(CleanupTarget target) =>
        target.Kind is not (CleanupKind.Registry or CleanupKind.RecycleBin or CleanupKind.SystemTool)
        && ReviewCategory.SafetyOf(ReviewCategory.CategoryFor(target.Kind, target.Group, target.Category)) == SafetyLevel.Safe;

    // Read-only supported query (SHQueryRecycleBin); nothing is changed by scanning.
    private void ScanRecycleBin(List<CleanupItem> found)
    {
        try
        {
            var info = recycleBin!.Query();
            if (info.Items <= 0) return;
            found.Add(new CleanupItem { Path = "Recycle Bin", Root = "Recycle Bin", Group = "Recycle Bin", Kind = CleanupKind.RecycleBin,
                CategoryId = "RecycleBin", Bytes = info.Bytes, ItemCount = info.Items,
                Detail = info.Items == 1 ? "1 deleted item on all drives" : $"{info.Items:N0} deleted items on all drives", Selected = false });
        }
        catch (Exception ex) when (IsFileError(ex) || ex is COMException) { }
    }

    // Read-only probe of the Windows tools that apply to this PC (Disk Cleanup, DISM, clipboard history). Always opt-in.
    private void ScanSystemTools(List<CleanupItem> found, CancellationToken token)
    {
        try
        {
            foreach (var offer in systemTools!.Probe(token))
                found.Add(new CleanupItem { Path = offer.Title, Root = "Windows tool", Group = offer.Title, Kind = CleanupKind.SystemTool, CategoryId = offer.CategoryId,
                    Bytes = offer.Bytes, ItemCount = offer.ItemCount, ToolId = offer.ToolId, Detail = offer.Detail, Selected = offer.SelectedByDefault });
        }
        catch (Exception ex) when (IsFileError(ex) || ex is COMException or InvalidOperationException) { }
    }

    // True when this user could open the file exclusively for deletion right now (the same open the delete uses).
    private static bool CanDelete(string path)
    {
        using SafeFileHandle handle = Native.CreateFile(path, 0x80010080, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        return !handle.IsInvalid;
    }

    // Free bytes on every fixed drive, as Windows reports them. Measured before and after a clean to show what the disk really gained.
    internal static long FreeSpace()
    {
        long total = 0;
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
                if (drive.DriveType == DriveType.Fixed && drive.IsReady) total += drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (IsFileError(ex)) { }
        return total;
    }

    private sealed class AuditLog(StreamWriter writer)
    {
        // A line holds the time, the path and a status. Intent lines are written, then flushed, before a batch is touched.
        public void Write(string path, string status, long bytes = -1) =>
            writer.WriteLine($"{{\"utc\":\"{DateTime.UtcNow:O}\",\"path\":{JsonSerializer.Serialize(path)},\"status\":{JsonSerializer.Serialize(status)}{(bytes >= 0 ? $",\"bytes\":{bytes}" : "")}}}");
        public void Flush() => writer.Flush();
    }

    // note: a plain sentence for the "still working" state while one of Windows' own tools runs (they can take many minutes).
    internal CleanupResult Clean(IReadOnlyList<CleanupItem> items, IProgress<int>? progress, CancellationToken token, IProgress<string>? note = null)
    {
        // Open the audit log first: if it cannot be created, nothing is deleted.
        string logs = Path.Combine(dataDirectory, "Logs");
        Directory.CreateDirectory(logs);
        if (!SafePath(logs, dataDirectory)) throw new IOException("The Hush log directory must not be a link.");
        string logPath = Path.Combine(logs, $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        using var stream = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16);
        var audit = new AuditLog(writer);
        long freeBefore = FreeSpace();
        int deleted = 0, skipped = 0;
        long bytes = 0;
        var reasons = new Dictionary<string, int>();
        char[] pathBuffer = new char[32768]; // one reusable buffer for final-path checks, not one per file
        var context = new CleanContext();
        var reportClock = Stopwatch.StartNew();
        const int batchSize = 64;
        string[] batchPaths = new string[batchSize];
        for (int batchStart = 0; batchStart < items.Count && !token.IsCancellationRequested; batchStart += batchSize)
        {
            int batchEnd = Math.Min(items.Count, batchStart + batchSize);
            // Persist intent for the whole batch before any of it is performed (one flush per batch, not per file).
            for (int index = batchStart; index < batchEnd; index++) audit.Write(batchPaths[index - batchStart] = items[index].Path, "requested");
            audit.Flush();
            for (int index = batchStart; index < batchEnd; index++)
            {
                var item = items[index];
                string path = batchPaths[index - batchStart];
                if (token.IsCancellationRequested) { audit.Write(path, "Not processed (stopped)"); continue; }
                string outcome;
                try
                {
                    if (item.Kind == CleanupKind.Registry) DeleteRegistry(item);
                    else if (item.Kind == CleanupKind.RecycleBin) EmptyRecycleBin(item);
                    else if (item.Kind == CleanupKind.SystemTool) RunSystemTool(item, token, note);
                    else DeleteFile(item, path, pathBuffer, context);
                    deleted++;
                    bytes += item.Bytes;
                    outcome = "Deleted";
                }
                catch (Exception ex) when (IsFileError(ex) || ex is InvalidOperationException or System.Security.SecurityException or COMException or OperationCanceledException)
                {
                    skipped++;
                    outcome = "Skipped: " + ex.Message;
                    reasons[ex.Message] = reasons.GetValueOrDefault(ex.Message) + 1;
                }
                item.Outcome = outcome;
                audit.Write(path, outcome, item.Bytes);
                // Progress reaches the UI thread a few times a second, not once per file.
                if (deleted + skipped == 1 || reportClock.ElapsedMilliseconds >= 50) { progress?.Report(deleted + skipped); reportClock.Restart(); }
            }
            audit.Flush();
        }
        progress?.Report(deleted + skipped);
        audit.Flush();
        return new(deleted, skipped, bytes, token.IsCancellationRequested, logPath, freeBefore, FreeSpace(), reasons);
    }

    // Per-clean caches: directory chains already proven free of links, and the approved target of each (root, kind, group).
    private sealed class CleanContext
    {
        public readonly Dictionary<string, long> VerifiedFolders = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<(string Root, CleanupKind Kind, string Group), Approved?> Targets = new();
    }

    // Emptying is all-or-nothing and permanent, so it only proceeds if the bin still matches exactly what the user reviewed.
    private void EmptyRecycleBin(CleanupItem item)
    {
        if (recycleBin == null) throw new IOException("Recycle Bin cleaning is not available.");
        var current = recycleBin.Query();
        if (current.Items != item.ItemCount || current.Bytes != item.Bytes) throw new IOException("The Recycle Bin changed after the scan; scan again.");
        recycleBin.Empty();
    }

    // Windows' own tools do the work. The tool must still be offered by a fresh read-only probe, so a forged item cannot start anything.
    private void RunSystemTool(CleanupItem item, CancellationToken token, IProgress<string>? note = null)
    {
        if (systemTools == null || item.ToolId == null) throw new IOException("This Windows cleanup is not available.");
        if (!systemTools.Probe(token).Any(offer => offer.ToolId == item.ToolId)) throw new IOException("Windows no longer offers this cleanup; scan again.");
        note?.Report($"Windows is running \"{item.Group}\". This can take several minutes; Hush is still working…");
        systemTools.Run(item.ToolId, token);
    }

    private void DeleteFile(CleanupItem item, string itemPath, char[] pathBuffer, CleanContext context)
    {
        var key = (item.Root, item.Kind, item.Group);
        if (!context.Targets.TryGetValue(key, out var entry))
            context.Targets[key] = entry = approved.GetValueOrDefault(ApprovalKey(item.Root, item.Kind, item.Group));
        if (entry == null) throw new IOException("Outside approved cleanup locations.");
        var target = entry.Target;
        string full = Path.GetFullPath(itemPath);
        string root = Normalize(target.Root);
        if (!UnderRoot(full, root) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) throw new IOException("The path is unsafe or has changed.");
        if (!FolderChainSafe(Path.GetDirectoryName(full)!, context)) throw new IOException("The path is unsafe or has changed.");
        if (entry.Patterns != null && !Matches(entry.Patterns, Path.GetFileName(full)))
            throw new IOException("File is not an approved cleanup type.");
        if (BrowserRunning(target) || AppOpen(target)) throw new IOException("The browser is running.");

        // Hold an exclusive handle through metadata validation and deletion. Never follow a final symlink.
        using SafeFileHandle handle = Native.CreateFile(itemPath, 0x80010080, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException(WindowsSystemTools.IsElevated() ? "In use by a program right now" : "In use, or needs administrator rights");
        if (!Native.GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot verify file identity.");
        if ((info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.ReadOnly)) != 0
            || info.NumberOfLinks != 1) throw new IOException("Protected, linked, or read-only file.");
        uint written = Native.GetFinalPathNameByHandle(handle, pathBuffer, (uint)pathBuffer.Length, 0);
        if (written == 0 || written >= pathBuffer.Length) throw new IOException("Cannot verify final path.");
        string resolved = new(pathBuffer, 0, (int)written);
        if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        // The handle's real path must equal the checked path: a link anywhere in the chain would make them differ.
        if (!string.Equals(full, resolved, StringComparison.OrdinalIgnoreCase)) throw new IOException("File location changed after scan.");
        long length = ((long)info.SizeHigh << 32) | info.SizeLow;
        DateTime modified = DateTime.FromFileTimeUtc(((long)info.WriteHigh << 32) | info.WriteLow);
        DateTime created = DateTime.FromFileTimeUtc(((long)info.CreationHigh << 32) | info.CreationLow);
        if (length != item.Bytes || modified != item.ModifiedUtc || created != item.CreatedUtc || !OldEnough(modified, created, target.MinimumAge))
            throw new IOException("Changed since the scan");
        if (item.Kind == CleanupKind.Shortcut)
        {
            if (item.ShortcutTarget == null || item.ShortcutHash == null || !IsOrphanExecutable(item.ShortcutTarget))
                throw new IOException("Shortcut target is no longer verified missing.");
            using var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), false);
            using var content = new FileStream(borrowed, FileAccess.Read);
            if (!Convert.ToHexString(SHA256.HashData(content)).Equals(item.ShortcutHash, StringComparison.Ordinal))
                throw new IOException("Shortcut changed after scanning.");
        }
        Native.Disposition disposition = new() { Delete = true };
        if (!Native.SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<Native.Disposition>()))
            throw new IOException("Windows could not delete this file.");
    }

    // Folder chains are verified once per folder per clean (for ten seconds), not once per file; the per-file final-path check above
    // is what catches a link swapped in afterwards.
    private static bool FolderChainSafe(string folder, CleanContext context)
    {
        long now = Environment.TickCount64;
        if (context.VerifiedFolders.TryGetValue(folder, out long stamp) && now - stamp < 10000) return true;
        try
        {
            string? cursor = folder;
            while (cursor != null)
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) return false;
                cursor = Path.GetDirectoryName(cursor);
            }
        }
        catch (Exception ex) when (IsFileError(ex)) { return false; }
        context.VerifiedFolders[folder] = now;
        return true;
    }

    // Shape check: a local, normal path strictly inside the root (no UNC, no alternate data stream, no short root).
    private static bool UnderRoot(string path, string root)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0 || root.Length < 4) return false;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SafePath(string path, string root)
    {
        try
        {
            path = Path.GetFullPath(path);
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!UnderRoot(path, root)) return false;
            string? cursor = path;
            while (cursor != null)
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) return false;
                cursor = Path.GetDirectoryName(cursor);
            }
            return true;
        }
        catch (Exception ex) when (IsFileError(ex)) { return false; }
    }

    private static bool OldEnough(DateTime modified, DateTime created, TimeSpan age) =>
        modified < DateTime.UtcNow - age && created < DateTime.UtcNow - age;

    // One process snapshot answers every "is the application running" question until it ages out: a scan asks about
    // dozens of cache folders and a clean about every file, so enumerating processes each time would dominate the run.
    // Scans accept half a second of age, cleans two seconds; a browser that starts mid-clean is still protected by the exclusive file handle.
    // An app that keeps a lock file while it runs (Chromium's "lockfile") is open while that file exists. Answers are cached for two seconds per target
    // so a clean never stats the disk once per file.
    private readonly Dictionary<string, (long Tick, bool Open)> openApps = new(StringComparer.OrdinalIgnoreCase);
    internal static int AppOpenCacheMilliseconds = 2000; // tests set 0 so they need not wait
    private bool AppOpen(CleanupTarget target)
    {
        if (target.LockFiles == null) return false;
        lock (openApps)
        {
            long now = Environment.TickCount64;
            if (openApps.TryGetValue(target.Root, out var known) && now - known.Tick < AppOpenCacheMilliseconds) return known.Open;
            bool open = false;
            foreach (string file in target.LockFiles)
            {
                try { if (File.Exists(file)) { open = true; break; } }
                catch (Exception ex) when (IsFileError(ex)) { open = true; break; } // cannot tell: treat the app as open
            }
            openApps[target.Root] = (now, open);
            return open;
        }
    }

    private bool BrowserRunning(CleanupTarget target, bool fresh = false)
    {
        foreach (string name in target.Processes ?? [])
            if (processes.Running(name, fresh ? 500 : 2000)) return true;
        return false;
    }

    private sealed class ProcessGate
    {
        private HashSet<string>? names;
        private long tick = long.MinValue;
        private readonly object gate = new();
        internal bool Running(string name, int maxAgeMilliseconds)
        {
            lock (gate)
            {
                long now = Environment.TickCount64;
                if (now - tick > maxAgeMilliseconds || tick == long.MinValue) { names = Snapshot(); tick = now; }
                return names == null || names.Contains(name); // If processes cannot be listed, treat everything as running.
            }
        }
        private static HashSet<string>? Snapshot()
        {
            try
            {
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var process in Process.GetProcesses())
                {
                    try { result.Add(process.ProcessName); } catch { } finally { process.Dispose(); }
                }
                return result;
            }
            catch { return null; }
        }
    }

    private static void ScanRegistry(List<CleanupItem> found, CancellationToken token)
    {
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(AppPaths);
            if (root == null) return;
            foreach (string name in root.GetSubKeyNames())
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    using var key = root.OpenSubKey(name);
                    if (key == null || key.SubKeyCount != 0 || name.IndexOfAny(['\r', '\n', '[', ']']) >= 0 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (key.GetValue("") is not string executable || !IsOrphanExecutable(executable)) continue;
                    string snapshot = ExportKey(key, AppPaths + "\\" + name);
                    found.Add(new CleanupItem { Path = @"HKCU\" + AppPaths + "\\" + name, Root = AppPaths,
                        Group = "App leftovers", Kind = CleanupKind.Registry, RegistryExecutable = executable,
                        RegistrySnapshot = snapshot, Selected = false });
                }
                catch (Exception ex) when (IsFileError(ex) || ex is System.Security.SecurityException or InvalidOperationException) { }
            }
        }
        catch (Exception ex) when (IsFileError(ex) || ex is System.Security.SecurityException) { }
    }

    internal static bool IsOrphanExecutable(string value)
    {
        try
        {
            string executable = Environment.ExpandEnvironmentVariables(value).Trim().Trim('"');
            if (!Path.IsPathFullyQualified(executable) || executable.StartsWith(@"\\") || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            executable = Path.GetFullPath(executable);
            string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")];
            if (!roots.Any(root => root.Length > 3 && executable.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return false;
            var drive = new DriveInfo(Path.GetPathRoot(executable)!);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return false;
            // Require BOTH the executable and its installation folder to be absent.
            string parent = Path.GetDirectoryName(executable)!;
            if (!DefinitelyMissing(executable) || !DefinitelyMissing(parent)) return false;
            string? ancestor = Path.GetDirectoryName(parent);
            while (ancestor != null && !Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor);
            return ancestor != null && SafePath(ancestor, roots.First(root => executable.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

    private static bool DefinitelyMissing(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }

    private void DeleteRegistry(CleanupItem item)
    {
        string prefix = @"HKCU\" + AppPaths + "\\";
        if (!includeRegistry || item.Root != AppPaths || !item.Path.StartsWith(prefix, StringComparison.Ordinal)
            || item.RegistrySnapshot == null) throw new IOException("Not an approved registry entry.");
        string name = item.Path[prefix.Length..];
        if (name.IndexOfAny(['\\', '\r', '\n', '[', ']']) >= 0 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid app path key.");
        using var root = Registry.CurrentUser.OpenSubKey(AppPaths, true) ?? throw new IOException("Registry key no longer exists.");
        using (var key = root.OpenSubKey(name))
        {
            if (key == null || key.SubKeyCount != 0 || key.GetValue("") is not string executable
                || executable != item.RegistryExecutable || !IsOrphanExecutable(executable)
                || ExportKey(key, AppPaths + "\\" + name) != item.RegistrySnapshot) throw new IOException("Registry entry changed or is no longer orphaned.");
        }
        string backups = Path.Combine(dataDirectory, "Registry backups");
        Directory.CreateDirectory(backups);
        if (!SafePath(backups, dataDirectory)) throw new IOException("The backup directory must not be a link.");
        string backup = Path.Combine(backups, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.reg");
        using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Encoding.Unicode))
        { writer.Write(item.RegistrySnapshot); writer.Flush(); stream.Flush(true); }
        using (var key = root.OpenSubKey(name))
        {
            if (key == null || key.SubKeyCount != 0 || ExportKey(key, AppPaths + "\\" + name) != item.RegistrySnapshot
                || !IsOrphanExecutable(item.RegistryExecutable!)) throw new IOException("Registry entry changed while backing up; left untouched.");
        }
        root.DeleteSubKey(name, true);
    }

    private static string ExportKey(RegistryKey key, string path)
    {
        StringBuilder result = new("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\" + path + "]\r\n");
        foreach (string name in key.GetValueNames().Order(StringComparer.Ordinal))
        {
            string label = name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";
            var kind = key.GetValueKind(name);
            object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string encoded = kind switch
            {
                RegistryValueKind.String => "\"" + Escape((string)value!) + "\"",
                RegistryValueKind.ExpandString => "hex(2):" + Hex(Encoding.Unicode.GetBytes((string)value! + "\0")),
                RegistryValueKind.MultiString => "hex(7):" + Hex(Encoding.Unicode.GetBytes(string.Join('\0', (string[])value!) + "\0\0")),
                RegistryValueKind.DWord => "dword:" + unchecked((uint)(int)value!).ToString("x8"),
                RegistryValueKind.QWord => "hex(b):" + Hex(BitConverter.GetBytes((long)value!)),
                RegistryValueKind.Binary => "hex:" + Hex((byte[])value!),
                _ => throw new IOException("Unsupported registry value; left untouched.")
            };
            result.Append(label).Append('=').Append(encoded).Append("\r\n");
        }
        return result.ToString();
    }

    private static string Escape(string value)
    {
        if (value.Contains('\r') || value.Contains('\n') || value.Contains('\0')) throw new IOException("Unusual registry value; left untouched.");
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
    private static string Hex(byte[] bytes) => string.Join(',', bytes.Select(b => b.ToString("x2")));
    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, ref Disposition info, uint size);
}
