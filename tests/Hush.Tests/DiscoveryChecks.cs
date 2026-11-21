using System.IO;
using Hush;

// AppDiscovery on synthetic folder trees: what it recognizes, what it never enters, and that a running app (its lockfile) is left alone.
internal static class DiscoveryChecks
{
    internal static void Run(string fixture, Action<bool, string> check)
    {
        string root = Path.Combine(fixture, "discovery-" + Guid.NewGuid().ToString("N"));
        string local = Path.Combine(root, "Local"), roaming = Path.Combine(root, "Roaming"), temp = Path.Combine(local, "Temp");
        void Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine(parts));
        void OldFile(string path, string name = "old.bin")
        {
            Directory.CreateDirectory(path);
            string file = Path.Combine(path, name);
            File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-30)); File.SetCreationTimeUtc(file, DateTime.UtcNow.AddDays(-30));
        }
        try
        {
            // An Electron-style app with a lockfile slot, user data it must never touch, logs, temp and crash dumps.
            foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache" }) OldFile(Path.Combine(local, "Foo", cache));
            foreach (string data in new[] { "IndexedDB", "Local Storage", "Network", "Session Storage" }) OldFile(Path.Combine(local, "Foo", data));
            OldFile(Path.Combine(roaming, "Bar", "Code Cache")); OldFile(Path.Combine(roaming, "Bar", "Cache"));
            OldFile(Path.Combine(roaming, "Bar", "logs"), "app.log"); OldFile(Path.Combine(roaming, "Bar", "tmp"));
            OldFile(Path.Combine(roaming, "Bar", "Crashpad", "reports"), "x.dmp"); OldFile(Path.Combine(roaming, "Bar", "Crashpad", "settings"));
            OldFile(Path.Combine(local, "CrashDumps"), "a.dmp");
            // A Cache folder alone is not a Chromium data folder.
            OldFile(Path.Combine(roaming, "Plain", "Cache"));
            // Trees that are never entered.
            OldFile(Path.Combine(local, "Microsoft", "Thing", "Code Cache")); OldFile(Path.Combine(local, "Microsoft", "Thing", "Logs"), "a.log");
            OldFile(Path.Combine(local, "Programs", "Y", "logs"), "a.log");
            OldFile(Path.Combine(local, "Hush", "Logs"), "cleanup.jsonl.log");
            OldFile(Path.Combine(temp, "Sub", "logs"), "a.log");
            OldFile(Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Code Cache"));
            OldFile(Path.Combine(local, "Packages", "Microsoft.Windows.Z_abc", "LocalState", "EBWebView", "Default", "Code Cache"));
            OldFile(Path.Combine(local, "Packages", "Vendor.App_abc", "LocalState", "EBWebView", "Default", "Code Cache"));
            OldFile(Path.Combine(local, "Packages", "Vendor.App_abc", "LocalState", "EBWebView", "Default", "Cache"));
            OldFile(Path.Combine(local, "node_modules", "pkg", "logs"), "a.log");
            // Firefox-family: cache2 under Local is listed, Roaming profiles (bookmarks, passwords) are never entered, Mozilla itself has its own rules.
            OldFile(Path.Combine(local, "zen", "Profiles", "p1.default", "cache2")); Dir(local, "zen", "Profiles", "p1.default", "storage");
            OldFile(Path.Combine(roaming, "zen", "Profiles", "p1.default", "weave", "logs"), "sync.log");
            OldFile(Path.Combine(local, "Mozilla", "Firefox", "Profiles", "q.default", "cache2"));
            // A vendor folder holding several products: the product names the app.
            OldFile(Path.Combine(local, "Google", "AndroidStudio2025", "tmp"));

            var found = AppDiscovery.Discover(local, roaming, temp, []);
            string Rel(CleanupTarget target) => Path.GetRelativePath(root, target.Root);
            var roots = found.Select(Rel).ToList();
            bool Has(params string[] parts) => roots.Contains(Path.Combine(parts), StringComparer.OrdinalIgnoreCase);
            check(Has("Local", "Foo", "Cache") && Has("Local", "Foo", "Code Cache") && Has("Local", "Foo", "GPUCache") && Has("Roaming", "Bar", "Cache") && Has("Roaming", "Bar", "Code Cache"),
                "A Chromium data folder (Code Cache or GPUCache next to Cache) gets its cache folders listed");
            check(!roots.Any(path => path.Contains("IndexedDB") || path.Contains("Local Storage") || path.Contains("Network") || path.Contains("Session Storage") || path.EndsWith("settings", StringComparison.OrdinalIgnoreCase)),
                "Site data, sessions and Crashpad's database are never listed");
            check(!Has("Roaming", "Plain", "Cache"), "A lone Cache folder (no Code Cache or GPUCache) is not assumed to be a cache");
            check(Has("Roaming", "Bar", "logs") && Has("Roaming", "Bar", "tmp") && Has("Roaming", "Bar", "Crashpad", "reports") && Has("Local", "CrashDumps"),
                "Logs, temp, Crashpad report and CrashDumps folders are found");
            check(found.Where(t => t.Category == "AppLogs").All(t => t.Pattern == "*.log;*.log.*;*.etl" && t.MinimumAge == TimeSpan.FromDays(14))
                && found.Where(t => t.Category == "Crash").All(t => t.Pattern is "*.dmp" or "*.dmp;*.meta")
                && found.Where(t => t.Category == "AppTemp").All(t => t.Kind == CleanupKind.Temp && t.Pattern == null),
                "Logs list only log files, crash folders only dumps and metadata, temp folders are plain temp");
            check(!roots.Any(path => path.StartsWith(Path.Combine("Local", "Microsoft")) || path.StartsWith(Path.Combine("Local", "Programs")) || path.StartsWith(Path.Combine("Local", "Hush"))
                    || path.StartsWith(Path.Combine("Local", "Temp")) || path.StartsWith(Path.Combine("Local", "node_modules")) || path.Contains(Path.Combine("Google", "Chrome"))
                    || path.Contains("Microsoft.Windows.Z") || path.Contains("Mozilla")),
                "Microsoft, Programs, Hush's own logs, the Temp folder, node_modules, Chrome, Windows' own packages and Mozilla are never entered");
            check(Has("Local", "Packages", "Vendor.App_abc", "LocalState", "EBWebView", "Default", "Code Cache") && Has("Local", "Packages", "Vendor.App_abc", "LocalState", "EBWebView", "Default", "Cache"),
                "A Store app's WebView2 caches are found");
            check(Has("Local", "zen", "Profiles", "p1.default", "cache2") && !roots.Any(path => path.StartsWith(Path.Combine("Roaming", "zen"))
                && !path.EndsWith("cache2")) && found.Where(t => Rel(t).Contains("zen")).All(t => t.Processes is ["zen"]),
                "Firefox-family caches are listed under Local only, guarded by the browser's process, and Roaming profiles are never entered");
            check(found.Single(t => Rel(t).EndsWith(Path.Combine("AndroidStudio2025", "tmp"))).Group == "AndroidStudio2025 temp files"
                && found.Where(t => t.Group.StartsWith("Foo")).All(t => t.Group is "Foo cache"), "Groups are named after the app, not its vendor folder");
            check(found.Where(t => t.Category == "Cache" && t.Processes == null).All(t => t.LockFiles is { Length: 2 } && t.LockFiles.All(file => Path.GetFileName(file) == "lockfile")),
                "Chromium caches are guarded by the app's lockfile");
            check(found.All(t => t.Category is "Cache" or "AppLogs" or "AppTemp" or "Crash" && !t.DeletableOnly && t.Kind is CleanupKind.Cache or CleanupKind.Temp
                && Path.GetFullPath(t.Root).StartsWith(root + Path.DirectorySeparatorChar)), "Everything discovered is an opt-in Review category inside the user's AppData");

            // A location a hand-written rule already owns is never added a second time.
            var owned = AppDiscovery.Discover(local, roaming, temp, [Path.Combine(local, "Foo", "Cache")]);
            check(!owned.Any(t => Rel(t) == Path.Combine("Local", "Foo", "Cache")) && owned.Any(t => Rel(t) == Path.Combine("Local", "Foo", "Code Cache")), "A location another rule owns is not listed twice");
            // The walk is bounded.
            var limited = AppDiscovery.Discover(local, roaming, temp, [], new AppDiscovery.Limits(MaxDirectories: 2));
            check(limited.Count < found.Count, "A tiny directory budget stops the walk early without failing");

            // Engine: a running app (its lockfile exists) is skipped as busy; once it is closed its old cache files are listed.
            var fooCache = found.Single(t => Rel(t) == Path.Combine("Local", "Foo", "Cache"));
            CleanupEngine.AppOpenCacheMilliseconds = 0; // no waiting for the two-second answer cache
            var engine = new CleanupEngine([fooCache], includeRegistry: false, dataDirectory: Path.Combine(root, "engine-data"));
            string lockfile = Path.Combine(local, "Foo", "lockfile");
            File.WriteAllText(lockfile, "");
            var running = engine.Scan(null, CancellationToken.None);
            check(running.Items.Count == 0 && running.BusyCaches == 1, "A running app's cache (lockfile present) is skipped as busy");
            File.Delete(lockfile);
            var closed = engine.Scan(null, CancellationToken.None);
            check(closed.Items.Count == 1 && closed.BusyCaches == 0 && closed.Items[0].Safety == SafetyLevel.Review && closed.Items[0].Selected == false,
                "Once the app is closed its old cache files are listed as an opt-in Review item");
            var item = closed.Items[0];
            File.WriteAllText(lockfile, "");
            var blocked = engine.Clean([item], null, CancellationToken.None);
            check(blocked.Deleted == 0 && blocked.Skipped == 1 && File.Exists(item.Path), "Cleaning also refuses while the app's lockfile exists");
        }
        finally { CleanupEngine.AppOpenCacheMilliseconds = 2000; try { Directory.Delete(root, true); } catch { } }
    }
}
