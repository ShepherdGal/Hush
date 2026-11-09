using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hush;

// Engine checks for the streaming scan (overlap ownership, excluded folders, count-only and filtered scans), the Windows-tool step
// and the audit log, plus `--stress-scan`: a scan and clean of 250,000+ synthetic files and a virtualized review of that many rows.
internal static class ScaleChecks
{
    private sealed class FakeTools : ISystemTools
    {
        public readonly List<string> Runs = [];
        public bool Offered = true;
        public IReadOnlyList<SystemToolOffer> Probe(CancellationToken token) =>
            Offered ? [new("windows-old", "WindowsOld", "Previous Windows installation", "fixture", 5000)] : [];
        public void Run(string toolId, CancellationToken token) => Runs.Add(toolId);
    }

    internal static void Run(string fixture, Action<bool, string> check)
    {
        string Make(string folder, string name, int length = 10)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, new byte[length]);
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-30)); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
            return path;
        }
        var age = TimeSpan.FromDays(2);

        // Nested targets: the inner (more specific) target owns its files; the outer one never counts them a second time.
        string outer = Path.Combine(fixture, "nest"), inner = Path.Combine(outer, "deep");
        Make(outer, "o1.tmp"); Make(outer, "o2.tmp"); Make(inner, "i1.tmp", 20); Make(inner, "i2.tmp", 20); Make(Path.Combine(inner, "more"), "i3.tmp", 20);
        var nested = new CleanupEngine([new(outer, "Outer", CleanupKind.Temp, age, Category: "Temp"), new(inner, "Inner", CleanupKind.Cache, age, Category: "Cache")],
            dataDirectory: Path.Combine(fixture, "audit-scale")).Scan(null, CancellationToken.None);
        check(nested.Items.Count == 5 && nested.Items.Count(i => i.Group == "Outer") == 2 && nested.Items.Count(i => i.Group == "Inner") == 3
            && nested.Items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 5,
            "Nested locations: the specific location owns its files and nothing is counted twice");
        check(nested.FileCount == 5 && nested.TotalBytes == 2 * 10 + 3 * 20, "Scan totals match the listed files exactly");

        // Excluded folders are never entered.
        string cache = Path.Combine(fixture, "inet");
        Make(Path.Combine(cache, "IE"), "page.htm"); Make(Path.Combine(cache, "Content.Outlook"), "attachment.pdf"); Make(Path.Combine(cache, "IE", "Content.Word"), "draft.docx");
        var excluded = new CleanupEngine([new(cache, "Fixture", CleanupKind.Cache, age, Category: "InternetCache", ExcludeFolders: ["Content.Outlook", "Content.Word"])],
            dataDirectory: Path.Combine(fixture, "audit-scale")).Scan(null, CancellationToken.None);
        check(excluded.Items.Count == 1 && excluded.Items[0].Name == "page.htm", "Excluded folders (Outlook and Word attachment copies) are never listed");

        // Count-only and filtered scans: used by Quick Boost and background monitoring.
        string mix = Path.Combine(fixture, "mix");
        for (int i = 0; i < 40; i++) Make(Path.Combine(mix, "t"), $"f{i}.tmp", 100);
        for (int i = 0; i < 10; i++) Make(Path.Combine(mix, "c"), $"c{i}.bin", 1000);
        var mixEngine = new CleanupEngine([new(Path.Combine(mix, "t"), "Temp", CleanupKind.Temp, age, Category: "Temp"), new(Path.Combine(mix, "c"), "Cache", CleanupKind.Cache, age, Category: "BrowserChrome")],
            dataDirectory: Path.Combine(fixture, "audit-scale"));
        var full = mixEngine.Scan(null, CancellationToken.None);
        var counted = mixEngine.Scan(null, CancellationToken.None, new(CountOnly: true));
        check(counted.Items.Count == 0 && counted.FileCount == full.FileCount && counted.TotalBytes == full.TotalBytes && full.FileCount == 50, "A count-only scan measures exactly what a full scan lists without keeping items");
        var safeOnly = mixEngine.Scan(null, CancellationToken.None, new(Include: target => ReviewCategory.SafetyOf(ReviewCategory.CategoryFor(target.Kind, target.Group, target.Category)) == SafetyLevel.Safe));
        check(safeOnly.Items.Count == 40 && safeOnly.Items.All(i => i.Safety == SafetyLevel.Safe), "A Safe-only scan visits only Safe locations");
        var slow = new ProgressRecorder();
        mixEngine.Scan(slow, CancellationToken.None, new(ReportIntervalMs: 100000));
        check(slow.Reports.Count < 12, "Low-resource scans report progress far less often");

        // Windows tools (Disk Cleanup, DISM, clipboard) are opt-in, probed read-only, re-verified and run exactly once.
        var tools = new FakeTools();
        string empty = Path.Combine(fixture, "tools-empty"); Directory.CreateDirectory(empty);
        CleanupEngine ToolEngine(ISystemTools? service) => new([new(empty, "Fixture", CleanupKind.Temp, age)], dataDirectory: Path.Combine(fixture, "audit-tools"), systemTools: service);
        var toolScan = ToolEngine(tools).Scan(null, CancellationToken.None);
        var tool = toolScan.Items.Single();
        check(tool.Kind == CleanupKind.SystemTool && !tool.Selected && tool.Safety == SafetyLevel.Advanced && tool.ToolId == "windows-old" && tools.Runs.Count == 0,
            "A Windows tool is found read-only as an opt-in Advanced item");
        check(ToolEngine(null).Scan(null, CancellationToken.None).Items.Count == 0, "No Windows tool appears when none is available");
        tools.Offered = false;
        check(ToolEngine(tools).Clean([tool], null, CancellationToken.None).Skipped == 1 && tools.Runs.Count == 0, "A tool that Windows no longer offers is not started");
        tools.Offered = true;
        var forged = new CleanupItem { Path = "x", Root = "Windows tool", Group = "x", Kind = CleanupKind.SystemTool, ToolId = "format-c" };
        check(ToolEngine(tools).Clean([forged], null, CancellationToken.None).Skipped == 1 && tools.Runs.Count == 0, "A forged tool id is rejected before anything starts");
        var ran = ToolEngine(tools).Clean([tool], null, CancellationToken.None);
        check(ran.Deleted == 1 && tools.Runs.SequenceEqual(["windows-old"]), "An offered, reviewed tool runs exactly once");
        string line = File.ReadAllLines(ran.LogPath).Last();
        check(line.Contains("Previous Windows installation") && line.Contains("Deleted"), "Tool runs are audited");
        var summary = new CleanupResult(0, 5, 0, false, "x", SkipReasons: new Dictionary<string, int> { ["In use by a program right now"] = 4, ["Changed since the scan"] = 1 });
        check(summary.SkipSummary == "4 in use by a program right now · 1 changed since the scan", "A clean says why files were skipped, in plain words with counts");

        // Audit log: the intent of a batch is on disk before its results, and results carry the measured outcome.
        string logRoot = Path.Combine(fixture, "audit-order");
        var paths = Enumerable.Range(0, 5).Select(i => Make(logRoot, $"a{i}.tmp")).ToArray();
        var auditEngine = new CleanupEngine([new(logRoot, "Fixture", CleanupKind.Temp, age)], dataDirectory: Path.Combine(fixture, "audit-order-data"));
        var auditResult = auditEngine.Clean(auditEngine.Scan(null, CancellationToken.None).Items, null, CancellationToken.None);
        var lines = File.ReadAllLines(auditResult.LogPath);
        int lastRequested = Array.FindLastIndex(lines, l => l.Contains("\"requested\"")), firstDeleted = Array.FindIndex(lines, l => l.Contains("\"Deleted\""));
        check(auditResult.Deleted == 5 && lines.Length == 10 && lastRequested < firstDeleted && paths.All(p => !File.Exists(p)),
            "Cleanup logs every intent before its results and deletes exactly the reviewed files");
        check(auditResult.FreeSpaceBefore > 0 && auditResult.FreeSpaceAfter > 0, "A clean records the disk's free space before and after");

        // Throttled progress: a clean never floods the UI thread with one report per file.
        string many = Path.Combine(fixture, "progress");
        for (int i = 0; i < 300; i++) Make(many, $"p{i}.tmp", 1);
        var manyEngine = new CleanupEngine([new(many, "Fixture", CleanupKind.Temp, age)], dataDirectory: Path.Combine(fixture, "audit-progress"));
        int reports = 0, last = 0;
        var manyResult = manyEngine.Clean(manyEngine.Scan(null, CancellationToken.None).Items, new Sync(count => { reports++; last = count; }), CancellationToken.None);
        check(manyResult.Deleted == 300 && last == 300 && reports < 300, $"Clean progress is throttled and still ends exactly at the total ({reports} reports for 300 files)");
        check(Directory.Exists(many), "Cleanup never removes the target folder itself");

        // Browser history and cookies are Settings-only: a normal scan never lists them, an on-demand scan lists them unselected.
        string cookies = Path.Combine(fixture, "settings-only"); Make(cookies, "Cookies", 50); Make(Path.Combine(fixture, "settings-only-temp"), "t.tmp", 50);
        var settingsEngine = new CleanupEngine([new(cookies, "Chrome cookies", CleanupKind.Junk, TimeSpan.Zero, "Cookies", Category: "BrowserCookies"),
            new(Path.Combine(fixture, "settings-only-temp"), "Temp", CleanupKind.Temp, age, Category: "Temp")], dataDirectory: Path.Combine(fixture, "audit-settings-only"));
        var normalScan = settingsEngine.Scan(null, CancellationToken.None);
        var onDemand = settingsEngine.Scan(null, CancellationToken.None, new(Include: target => target.Category == "BrowserCookies"));
        check(normalScan.Items.Count == 1 && normalScan.Items[0].CategoryId == "Temp" && onDemand.Items.Count == 1 && onDemand.Items[0].CategoryId == "BrowserCookies" && !onDemand.Items[0].Selected,
            "A normal scan never lists browser history or cookies; Settings can scan them on demand, unselected");
        check(ReviewCategory.SettingsOnly.SetEquals(["BrowserHistory", "BrowserCookies"]), "Only browser history and cookies are Settings-only");

        // PC Manager style additions: Defender diagnostics, browser logs, Firefox updates and NVIDIA logs stay narrow.
        var defaults = CleanupEngine.DefaultTargets();
        string defenderRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender");
        check(defaults.Where(t => t.Root.StartsWith(defenderRoot, StringComparison.OrdinalIgnoreCase)).All(t => t.Category == "Defender" && t.Root.EndsWith(@"Windows Defender\Support") && t.DeletableOnly && !t.Recursive
                && (t.Pattern ?? "").Split(';').All(p => (p.StartsWith("MPLog") || p.StartsWith("MpCmdRun") || p.StartsWith("MpWppTracing") || p.StartsWith("MpDetection")) && (p.EndsWith(".log") || p.EndsWith(".etl")))),
            "Defender cleaning is limited to its diagnostic support logs; protection history, quarantine, scans and definitions are never targeted");
        check(defaults.Where(t => t.Category == "BrowserLogs").All(t => t.Pattern is "*.dmp;*.meta" or "*.pma" && t.Processes != null) && defaults.Where(t => t.Category == "BrowserUpdates").All(t => t.Pattern == "*.mar;*.log;*.bak" && t.Processes != null),
            "Browser log and update rules are narrow file types and need the browser closed");
        check(ReviewCategory.SafetyOf("Defender") == SafetyLevel.Review && ReviewCategory.SafetyOf("BrowserLogs") == SafetyLevel.Review && ReviewCategory.SafetyOf("BrowserUpdates") == SafetyLevel.Review, "The new categories are Review (opt-in)");
        var traces = new ReviewCategory("RecentAccess", "Windows Recent Access", SafetyLevel.Review, "fixture");
        traces.Replace([new CleanupItem { Path = @"C:\r\a.lnk", Root = @"C:\r", Group = "x", Bytes = 600 }, new CleanupItem { Path = @"C:\r\b.lnk", Root = @"C:\r", Group = "x", Bytes = 700 }]);
        check(traces.TotalLabel == "2 Items", "Usage-trace categories count items instead of bytes, like Microsoft PC Manager");

        // Memory release: only the test process is ever trimmed here; the real call is never pointed at the person's programs by a test.
        var trimmedSelf = new WindowsMemoryBooster(process => process.Id == Environment.ProcessId).Release(CancellationToken.None);
        check(trimmedSelf.ProgramsTrimmed == 1 && trimmedSelf.UsedBefore > 0 && trimmedSelf.UsedAfter > 0, "Memory release works through the Windows API and honors its filter (only the test process was trimmed)");
        check(new MemoryBoostResult(1, 100, 60).Released == 40 && new MemoryBoostResult(1, 100, 130).Released == 0, "Released memory is the measured drop and never negative");
    }

    private sealed class Sync(Action<int> report) : IProgress<int> { public void Report(int value) => report(value); }

    // `Hush.Tests.exe --stress-scan [files]`: synthetic only, in a unique .fixtures-* folder that is removed afterwards.
    internal static int Stress(int files)
    {
        string testsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string fixture = Path.Combine(testsDirectory, ".fixtures-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "stress");
        try
        {
            var clock = Stopwatch.StartNew();
            const int folders = 400;
            for (int d = 0; d < folders; d++) Directory.CreateDirectory(Path.Combine(root, "d" + (d % 20), "s" + d));
            Parallel.For(0, files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                string path = Path.Combine(root, "d" + ((i % folders) % 20), "s" + (i % folders), $"file-{i}.tmp");
                File.WriteAllBytes(path, new byte[16]);
            });
            Console.WriteLine($"Created {files:N0} files in {folders} folders in {clock.Elapsed.TotalSeconds:N1} s.");
            var engine = new CleanupEngine([new(root, "Stress cache", CleanupKind.Cache, TimeSpan.Zero, Category: "Cache")], dataDirectory: Path.Combine(fixture, "audit"));

            GC.Collect(); GC.WaitForPendingFinalizers(); long heapBefore = GC.GetTotalMemory(true);
            var progress = new ProgressRecorder();
            clock.Restart();
            var scan = engine.Scan(progress, CancellationToken.None);
            double scanSeconds = clock.Elapsed.TotalSeconds;
            GC.Collect(); long heapAfter = GC.GetTotalMemory(true);
            Require(scan.Items.Count == files && scan.FileCount == files && scan.TotalBytes == 16L * files && !scan.Stopped, $"Scan returns every file exactly once ({scan.Items.Count:N0} of {files:N0})");
            Require(scan.Items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == files, "No file is listed twice");
            Require(files > 150000, "The stress run is above the old 150,000-file limit");
            Require(progress.Reports.Zip(progress.Reports.Skip(1)).All(p => p.First.Fraction <= p.Second.Fraction && p.First.FilesFound <= p.Second.FilesFound) && progress.Reports[^1].Fraction == 1,
                "Progress and live counts stay monotonic and end at 100%");
            Console.WriteLine($"Scan: {scanSeconds:N1} s ({files / scanSeconds:N0} files/s), retained {(heapAfter - heapBefore) / 1048576.0:N0} MB = {(heapAfter - heapBefore) / (double)files:N0} bytes per file, {progress.Reports.Count:N0} progress reports.");

            GC.Collect(); heapBefore = GC.GetTotalMemory(true);
            clock.Restart();
            var counted = engine.Scan(null, CancellationToken.None, new(CountOnly: true));
            Require(counted.Items.Count == 0 && counted.FileCount == files && counted.TotalBytes == 16L * files, "A count-only scan (background monitoring) is exact and keeps no items");
            Console.WriteLine($"Count-only scan: {clock.Elapsed.TotalSeconds:N1} s, no items retained.");

            using (var cancel = new CancellationTokenSource())
            {
                var halfway = new ProgressRecorder(p => { if (p.FilesFound > files / 3) cancel.Cancel(); });
                clock.Restart();
                var stopped = engine.Scan(halfway, cancel.Token);
                Require(stopped.Stopped && stopped.Items.Count > 0 && stopped.Items.Count < files, $"Cancellation stops a huge scan quickly with partial results ({stopped.Items.Count:N0} files, {clock.ElapsedMilliseconds} ms)");
            }

            // UI: the review list of this many rows must stay virtualized and responsive.
            VerifyVirtualizedReview(scan.Items);

            var cleanClock = Stopwatch.StartNew();
            var clean = engine.Clean(scan.Items, null, CancellationToken.None);
            Require(clean.Deleted == files && clean.Skipped == 0 && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).FirstOrDefault() == null, "Cleaning removes every listed file and nothing else");
            Require(File.ReadLines(clean.LogPath).Count() == 2 * files, "The audit log holds one intent and one result per file");
            Console.WriteLine($"Clean: {cleanClock.Elapsed.TotalSeconds:N1} s ({files / cleanClock.Elapsed.TotalSeconds:N0} files/s), disk reports {Sizes.Format(clean.MeasuredFreed)} more free.");
            Console.WriteLine($"Working set at the end: {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:N0} MB.");
            Console.WriteLine($"\nPASS: stress scan of {files:N0} files.");
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    private static void VerifyVirtualizedReview(IReadOnlyList<CleanupItem> items)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            App? app = null; MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                app = new App(); app.InitializeComponent();
                string preferences = Path.Combine(Path.GetTempPath(), "hush-stress-" + Guid.NewGuid().ToString("N") + ".json");
                window = new MainWindow(new CleanupEngine([], dataDirectory: Path.Combine(Path.GetTempPath(), "hush-stress-data")), preferencesPath: preferences);
                var fake = items.Select(i => new CleanupItem { Folder = i.Folder, FileName = i.FileName, Root = i.Root, Group = i.Group, Kind = i.Kind, CategoryId = i.CategoryId, Bytes = i.Bytes, ModifiedTicks = i.ModifiedTicks, CreatedTicks = i.CreatedTicks, Selected = false }).ToList();
                var clock = Stopwatch.StartNew();
                window.LoadReviewItems(fake);
                double loadMs = clock.Elapsed.TotalMilliseconds;
                window.ShowReviewPage(true);
                var category = window.Categories.Single(c => c.Id == "Cache");
                category.Expanded = true;
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(920, 820)); root.Arrange(new Rect(0, 0, 920, 820)); root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                root.UpdateLayout();
                int realized = Descendants<ListBoxItem>(root).Count();
                Require(category.Files.Count == fake.Count && realized > 0 && realized < 200, $"The review list is virtualized: {realized} rows exist for {fake.Count:N0} files (loaded in {loadMs:N0} ms)");
                clock.Restart();
                category.Selected = true;
                Require(category.SelectedCount == fake.Count && clock.ElapsedMilliseconds < 3000, $"Selecting {fake.Count:N0} files is instant ({clock.ElapsedMilliseconds} ms)");
                clock.Restart();
                fake[0].Selected = false;
                for (int i = 0; i < 1000; i++) fake[i].Selected = !fake[i].Selected;
                // Item 0 went off then on again; items 1..999 went off: 999 fewer than all.
                Require(clock.ElapsedMilliseconds < 1000 && category.SelectedCount == fake.Count - 999 && category.Selected == null, $"1,000 single toggles stay O(1) each and keep exact counts ({clock.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex) { failure = ex; }
            finally { try { window?.Close(); app?.Shutdown(); } catch { } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(3))) throw new TimeoutException("The virtualized review check did not finish.");
        if (failure != null) throw new Exception("Virtualized review failed", failure);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
