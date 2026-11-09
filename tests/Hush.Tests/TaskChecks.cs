using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hush;

// Settings > Scheduled tasks (0.9): the curated list, leftovers whose program is gone, save-first changes, verification, Undo and the card. A fake Windows is
// used throughout: no real task is ever changed by a test. The only real calls are read-only (RealTaskSystem.List and FileExists on fixture files).
internal static class TaskChecks
{
    internal sealed class FakeTasks : ITaskSystem
    {
        internal List<TaskInfo> All = [];
        internal bool Admin = true;
        internal HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Refuse = new(StringComparer.OrdinalIgnoreCase), WontStick = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Calls = [];
        internal Func<string, bool>? OnBeforeChange;
        internal bool HistoryExistedBeforeFirstChange;
        internal int ListCalls, ReadCalls;
        public bool IsAdministrator => Admin;
        public IReadOnlyList<TaskInfo> List() { ListCalls++; return All.ToList(); }
        public TaskInfo? Read(string path) { ReadCalls++; return All.FirstOrDefault(task => task.Path.Equals(path, StringComparison.OrdinalIgnoreCase)); }
        public bool FileExists(string path) => !Missing.Contains(path);
        public bool TrySetEnabled(string path, bool enabled, out string error)
        {
            error = "";
            if (OnBeforeChange != null && Calls.Count == 0) HistoryExistedBeforeFirstChange = OnBeforeChange(path);
            Calls.Add($"{(enabled ? "enable" : "disable")} {path}");
            if (Refuse.Contains(path)) { error = "Windows refused the change"; return false; }
            if (WontStick.Contains(path)) return true; // says yes but nothing changes: the read-back must catch it
            int index = All.FindIndex(task => task.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (index < 0) { error = "not found"; return false; }
            All[index] = All[index] with { Enabled = enabled };
            return true;
        }
    }

    private const string Win = @"\Microsoft\Windows\";
    internal const string Appraiser = Win + @"Application Experience\Microsoft Compatibility Appraiser";
    internal const string ProgramData = Win + @"Application Experience\ProgramDataUpdater";
    internal const string Queue = Win + @"Windows Error Reporting\QueueReporting";
    internal const string Maps = Win + @"Maps\MapsUpdateTask";
    internal const string Google = @"\GoogleUpdateTaskMachineCore{6A2C1B4E-1111-2222-3333-444455556666}";
    internal const string Leftover = @"\OldVendor Updater";
    internal static readonly string Gone = @"C:\Program Files\Removed Vendor\updater.exe";

    internal static FakeTasks Pc()
    {
        string userGone = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Gone Vendor\gone.exe");
        return new()
        {
            Missing = new(StringComparer.OrdinalIgnoreCase) { Gone, @"C:\Program Files\Security Vendor\gone.exe", @"C:\Program Files\Other Vendor\gone.exe", userGone, @"C:\Program Files\Hush\Hush.exe" },
            All =
            [
                new(Appraiser, true, @"%windir%\system32\compattelrunner.exe", false, false),
                new(ProgramData, true, @"%windir%\system32\compattelrunner.exe", true, false),
                new(Queue, true, @"%windir%\system32\wermgr.exe", false, true),
                new(Maps, false, @"%windir%\System32\MapsUpdateTask.exe", false, false),                                                  // already off: not offered
                new(Win + @"UpdateOrchestrator\Schedule Scan", true, @"%systemroot%\system32\usoclient.exe", false, false),             // never
                new(Win + @"Windows Defender\Windows Defender Scheduled Scan", true, @"C:\ProgramData\Microsoft\Windows Defender\MpCmdRun.exe", false, false), // never
                new(Win + @"Some Feature\Helper", true, @"C:\Program Files\Other Vendor\gone.exe", false, false),                       // under \Microsoft\Windows\, not in the catalog: never, even with a missing file
                new(@"\" + AdminMode.TaskName, true, @"C:\Program Files\Hush\Hush.exe", true, false),                                // Hush's own task: never
                new(Google, true, @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", false, false),
                new(@"\GoogleUpdateTaskMachineUA{6A2C1B4E-1111-2222-3333-444455556666}", false, @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", false, false), // off already
                new(@"\Sub\GoogleUpdateTaskMachineCoreX", true, @"C:\Program Files\Vendor\agent.exe", false, false),                // a pattern never crosses a folder
                new(Leftover, true, "\"" + Gone + "\"", true, false),                                                                // leftover: file is gone
                new(@"\Installed Vendor Task", true, @"C:\Program Files\Vendor\agent.exe", false, false),                          // its file exists
                new(@"\Windows Gone", true, @"C:\Windows\missing-under-windows.exe", false, false),                                // under the Windows folder: never a leftover
                new(@"\User Var Task", true, @"%LOCALAPPDATA%\Gone Vendor\gone.exe", false, false),                                // per-user variable: cannot tell, never a leftover
                new(@"\Security Vendor Task", true, @"C:\Program Files\Security Vendor\gone.exe", false, false),                    // "Security" in the path: never
                new(@"\Other Vendor\Disabled Leftover", false, @"C:\Program Files\Other Vendor\gone.exe", false, false),           // already off: not offered
            ]
        };
    }

    // Every catalog entry present and enabled (wildcards get a suffix) plus three leftovers: the whole list at once.
    internal static FakeTasks BigPc()
    {
        var pc = new FakeTasks();
        foreach (var entry in TaskTuning.Catalog)
            pc.All.Add(new(entry.Pattern.EndsWith('*') ? entry.Pattern[..^1] + "{0A1B}" : entry.Pattern, true, @"C:\Program Files\Vendor\app.exe", false, false));
        for (int i = 1; i <= 3; i++)
        {
            string file = $@"C:\Program Files\Removed {i}\updater.exe";
            pc.Missing.Add(file);
            pc.All.Add(new($@"\Removed {i} Updater", true, file, i == 1, false));
        }
        return pc;
    }

    internal static void RunEngine(string fixture, Action<bool, string> check)
    {
        // ---- the curated list itself ----
        var patterns = TaskTuning.Catalog.Select(item => item.Pattern).ToList();
        check(patterns.Distinct(StringComparer.OrdinalIgnoreCase).Count() == patterns.Count && patterns.Count >= 25, $"The scheduled tasks list has {patterns.Count} unique, curated entries");
        check(TaskTuning.Catalog.All(item => !TaskTuning.IsNeverTouch(item.Pattern.TrimEnd('*'))), "No Windows Update, security, restore, backup or system task is on the scheduled tasks list");
        check(TaskTuning.Catalog.All(item => item.Title.Length > 0 && item.Why.Length is > 20 and < 200 && item.Topic.Length > 0 && TaskTuning.TopicRank(item.Topic) < 4 && item.Level != TaskLevel.LeftBehind),
            "Every scheduled task entry has a plain name, a short plain explanation and one of the fixed topics");
        check(TaskTuning.Catalog.Where(item => item.Pattern.StartsWith(Win, StringComparison.OrdinalIgnoreCase)).All(item => !item.Pattern.Contains('*')),
            "Under \\Microsoft\\Windows\\ only exact task paths are listed (no wildcard)");
        check(new[] { Win + @"UpdateOrchestrator\Reboot", Win + @"WindowsUpdate\Scheduled Start", Win + @"WaaSMedic\PerformRemediation", Win + @"SystemRestore\SR", Win + @"Windows Defender\Windows Defender Cleanup",
                Win + @"Time Synchronization\SynchronizeTime", Win + @"BitLocker\BitLocker Encrypt All Drives", Win + @"Defrag\ScheduledDefrag", Win + "SecurityHealth", @"\Vendor Antivirus Update", @"\" + AdminMode.TaskName }
            .All(TaskTuning.IsNeverTouch), "Windows Update, Defender, restore, time, BitLocker, defrag, security and antivirus tasks and Hush's own task are never touched");
        var topics = new[] { TaskTuning.TelemetryTopic, TaskTuning.ExtrasTopic, TaskTuning.UpdatersTopic, TaskTuning.LeftBehindTopic };
        check(topics.Select(TaskTuning.TopicRank).SequenceEqual([0, 1, 2, 3]) && topics.All(topic => ServiceTuning.TopicRank(topic) >= 998 && topic != ServiceTuning.LeftBehindTopic),
            "The task topics are in a fixed order (left-behind last) and their names differ from the Services topics (open headings are remembered by name)");
        check(TaskTuning.CatalogEntry(@"\NvTmRep_CrashReport1_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}")?.Title == "NVIDIA crash and telemetry reports" && TaskTuning.CatalogEntry(@"\NvTmRep_") == null
            && TaskTuning.CatalogEntry(@"\Sub\NvTmRep_x") == null && TaskTuning.CatalogEntry(@"\MOZILLA\Firefox Background Update 308046B0AF4A39CB") != null,
            "A wildcard entry matches a task name with any suffix in its own folder only");

        // ---- what is offered ----
        string directory = Path.Combine(fixture, "tasks-engine");
        var pc = Pc();
        var tuning = new TaskTuning(pc, directory);
        var offered = tuning.Scan();
        check(offered.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(new[] { Leftover, Google, Appraiser, ProgramData, Queue }.OrderBy(p => p, StringComparer.Ordinal)),
            "Only present and enabled catalog tasks plus a leftover whose program is gone are offered (never Windows Update, Defender, Hush's own task, other \\Microsoft\\Windows\\ tasks, existing programs, the Windows folder, per-user paths or disabled tasks)");
        check(pc.ListCalls == 1 && pc.Calls.Count == 0 && !File.Exists(Path.Combine(directory, "tasks-changed.json")), "Scanning reads the task list once and is read-only: nothing is changed or saved");
        var left = offered.Single(c => c.Path == Leftover); var program = offered.Single(c => c.Path == ProgramData);
        check(left.Level == TaskLevel.LeftBehind && left.Topic == TaskTuning.LeftBehindTopic && left.Title == "OldVendor Updater" && left.Why.Contains("updater.exe") && left.Status == "Runs at sign-in · " + Leftover
            && program.Level == TaskLevel.Recommended && program.Status.StartsWith("Runs at sign-in · ") && offered.Single(c => c.Path == Queue).Status.StartsWith("Runs when Windows starts"),
            "A leftover says which file is missing; each task shows where it lives and when it runs");
        check(offered.Select(c => TaskTuning.TopicRank(c.Topic)).SequenceEqual(offered.Select(c => TaskTuning.TopicRank(c.Topic)).Order()) && offered.Last().Path == Leftover,
            "Offered tasks are in topic order, left-behind last");

        // ---- changing needs administrator rights ----
        pc.Admin = false;
        bool refused = false;
        try { tuning.Apply([ProgramData]); } catch (IOException ex) { refused = ex.Message.Contains("Administrator"); }
        check(refused && pc.Calls.Count == 0 && pc.Read(ProgramData)!.Enabled && !tuning.HasChanges, "Without administrator rights no scheduled task is changed");
        pc.Admin = true;

        // ---- apply: only offered paths, originals saved BEFORE the first change, verified ----
        pc.OnBeforeChange = _ => File.Exists(Path.Combine(directory, "tasks-changed.json"));
        pc.ListCalls = 0;
        var report = tuning.Apply([ProgramData, Google, Leftover, Maps, Win + @"UpdateOrchestrator\Schedule Scan", @"\" + AdminMode.TaskName, @"\Installed Vendor Task", @"\No Such Task", Win + @"Some Feature\Helper"]);
        check(pc.HistoryExistedBeforeFirstChange, "The original state is saved to disk before the first task changes");
        check(report.Changed.OrderBy(n => n).SequenceEqual(["Google Update (daily)", "OldVendor Updater", "Program data collector"]) && report.Failed.Count == 0
            && !pc.Read(ProgramData)!.Enabled && !pc.Read(Google)!.Enabled && !pc.Read(Leftover)!.Enabled && pc.Read(Appraiser)!.Enabled
            && pc.Read(Win + @"UpdateOrchestrator\Schedule Scan")!.Enabled && pc.Read(@"\" + AdminMode.TaskName)!.Enabled && pc.Read(@"\Installed Vendor Task")!.Enabled && pc.Read(Win + @"Some Feature\Helper")!.Enabled
            && pc.Calls.Count == 3,
            "Only what was offered and chosen is turned off; protected, unknown, Hush's own and not-chosen tasks are ignored");
        check(pc.ListCalls == 1, $"Applying reads the task list once ({pc.ListCalls})");
        check(tuning.HasChanges && tuning.ChangedCount == 3 && report.Text(false).Contains("3 scheduled tasks turned off and checked"), "Hush remembers what it changed and reports it plainly");
        check(File.ReadAllText(Path.Combine(directory, "..", "Logs", "tasks.jsonl")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3, "Each task change is logged");
        check(tuning.Scan().Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(new[] { Appraiser, Queue }.OrderBy(p => p, StringComparer.Ordinal)), "Turned-off tasks are no longer offered");

        // ---- Undo ----
        pc.Calls.Clear();
        var undo = tuning.Undo();
        check(undo.Changed.Count == 3 && undo.Failed.Count == 0 && pc.Read(ProgramData)!.Enabled && pc.Read(Google)!.Enabled && pc.Read(Leftover)!.Enabled
            && !tuning.HasChanges && !File.Exists(Path.Combine(directory, "tasks-changed.json")) && undo.Text(true).Contains("turned back on"),
            "Undo turns every task back on, checks it and closes the history");

        // ---- Windows refuses one change, another does not stick ----
        pc = Pc(); pc.Refuse.Add(Google); pc.WontStick.Add(Queue);
        tuning = new TaskTuning(pc, Path.Combine(fixture, "tasks-engine-2"));
        report = tuning.Apply([ProgramData, Google, Queue]);
        check(report.Changed.SequenceEqual(["Program data collector"]) && report.Failed.Count == 2 && report.Failed.Any(f => f.Contains("Google Update") && f.Contains("refused")) && report.Failed.Any(f => f.Contains("Queued error reports"))
            && tuning.ChangedCount == 1 && pc.Read(Google)!.Enabled && report.Text(false).Contains("Could not be changed"),
            "A refused change and one that does not stick are reported and not remembered");
        tuning.Undo();
        check(pc.Read(ProgramData)!.Enabled && !tuning.HasChanges, "Undo works after a partial change too");

        // ---- a damaged or hostile history can never aim Hush at another task ----
        string hostile = Path.Combine(fixture, "tasks-engine-3"); Directory.CreateDirectory(hostile);
        string Json(params (string Path, bool Enabled)[] tasks) => System.Text.Json.JsonSerializer.Serialize(new { Schema = 1, Tasks = tasks.Select(t => new { t.Path, t.Enabled }) });
        pc = Pc();
        pc.All[pc.All.FindIndex(t => t.Path == Leftover)] = pc.Read(Leftover)! with { Enabled = false };                                   // Hush had turned it off
        pc.All[pc.All.FindIndex(t => t.Path == @"\Installed Vendor Task")] = pc.Read(@"\Installed Vendor Task")! with { Enabled = false };   // the person turned it off themselves
        pc.All[pc.All.FindIndex(t => t.Path == Win + @"UpdateOrchestrator\Schedule Scan")] = pc.Read(Win + @"UpdateOrchestrator\Schedule Scan")! with { Enabled = false };
        File.WriteAllText(Path.Combine(hostile, "tasks-changed.json"), Json((Leftover, true), (@"\Installed Vendor Task", true), (@"\No Such Task", true),
            (Win + @"UpdateOrchestrator\Schedule Scan", true), (@"\" + AdminMode.TaskName, true), (ProgramData, false)));
        tuning = new TaskTuning(pc, hostile);
        var forged = tuning.Undo();
        check(pc.Calls.SequenceEqual(["enable " + Leftover]) && forged.Changed.SequenceEqual(["OldVendor Updater"]) && !pc.Read(@"\Installed Vendor Task")!.Enabled
            && !pc.Read(Win + @"UpdateOrchestrator\Schedule Scan")!.Enabled && pc.Read(ProgramData)!.Enabled
            && forged.Failed.Count == 2 && forged.Failed.Any(text => text.Contains("Installed Vendor Task")) && forged.Failed.Any(text => text.Contains("No Such Task")) && !tuning.HasChanges,
            "Undo only touches tasks Hush could have changed (a catalog task or a leftover whose file is still missing); a forged entry, a never-touch task or an impossible state is left alone and reported");
        pc.Calls.Clear();
        File.WriteAllText(Path.Combine(hostile, "tasks-changed.json"), "not json at all");
        bool damaged = false;
        try { tuning.Undo(); } catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { damaged = true; }
        check(damaged && !tuning.HasChanges && pc.Calls.Count == 0, "A damaged scheduled tasks history is refused and changes nothing");
        File.WriteAllText(Path.Combine(hostile, "tasks-changed.json"), new string(' ', 70000) + "{}");
        damaged = false;
        try { tuning.Undo(); } catch (InvalidDataException) { damaged = true; }
        check(damaged && pc.Calls.Count == 0, "An oversized scheduled tasks history is refused");
        File.Delete(Path.Combine(hostile, "tasks-changed.json"));

        // ---- many changes: one list read, one read-back per change ----
        var many = BigPc();
        var manyTuning = new TaskTuning(many, Path.Combine(fixture, "tasks-engine-many"));
        var manyReport = manyTuning.Apply(many.All.Select(task => task.Path).ToList());
        check(manyReport.Changed.Count == many.All.Count && many.ListCalls == 1 && many.ReadCalls <= manyReport.Changed.Count + 1,
            $"Turning off {manyReport.Changed.Count} tasks lists the tasks once ({many.ListCalls}) and reads each changed one only by path ({many.ReadCalls})");

        // ---- the real Windows: read-only only ----
        var real = new RealTaskSystem();
        var realTasks = real.List();
        check(realTasks.Count > 20 && realTasks.All(task => task.Path.StartsWith('\\')), $"The real scheduled task list can be read ({realTasks.Count} tasks; read-only, nothing changed)");
        string present = Path.Combine(fixture, "tasks-files", "present.exe"); Directory.CreateDirectory(Path.GetDirectoryName(present)!); File.WriteAllText(present, "x");
        string drive = Enumerable.Range('D', 23).Select(letter => $"{(char)letter}:\\").FirstOrDefault(root => !Directory.Exists(root)) ?? "Z:\\";
        check(real.FileExists(present) && !real.FileExists(Path.Combine(fixture, "tasks-files", "gone.exe")) && !real.FileExists(Path.Combine(fixture, "tasks-files", "no-folder", "gone.exe"))
            && real.FileExists(drive + @"Program Files\Vendor\gone.exe"),
            "A program file counts as missing only when it certainly is: a file on an absent drive is never called missing");
    }

    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        static IEnumerable<System.Windows.Controls.Primitives.ToggleButton> TopicHeaders(DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Controls.Primitives.ToggleButton { Uid: "topic" } header) yield return header;
                foreach (var inner in TopicHeaders(child)) yield return inner;
            }
        }
        // ---- a long list: sub-categories, all closed, closing the card forgets ----
        var bigPc = BigPc();
        var bigWindow = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "tasks-big-preferences.json"), taskSystem: bigPc) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            bigWindow.Show();
            var root = (FrameworkElement)bigWindow.Content;
            ((Button)bigWindow.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiLayoutChecks.PumpUntil(() => !bigWindow.IsPageTransitioning); UiLayoutChecks.Wait(300);
            check(bigWindow.TaskChoices.Count == 0 && bigPc.ListCalls == 0, "Opening Settings does not read the scheduled tasks: the list is read when its card is opened");
            var card = (Expander)bigWindow.FindName("SettingsTasksSection");
            card.IsExpanded = true;
            UiLayoutChecks.PumpUntil(() => bigWindow.TaskChoices.Count > 30);
            UiLayoutChecks.Wait(300);
            var headers = TopicHeaders((DependencyObject)bigWindow.FindName("TasksList")).ToList();
            check(headers.Count >= 4 && headers.All(header => header.IsChecked != true), "Opening the Scheduled tasks card opens nothing inside it: every sub-category starts closed");
            var ranks = bigWindow.TaskChoices.Select(choice => TaskTuning.TopicRank(choice.Candidate.Topic)).ToList();
            check(ranks.SequenceEqual(ranks.Order()) && bigWindow.TaskChoices.All(choice => choice.On && choice.Topic.Length > 0) && bigWindow.TaskChoices.Last().Candidate.Level == TaskLevel.LeftBehind,
                "The tasks are in order (topics in their fixed order, left-behind last) and every switch starts ON, because every task is enabled right now");
            var view = (System.Windows.Data.ListCollectionView)((ItemsControl)bigWindow.FindName("TasksList")).ItemsSource;
            check(view.Groups.OfType<System.Windows.Data.CollectionViewGroup>().All(group => group.ItemCount <= ListGrouping.GroupLimit), $"The Scheduled tasks card shows {bigWindow.TaskChoices.Count} tasks under {view.Groups.Count} sub-category headings (none longer than {ListGrouping.GroupLimit})");
            headers[0].IsChecked = true; UiLayoutChecks.Wait(400);
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots)
            {
                Directory.CreateDirectory(shots);
                UiLayoutChecks.Layout(root, new Size(920, 820)); card.BringIntoView(); UiLayoutChecks.Wait(200); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "tasks-card-dark-920.png"));
                headers[0].IsChecked = false; headers[^1].IsChecked = true; UiLayoutChecks.Wait(400);
                bigWindow.TaskChoices.First(choice => choice.Candidate.Level == TaskLevel.LeftBehind).On = false; UiLayoutChecks.Wait(100);
                UiLayoutChecks.Layout(root, new Size(920, 820)); ((FrameworkElement)bigWindow.FindName("TasksReviewButton")).BringIntoView(); UiLayoutChecks.Wait(200); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "tasks-card-footer-dark-920.png"));
                bigWindow.TaskChoices.First(choice => choice.Candidate.Level == TaskLevel.LeftBehind).On = true; headers[^1].IsChecked = false; headers[0].IsChecked = true; UiLayoutChecks.Wait(400);
                bigWindow.ToggleTheme(); UiLayoutChecks.Wait(400); UiLayoutChecks.Layout(root, new Size(920, 820)); card.BringIntoView(); UiLayoutChecks.Wait(200); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "tasks-card-light-920.png"));
                bigWindow.ToggleTheme(); UiLayoutChecks.Wait(400);
            }
            card.IsExpanded = false; UiLayoutChecks.Wait(400);
            card.IsExpanded = true; UiLayoutChecks.Wait(400);
            check(TopicHeaders((DependencyObject)bigWindow.FindName("TasksList")).All(header => header.IsChecked != true), "Closing the Scheduled tasks card forgets which sub-categories were open");
        }
        finally { bigWindow.Close(); }

        // ---- the small PC: summary, switches, review, administrator restart, change, Undo, layout ----
        var pc = Pc();
        var window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "tasks-ui-preferences.json"), taskSystem: pc) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string Text(string name) => ((TextBlock)window.FindName(name)).Text;
            Click("SettingsButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            check(Text("TasksSummary").StartsWith("Open to see") && pc.ListCalls == 0, "Before its card is opened the Scheduled tasks summary says so instead of reading the tasks");
            var card = (Expander)window.FindName("SettingsTasksSection");
            card.IsExpanded = true;
            UiLayoutChecks.PumpUntil(() => window.TaskChoices.Count > 0);
            UiLayoutChecks.Layout(root, new Size(920, 820));
            check(Text("TasksSummary") == "5 tasks you can turn off" && window.TaskChoices.Any(choice => choice.Title == "Program data collector") && ((TextBlock)window.FindName("TasksHint")).Text.Contains("Review changes"),
                "The Scheduled tasks card reads the PC and says how many tasks can be turned off (no speed claim)");
            // Layout: the two columns stay three cards each and end together; the new card sits full width under them, before Privacy & telemetry.
            var leftColumn = (FrameworkElement)window.FindName("SettingsLeftColumn"); var rightColumn = (FrameworkElement)window.FindName("SettingsRightColumn");
            var privacy = (FrameworkElement)window.FindName("SettingsPrivacySection");
            Rect Bounds(FrameworkElement e) => e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize));
            card.IsExpanded = false; UiLayoutChecks.Wait(400); UiLayoutChecks.Layout(root, new Size(920, 820));
            check(!card.IsDescendantOf(leftColumn) && !card.IsDescendantOf(rightColumn) && ((Panel)leftColumn).Children.Count == 3 && ((Panel)rightColumn).Children.Count == 3
                && Math.Abs(Bounds(leftColumn).Bottom - Bounds(rightColumn).Bottom) < 30 && Bounds(card).Top >= Math.Max(Bounds(leftColumn).Bottom, Bounds(rightColumn).Bottom) - 0.5
                && Math.Abs(Bounds(card).Left - Bounds(leftColumn).Left) < 1 && Math.Abs(Bounds(card).Right - Bounds(rightColumn).Right) < 1 && Bounds(privacy).Top >= Bounds(card).Bottom,
                "Scheduled tasks sits full width under the two balanced columns (three cards each) and before Privacy & telemetry");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string placementShots)
            {
                Directory.CreateDirectory(placementShots);
                privacy.BringIntoView(); UiLayoutChecks.Wait(200); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(placementShots, "tasks-placement-dark-920.png"));
            }
            card.IsExpanded = true; UiLayoutChecks.Wait(400); UiLayoutChecks.Layout(root, new Size(920, 820));
            var review = (Button)window.FindName("TasksReviewButton");
            check(!review.IsEnabled && window.TaskChoices.All(choice => !choice.Selected && choice.On), "No task is selected by default: every switch is ON and nothing can be changed yet");
            var program = window.TaskChoices.Single(choice => choice.Path == ProgramData);
            program.On = false;
            check(program.Selected && program.Status.StartsWith("Will be turned off after you review") && review.IsEnabled && review.Content?.ToString() == "Review 1 change…",
                "Switching a task OFF stages a change, says what will happen and enables Review");
            // Without administrator rights the same button asks to restart as administrator, and nothing is changed until then.
            pc.Admin = false;
            var launched = new List<string>(); var oldLauncher = MainWindow.ElevatedLauncher; var oldExit = MainWindow.ExitForRestart;
            MainWindow.ElevatedLauncher = argument => { launched.Add(argument); return true; }; MainWindow.ExitForRestart = () => launched.Add("exit");
            try
            {
                Click("TasksReviewButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
                check(Text("ConsentTitle") == "Turn off 1 scheduled task?" && Text("ConsentText").Contains("Program data collector") && Text("ConsentText").Contains("Nothing is deleted")
                    && ((Button)window.FindName("ConsentConfirmButton")).Content?.ToString() == "Restart as administrator" && pc.Calls.Count == 0,
                    "Review lists exactly which tasks will be turned off and, without administrator rights, offers Restart as administrator");
                Click("ConsentConfirmButton"); UiLayoutChecks.PumpUntil(() => launched.Count == 2);
                check(launched.SequenceEqual(["--tasks", "exit"]) && pc.Calls.Count == 0, "Restart as administrator relaunches Hush for the Scheduled tasks card and changes nothing itself");
            }
            finally { MainWindow.ElevatedLauncher = oldLauncher; MainWindow.ExitForRestart = oldExit; }
            // With administrator rights: cancel changes nothing; confirm changes, reports, Undo puts it back.
            pc.Admin = true;
            Click("TasksReviewButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(((Button)window.FindName("ConsentConfirmButton")).Content?.ToString() == "Turn them off", "With administrator rights the action is Turn them off");
            Click("ConsentCancelButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Collapsed);
            check(pc.Calls.Count == 0 && pc.Read(ProgramData)!.Enabled, "Cancel changes no task");
            Click("TasksReviewButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            Click("ConsentConfirmButton");
            UiLayoutChecks.PumpUntil(() => !pc.Read(ProgramData)!.Enabled && ((TextBlock)window.FindName("TasksStatus")).Visibility == Visibility.Visible && window.Tasks.HasChanges);
            UiLayoutChecks.PumpUntil(() => ((Button)window.FindName("TasksUndoButton")).Visibility == Visibility.Visible);
            check(Text("TasksStatus").Contains("1 scheduled task turned off and checked") && Text("TasksStatus").Contains("Undo turns them back on") && Text("TasksSummary").Contains("1 changed by Hush")
                && window.TaskChoices.All(choice => choice.Path != ProgramData), "The result is reported exactly and the card says how many Hush changed");
            // Undo without administrator rights restarts as administrator for the Undo and changes nothing itself.
            pc.Admin = false;
            var undoLaunched = new List<string>(); var keepLauncher = MainWindow.ElevatedLauncher; var keepExit = MainWindow.ExitForRestart;
            MainWindow.ElevatedLauncher = argument => { undoLaunched.Add(argument); return true; }; MainWindow.ExitForRestart = () => undoLaunched.Add("exit");
            try
            {
                Click("TasksUndoButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
                check(((Button)window.FindName("ConsentConfirmButton")).Content?.ToString() == "Restart as administrator", "Undo without administrator rights offers Restart as administrator");
                Click("ConsentConfirmButton"); UiLayoutChecks.PumpUntil(() => undoLaunched.Count == 2);
                check(undoLaunched.SequenceEqual(["--tasks-undo", "exit"]) && !pc.Read(ProgramData)!.Enabled, "Restart as administrator for Undo relaunches Hush with --tasks-undo and changes nothing itself");
            }
            finally { MainWindow.ElevatedLauncher = keepLauncher; MainWindow.ExitForRestart = keepExit; }
            pc.Admin = true;
            UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Collapsed);
            Click("TasksUndoButton");
            UiLayoutChecks.PumpUntil(() => pc.Read(ProgramData)!.Enabled && !window.Tasks.HasChanges);
            UiLayoutChecks.PumpUntil(() => ((Button)window.FindName("TasksUndoButton")).Visibility == Visibility.Collapsed);
            check(Text("TasksStatus").Contains("turned back on"), "Undo turns the task back on and says so");
            check(MainWindow.TaskHistoryDirectory != null && !MainWindow.TaskHistoryDirectory.StartsWith(CleanupEngine.DataDirectory, StringComparison.OrdinalIgnoreCase),
                "Test windows keep the scheduled tasks history in a throw-away folder, never in the real Hush data folder");
        }
        finally { window.Close(); }
    }
}
