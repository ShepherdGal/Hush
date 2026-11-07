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

        // Several wildcards per target; unrelated files are never matched.
        string dumps = Path.Combine(fixture, "multi");
        string dmp = Make(dumps, "a.dmp"), hdmp = Make(dumps, "b.hdmp"), notes = Make(dumps, "notes.txt");
        var multi = Engine("audit-multi", new(dumps, "Crash reports", CleanupKind.Temp, TimeSpan.FromDays(14), "*.dmp;*.hdmp", Category: "Crash"));
        var multiScan = multi.Scan(null, CancellationToken.None);
        check(multiScan.Items.Count == 2 && multiScan.Items.All(item => item.Path != notes), "A target can list several wildcards without matching other files");
        multi.Clean(multiScan.Items, null, CancellationToken.None);
        check(!File.Exists(dmp) && !File.Exists(hdmp) && File.Exists(notes), "Multi-pattern cleanup deletes only approved file types");

        // Risk classification and default selection come from the category, and only Safe is preselected.
        foreach (var (id, safety, selected) in new[] { ("Temp", SafetyLevel.Safe, true), ("Thumbnails", SafetyLevel.Safe, true), ("DirectX", SafetyLevel.Safe, true),
            ("Update", SafetyLevel.Review, false), ("Delivery", SafetyLevel.Review, false), ("Setup", SafetyLevel.Review, false),
            ("Crash", SafetyLevel.Review, false), ("Junk", SafetyLevel.Review, false), ("RecycleBin", SafetyLevel.Caution, false),
            ("DevCache", SafetyLevel.Review, false), ("SystemLogs", SafetyLevel.Review, false), ("Installer", SafetyLevel.Review, false), ("Traces", SafetyLevel.Review, false), ("Cache", SafetyLevel.Safe, true) })
        {
            string folder = Path.Combine(fixture, "cat-" + id); Make(folder, "old.tmp");
            var item = Engine("audit-cat-" + id, new(folder, "Fixture " + id, CleanupKind.Temp, TimeSpan.FromDays(2), Category: id)).Scan(null, CancellationToken.None).Items.Single();
            check(item.Safety == safety && item.Selected == selected && ReviewCategory.CategoryFor(item) == id, $"{id} is classified {safety} and {(selected ? "selected" : "opt-in")} by default");
        }
        check(ReviewCategory.Definitions.Select(d => d.Id).Distinct().Count() == ReviewCategory.Definitions.Length
            && ReviewCategory.Definitions.Where(d => d.Safety != SafetyLevel.Safe).All(d => d.Id is not ("Temp" or "Cache" or "DirectX" or "Thumbnails")),
            "Category catalog has unique ids and no duplicate categories");

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
        check(entry.Kind == CleanupKind.RecycleBin && !entry.Selected && entry.Safety == SafetyLevel.Caution && entry.Bytes == 4096 && entry.ItemCount == 3 && bin.Empties == 0,
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
        check(defaults.Where(t => t.Category == "Traces").All(t => t.Pattern == "*.lnk" && !t.Recursive), "Usage traces only ever remove recent-file shortcuts, never the files they point to");
        check(defaults.Where(t => t.Category is "SystemLogs" or "Installer" or "Update" or "Delivery" or "Setup").All(t => t.DeletableOnly), "Every system-wide category uses the deletable-only probe");
        string[] userData = new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData }.Select(Environment.GetFolderPath).Select(Path.GetFullPath).ToArray();
        check(defaults.Where(t => t.Category is "Cache" or "DevCache" or "DirectX").All(t => userData.Any(root => Path.GetFullPath(t.Root).StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))),
            "Application, developer and shader caches are only ever inside the current user's AppData");
        check(defaults.Where(t => t.Category == "DevCache").All(t => ReviewCategory.SafetyOf(t.Category!) == SafetyLevel.Review), "Developer caches stay opt-in because rebuilding them costs time");
        check(defaults.Where(t => t.Kind == CleanupKind.Shortcut).All(t => t.Pattern == "*.lnk")
            && defaults.Where(t => Path.GetFullPath(t.Root).StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase) && !t.Group.EndsWith("temp")).All(t => t.DeletableOnly),
            "Shortcuts use narrow rules and every Windows-folder location (except Windows temp) uses the deletable-only probe");
    }
}
