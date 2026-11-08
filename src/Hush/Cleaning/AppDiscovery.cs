using System.IO;

namespace Hush;

// Finds the cache, log, temp and crash-dump folders of apps Hush has no hand-written rule for, by recognizing what they are:
//  - a Chromium data folder (Electron apps, WebView2, CEF launchers) is any folder that has a "Code Cache" or "GPUCache" folder next to its
//    "Cache"; only the well-known cache sub-folders are listed (never IndexedDB, Local Storage, cookies or any profile data). While the app runs,
//    Chromium holds a "lockfile" beside its data, and a target whose lockfile exists is skipped like a running browser.
//  - Firefox-family browsers other than Firefox (Zen, Waterfox, LibreWolf ...): Profiles\<profile>\cache2 and friends under LocalAppData.
//  - folders named Logs or Log (log files only), Temp or tmp (older files only), Crashpad (dump and metadata files only), CrashDumps (*.dmp).
// Everything is inside the current user's AppData, everything is Review (opt-in), and the walk is bounded (directory count and time) so a huge
// AppData can never slow the first scan down. Trees Windows, Microsoft, the browsers and Hush itself own are never entered.
internal static class AppDiscovery
{
    // Only folders that are purely caches. blob_storage and the Cached* folders are V8/Electron caches (the same list BleachBit uses for VS Code).
    internal static readonly string[] ChromiumCaches =
    [
        "Cache", "Code Cache", "GPUCache", "DawnCache", "GraphiteDawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "ShaderCache",
        "blob_storage", "CachedData", "CachedExtensionVSIXs", "CachedConfigurations", "CachedProfilesData"
    ];
    internal static readonly string[] FirefoxCaches = ["cache2", "startupCache", "thumbnails", "OfflineCache", "jumpListCache"];
    internal static readonly string[] CrashpadFolders = ["reports", "completed", "pending", "new"];
    // Whole top-level trees that are never entered: Windows and Microsoft components, installed programs, the browsers (hand-written rules with
    // "browser closed" checks own them), game launchers with their own rules, Hush's own logs and Android's SDK.
    private static readonly string[] SkippedTrees =
    [
        "Microsoft", "Programs", "Temp", "Hush", "Packages\\*", "Google\\Chrome", "Google\\Chrome Beta", "Google\\Chrome SxS", "BraveSoftware", "Vivaldi", "Opera Software",
        "Mozilla", "Chromium", "Yandex", "Steam", "EpicGamesLauncher", "Android\\Sdk", "Package Cache", "Programs", "WindowsApps"
    ];
    // Never entered: tooling trees, and the insides of a Chromium data folder (caches are leaves; site data, sessions and extensions are not ours to walk).
    private static readonly HashSet<string> NeverEntered = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", "WinSxS", "Package Cache", "IndexedDB", "Local Storage", "Service Worker", "Session Storage", "Sessions", "Storage",
        "WebStorage", "Extensions", "Extension State", "File System", "databases", "Network", "Shared Dictionary",
        "Cache", "Code Cache", "GPUCache", "DawnCache", "GraphiteDawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "ShaderCache",
        "blob_storage", "CachedData", "CachedExtensionVSIXs", "CachedConfigurations", "CachedProfilesData"
    };

    // Folders that hold several different products of one company: the product folder below them names the app.
    private static readonly HashSet<string> VendorFolders = new(StringComparer.OrdinalIgnoreCase)
        { "Google", "JetBrains", "Adobe", "Autodesk", "Electronic Arts", "Apple Computer", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Unity", "Topaz Labs LLC" };

    internal sealed record Limits(int MaxDirectories = 60000, int MaxMilliseconds = 5000);

    internal static List<CleanupTarget> Discover(string local, string roaming, string tempRoot, IEnumerable<string> existingRoots, Limits? limits = null)
    {
        limits ??= new();
        var result = new List<CleanupTarget>();
        var covered = existingRoots.Select(SafeFull).Where(path => path != null).Select(path => path!.TrimEnd('\\')).ToList();
        string temp = SafeFull(tempRoot)?.TrimEnd('\\') ?? "";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int visited = 0;
        foreach (var (root, isLocal) in new[] { (roaming, false), (local, true) })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            string baseRoot = SafeFull(root)!.TrimEnd('\\');
            var stack = new Stack<(string Path, int Depth)>();
            stack.Push((baseRoot, 0));
            while (stack.Count > 0)
            {
                if (visited >= limits.MaxDirectories || clock.ElapsedMilliseconds > limits.MaxMilliseconds) return result;
                var (directory, depth) = stack.Pop();
                string relative = depth == 0 ? "" : directory[(baseRoot.Length + 1)..];
                int maxDepth = relative.StartsWith("Packages\\", StringComparison.OrdinalIgnoreCase) ? 8 : 5;
                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(directory, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                visited++;
                foreach (string child in children)
                {
                    string name = Path.GetFileName(child);
                    string childRelative = relative.Length == 0 ? name : relative + "\\" + name;
                    if (NeverEntered.Contains(name) || IsSkippedTree(childRelative) || (isLocal && child.TrimEnd('\\').Equals(temp, StringComparison.OrdinalIgnoreCase))) continue;
                    // Firefox-family profiles under Roaming hold bookmarks, passwords and sessions: never entered. (Under Local only the cache folders are read.)
                    if (!isLocal && name.Equals("Profiles", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Classify(child, name, childRelative, isLocal, result, covered)) continue; // a leaf: its content is never walked
                    if (depth + 1 < maxDepth) stack.Push((child, depth + 1));
                }
            }
        }
        return result;
    }

    private static bool IsSkippedTree(string relative)
    {
        foreach (string tree in SkippedTrees)
        {
            if (tree.EndsWith("\\*", StringComparison.Ordinal))
            {
                // Packages\<package>: only Windows' own packages are skipped.
                string prefix = tree[..^1];
                if (relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string package = relative[prefix.Length..].Split('\\')[0];
                    if (package.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase) || package.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase)
                        || package.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) || package.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) return true;
                }
                continue;
            }
            if (relative.Equals(tree, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Looks at one folder. Returns true when the folder is a leaf (a cache, log, temp or crash folder) that must not be entered.
    private static bool Classify(string directory, string name, string relative, bool isLocal, List<CleanupTarget> result, List<string> covered)
    {
        string app = AppName(relative);
        // Logs and Temp and crash dumps.
        if (name.Equals("Logs", StringComparison.OrdinalIgnoreCase) || name.Equals("Log", StringComparison.OrdinalIgnoreCase))
        {
            Add(result, covered, new(directory, $"{app} logs", CleanupKind.Temp, TimeSpan.FromDays(14), "*.log;*.log.*;*.etl", Category: "AppLogs"));
            return true;
        }
        if (name.Equals("Temp", StringComparison.OrdinalIgnoreCase) || name.Equals("tmp", StringComparison.OrdinalIgnoreCase))
        {
            Add(result, covered, new(directory, $"{app} temp files", CleanupKind.Temp, TimeSpan.FromDays(14), null, Category: "AppTemp"));
            return true;
        }
        if (name.Equals("CrashDumps", StringComparison.OrdinalIgnoreCase))
        {
            Add(result, covered, new(directory, $"{app} crash dumps", CleanupKind.Temp, TimeSpan.FromDays(14), "*.dmp", Category: "Crash"));
            return true;
        }
        if (name.Equals("Crashpad", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string sub in CrashpadFolders)
            {
                string folder = Path.Combine(directory, sub);
                if (Directory.Exists(folder)) Add(result, covered, new(folder, $"{app} crash reports ({sub})", CleanupKind.Temp, TimeSpan.FromDays(14), "*.dmp;*.meta", Category: "Crash"));
            }
            return true;
        }
        // Chromium data folder: next to its Cache there is a Code Cache or GPUCache. Only the cache sub-folders are added.
        bool chromium = Directory.Exists(Path.Combine(directory, "Code Cache")) || Directory.Exists(Path.Combine(directory, "GPUCache"));
        if (chromium)
        {
            string parent = Path.GetDirectoryName(directory) ?? directory;
            string[] locks = [Path.Combine(directory, "lockfile"), Path.Combine(parent, "lockfile")];
            foreach (string cache in ChromiumCaches)
            {
                string folder = Path.Combine(directory, cache);
                if (Directory.Exists(folder)) Add(result, covered, new(folder, $"{app} cache", CleanupKind.Cache, TimeSpan.FromDays(7), null, Category: "Cache", LockFiles: locks));
            }
        }
        // Firefox-family browsers (not Firefox itself, which has its own rules): Profiles\<profile>\cache2 under LocalAppData.
        if (isLocal && name.Equals("Profiles", StringComparison.OrdinalIgnoreCase) && relative.Count(c => c == '\\') == 1)
        {
            string process = app.Replace(" ", "").ToLowerInvariant();
            foreach (string profile in SafeDirectories(directory))
            {
                if (!Directory.Exists(Path.Combine(profile, "cache2"))) continue;
                foreach (string cache in FirefoxCaches)
                {
                    string folder = Path.Combine(profile, cache);
                    if (Directory.Exists(folder)) Add(result, covered, new(folder, $"{app} cache", CleanupKind.Cache, TimeSpan.FromDays(7), null, [process], Category: "Cache"));
                }
            }
            return true;
        }
        return false;
    }

    private static void Add(List<CleanupTarget> result, List<string> covered, CleanupTarget target)
    {
        string? full = SafeFull(target.Root)?.TrimEnd('\\');
        if (full == null) return;
        // A location a hand-written rule already owns (or lies inside, or contains) is never added a second time.
        foreach (string existing in covered)
            if (full.Equals(existing, StringComparison.OrdinalIgnoreCase) || full.StartsWith(existing + "\\", StringComparison.OrdinalIgnoreCase)
                || existing.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return;
        if (result.Any(item => SafeFull(item.Root)?.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase) == true)) return;
        result.Add(target);
    }

    // "Windsurf" for Roaming\Windsurf\Cache, "Codex" for Packages\OpenAI.Codex_xxx\...: the app's own folder name, used in the group name.
    private static string AppName(string relative)
    {
        var parts = relative.Split('\\');
        string name = parts[0].Equals("Packages", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 ? parts[1]
            : VendorFolders.Contains(parts[0]) && parts.Length > 2 ? parts[1] : parts[0]; // Google\AndroidStudio2025\tmp is "AndroidStudio2025", not "Google"
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        int dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1 && parts[0].Equals("Packages", StringComparison.OrdinalIgnoreCase)) name = name[(dot + 1)..];
        return name.Trim();
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string? SafeFull(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
