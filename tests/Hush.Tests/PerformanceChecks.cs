using System.Diagnostics;
using System.IO;
using Hush;

// Synthetic-only benchmark: `Hush.Tests.exe --benchmark`. Prints scan/clean time and allocations
// for a nested fixture, so engine changes can be compared before and after. Never touches real folders.
internal static class PerformanceChecks
{
    // Read-only: scans this PC with the default targets and prints what each category would offer. Never calls Clean.
    // Read-only: shows how the Windows settings page sees this PC (edition, admin, managed) and each switch's state. Writes nothing to the registry.
    internal static int WindowsReport()
    {
        var environment = new RegistryPolicyStore().Environment;
        Console.WriteLine($"Windows build {environment.Build}, edition {environment.Edition}, administrator {environment.Administrator}, managed {environment.Managed}, home {environment.Home}");
        var folder = Path.Combine(Path.GetTempPath(), "Hush.WindowsReport." + Guid.NewGuid().ToString("N"));
        try
        {
            var state = new WindowsSettingsService(new RegistryPolicyStore(), folder).Read();
            Console.WriteLine(state.Summary);
            foreach (var row in state.Settings) Console.WriteLine($"  {row.Definition.Id,-18} on={row.Enabled,-5} editable={row.CanEdit,-5} {row.Status}");
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
        return 0;
    }

    // Read-only: lists every default location Hush would visit on this PC (after discovery), by category, with its rule. Never scans or cleans.
    internal static int TargetReport()
    {
        var clock = Stopwatch.StartNew();
        var targets = CleanupEngine.DefaultTargets();
        Console.WriteLine($"{targets.Count} default locations built in {clock.ElapsedMilliseconds} ms");
        foreach (var group in targets.GroupBy(target => target.Category).OrderBy(group => group.Key))
        {
            Console.WriteLine($"[{group.Key}] {group.Count()}");
            foreach (var target in group)
                Console.WriteLine($"   {target.Group,-40} {target.MinimumAge.TotalDays,5:0.##}d {(target.Pattern ?? "*"),-26} {(target.LockFiles != null ? "lock " : "")}{(target.Processes != null ? "proc " : "")}{target.Root}");
        }
        return 0;
    }

    // Read-only: what the Services card would offer on this PC, with each service's start type and state. Changes nothing.
    internal static int ServiceReport()
    {
        var tuning = new ServiceTuning(new RealServiceSystem(), Path.Combine(Path.GetTempPath(), "Hush.ServiceReport." + Guid.NewGuid().ToString("N")));
        var offered = tuning.Scan();
        Console.WriteLine($"{offered.Count} services offered on this PC ({offered.Count(c => c.Running)} running now), from a list of {ServiceTuning.Catalog.Length}.");
        foreach (var c in offered) Console.WriteLine($"  {c.Level,-11} {c.Name,-34} {c.Status,-34} {c.Title}");
        return 0;
    }

    // Read-only: what the Scheduled tasks card would offer on this PC. Changes nothing (Scan only reads the task definition files).
    internal static int TaskReport()
    {
        var clock = Stopwatch.StartNew();
        var system = new RealTaskSystem();
        var tuning = new TaskTuning(system, Path.Combine(Path.GetTempPath(), "Hush.TaskReport." + Guid.NewGuid().ToString("N")));
        var offered = tuning.Scan();
        Console.WriteLine($"{offered.Count} scheduled tasks offered on this PC, from a list of {TaskTuning.Catalog.Length} (read in {clock.ElapsedMilliseconds} ms{(system.IsAdministrator ? "" : "; not administrator, so tasks in the top folder are hidden")}).");
        foreach (var c in offered) Console.WriteLine($"  {c.Level,-11} {c.Title,-40} {c.Status}");
        return 0;
    }

    // Read-only: the apps the Process card would list on this PC (never ends anything).
    internal static int ProcessReport()
    {
        var clock = Stopwatch.StartNew();
        var apps = new WindowsProcessManager().List();
        Console.WriteLine($"{apps.Count} apps with a window, {Sizes.Format(apps.Sum(app => app.MemoryBytes))} together, listed in {clock.ElapsedMilliseconds} ms");
        foreach (var app in apps) Console.WriteLine($"   {app.Name,-34} {Sizes.Format(app.MemoryBytes),10}  {app.ProcessCount,3} processes  {app.Path}");
        return 0;
    }

    // Read-only: what the Info card's Temporary files meter and each Temporary files limit find on this PC right now.
    internal static int MeterReport()
    {
        var controller = new SmartBoostController(new CleanupEngine());
        var reading = controller.Measure(CancellationToken.None);
        Console.WriteLine($"Meter: {Sizes.Format(reading.Bytes)} in {reading.Files:N0} files ready (Temp category, any age)");
        return 0;
    }

    internal static int ScanReport()
    {
        var engine = new CleanupEngine();
        var clock = Stopwatch.StartNew();
        var result = engine.Scan(null, CancellationToken.None);
        clock.Stop();
        Console.WriteLine($"Read-only scan of this PC: {clock.Elapsed.TotalMilliseconds:N0} ms, {result.Items.Count:N0} items, {Sizes.Format(result.Items.Sum(item => item.Bytes))}. Inaccessible {result.Inaccessible:N0}, busy caches {result.BusyCaches:N0}.");
        foreach (var category in ReviewCategory.Definitions)
        {
            var items = result.Items.Where(item => ReviewCategory.CategoryFor(item) == category.Id).ToList();
            Console.WriteLine($"  {category.Title,-28} {category.Safety,-8} {items.Count,7:N0} items  {Sizes.Format(items.Sum(item => item.Bytes)),10}  default-selected {items.Count(item => item.Selected):N0}");
            foreach (var group in items.GroupBy(item => item.Group).OrderByDescending(group => group.Sum(item => item.Bytes)).Take(4))
                Console.WriteLine($"      {group.Key,-34} {group.Count(),7:N0} items  {Sizes.Format(group.Sum(item => item.Bytes)),10}");
        }
        return 0;
    }

    internal static int Run()
    {
        string testsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string fixture = Path.Combine(testsDirectory, ".fixtures-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "bench");
        try
        {
            Directory.CreateDirectory(root);
            var old = DateTime.UtcNow.AddDays(-10);
            int oldFiles = 0, freshFiles = 0;
            for (int a = 0; a < 20; a++)
                for (int b = 0; b < 10; b++)
                {
                    string folder = Path.Combine(root, "d" + a, "s" + b, "leaf");
                    Directory.CreateDirectory(folder);
                    for (int i = 0; i < 100; i++)
                    {
                        string path = Path.Combine(folder, $"file{i}.tmp");
                        File.WriteAllText(path, "synthetic benchmark data");
                        if (i % 2 == 0) { File.SetCreationTimeUtc(path, old); File.SetLastWriteTimeUtc(path, old); oldFiles++; }
                        else freshFiles++;
                    }
                }
            Console.WriteLine($"Fixture: {oldFiles + freshFiles:N0} files ({oldFiles:N0} eligible) in {20 * 10 * 2 + 20:N0} directories.");
            // The process list exercises the per-file "is the browser running" check during cleanup.
            string[] processes = ["hush-benchmark-no-such-process"];
            var engine = new CleanupEngine([new(root, "Benchmark cache", CleanupKind.Cache, TimeSpan.FromDays(2), Processes: processes)],
                dataDirectory: Path.Combine(fixture, "audit"));
            double bestScan = double.MaxValue; long allocated = 0; ScanResult? scan = null;
            for (int run = 0; run < 4; run++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                long before = GC.GetAllocatedBytesForCurrentThread();
                var clock = Stopwatch.StartNew();
                scan = engine.Scan(null, CancellationToken.None);
                clock.Stop();
                if (run == 0) continue; // warm-up: JIT and file-system cache
                if (clock.Elapsed.TotalMilliseconds < bestScan) { bestScan = clock.Elapsed.TotalMilliseconds; allocated = GC.GetAllocatedBytesForCurrentThread() - before; }
            }
            Console.WriteLine($"Scan: {bestScan:N0} ms best of 3, {allocated / 1048576.0:N1} MB allocated, {scan!.Items.Count:N0} candidates.");
            var cleanClock = Stopwatch.StartNew();
            long cleanBefore = GC.GetAllocatedBytesForCurrentThread();
            var result = engine.Clean(scan.Items, null, CancellationToken.None);
            cleanClock.Stop();
            Console.WriteLine($"Clean: {cleanClock.Elapsed.TotalMilliseconds:N0} ms, {(GC.GetAllocatedBytesForCurrentThread() - cleanBefore) / 1048576.0:N1} MB allocated, deleted {result.Deleted:N0}, skipped {result.Skipped:N0}.");
            Console.WriteLine($"Working set: {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:N0} MB; managed heap {GC.GetTotalMemory(true) / 1048576.0:N1} MB.");
            return 0;
        }
        finally
        {
            string resolved = Path.GetFullPath(fixture);
            if (resolved.StartsWith(testsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith(".fixtures-") && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }
}
