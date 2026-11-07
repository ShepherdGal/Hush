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
    private const int CandidateLimit = 150000;
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hush");
    // Targets (and the delete-time lookup of the approved target for an item) are built on first use: the default list
    // looks at the disk, which must not slow window startup, and the lookup is never a per-file scan of the list.
    private readonly Lazy<IReadOnlyList<CleanupTarget>> targetSource;
    private readonly Lazy<Dictionary<string, CleanupTarget>> approvedSource;
    private IReadOnlyList<CleanupTarget> targets => targetSource.Value;
    private Dictionary<string, CleanupTarget> approved => approvedSource.Value;
    private readonly bool includeRegistry;
    private readonly IRecycleBin? recycleBin;
    private readonly string dataDirectory;
    private readonly ProcessGate processes = new();

    public CleanupEngine() : this(new Lazy<IReadOnlyList<CleanupTarget>>(DefaultTargets), true, null, new ShellRecycleBin()) { }
    internal CleanupEngine(IReadOnlyList<CleanupTarget> targets, bool includeRegistry = false, string? dataDirectory = null, IRecycleBin? recycleBin = null)
        : this(new Lazy<IReadOnlyList<CleanupTarget>>(targets), includeRegistry, dataDirectory, recycleBin) { }
    private CleanupEngine(Lazy<IReadOnlyList<CleanupTarget>> targetSource, bool includeRegistry, string? dataDirectory, IRecycleBin? recycleBin)
    {
        this.targetSource = targetSource;
        this.includeRegistry = includeRegistry;
        this.recycleBin = recycleBin;
        this.dataDirectory = dataDirectory ?? DataDirectory;
        approvedSource = new(() =>
        {
            var lookup = new Dictionary<string, CleanupTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targetSource.Value)
            {
                try { lookup.TryAdd(ApprovalKey(Path.GetFullPath(target.Root), target.Kind, target.Group), target); }
                catch (ArgumentException) { } // An unresolvable location can never be scanned or cleaned.
            }
            return lookup;
        });
    }

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
            new(Path.GetTempPath(), "User temp", CleanupKind.Temp, Days(2), Category: "Temp"),
            new(Path.Combine(windows, "Temp"), "Windows temp", CleanupKind.Temp, Days(7), Category: "Temp"),
            // Explorer keeps these open, so only unlocked files are listed.
            new(Path.Combine(local, "Microsoft", "Windows", "Explorer"), "Thumbnail cache", CleanupKind.Cache, Days(1),
                "thumbcache_*.db;iconcache_*.db", Recursive: false, Category: "Thumbnails", DeletableOnly: true),
            // Windows Error Reporting archives/queues (per user, then machine-wide).
            new(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"), "Windows diagnostics", CleanupKind.Junk, Days(30), "*.wer;" + dumps, Category: "Junk"),
            new(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"), "Windows diagnostics", CleanupKind.Junk, Days(30), "*.wer;" + dumps, Category: "Junk"),
            new(Path.Combine(common, "Microsoft", "Windows", "WER", "ReportArchive"), "Windows diagnostics", CleanupKind.Junk, Days(30), "*.wer;" + dumps, Category: "Junk", DeletableOnly: true),
            new(Path.Combine(common, "Microsoft", "Windows", "WER", "ReportQueue"), "Windows diagnostics", CleanupKind.Junk, Days(30), "*.wer;" + dumps, Category: "Junk", DeletableOnly: true),
            // Crash reports and memory dumps (never anything but dump files).
            new(Path.Combine(local, "CrashDumps"), "Crash reports", CleanupKind.Temp, Days(14), "*.dmp", Category: "Crash"),
            new(Path.Combine(windows, "Minidump"), "Crash reports", CleanupKind.Temp, Days(14), "*.dmp", Category: "Crash", DeletableOnly: true),
            new(Path.Combine(windows, "LiveKernelReports"), "Crash reports", CleanupKind.Temp, Days(14), "*.dmp", Category: "Crash", DeletableOnly: true),
            new(windows, "Crash reports", CleanupKind.Temp, Days(14), "MEMORY.DMP", Recursive: false, Category: "Crash", DeletableOnly: true),
            // Windows Update downloads, update logs, Delivery Optimization and setup logs (Review, opt-in, usually need administrator rights).
            new(Path.Combine(windows, "SoftwareDistribution", "Download"), "Windows Update downloads", CleanupKind.Temp, Days(14), Category: "Update", DeletableOnly: true),
            new(Path.Combine(windows, "Logs", "WindowsUpdate"), "Windows Update logs", CleanupKind.Temp, Days(14), "*.etl", Category: "Update", DeletableOnly: true),
            new(Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
                "Delivery Optimization cache", CleanupKind.Cache, Days(7), Category: "Delivery", DeletableOnly: true),
            new(Path.Combine(windows, "SoftwareDistribution", "DeliveryOptimization"), "Delivery Optimization cache", CleanupKind.Cache, Days(7), Category: "Delivery", DeletableOnly: true),
            new(Path.Combine(windows, "Panther"), "Windows setup logs", CleanupKind.Temp, Days(30), "*.log;*.etl", Category: "Setup", DeletableOnly: true),
            // Windows servicing, DISM and diagnostic-trace logs (Review, opt-in, protected).
            new(Path.Combine(windows, "Logs", "CBS"), "Windows servicing logs", CleanupKind.Temp, Days(30), "*.log;*.cab", Category: "SystemLogs", DeletableOnly: true),
            new(Path.Combine(windows, "Logs", "DISM"), "Windows servicing logs", CleanupKind.Temp, Days(30), "*.log", Category: "SystemLogs", DeletableOnly: true),
            new(Path.Combine(windows, "Logs", "MoSetup"), "Windows servicing logs", CleanupKind.Temp, Days(30), "*.log;*.etl", Category: "SystemLogs", DeletableOnly: true),
            new(Path.Combine(windows, "Debug"), "Windows debug logs", CleanupKind.Temp, Days(30), "*.log", Recursive: false, Category: "SystemLogs", DeletableOnly: true),
            new(Path.Combine(common, "Microsoft", "Diagnosis", "ETLLogs"), "Windows diagnostic traces", CleanupKind.Temp, Days(30), "*.etl", Category: "SystemLogs", DeletableOnly: true),
            // Leftover graphics-driver installer extractions at the drive root (Review, opt-in, protected).
            new(Path.Combine(systemDrive, "NVIDIA"), "Driver installer leftovers", CleanupKind.Temp, Days(30), Category: "Installer", DeletableOnly: true),
            new(Path.Combine(systemDrive, "AMD"), "Driver installer leftovers", CleanupKind.Temp, Days(30), Category: "Installer", DeletableOnly: true),
            // Usage traces: only the shortcuts Windows and Office keep to list recent files. The files themselves are never touched.
            new(Path.Combine(roaming, "Microsoft", "Windows", "Recent"), "Recent item shortcuts", CleanupKind.Temp, Days(7), "*.lnk", Recursive: false, Category: "Traces"),
            new(Path.Combine(roaming, "Microsoft", "Office", "Recent"), "Recent item shortcuts", CleanupKind.Temp, Days(7), "*.lnk", Recursive: false, Category: "Traces"),
            // Shortcuts whose application and install folder are verified missing.
            new(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Broken shortcuts", CleanupKind.Shortcut, Days(7), "*.lnk", Recursive: false, Category: "Shortcuts"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Broken shortcuts", CleanupKind.Shortcut, Days(7), "*.lnk", Category: "Shortcuts"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Broken shortcuts", CleanupKind.Shortcut, Days(7), "*.lnk", Recursive: false, Category: "Shortcuts", DeletableOnly: true),
            new(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Broken shortcuts", CleanupKind.Shortcut, Days(7), "*.lnk", Category: "Shortcuts", DeletableOnly: true),
        };
        // Adds one target per named sub-folder of baseDir that exists today. The application must be closed (processes) for its cache.
        void AddFolders(string category, string group, CleanupKind kind, int days, string baseDir, string[] subfolders, string[]? processes = null, string? pattern = null)
        {
            if (!Directory.Exists(baseDir)) return;
            foreach (var sub in subfolders)
            {
                string folder = Path.Combine(baseDir, sub);
                if (Directory.Exists(folder)) list.Add(new(folder, group, kind, Days(days), pattern, processes, Category: category));
            }
        }
        List<string> Folders(string parent)
        {
            try { return Directory.Exists(parent) ? Directory.EnumerateDirectories(parent).ToList() : []; }
            catch (Exception ex) when (IsFileError(ex)) { return []; }
        }
        // Shader caches (Windows, NVIDIA, AMD, Intel) are rebuilt by the graphics driver on demand.
        AddFolders("DirectX", "DirectX cache", CleanupKind.Cache, 7, local, ["D3DSCache"]);
        AddFolders("DirectX", "DirectX cache", CleanupKind.Cache, 7, Path.Combine(local, "NVIDIA"), ["DXCache", "GLCache", "ComputeCache"]);
        AddFolders("DirectX", "DirectX cache", CleanupKind.Cache, 7, Path.Combine(local, "NVIDIA Corporation"), ["NV_Cache"]);
        AddFolders("DirectX", "DirectX cache", CleanupKind.Cache, 7, Path.Combine(local, "AMD"), ["DxCache", "DxcCache", "GLCache"]);
        AddFolders("DirectX", "DirectX cache", CleanupKind.Cache, 7, Path.Combine(local, "Intel"), ["ShaderCache"]);
        // Windows apps (Store/MSIX) keep their own temporary state and web cache inside each package folder.
        foreach (var package in Folders(Path.Combine(local, "Packages")))
        {
            AddFolders("Temp", "Windows app temp files", CleanupKind.Temp, 3, package, ["TempState", Path.Combine("AC", "Temp")]);
            AddFolders("Cache", "Windows app cache", CleanupKind.Cache, 7, package, [Path.Combine("AC", "INetCache")]);
        }
        AddFolders("Cache", "Microsoft Store cache", CleanupKind.Cache, 7, Path.Combine(local, "Packages", "Microsoft.WindowsStore_8wekyb3d8bbwe"), ["LocalCache"], ["WinStore.App"]);
        AddFolders("Cache", "Remote Desktop cache", CleanupKind.Cache, 7, Path.Combine(local, "Microsoft", "Terminal Server Client"), ["Cache"], pattern: "*.bmc;*.bin");
        // Chromium browsers; group names stay stable (Brave-Browser) for existing audit logs.
        foreach (var browser in new[] { (Path.Combine("Google", "Chrome", "User Data"), "Chrome", "chrome"),
            (Path.Combine("Microsoft", "Edge", "User Data"), "Edge", "msedge"),
            (Path.Combine("BraveSoftware", "Brave-Browser", "User Data"), "Brave-Browser", "brave"),
            (Path.Combine("Vivaldi", "User Data"), "Vivaldi", "vivaldi") })
        {
            string userData = Path.Combine(local, browser.Item1);
            if (!Directory.Exists(userData) || !SafePath(userData, userData)) continue;
            AddFolders("Cache", browser.Item2 + " cache", CleanupKind.Cache, 7, userData, ["GrShaderCache", "ShaderCache", "DawnCache", "GraphiteDawnCache"], [browser.Item3]);
            foreach (var profile in Folders(userData))
            {
                string name = Path.GetFileName(profile);
                if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                AddFolders("Cache", browser.Item2 + " cache", CleanupKind.Cache, 7, profile,
                    ["Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", "Media Cache"], [browser.Item3]);
            }
        }
        AddFolders("Cache", "Opera cache", CleanupKind.Cache, 7, Path.Combine(local, "Opera Software", "Opera Stable"), ["Cache", "Code Cache", "GPUCache"], ["opera"]);
        AddFolders("Cache", "Opera cache", CleanupKind.Cache, 7, Path.Combine(local, "Opera Software", "Opera Stable", "Default"), ["Cache", "Code Cache", "GPUCache"], ["opera"]);
        foreach (var profile in Folders(Path.Combine(local, "Mozilla", "Firefox", "Profiles")))
            AddFolders("Cache", "Firefox cache", CleanupKind.Cache, 7, profile, ["cache2"], ["firefox"]);
        // Chat, editor and launcher apps built on web engines: their caches rebuild on next start.
        AddFolders("Cache", "Discord cache", CleanupKind.Cache, 7, Path.Combine(roaming, "discord"), ["Cache", "Code Cache", "GPUCache"], ["discord"]);
        AddFolders("Cache", "Slack cache", CleanupKind.Cache, 7, Path.Combine(roaming, "Slack"), ["Cache", "Code Cache", "GPUCache"], ["slack"]);
        AddFolders("Cache", "Teams cache", CleanupKind.Cache, 7, Path.Combine(roaming, "Microsoft", "Teams"), ["Cache", "blob_storage", "GPUCache", "tmp", "Code Cache"], ["teams"]);
        AddFolders("Cache", "VS Code cache", CleanupKind.Cache, 7, Path.Combine(roaming, "Code"), ["Cache", "CachedData", "CachedExtensionVSIXs", "Code Cache", "GPUCache"], ["code"]);
        AddFolders("Cache", "Steam web cache", CleanupKind.Cache, 7, Path.Combine(local, "Steam"), ["htmlcache"], ["steam", "steamwebhelper"]);
        // Developer and creator caches: rebuildable, but re-downloading or re-indexing costs time, so they are Review (opt-in).
        AddFolders("DevCache", "NuGet cache", CleanupKind.Cache, 14, Path.Combine(local, "NuGet"), ["v3-cache", "plugins-cache"]);
        AddFolders("DevCache", "npm cache", CleanupKind.Cache, 14, local, ["npm-cache"]);
        AddFolders("DevCache", "pip cache", CleanupKind.Cache, 14, Path.Combine(local, "pip"), ["Cache"]);
        AddFolders("DevCache", "Yarn cache", CleanupKind.Cache, 14, Path.Combine(local, "Yarn"), ["Cache"]);
        foreach (var ide in Folders(Path.Combine(local, "JetBrains")))
            AddFolders("DevCache", "JetBrains caches", CleanupKind.Cache, 14, ide, ["caches"]);
        foreach (var studio in Folders(Path.Combine(local, "Google")).Where(path => Path.GetFileName(path).StartsWith("AndroidStudio", StringComparison.Ordinal)))
            AddFolders("DevCache", "Android Studio caches", CleanupKind.Cache, 14, studio, ["caches"]);
        AddFolders("DevCache", "Adobe media cache", CleanupKind.Cache, 14, Path.Combine(roaming, "Adobe", "Common"),
            ["Media Cache Files", "Media Cache", "Peak Files"], ["Adobe Premiere Pro", "AfterFX", "Adobe Media Encoder", "Audition"]);
        return list;
    }

    private readonly record struct Candidate(string Path, bool IsDirectory, long Length, DateTime ModifiedUtc, DateTime CreatedUtc);

    private static bool Matches(string[]? patterns, ReadOnlySpan<char> name)
    {
        if (patterns == null) return true;
        foreach (string pattern in patterns)
            if (FileSystemName.MatchesSimpleExpression(pattern, name)) return true;
        return false;
    }

    private static string[]? Patterns(string? pattern) => pattern?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal ScanResult Scan(IProgress<ScanProgress>? progress, CancellationToken token)
    {
        List<CleanupItem> found = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int inaccessible = 0, busy = 0, examined = 0;
        long bytes = 0;
        bool limited = false;
        int stages = Math.Max(1, targets.Count + (includeRegistry ? 1 : 0) + (recycleBin != null ? 1 : 0));
        progress?.Report(new("Preparing scan", 0, 0, 0, 0));
        // One enumeration pass per directory returns size, times and attributes, so no file is stat'ed twice.
        // Reparse points and system files are never returned; hidden files are (the default would skip them).
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            if (token.IsCancellationRequested || limited) break;
            var target = targets[targetIndex];
            double start = (double)targetIndex / stages;
            double end = (double)(targetIndex + 1) / stages;
            progress?.Report(new(target.Group, examined, found.Count, bytes, start));
            if (!Directory.Exists(target.Root) || !SafePath(target.Root, target.Root))
            { progress?.Report(new(target.Group, examined, found.Count, bytes, end)); continue; }
            if (BrowserRunning(target, fresh: true))
            { busy++; progress?.Report(new(target.Group, examined, found.Count, bytes, end)); continue; }
            // Discover one location at a time, then inspect a known number of files.
            // Progress measures completed locations and files within the current location, not elapsed time.
            string fullRoot = Path.GetFullPath(target.Root);
            string[]? patterns = Patterns(target.Pattern);
            DateTime cutoff = DateTime.UtcNow - target.MinimumAge;
            List<Candidate> files = [];
            var reportClock = Stopwatch.StartNew();
            bool discoveryLimited = false;
            // Live counting: files in a plain location are already final candidates when discovered (deduplicated here),
            // so they are counted immediately. Protected and shortcut locations are counted only once verified.
            bool plain = target.Kind != CleanupKind.Shortcut && !target.DeletableOnly;
            int examinedBefore = examined;
            // Discovery has no known total, so it fills up to half of this location's share on a saturating curve;
            // inspecting the discovered files fills the other half. The gauge therefore moves while files are found.
            double DiscoveryFraction() => start + (end - start) * 0.5 * (examined - examinedBefore) / (examined - examinedBefore + 3000.0);
            int Live(int pendingCandidates) => Math.Min(CandidateLimit, found.Count + (plain ? pendingCandidates : 0));
            Stack<string> pending = new();
            pending.Push(target.Root);
            while (pending.Count > 0 && !token.IsCancellationRequested && !discoveryLimited)
            {
                string directory = pending.Pop();
                if (!directory.StartsWith(Path.TrimEndingDirectorySeparator(target.Root), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var entries = new FileSystemEnumerable<Candidate>(directory,
                        (ref FileSystemEntry entry) => new Candidate(entry.ToFullPath(), entry.IsDirectory, entry.IsDirectory ? 0 : entry.Length,
                            entry.LastWriteTimeUtc.UtcDateTime, entry.CreationTimeUtc.UtcDateTime), options)
                    {
                        ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.IsDirectory
                            ? target.Recursive
                            : Matches(patterns, entry.FileName) && entry.LastWriteTimeUtc.UtcDateTime < cutoff && entry.CreationTimeUtc.UtcDateTime < cutoff
                    };
                    foreach (var entry in entries)
                    {
                        if (token.IsCancellationRequested) break;
                        examined++;
                        if (entry.IsDirectory) { pending.Push(entry.Path); continue; }
                        if (!seen.Add(entry.Path)) continue;
                        files.Add(entry);
                        if (files.Count >= 100000) { discoveryLimited = true; break; }
                        // Report from inside a huge flat folder too, not only between folders.
                        if ((files.Count & 127) == 0 && reportClock.ElapsedMilliseconds >= 40)
                        {
                            progress?.Report(new("Discovering " + target.Group.ToLowerInvariant(), examined, Live(files.Count), bytes, DiscoveryFraction()));
                            reportClock.Restart();
                        }
                    }
                }
                catch (Exception ex) when (IsFileError(ex)) { if (!target.DeletableOnly) inaccessible++; }
                if (reportClock.ElapsedMilliseconds >= 40)
                {
                    progress?.Report(new("Discovering " + target.Group.ToLowerInvariant(), examined, Live(files.Count), bytes, DiscoveryFraction()));
                    reportClock.Restart();
                }
            }
            for (int index = 0; index < files.Count && !token.IsCancellationRequested && !limited; index++)
            {
                try
                {
                    var candidate = files[index];
                    if (!target.DeletableOnly || CanDelete(candidate.Path))
                    {
                        string? shortcutTarget = null, shortcutHash = null;
                        bool verified = true;
                        if (target.Kind == CleanupKind.Shortcut)
                        {
                            shortcutTarget = ShortcutProbe.MissingApplication(candidate.Path);
                            verified = shortcutTarget != null;
                            if (verified)
                            {
                                using var shortcut = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                                shortcutHash = Convert.ToHexString(SHA256.HashData(shortcut));
                            }
                        }
                        if (verified)
                        {
                            found.Add(new CleanupItem { Path = candidate.Path, Root = fullRoot, Group = target.Group,
                                Kind = target.Kind, CategoryId = target.Category, Bytes = candidate.Length, ModifiedUtc = candidate.ModifiedUtc,
                                CreatedUtc = candidate.CreatedUtc, ShortcutTarget = shortcutTarget, ShortcutHash = shortcutHash,
                                Selected = SelectedByDefault(target) });
                            bytes += candidate.Length;
                            if (found.Count >= CandidateLimit) limited = true;
                        }
                    }
                }
                catch (Exception ex) when (IsFileError(ex)) { inaccessible++; }
                if (index == 0 || (index % 32 == 0 && reportClock.ElapsedMilliseconds >= 40) || index == files.Count - 1)
                {
                    progress?.Report(new(target.Group, examined, Live(files.Count - index - 1), bytes, start + (end - start) * (0.5 + 0.5 * (index + 1) / Math.Max(1, files.Count))));
                    reportClock.Restart();
                }
            }
            limited |= discoveryLimited;
            if (!token.IsCancellationRequested && !limited) progress?.Report(new(target.Group, examined, found.Count, bytes, end));
        }
        int nextStage = targets.Count;
        if (includeRegistry && !token.IsCancellationRequested && !limited)
        {
            progress?.Report(new("App registry leftovers", examined, found.Count, bytes, (double)nextStage++ / stages));
            ScanRegistry(found, token);
        }
        if (recycleBin != null && !token.IsCancellationRequested && !limited)
        {
            progress?.Report(new("Recycle Bin", examined, found.Count, bytes, (double)nextStage++ / stages));
            ScanRecycleBin(found);
        }
        if (!token.IsCancellationRequested && !limited) progress?.Report(new("Complete", examined, found.Count, bytes, 1,
            RegistryEntries: found.Count(item => item.Kind is CleanupKind.Registry or CleanupKind.RecycleBin)));
        return new(found.OrderByDescending(x => x.Bytes).ToList(), inaccessible, busy, token.IsCancellationRequested, limited);
    }

    // Only the Safe category is selected by default; Review, Caution and the Recycle Bin are always opt-in.
    private static bool SelectedByDefault(CleanupTarget target) =>
        target.Kind is not (CleanupKind.Registry or CleanupKind.RecycleBin)
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

    // True when this user could open the file exclusively for deletion right now (the same open the delete uses).
    private static bool CanDelete(string path)
    {
        using SafeFileHandle handle = Native.CreateFile(path, 0x80010080, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        return !handle.IsInvalid;
    }

    internal CleanupResult Clean(IReadOnlyList<CleanupItem> items, IProgress<int>? progress, CancellationToken token)
    {
        // Open the audit log first: if it cannot be created, nothing is deleted.
        string logs = Path.Combine(dataDirectory, "Logs");
        Directory.CreateDirectory(logs);
        if (!SafePath(logs, dataDirectory)) throw new IOException("The Hush log directory must not be a link.");
        string logPath = Path.Combine(logs, $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        using var audit = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        int deleted = 0, skipped = 0;
        long bytes = 0;
        char[] pathBuffer = new char[32768]; // one reusable buffer for final-path checks, not one per file
        foreach (var item in items)
        {
            if (token.IsCancellationRequested) break;
            string outcome;
            try
            {
                // Persist intent before performing the operation.
                audit.WriteLine(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, path = item.Path, status = "requested" }));
                if (item.Kind == CleanupKind.Registry) DeleteRegistry(item);
                else if (item.Kind == CleanupKind.RecycleBin) EmptyRecycleBin(item);
                else DeleteFile(item, pathBuffer);
                deleted++;
                bytes += item.Bytes;
                outcome = "Deleted";
            }
            catch (Exception ex) when (IsFileError(ex) || ex is InvalidOperationException or System.Security.SecurityException or COMException)
            {
                skipped++;
                outcome = "Skipped: " + ex.Message;
            }
            item.Outcome = outcome;
            audit.WriteLine(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, path = item.Path, bytes = item.Bytes, status = outcome }));
            progress?.Report(deleted + skipped);
        }
        return new(deleted, skipped, bytes, token.IsCancellationRequested, logPath);
    }

    // Emptying is all-or-nothing and permanent, so it only proceeds if the bin still matches exactly what the user reviewed.
    private void EmptyRecycleBin(CleanupItem item)
    {
        if (recycleBin == null) throw new IOException("Recycle Bin cleaning is not available.");
        var current = recycleBin.Query();
        if (current.Items != item.ItemCount || current.Bytes != item.Bytes) throw new IOException("The Recycle Bin changed after the scan; scan again.");
        recycleBin.Empty();
    }

    private void DeleteFile(CleanupItem item, char[] pathBuffer)
    {
        if (!approved.TryGetValue(ApprovalKey(item.Root, item.Kind, item.Group), out var target)) throw new IOException("Outside approved cleanup locations.");
        if (!SafePath(item.Path, target.Root) || string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(target.Root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The path is unsafe or has changed.");
        if (target.Pattern != null && !Matches(Patterns(target.Pattern), Path.GetFileName(item.Path)))
            throw new IOException("File is not an approved cleanup type.");
        if (BrowserRunning(target)) throw new IOException("The browser is running.");

        // Hold an exclusive handle through metadata validation and deletion. Never follow a final symlink.
        using SafeFileHandle handle = Native.CreateFile(item.Path, 0x80010080, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("File is busy or inaccessible.");
        if (!Native.GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot verify file identity.");
        if ((info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.ReadOnly)) != 0
            || info.NumberOfLinks != 1) throw new IOException("Protected, linked, or read-only file.");
        uint written = Native.GetFinalPathNameByHandle(handle, pathBuffer, (uint)pathBuffer.Length, 0);
        if (written == 0 || written >= pathBuffer.Length) throw new IOException("Cannot verify final path.");
        string resolved = new(pathBuffer, 0, (int)written);
        if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (!string.Equals(Path.GetFullPath(item.Path), resolved, StringComparison.OrdinalIgnoreCase) || !SafePath(resolved, target.Root))
            throw new IOException("File location changed after scan.");
        long length = ((long)info.SizeHigh << 32) | info.SizeLow;
        DateTime modified = DateTime.FromFileTimeUtc(((long)info.WriteHigh << 32) | info.WriteLow);
        DateTime created = DateTime.FromFileTimeUtc(((long)info.CreationHigh << 32) | info.CreationLow);
        if (length != item.Bytes || modified != item.ModifiedUtc || created != item.CreatedUtc || !OldEnough(modified, created, target.MinimumAge))
            throw new IOException("File changed after scan; scan again.");
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

    internal static bool SafePath(string path, string root)
    {
        try
        {
            path = Path.GetFullPath(path);
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0 || root.Length < 4) return false;
            if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
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
