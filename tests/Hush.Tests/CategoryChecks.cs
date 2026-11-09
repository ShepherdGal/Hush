using System.IO;
using Hush;

// Engine checks for risk categories, multi-pattern rules, protected-location probing, the Recycle Bin service
// and the permanent "never touch" list for default targets. Synthetic fixtures and a fake Recycle Bin only.
internal static class CategoryChecks
{
    private sealed class SyncProgress(Action<int> report) : IProgress<int> { public void Report(int value) => report(value); }
    private sealed class FakeRecycleBin(long bytes, long items) : IRecycleBin
    {
        public long Bytes = bytes, Items = items; public int Empties; public bool Fail;
        public RecycleBinInfo Query() => new(Bytes, Items);
        public void Empty() { if (Fail) throw new IOException("fixture failure"); Empties++; Bytes = 0; Items = 0; }
    }

    internal static void Run(string fixture, Action<bool, string> check)
    {
        string Make(string folder, string name)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, "synthetic");
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-60)); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60));
            return path;
        }
        CleanupEngine Engine(string audit, CleanupTarget target) => new([target], dataDirectory: Path.Combine(fixture, audit));

        // A location that never answers (a stuck network path, a security product holding a file) must not freeze the scan: the watchdog
        // skips it and the others finish; and Stop returns at once even while a location is stuck.
        string[] stallFolders = new[] { "stall-a", "stall-b", "stall-c" }.Select(name => Path.Combine(fixture, name)).ToArray();
        foreach (string folder in stallFolders) Make(folder, "synthetic.tmp");
        var stallEngine = new CleanupEngine(stallFolders.Select((folder, index) => new CleanupTarget(folder, "Stall " + "ABC"[index], CleanupKind.Temp, TimeSpan.FromDays(2))).ToList(),
            dataDirectory: Path.Combine(fixture, "audit-stall"));
        using var unblock = new ManualResetEventSlim(false);
        var stallClock = System.Diagnostics.Stopwatch.StartNew();
        var stalledScan = stallEngine.Scan(null, CancellationToken.None, new ScanOptions(StallMilliseconds: 1500, OnTargetStart: target => { if (target.Group == "Stall B") unblock.Wait(20000); }));
        stallClock.Stop();
        check(stalledScan.Stalled == 1 && !stalledScan.Stopped && stalledScan.Items.Count == 2 && stalledScan.Items.All(item => item.Group != "Stall B") && stallClock.ElapsedMilliseconds < 8000,
            "A location that stops responding is skipped and the scan still finishes with the other locations");
        unblock.Set(); // releases the abandoned worker; it must not add anything afterwards
        Thread.Sleep(300);
        check(stalledScan.Items.Count == 2 && File.Exists(Path.Combine(stallFolders[1], "synthetic.tmp")), "An abandoned location adds nothing later and nothing is deleted");
        using (var stopNow = new CancellationTokenSource())
        using (var hold = new ManualResetEventSlim(false))
        {
            _ = Task.Run(async () => { await Task.Delay(400); stopNow.Cancel(); });
            var stopClock = System.Diagnostics.Stopwatch.StartNew();
            var stopped = stallEngine.Scan(null, stopNow.Token, new ScanOptions(StallMilliseconds: 0, OnTargetStart: target => { if (target.Group == "Stall A") hold.Wait(20000); }));
            stopClock.Stop();
            hold.Set();
            check(stopped.Stopped && stopClock.ElapsedMilliseconds < 6000, "Stop returns promptly even while a location is stuck");
        }

        // Several wildcards per target; unrelated files are never matched.
        string dumps = Path.Combine(fixture, "multi");
        string dmp = Make(dumps, "a.dmp"), hdmp = Make(dumps, "b.hdmp"), notes = Make(dumps, "notes.txt");
        var multi = Engine("audit-multi", new(dumps, "Crash reports", CleanupKind.Temp, TimeSpan.FromDays(14), "*.dmp;*.hdmp", Category: "Crash"));
        var multiScan = multi.Scan(null, CancellationToken.None);
        check(multiScan.Items.Count == 2 && multiScan.Items.All(item => item.Path != notes), "A target can list several wildcards without matching other files");
        multi.Clean(multiScan.Items, null, CancellationToken.None);
        check(!File.Exists(dmp) && !File.Exists(hdmp) && File.Exists(notes), "Multi-pattern cleanup deletes only approved file types");

        // Risk classification and default selection come from the category (every catalogued category is checked), and only Safe is preselected.
        foreach (var definition in ReviewCategory.Definitions)
        {
            string folder = Path.Combine(fixture, "cat-" + definition.Id); Make(folder, "old.tmp");
            var item = Engine("audit-cat-" + definition.Id, new(folder, "Fixture " + definition.Id, CleanupKind.Temp, TimeSpan.FromDays(2), Category: definition.Id)).Scan(null, CancellationToken.None, new(Include: _ => true)).Items.Single(); // Include also reaches the Settings-only categories
            bool selected = definition.Safety == SafetyLevel.Safe;
            check(item.Safety == definition.Safety && item.Selected == selected && ReviewCategory.CategoryFor(item) == definition.Id,
                $"{definition.Id} is classified {definition.Safety} and {(selected ? "selected" : "opt-in")} by default");
        }
        check(ReviewCategory.Definitions.Where(d => d.Safety == SafetyLevel.Safe).Select(d => d.Id).Order().SequenceEqual(["Temp", "Thumbnails"]),
            "Only verified disposable temp files and thumbnail caches are Safe; browser caches, shader caches, logs and traces are Review");
        check(new[] { "WindowsOld", "RecycleBin", "Registry", "BrowserCookies", "AppDownloads" }.All(id => ReviewCategory.SafetyOf(id) == SafetyLevel.Advanced)
            && ReviewCategory.Definitions.Where(d => d.Safety == SafetyLevel.Advanced).Count() == 5,
            "Irreversible or recovery-related categories are Advanced (red) and nothing else is");
        check(ReviewCategory.Definitions.Select(d => d.Id).Distinct().Count() == ReviewCategory.Definitions.Length
            && ReviewCategory.Definitions.All(d => ReviewCategory.GroupDefinitions.Any(g => g.Id == d.GroupId)),
            "Category catalog has unique ids and every category belongs to a section");
        check(SafetyPalette.Text(SafetyLevel.Safe, false) != SafetyPalette.Text(SafetyLevel.Review, false)
            && SafetyPalette.Text(SafetyLevel.Review, false) != SafetyPalette.Text(SafetyLevel.Advanced, false)
            && SafetyPalette.Text(SafetyLevel.Safe, true) != SafetyPalette.Text(SafetyLevel.Review, true)
            && SafetyPalette.Text(SafetyLevel.Review, true) != SafetyPalette.Text(SafetyLevel.Advanced, true),
            "Safe, Review and Advanced have three distinct colors in both themes");
        // Protected locations list only what can be deleted right now; locks are never reported as skipped locations.
        string guarded = Path.Combine(fixture, "guarded");
        string free = Make(guarded, "free.db"), busy = Make(guarded, "busy.db");
        var probing = Engine("audit-probe", new(guarded, "Thumbnail cache", CleanupKind.Cache, TimeSpan.FromDays(1), "*.db", Category: "Thumbnails", DeletableOnly: true));
        var plain = Engine("audit-plain", new(guarded, "Thumbnail cache", CleanupKind.Cache, TimeSpan.FromDays(1), "*.db", Category: "Thumbnails"));
        using (var held = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var probed = probing.Scan(null, CancellationToken.None);
            check(probed.Items.Count == 1 && probed.Items[0].Path == free && probed.Inaccessible == 0, "Deletable-only locations hide locked files without reporting skipped locations");
            check(plain.Scan(null, CancellationToken.None).Items.Count == 2, "Ordinary locations still list locked files, which are skipped at delete time");
        }
        check(probing.Scan(null, CancellationToken.None).Items.Count == 2, "Files become listed once they are no longer locked");

        // Recycle Bin: supported-API service, scan is read-only, cleaning is opt-in and exact.
        var bin = new FakeRecycleBin(4096, 3);
        string empty = Path.Combine(fixture, "empty"); Directory.CreateDirectory(empty);
        CleanupEngine BinEngine(string audit, IRecycleBin? service) =>
            new([new(empty, "Fixture", CleanupKind.Temp, TimeSpan.FromDays(2))], dataDirectory: Path.Combine(fixture, audit), recycleBin: service);
        var binProgress = new ProgressRecorder();
        var binScan = BinEngine("audit-bin", bin).Scan(binProgress, CancellationToken.None);
        var entry = binScan.Items.Single();
        check(entry.Kind == CleanupKind.RecycleBin && !entry.Selected && entry.Safety == SafetyLevel.Advanced && entry.Bytes == 4096 && entry.ItemCount == 3 && bin.Empties == 0,
            "Recycle Bin is found read-only as an opt-in Caution item");
        check(binProgress.Reports[^1].Fraction == 1 && binProgress.Reports[^1].FilesFound == 0
            && binProgress.Reports.Zip(binProgress.Reports.Skip(1)).All(pair => pair.First.Fraction <= pair.Second.Fraction), "Recycle Bin scanning keeps progress monotonic and is not counted as a file");
        check(BinEngine("audit-nobin", null).Scan(null, CancellationToken.None).Items.Count == 0 && BinEngine("audit-emptybin", new FakeRecycleBin(0, 0)).Scan(null, CancellationToken.None).Items.Count == 0,
            "No Recycle Bin item appears when it is unavailable or empty");
        var binEngine = BinEngine("audit-bin2", bin);
        bin.Items = 5; var changed = binEngine.Clean([entry], null, CancellationToken.None);
        check(changed.Skipped == 1 && bin.Empties == 0, "A Recycle Bin that changed after review is not emptied");
        bin.Items = 3; bin.Fail = true; var failed = binEngine.Clean([entry], null, CancellationToken.None);
        check(failed.Skipped == 1 && failed.Deleted == 0, "A failed Recycle Bin operation is reported as skipped, not as success");
        bin.Fail = false; using var stop = new CancellationTokenSource(); stop.Cancel();
        check(binEngine.Clean([entry], null, stop.Token).Deleted == 0 && bin.Empties == 0, "Canceling before the Recycle Bin step leaves it untouched");
        var emptied = binEngine.Clean([entry], null, CancellationToken.None);
        check(emptied.Deleted == 1 && bin.Empties == 1 && emptied.Bytes == 4096 && File.ReadAllText(emptied.LogPath).Contains("Recycle Bin"), "A reviewed, unchanged Recycle Bin is emptied once and audited");
        var forgedBin = new CleanupItem { Path = "Recycle Bin", Root = "Recycle Bin", Group = "Recycle Bin", Kind = CleanupKind.RecycleBin, Bytes = 1, ItemCount = 1 };
        check(Engine("audit-forged-bin", new(empty, "Fixture", CleanupKind.Temp, TimeSpan.FromDays(2))).Clean([forgedBin], null, CancellationToken.None).Skipped == 1,
            "Recycle Bin items are rejected by engines that do not offer the Recycle Bin");

        // Permission restrictions and cancellation: failures are skipped and reported, never thrown or half-applied.
        string restricted = Path.Combine(fixture, "restricted");
        string deniedFile = Make(restricted, "denied.tmp"), allowedFile = Make(restricted, "allowed.tmp");
        string lockedFolder = Path.Combine(restricted, "locked-folder"); Make(lockedFolder, "inside.tmp");
        var permissionEngine = Engine("audit-permissions", new(restricted, "Fixture", CleanupKind.Temp, TimeSpan.FromDays(2)));
        var guardedPermissionEngine = Engine("audit-permissions-guarded", new(restricted, "Fixture", CleanupKind.Temp, TimeSpan.FromDays(2), DeletableOnly: true));
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var fileRule = new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.ReadData | System.Security.AccessControl.FileSystemRights.Delete, System.Security.AccessControl.AccessControlType.Deny);
        var folderRule = new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var deniedInfo = new FileInfo(deniedFile); var folderInfo = new DirectoryInfo(lockedFolder);
        var deniedScan = permissionEngine.Scan(null, CancellationToken.None);
        var deniedItem = deniedScan.Items.Single(x => x.Path == deniedFile);
        var allowedItem = deniedScan.Items.Single(x => x.Path == allowedFile);
        try
        {
            var fileAcl = deniedInfo.GetAccessControl(); fileAcl.AddAccessRule(fileRule); deniedInfo.SetAccessControl(fileAcl);
            var folderAcl = folderInfo.GetAccessControl(); folderAcl.AddAccessRule(folderRule); folderInfo.SetAccessControl(folderAcl);
            var denied = permissionEngine.Clean([deniedItem, allowedItem], null, CancellationToken.None);
            check(denied.Skipped == 1 && denied.Deleted == 1 && File.Exists(deniedFile) && !File.Exists(allowedFile), "A file this user cannot open is skipped and reported while the rest is cleaned");
            var blocked = permissionEngine.Scan(null, CancellationToken.None);
            check(blocked.Inaccessible >= 1 && !blocked.Stopped && blocked.Items.Any(x => x.Path == deniedFile), "An unreadable folder is counted as skipped and does not stop the scan");
            check(guardedPermissionEngine.Scan(null, CancellationToken.None).Inaccessible == 0, "Protected locations do not report permission failures as skipped locations");
            check(guardedPermissionEngine.Scan(null, CancellationToken.None).Items.All(x => x.Path != deniedFile), "Protected locations never list a file that cannot be deleted");
        }
        finally
        {
            var fileAcl = deniedInfo.GetAccessControl(); fileAcl.RemoveAccessRule(fileRule); deniedInfo.SetAccessControl(fileAcl);
            var folderAcl = folderInfo.GetAccessControl(); folderAcl.RemoveAccessRule(folderRule); folderInfo.SetAccessControl(folderAcl);
        }
        string[] batch = [Make(restricted, "batch1.tmp"), Make(restricted, "batch2.tmp"), Make(restricted, "batch3.tmp")];
        var batchItems = permissionEngine.Scan(null, CancellationToken.None).Items.Where(x => batch.Contains(x.Path)).OrderBy(x => x.Path).ToList();
        using var midClean = new CancellationTokenSource();
        var firstDone = new SyncProgress(count => { if (count >= 1) midClean.Cancel(); });
        var partial = permissionEngine.Clean(batchItems, firstDone, midClean.Token);
        check(partial.Stopped && partial.Deleted == 1 && batch.Count(File.Exists) == 2, "Canceling during a clean stops after the current file and leaves the rest untouched");

        // Large categories: selection must stay O(1) per change (a quadratic version froze the window with ~25,000 files).
        var big = new ReviewCategory("Cache", "Big", SafetyLevel.Safe, "fixture");
        var bigItems = Enumerable.Range(0, 120000).Select(i => new CleanupItem { Path = "fixture-" + i, Root = "fixture-root", Group = "Fixture" }).ToList();
        var bigClock = System.Diagnostics.Stopwatch.StartNew();
        big.Replace(bigItems);
        bool allSelected = big.Selected == true;
        for (int i = 0; i < 2000; i++) bigItems[i].Selected = false;
        bool mixed = big.Selected == null;
        big.Selected = false; bool none = big.Selected == false && bigItems.All(item => !item.Selected);
        big.Selected = true; bool again = big.Selected == true && bigItems.All(item => item.Selected);
        bigClock.Stop();
        check(allSelected && mixed && none && again && bigClock.ElapsedMilliseconds < 3000, $"Selecting inside a 120,000-file category stays instant and tri-state correct ({bigClock.ElapsedMilliseconds} ms)");

        // Permanent boundaries for the shipped default locations.
        var defaults = CleanupEngine.DefaultTargets();
        string[] personal = new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.System }
            .Select(Environment.GetFolderPath).Where(path => path.Length > 3).Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToArray();
        string windowsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        string[] neverTouch = ["Windows.old", "WinSxS", "$PatchCache$", "Package Cache", "Installer", "System32", "SysWOW64", "Recovery", "Boot", "Downloads"];
        bool IsPersonal(string root) => personal.Any(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(path, StringComparison.OrdinalIgnoreCase));
        check(defaults.All(t => t.Category != null && ReviewCategory.Definitions.Any(d => d.Id == t.Category)), "Every default location belongs to a catalogued category");
        check(defaults.All(t => !IsPersonal(t.Root) && !neverTouch.Any(name => t.Root.Split('\\', '/').Contains(name, StringComparer.OrdinalIgnoreCase))),
            "No default location is a personal folder, a program folder, Windows.old, the component store, the installer cache or recovery data");
        check(defaults.Where(t => Path.TrimEndingDirectorySeparator(Path.GetFullPath(t.Root)).Equals(windowsRoot, StringComparison.OrdinalIgnoreCase))
            .All(t => t.Pattern == "MEMORY.DMP" && !t.Recursive && t.DeletableOnly), "The Windows folder itself is only ever used for the single memory dump file");
        check(defaults.Where(t => t.Pattern == null).All(t => t.Kind is CleanupKind.Temp or CleanupKind.Cache), "Only temp and cache locations may clean every file type");
        check(defaults.GroupBy(t => (Path.GetFullPath(t.Root).ToLowerInvariant(), t.Kind, t.Group)).All(group => group.Count() == 1), "Default locations contain no duplicate entries");

        // Unsafe roots (0.9 audit): the temp location follows the TEMP/TMP variables, so a variable pointed at a personal or protected folder must never
        // turn that folder into a cleanup location. Only the process environment is changed (and restored); nothing on disk is read or touched.
        string? savedTmp = Environment.GetEnvironmentVariable("TMP"), savedTemp = Environment.GetEnvironmentVariable("TEMP");
        string systemRoot = Path.GetPathRoot(windowsRoot)!;
        string[] hostile = [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Scratch"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), systemRoot];
        var rejectedNotes = new List<string>();
        bool hostileRejected = true;
        try
        {
            foreach (string candidate in hostile)
            {
                Environment.SetEnvironmentVariable("TMP", candidate); Environment.SetEnvironmentVariable("TEMP", candidate);
                var targets = CleanupEngine.DefaultTargets(rejectedNotes.Add);
                string wanted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
                if (targets.Any(t => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(t.Root)), wanted, StringComparison.OrdinalIgnoreCase) && t.Group == "User temp")) hostileRejected = false;
            }
        }
        finally { Environment.SetEnvironmentVariable("TMP", savedTmp); Environment.SetEnvironmentVariable("TEMP", savedTemp); }
        check(hostileRejected && rejectedNotes.Count >= hostile.Length, "A temp folder pointed at Documents, the Desktop, Downloads, the profile, AppData or a drive root is refused and noted, never cleaned");
        check(CleanupEngine.DefaultTargets().Any(t => t.Group == "User temp"), "The ordinary temp folder is still a cleanup location");
        var fakeRules = new CleanupEngine.RootRules([@"X:\Users\Pat\Documents"], [@"X:\Users\Pat", @"X:\Users", @"X:\Windows"]);
        bool Accepts(string root, string? pattern = null, bool recursive = true) => CleanupEngine.IsAcceptableRoot(new CleanupTarget(root, "t", CleanupKind.Temp, TimeSpan.Zero, pattern, null, recursive, "Temp"), fakeRules, out _);
        check(Accepts(@"X:\Users\Pat\AppData\Local\Temp") && !Accepts(@"X:\Users\Pat") && !Accepts(@"X:\Users\Pat\Documents\Temp") && !Accepts(@"X:\Users\Pat\Documents")
            && !Accepts(@"X:\Users") && !Accepts(@"X:\") && !Accepts(@"X:\Windows"), "Root rules: a temp-style location inside AppData passes; profile, personal, parent and drive roots do not");
        check(Accepts(@"X:\Windows", "MEMORY.DMP", false) && Accepts(@"X:\Users\Pat\Documents", "*.lnk", false) && !Accepts(@"X:\", "*.lnk", false), "Root rules: a single-pattern, non-recursive location (Desktop shortcuts, the memory dump) is allowed but never a drive root");
        check(defaults.Where(t => t.Category is "RecentAccess" or "RecentDocs").All(t => t.Pattern == "*.lnk" && !t.Recursive), "Recent-access traces only ever remove recent-file shortcuts, never the files they point to");
        check(defaults.Where(t => t.Category is "SystemLogs" or "Installer" or "Update" or "Delivery" or "Setup" or "EventTrace" && !(t.Category == "EventTrace" && !t.Root.StartsWith(windowsRoot + "\\", StringComparison.OrdinalIgnoreCase) && !t.Root.Contains("ProgramData", StringComparison.OrdinalIgnoreCase))).All(t => t.DeletableOnly), "Every system-wide category uses the deletable-only probe");
        string[] userData = new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData }.Select(Environment.GetFolderPath).Select(Path.GetFullPath).ToArray();
        string programData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        bool InUserData(CleanupTarget t) => userData.Any(root => Path.GetFullPath(t.Root).StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
        var appCategories = new[] { "Cache", "AppLogs", "AppTemp", "DevCache", "DirectX", "GpuShader", "NvidiaShader", "NvidiaApp", "InternetCache", "BrowserEdge", "BrowserFirefox", "BrowserChrome", "BrowserOther", "BrowserHistory", "BrowserCookies", "UsageHistory" };
        check(defaults.Where(t => appCategories.Contains(t.Category)).All(t => InUserData(t) || (t.DeletableOnly && Path.GetFullPath(t.Root).StartsWith(programData + "\\", StringComparison.OrdinalIgnoreCase) && t.Category is "NvidiaShader" or "NvidiaApp")),
            "Application, browser, developer and shader caches are only inside the current user's AppData (the NVIDIA machine-wide cache is deletable-only)");
        string inetCache = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "INetCache"));
        check(defaults.Where(t => t.Category == "InternetCache").All(t => Path.GetFullPath(t.Root) != inetCache
            && (Path.GetFileName(t.Root) is "IE" or "Content.IE5") && !t.Root.Contains("Outlook", StringComparison.OrdinalIgnoreCase) && !t.Root.Contains("Word", StringComparison.OrdinalIgnoreCase)),
            "Temporary Internet Files only cleans the legacy IE cache folders, never INetCache as a whole, Outlook or Word attachment copies");
        string[] neverListed = ["Login Data", "Web Data", "Bookmarks", "Preferences", "Current Session", "Last Session", "Current Tabs", "Last Tabs", "places.sqlite", "key4.db", "logins.json", "formhistory.sqlite", "Cookies"];
        check(defaults.Where(t => t.Category is "BrowserHistory" or "BrowserCookies" or "BrowserEdge" or "BrowserChrome" or "BrowserOther" or "BrowserFirefox")
                .All(t => t.Category == "BrowserCookies" || !(t.Pattern ?? "").Split(';').Any(p => neverListed.Contains(p))),
            "Browser passwords, bookmarks, saved sessions and form data are never in any rule, and cookies appear only in the Advanced cookie category");
        check(defaults.Where(t => t.Category == "BrowserCookies").All(t => ReviewCategory.SafetyOf("BrowserCookies") == SafetyLevel.Advanced
                && (t.Pattern ?? "").Split(';').All(p => p.StartsWith("Cookies") || p.StartsWith("cookies.sqlite")) && !t.Recursive),
            "Cookie rules list only cookie database files and are Advanced");
        check(defaults.Where(t => t.Category == "AppDownloads").All(t => t.Processes is { Length: > 0 } && t.Kind == CleanupKind.Cache && !t.DeletableOnly) && ReviewCategory.SafetyOf("AppDownloads") == SafetyLevel.Advanced,
            "Large app downloads and installed-app files are Advanced (red, opt-in), each only while its app is closed");
        check(defaults.Where(t => t.Category == "BrowserHistory").All(t => !(t.Pattern ?? "").Contains("places") && !(t.Pattern ?? "").Contains("Bookmarks")),
            "Browser history never includes Firefox places.sqlite (it also holds bookmarks) or bookmark files");
        check(defaults.Where(t => t.Category == "DevCache").All(t => ReviewCategory.SafetyOf(t.Category!) == SafetyLevel.Review), "Developer caches stay opt-in because rebuilding them costs time");
        check(defaults.Where(t => t.Kind == CleanupKind.Shortcut).All(t => t.Pattern == "*.lnk")
            && defaults.Where(t => Path.GetFullPath(t.Root).StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase) && !t.Group.EndsWith("temp")).All(t => t.DeletableOnly),
            "Shortcuts use narrow rules and every Windows-folder location (except Windows temp) uses the deletable-only probe");

        // Temporary files (0.9): both Temp folders list files of any age (a file in use is skipped when deleting, and every file is re-checked).
        var tempTargets = CleanupEngine.DefaultTargets().Where(t => t.Category == "Temp" && t.Group is "User temp" or "Windows temp").ToList();
        check(tempTargets.Count >= 1 && tempTargets.All(t => t.MinimumAge == TimeSpan.Zero && t.Kind == CleanupKind.Temp), "The Temp folders find files of any age");

        // Rules added after a real PC showed what was missing: narrow, user-AppData-only (Prefetch is the one deletable-only system folder), Review level.
        string windowsPrefetch = Path.Combine(windowsRoot, "Prefetch");
        check(defaults.Where(t => t.Category == "Prefetch").All(t => Path.GetFullPath(t.Root).Equals(windowsPrefetch, StringComparison.OrdinalIgnoreCase)
                && t.Pattern == "*.pf" && !t.Recursive && t.DeletableOnly && ReviewCategory.SafetyOf("Prefetch") == SafetyLevel.Review),
            "Prefetch cleaning lists only .pf files, is deletable-only and stays a Review (opt-in) category");
        check(defaults.Where(t => t.Group.StartsWith("Update downloads (", StringComparison.Ordinal)).All(t => InUserData(t) && t.Kind == CleanupKind.Cache && !t.DeletableOnly
                && (t.Pattern == "installer.exe;current.blockmap" && !t.Recursive || t.Pattern == null && Path.GetFileName(t.Root) == "pending")),
            "Updater download cleaning removes only installer.exe, current.blockmap and the pending folder inside an app's own updater folder");
        check(defaults.Where(t => t.Group is "CapCut cache" or "Office add-in cache").All(t => InUserData(t) && t.Kind == CleanupKind.Cache && t.Processes is { Length: > 0 }
                && (Path.GetFileName(t.Root) is "Cache" or "Wef") && !t.Root.Contains("OfficeFileCache", StringComparison.OrdinalIgnoreCase) && !t.Root.Contains("UnsavedFiles", StringComparison.OrdinalIgnoreCase)),
            "CapCut and Office add-in caches are user-AppData caches cleaned only while the program is closed, never Office's unsaved or unsynced work");
    }
}
