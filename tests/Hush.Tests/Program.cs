using System.IO;
using System.Runtime.InteropServices;
using Hush;

return TestRunner.Run(args, RunChecks);

static void RunChecks()
{
    string testsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    string fixture = Path.Combine(testsDirectory, ".fixtures-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fixture);
    int assertions = 0;
    void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
        assertions++;
    }
    string Make(string folder, string name, bool old = true)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "synthetic test data");
        if (old)
        {
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        }
        return path;
    }
    CleanupEngine Engine(string root, string? pattern = null, string[]? processes = null) =>
        new([new(root, "Fixture", CleanupKind.Temp, TimeSpan.FromDays(2), pattern, processes)], dataDirectory: Path.Combine(fixture, "audit"));
    try
    {
        string root = Path.Combine(fixture, "temp");
        var engine = Engine(root);
        string old = Make(root, "old.tmp");
        string fresh = Make(root, "fresh.tmp", false);
        string disguised = Make(root, "fresh-with-old-modified.tmp", false);
        File.SetLastWriteTimeUtc(disguised, DateTime.UtcNow.AddDays(-10));
        string system = Make(root, "system.tmp");
        File.SetAttributes(system, FileAttributes.System);
        var reported = new ProgressRecorder();
        var scan = engine.Scan(reported, CancellationToken.None);
        Check(reported.Reports[0].Fraction == 0 && reported.Reports[^1].Fraction == 1, "Completed scan reports real progress from zero to one");
        Check(reported.Reports.Zip(reported.Reports.Skip(1)).All(pair => pair.First.Fraction <= pair.Second.Fraction)
            && reported.Reports.All(p => p.Fraction is >= 0 and <= 1), "Scan progress is bounded and never moves backward");
        Check(reported.Reports[^1].FilesFound == scan.Items.Count(item => item.Kind != CleanupKind.Registry)
            && reported.Reports.Zip(reported.Reports.Skip(1)).All(pair => pair.First.FilesFound <= pair.Second.FilesFound),
            "Live file discoveries are monotonic and match eligible file results");
        // The engine only reports progress for files that qualify, so a second qualifying file gives the stop an in-between report.
        string midScanExtra = Make(root, "mid-scan-extra.tmp");
        using (var midScan = new CancellationTokenSource())
        {
            var midProgress = new ProgressRecorder(p => { if (p.Fraction > 0 && p.Fraction < 1) midScan.Cancel(); });
            var partial = engine.Scan(midProgress, midScan.Token);
            Check(partial.Stopped && midProgress.Reports[^1].Fraction < 1, "Mid-scan stop retains partial results without reporting completion");
            Check(File.Exists(old) && File.Exists(fresh), "Stopping a scan cannot delete files");
        }
        File.Delete(midScanExtra);
        Check(scan.Items.Count == 1 && scan.Items[0].Path == old, "Scan includes old temp; excludes fresh, recently created, and system files");
        var result = engine.Clean(scan.Items, null, CancellationToken.None);
        Check(result.Deleted == 1 && result.Skipped == 0 && !File.Exists(old), "Exclusive-handle deletion removes the verified fixture");
        Check(File.Exists(fresh) && File.Exists(disguised) && File.Exists(system), "Excluded files remain untouched");
        Check(File.ReadAllText(result.LogPath).Contains("Deleted"), "Cleanup audit records the result");
        Check(Directory.Exists(root), "Cleanup never removes target directories");
    
        string changed = Make(root, "changed.tmp");
        var changedItem = engine.Scan(null, CancellationToken.None).Items.Single(x => x.Path == changed);
        File.AppendAllText(changed, "new content");
        result = engine.Clean([changedItem], null, CancellationToken.None);
        Check(result.Skipped == 1 && File.Exists(changed), "Changed-after-scan file is preserved");
    
        string locked = Make(root, "locked.tmp");
        var lockedItem = engine.Scan(null, CancellationToken.None).Items.Single(x => x.Path == locked);
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = engine.Clean([lockedItem], null, CancellationToken.None);
            Check(result.Skipped == 1 && File.Exists(locked), "Locked file is skipped");
        }
    
        string readOnly = Make(root, "readonly.tmp");
        var readOnlyItem = engine.Scan(null, CancellationToken.None).Items.Single(x => x.Path == readOnly);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        result = engine.Clean([readOnlyItem], null, CancellationToken.None);
        Check(result.Skipped == 1 && File.Exists(readOnly), "Read-only file is preserved");
    
        string outside = Make(Path.Combine(fixture, "outside"), "outside.tmp");
        Check(!CleanupEngine.SafePath(outside, root), "Sibling path cannot escape allowlist");
        var forged = new CleanupItem { Path = outside, Root = root, Group = "Fixture", Kind = CleanupKind.Temp };
        result = engine.Clean([forged], null, CancellationToken.None);
        Check(result.Skipped == 1 && File.Exists(outside), "Forged outside-root candidate is rejected");
        Check(!CleanupEngine.SafePath(root + "-sibling", root), "Prefix collision is rejected");
        Check(!CleanupEngine.SafePath(root + ":stream", root), "Alternate data stream is rejected");
    
        string cancelFile = Make(root, "cancel.tmp");
        var cancelItem = engine.Scan(null, CancellationToken.None).Items.Single(x => x.Path == cancelFile);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var canceledScan = engine.Scan(null, cancel.Token);
        Check(canceledScan.Stopped && canceledScan.Items.Count == 0, "Scan cancellation reports partial state");
        result = engine.Clean([cancelItem], null, cancel.Token);
        Check(result.Stopped && result.Deleted == 0 && File.Exists(cancelFile), "Canceled cleanup preserves unprocessed file");
    
        string patterns = Path.Combine(fixture, "patterns");
        string dump = Make(patterns, "crash.dmp"); Make(patterns, "personal.txt");
        Check(Engine(patterns, "*.dmp").Scan(null, CancellationToken.None).Items.Single().Path == dump, "Crash-report filter cannot include arbitrary files");
        string processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        Check(Engine(patterns, processes: [processName]).Scan(null, CancellationToken.None).BusyCaches == 1, "Active process causes cache root to be skipped");
        var duplicateEngine = new CleanupEngine([
            new(patterns, "Fixture", CleanupKind.Temp, TimeSpan.FromDays(2)),
            new(patterns, "Fixture", CleanupKind.Temp, TimeSpan.FromDays(2))], dataDirectory: Path.Combine(fixture, "audit"));
        Check(duplicateEngine.Scan(null, CancellationToken.None).Items.Count == 2, "Overlapping roots do not duplicate candidates");
    
        string link = Path.Combine(root, "hardlink.tmp");
        Check(CreateHardLink(link, outside, IntPtr.Zero), "Hard-link fixture created");
        var linkItem = engine.Scan(null, CancellationToken.None).Items.Single(x => x.Path == link);
        result = engine.Clean([linkItem], null, CancellationToken.None);
        Check(result.Skipped == 1 && File.Exists(link) && File.Exists(outside), "Hard-linked file is preserved");
    
        string junction = Path.Combine(root, "junction");
        using (var command = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { Arguments = $"/c mklink /J \"{junction}\" \"{Path.GetDirectoryName(outside)}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
        { command!.WaitForExit(); Check(command.ExitCode == 0, "Junction fixture created"); }
        Check(!CleanupEngine.SafePath(Path.Combine(junction, "outside.tmp"), root), "Junction traversal is rejected");
        Check(!engine.Scan(null, CancellationToken.None).Items.Any(x => x.Path.Contains("junction")), "Scan skips directory junctions");
        Directory.Delete(junction);
    
        Check(!CleanupEngine.IsOrphanExecutable(@"C:\Windows\missing.exe"), "System registry executable is excluded");
        Check(!CleanupEngine.IsOrphanExecutable(@"\\server\share\missing.exe"), "Network registry executable is excluded");
        Check(!CleanupEngine.IsOrphanExecutable("relative.exe"), "Relative registry executable is excluded");
        Check(!CleanupEngine.IsOrphanExecutable(Environment.ProcessPath!), "Installed executable is excluded");
        Check(!CleanupEngine.IsOrphanExecutable(@"C:\Program Files\missing.exe --argument"), "Registry command line is excluded");
        Check(Sizes.Format(1536) == "1.5 KB", "Human-readable byte sizes");
        ExpandedSafetyChecks.Run(fixture, Check);
        CategoryChecks.Run(fixture, Check);
        DiscoveryChecks.Run(fixture, Check);
        ProcessChecks.RunEngine(fixture, Check);
        AdminChecks.Run(fixture, Check);
        WindowsUpdateChecks.RunEngine(fixture, Check);
        ScaleChecks.Run(fixture, Check);
        UiSizeChecks.Run(Check);
        ColorRuleChecks.Run(Check);
        TypographyChecks.Run(Check);
        DistributionChecks.Run(fixture, Check);
        SmartBoostChecks.RunEngine(fixture, Check);
        UiLayoutChecks.Run();
        Console.WriteLine($"\n{assertions} safety checks plus UI layout verification passed. No user files or registry entries were cleaned.");
    }
    finally
    {
        string resolved = Path.GetFullPath(fixture);
        if (!resolved.StartsWith(testsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith(".fixtures-")) throw new Exception("Unsafe fixture cleanup target.");
        // Only the uniquely named synthetic fixture is removed.
        foreach (string file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(resolved, true);
    }
    
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
}

