using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hush;

// Settings > Services (0.9): the curated list, leftovers whose program is gone, save-first changes, verification, Undo and the card. A fake Windows is used throughout:
// no real service is ever read for changing, stopped, started or reconfigured by a test.
internal static class ServiceChecks
{
    internal sealed class FakeServices : IServiceSystem
    {
        internal List<ServiceInfo> All = [];
        internal bool Admin = true;
        internal HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Refuse = new(StringComparer.OrdinalIgnoreCase), StopFails = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Calls = [];
        internal Func<string, bool>? OnBeforeChange;
        internal bool HistoryExistedBeforeFirstChange;
        public bool IsAdministrator => Admin;
        internal int ListCalls, ReadCalls;
        public IReadOnlyList<ServiceInfo> List() { ListCalls++; return All.ToList(); }
        public bool FileExists(string path) => !Missing.Contains(path);
        public long MemoryOf(int pid) => pid * 1048576L;
        public ServiceInfo? Read(string name) { ReadCalls++; return All.FirstOrDefault(info => info.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }
        public bool TrySetStart(string name, int start, bool delayed, out string error)
        {
            error = "";
            if (OnBeforeChange != null && Calls.Count == 0) HistoryExistedBeforeFirstChange = OnBeforeChange(name);
            Calls.Add($"set {name} {start}{(delayed ? " delayed" : "")}");
            if (Refuse.Contains(name)) { error = "Windows refused the change"; return false; }
            int index = All.FindIndex(info => info.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            All[index] = All[index] with { Start = start, Delayed = delayed };
            return true;
        }
        public void Stop(string name)
        {
            Calls.Add("stop " + name);
            if (StopFails.Contains(name)) throw new TimeoutException("did not stop");
            int index = All.FindIndex(info => info.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            All[index] = All[index] with { Running = false, Pid = 0 };
        }
        public void Start(string name)
        {
            Calls.Add("start " + name);
            int index = All.FindIndex(info => info.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            All[index] = All[index] with { Running = true, Pid = 40 };
        }
    }

    private const int Own = 0x10;
    internal static FakeServices Pc()
    {
        string old = @"C:\Program Files\Removed Vendor\old-agent.exe";
        return new()
        {
            Missing = new(StringComparer.OrdinalIgnoreCase) { old, @"C:\Program Files\Removed Vendor\gone.exe" },
            All =
            [
                new("Fax", "@%systemroot%\\system32\\fxsresm.dll,-118", Own, 2, false, @"C:\Windows\system32\fxssvc.exe", true, 12),
                new("SysMain", "SysMain", 0x20, 2, true, @"C:\Windows\system32\svchost.exe -k LocalSystemNetworkRestricted", true, 77),
                new("DiagTrack", "Telemetry", 0x20, 3, false, @"C:WindowsSystem32svchost.exe -k utcsvc", false, 0),       // Manual and not running: still offered (it can be disabled)
                new("XblAuthManager", "Xbox Live Auth Manager", 0x20, 3, false, @"C:WindowsSystem32svchost.exe -k netsvcs", false, 0), // Xbox services are Manual by default: they must be listed
                new("MapsBroker", "Maps", 0x20, 4, false, @"C:\Windows\System32\svchost.exe -k NetworkService", false, 0),    // already Disabled: not offered
                new("WinDefend", "Microsoft Defender Antivirus Service", Own, 2, false, @"C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe", true, 5), // never offered
                new("wuauserv", "Windows Update", 0x20, 2, true, @"C:\Windows\system32\svchost.exe -k netsvcs", true, 6),     // never offered
                new("OldVendorSvc", "Old Vendor Agent", Own, 2, false, "\"" + old + "\" --service", false, 0),                 // leftover: file is gone
                new("GoneManual", "Gone but Manual", Own, 3, false, @"C:\Program Files\Removed Vendor\gone.exe", false, 0),    // leftover but Manual: not offered
                new("InstalledVendor", "Installed Vendor", Own, 2, false, @"C:\Program Files\Vendor\agent.exe", false, 0),     // its file exists: not offered
                new("WindowsGone", "Under Windows", Own, 2, false, @"C:\Windows\missing-under-windows.exe", false, 0),         // under the Windows folder: never a leftover
                new("SomeDriver", "Some Driver", 1, 2, false, @"\SystemRoot\system32\drivers\some.sys", true, 0),             // a driver: never listed
            ]
        };
    }

    // Every catalog service present as Manual, a few Automatic and running: the whole list at once.
    internal static FakeServices BigPc()
    {
        string[] running = ["SysMain", "CDPSvc", "SSDPSRV", "stisvc", "lmhosts", "TrkWks"];
        return new() { All = ServiceTuning.Catalog.Select(item => new ServiceInfo(item.Name, item.Title, 0x20, running.Contains(item.Name) ? 2 : 3, false, @"C:WindowsSystem32svchost.exe -k x", running.Contains(item.Name), running.Contains(item.Name) ? 30 : 0)).ToList() };
    }

    internal static void RunEngine(string fixture, Action<bool, string> check)
    {
        // ---- the curated list itself ----
        var names = ServiceTuning.Catalog.Select(item => item.Name).ToList();
        check(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count && names.Count >= 20, $"The services list has {names.Count} unique, curated entries");
        check(!names.Any(name => ServiceTuning.NeverTouch.Contains(name)), "No security, update, network, sign-in, audio, storage or system service is on the list");
        string[] forbidden = ["WinDefend", "wscsvc", "mpssvc", "BFE", "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS", "EventLog", "RpcSs", "Dhcp", "Dnscache", "CryptSvc", "LanmanServer", "LanmanWorkstation", "TrustedInstaller", "Audiosrv", "AudioEndpointBuilder", "Themes", "Schedule"];
        check(forbidden.All(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase)), "The forbidden services (Defender, Firewall, Windows Update, event log, network, audio ...) are not on the list");
        check(ServiceTuning.Catalog.All(item => item.Title.Length > 0 && item.Why.Length is > 20 and < 200), "Every entry has a plain name and a short plain explanation");
        check(ServiceTuning.ExecutablePath("\"C:\\Program Files\\Vendor\\agent.exe\" --service") == @"C:\Program Files\Vendor\agent.exe"
            && ServiceTuning.ExecutablePath(@"C:\Tools\svc.exe -k group") == @"C:\Tools\svc.exe"
            && ServiceTuning.ExecutablePath(@"\\server\share\x.exe") == null && ServiceTuning.ExecutablePath(@"\SystemRoot\system32\drivers\a.sys") == null && ServiceTuning.ExecutablePath("") == null,
            "A service's program file is read from its command line (quoted or not); network paths and drivers are not file checks");

        // ---- sub-categories: every entry has a topic, and a long list splits itself automatically ----
        check(ServiceTuning.Catalog.All(item => ServiceTuning.TopicFor(item.Name, item.Level) != ListGrouping.Other) && ServiceTuning.Catalog.Select(item => ServiceTuning.TopicFor(item.Name, item.Level)).Distinct().Count() >= 8,
            "Every catalog service belongs to a topic, in at least eight sub-categories");
        var flatNames = ListGrouping.Names(Enumerable.Range(0, ListGrouping.FlatLimit).ToList(), _ => "Topic");
        check(flatNames.All(name => name.Length == 0), "A short list (up to 12 items) stays flat");
        var rows = new List<(string Topic, string Level)>();
        for (int i = 0; i < 14; i++) rows.Add(("Big", i % 2 == 0 ? "Rarely needed" : "Read first"));
        for (int i = 0; i < 5; i++) rows.Add(("Medium", "Rarely needed"));
        rows.Add(("Lonely A", "Read first")); rows.Add(("Lonely B", "Read first"));
        for (int i = 0; i < 30; i++) rows.Add(("Huge", "Read first"));
        var grouped = ListGrouping.Names(rows, row => row.Topic, row => row.Level);
        var counts = grouped.GroupBy(name => name).ToDictionary(group => group.Key, group => group.Count());
        check(counts.ContainsKey("Big · Rarely needed") && counts.ContainsKey("Big · Read first") && counts["Medium"] == 5 && counts["Other"] == 2 && !counts.ContainsKey("Lonely A")
            && counts.Where(pair => pair.Key.StartsWith("Huge")).All(pair => pair.Value <= ListGrouping.GroupLimit) && counts.Keys.Count(name => name.StartsWith("Huge · Read first (")) == 3 && counts.Values.All(count => count <= ListGrouping.GroupLimit),
            "A long list splits itself: by topic, a too-long topic by its second key, a still too-long piece into equal numbered parts, and lonely topics fold into Other");
        var bigPc = BigPc();
        var bigOffer = new ServiceTuning(bigPc, Path.Combine(fixture, "services-engine-big")).Scan();
        var bigNames = ListGrouping.Names(bigOffer, c => c.Topic, c => c.Level == ServiceLevel.Recommended ? "Rarely needed" : "Read first");
        check(bigOffer.Count > 60 && bigNames.Distinct().Count() >= 8 && bigNames.GroupBy(name => name).All(group => group.Count() <= ListGrouping.GroupLimit), $"The full list ({bigOffer.Count} services) is shown in {bigNames.Distinct().Count()} sub-categories of at most {ListGrouping.GroupLimit}");

        // ---- what is offered ----
        string directory = Path.Combine(fixture, "services-engine");
        var pc = Pc();
        var tuning = new ServiceTuning(pc, directory);
        var offered = tuning.Scan();
        check(offered.Select(c => c.Name).OrderBy(n => n).SequenceEqual(["DiagTrack", "Fax", "OldVendorSvc", "SysMain", "XblAuthManager"]),
            "Every listed service that is not already disabled is offered, Manual ones too (Xbox!), plus a leftover whose program is gone (never protected, driver, existing-program or disabled ones)");
        var fax = offered.Single(c => c.Name == "Fax"); var leftover = offered.Single(c => c.Name == "OldVendorSvc");
        check(fax.TargetStart == 4 && fax.Level == ServiceLevel.Recommended && fax.Running && fax.MemoryBytes == 12 * 1048576L && fax.Status == "Starts with Windows · Running · 12 MB" && offered.Single(c => c.Name == "XblAuthManager").Status == "Starts only when needed · Not running"
            && leftover.TargetStart == 4 && leftover.Level == ServiceLevel.LeftBehind && leftover.Title == "Old Vendor Agent" && !leftover.Running && leftover.Why.Contains("old-agent.exe"),
            "A listed service would be disabled and shows its start type and what it uses now; a leftover says which file is missing");
        check(pc.Calls.Count == 0 && !File.Exists(Path.Combine(directory, "services-changed.json")), "Scanning is read-only: nothing is changed or saved");

        // ---- changing needs administrator rights ----
        pc.Admin = false;
        bool refused = false;
        try { tuning.Apply(["Fax"]); } catch (IOException ex) { refused = ex.Message.Contains("Administrator"); }
        check(refused && pc.Calls.Count == 0 && pc.Read("Fax")!.Start == 2 && !tuning.HasChanges, "Without administrator rights nothing is changed");
        pc.Admin = true;

        // ---- apply: only offered names, originals saved BEFORE the first change, verified, running ones stopped ----
        pc.OnBeforeChange = _ => File.Exists(Path.Combine(directory, "services-changed.json"));
        var report = tuning.Apply(["Fax", "SysMain", "WinDefend", "wuauserv", "NoSuchService", "SomeDriver", "GoneManual", "OldVendorSvc"]);
        check(pc.HistoryExistedBeforeFirstChange, "The original settings are saved to disk before the first service changes");
        check(report.Changed.OrderBy(n => n).SequenceEqual(["Fax", "Old Vendor Agent", "SysMain (Superfetch)"]) && report.Failed.Count == 0
            && pc.Read("Fax")!.Start == 4 && pc.Read("SysMain")!.Start == 4 && pc.Read("OldVendorSvc")!.Start == 4 && pc.Read("DiagTrack")!.Start == 3
            && pc.Read("WinDefend")!.Start == 2 && pc.Read("wuauserv")!.Start == 2 && pc.Read("SomeDriver")!.Start == 2 && pc.Read("GoneManual")!.Start == 3,
            "Only what was offered and chosen is changed (disabled); protected, unknown, driver and not-chosen names are ignored");
        check(!pc.Read("Fax")!.Running && !pc.Read("SysMain")!.Running && pc.Calls.Contains("stop Fax") && !pc.Calls.Any(call => call.StartsWith("stop OldVendorSvc")), "Services that were running are stopped; one that was not running is not touched");
        check(tuning.HasChanges && tuning.ChangedCount == 3 && report.Text(false).Contains("3 services changed and checked"), "Hush remembers what it changed and reports it plainly");
        check(File.ReadAllText(Path.Combine(directory, "..", "Logs", "services.jsonl")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3, "Each change is logged");
        check(tuning.Scan().Select(c => c.Name).OrderBy(n => n).SequenceEqual(["DiagTrack", "XblAuthManager"]), "Changed services are no longer offered (the ones not chosen still are)");

        // ---- Undo ----
        pc.Calls.Clear();
        var undo = tuning.Undo();
        check(undo.Changed.Count == 3 && undo.Failed.Count == 0 && pc.Read("Fax")!.Start == 2 && pc.Read("SysMain")!.Start == 2 && pc.Read("SysMain")!.Delayed && pc.Read("OldVendorSvc")!.Start == 2
            && pc.Read("Fax")!.Running && pc.Read("SysMain")!.Running && !pc.Read("OldVendorSvc")!.Running && !tuning.HasChanges && !File.Exists(Path.Combine(directory, "services-changed.json")),
            "Undo puts back every original start type (delayed start included), restarts the ones that were running and closes the history");

        // ---- Windows refuses one change, another cannot be stopped ----
        pc = Pc(); pc.Refuse.Add("SysMain"); pc.StopFails.Add("Fax");
        tuning = new ServiceTuning(pc, Path.Combine(fixture, "services-engine-2"));
        report = tuning.Apply(["Fax", "SysMain"]);
        check(report.Changed.SequenceEqual(["Fax"]) && report.Failed.Count == 1 && report.Failed[0].Contains("SysMain") && report.StillRunning.SequenceEqual(["Fax"])
            && tuning.ChangedCount == 1 && pc.Read("SysMain")!.Start == 2 && report.Text(false).Contains("could not be stopped") && report.Text(false).Contains("Could not be changed"),
            "A refused change is reported and not remembered; a service that cannot be stopped is reported and stops at the next restart");
        pc.StopFails.Clear(); tuning.Undo();
        check(pc.Read("Fax")!.Start == 2 && !tuning.HasChanges, "Undo works after a partial change too");

        // ---- a damaged or hostile history can never aim Hush at another service ----
        string hostile = Path.Combine(fixture, "services-engine-3"); Directory.CreateDirectory(hostile);
        File.WriteAllText(Path.Combine(hostile, "services-changed.json"), "{\"Schema\":1,\"Services\":[{\"Name\":\"WinDefend\",\"Start\":4,\"Delayed\":false,\"WasRunning\":false},{\"Name\":\"Fax\",\"Start\":9,\"Delayed\":false,\"WasRunning\":false}]}");
        pc = Pc(); tuning = new ServiceTuning(pc, hostile);
        var hostileUndo = tuning.Undo();
        check(hostileUndo.Changed.Count == 0 && pc.Calls.Count == 0 && pc.Read("WinDefend")!.Start == 2, "A history that names a protected service or an impossible start type is ignored");
        // 0.9 audit (M3): the history is a file other programs can edit and Undo runs elevated, so a name Hush never offered is dropped even when it is not on the
        // never-touch list, a real leftover (program file still missing) is still put back, and a start type Hush cannot have saved (Boot/System) is ignored.
        File.WriteAllText(Path.Combine(hostile, "services-changed.json"),
            "{\"Schema\":1,\"Services\":[{\"Name\":\"InstalledVendor\",\"Start\":2,\"Delayed\":false,\"WasRunning\":false},{\"Name\":\"NoSuchService\",\"Start\":2,\"Delayed\":false,\"WasRunning\":false},"
            + "{\"Name\":\"SomeDriver\",\"Start\":0,\"Delayed\":false,\"WasRunning\":false},{\"Name\":\"OldVendorSvc\",\"Start\":2,\"Delayed\":false,\"WasRunning\":false}]}");
        pc = Pc(); pc.All[pc.All.FindIndex(info => info.Name == "OldVendorSvc")] = pc.Read("OldVendorSvc")! with { Start = 4 }; // Hush had disabled it
        pc.All[pc.All.FindIndex(info => info.Name == "InstalledVendor")] = pc.Read("InstalledVendor")! with { Start = 4 };
        tuning = new ServiceTuning(pc, hostile);
        var forged = tuning.Undo();
        check(pc.Calls.SequenceEqual(["set OldVendorSvc 2"]) && forged.Changed.Count == 1 && pc.Read("InstalledVendor")!.Start == 4 && pc.Read("SomeDriver")!.Start == 2
            && forged.Failed.Count == 2 && forged.Failed.Any(text => text.Contains("InstalledVendor")) && forged.Failed.Any(text => text.Contains("NoSuchService")) && !tuning.HasChanges,
            "Undo only touches services Hush could have changed: a catalog service or a leftover whose file is still missing; a forged name (even an ordinary installed program) is left alone and reported");
        pc.Calls.Clear();
        File.WriteAllText(Path.Combine(hostile, "services-changed.json"), "not json at all");
        bool damaged = false;
        try { tuning.Undo(); } catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { damaged = true; }
        check(damaged && !tuning.HasChanges && pc.Calls.Count == 0, "A damaged history is refused and changes nothing");
        // ---- T11: many services change with one list read, and a stop that fails is reported at once ----
        var many = BigPc(); many.StopFails.UnionWith(many.All.Where(info => info.Running).Select(info => info.Name));
        var manyTuning = new ServiceTuning(many, Path.Combine(fixture, "services-engine-many"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var manyReport = manyTuning.Apply(many.All.Select(info => info.Name).Take(40).ToList());
        clock.Stop();
        check(manyReport.Changed.Count >= 40 && many.ListCalls == 1 && many.ReadCalls <= manyReport.Changed.Count + 1,
            $"Changing {manyReport.Changed.Count} services lists the services once ({many.ListCalls}) and reads each changed one only by name ({many.ReadCalls})");
        check(manyReport.StillRunning.Count > 0 && manyReport.Text(false).Contains("could not be stopped") && clock.Elapsed < TimeSpan.FromSeconds(5),
            "A service that cannot be stopped is reported promptly and the rest still change");
        check(new RealServiceSystem().List().Count > 20, "The real service list can be read (read-only, no change made)");
    }

    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        // A long list gets its sub-category headings in the real card (the five-service fixture below stays flat).
        var bigWindow = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "services-big-preferences.json"), serviceSystem: BigPc()) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            bigWindow.Show();
            ((Button)bigWindow.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiLayoutChecks.PumpUntil(() => !bigWindow.IsPageTransitioning); UiLayoutChecks.Wait(300);
            check(bigWindow.ServiceChoices.Count == 0, "Opening Settings does not read the services (that costs a scan of every Windows service): the list is read when its card is opened");
            ((Expander)bigWindow.FindName("SettingsServicesSection")).IsExpanded = true;
            UiLayoutChecks.PumpUntil(() => bigWindow.ServiceChoices.Count > 60);
            var view = (System.Windows.Data.ListCollectionView)((ItemsControl)bigWindow.FindName("ServicesList")).ItemsSource;
            UiLayoutChecks.Wait(300);
            static IEnumerable<System.Windows.Controls.Primitives.ToggleButton> TopicHeaders(DependencyObject parent)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                    if (child is System.Windows.Controls.Primitives.ToggleButton { Uid: "topic" } header) yield return header;
                    foreach (var inner in TopicHeaders(child)) yield return inner;
                }
            }
            var headers = TopicHeaders((DependencyObject)bigWindow.FindName("ServicesList")).ToList();
            check(headers.Count >= 8 && headers.All(header => header.IsChecked != true), "Opening the Services card opens nothing inside it: every sub-category starts closed");
            var topicRanks = bigWindow.ServiceChoices.Select(choice => ServiceTuning.TopicRank(choice.Candidate.Topic)).ToList();
            check(topicRanks.SequenceEqual(topicRanks.Order()) && bigWindow.ServiceChoices.All(choice => choice.On),
                "The services are in order (topics in their fixed order, left-behind last) and every switch starts ON, because every service is enabled right now");
            // Opening a sub-category, closing the card and opening it again shows everything closed again.
            headers[0].IsChecked = true; UiLayoutChecks.Wait(100);
            ((Expander)bigWindow.FindName("SettingsServicesSection")).IsExpanded = false; UiLayoutChecks.Wait(400);
            ((Expander)bigWindow.FindName("SettingsServicesSection")).IsExpanded = true; UiLayoutChecks.Wait(400);
            check(TopicHeaders((DependencyObject)bigWindow.FindName("ServicesList")).All(header => header.IsChecked != true), "Closing a card forgets which sub-categories were open: opening it again shows them all closed");
            check(view.Groups.Count >= 8 && view.Groups.OfType<System.Windows.Data.CollectionViewGroup>().All(group => group.ItemCount <= ListGrouping.GroupLimit)
                && bigWindow.ServiceChoices.All(choice => choice.Topic.Length > 0), $"The Services card shows {bigWindow.ServiceChoices.Count} services under {view.Groups.Count} sub-category headings (none longer than {ListGrouping.GroupLimit})");
        }
        finally { bigWindow.Close(); }
        var pc = Pc();
        string directory = Path.Combine(fixture, "services-ui");
        var window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "services-ui-preferences.json"), serviceSystem: pc) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string Text(string name) => ((TextBlock)window.FindName(name)).Text;
            Click("SettingsButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            check(Text("ServicesSummary").StartsWith("Open to see"), "Before its card is opened the Services summary says so instead of scanning");
            ((Expander)window.FindName("SettingsServicesSection")).IsExpanded = true;
            UiLayoutChecks.PumpUntil(() => window.ServiceChoices.Count > 0);
            UiLayoutChecks.Layout(root, new Size(920, 820));
            var browser = (FrameworkElement)window.FindName("SettingsBrowserSection"); var services = (FrameworkElement)window.FindName("SettingsServicesSection");
            var update = (FrameworkElement)window.FindName("SettingsUpdateSection"); var left = (FrameworkElement)window.FindName("SettingsLeftColumn"); var right = (FrameworkElement)window.FindName("SettingsRightColumn");
            double Top(FrameworkElement e) => e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize)).Top;
            double Bottom(FrameworkElement e) => e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize)).Bottom;
            check(Text("ServicesSummary").StartsWith("5 services you can turn off") && window.ServiceChoices.Select(choice => choice.Title).Contains("Fax"),
                "The Services card reads the PC and says how many services start with Windows and are rarely needed (no speed claim)");
            check(services.IsDescendantOf(right) && Top(services) >= Bottom(browser) - 0.5 && !update.IsDescendantOf(left) && !update.IsDescendantOf(right) && Math.Abs(Bottom(left) - Bottom(right)) < 30 && Top(update) >= Bottom(left),
                "Services sits under Browser data, so the two columns have three cards each and end at the same height (no empty space)");
            ((Expander)services).IsExpanded = true; UiLayoutChecks.Wait(360); UiLayoutChecks.Layout(root, new Size(920, 820));
            var review = (Button)window.FindName("ServicesReviewButton");
            check(!review.IsEnabled && window.ServiceChoices.All(choice => !choice.Selected), "Nothing is selected by default, so nothing can be changed yet");
            var fax = window.ServiceChoices.Single(choice => choice.Name == "Fax");
            check(fax.On && fax.Status.StartsWith("Starts with Windows"), "A service that is enabled shows its switch ON and when it starts");
            fax.On = false; // the person switches it OFF
            check(fax.Selected && !fax.On && fax.Status.StartsWith("Will be turned off after you review"), "Switching a service OFF stages a change and says what will happen to it");
            check(review.IsEnabled && review.Content?.ToString() == "Review 1 change…", "Choosing a service enables Review");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots) { Directory.CreateDirectory(shots); UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "services-card-dark.png")); }
            // Without administrator rights the same button asks to restart as administrator, and nothing is changed until then.
            pc.Admin = false;
            var launched = new List<string>(); var oldLauncher = MainWindow.ElevatedLauncher; var oldExit = MainWindow.ExitForRestart;
            MainWindow.ElevatedLauncher = argument => { launched.Add(argument); return true; }; MainWindow.ExitForRestart = () => launched.Add("exit");
            try
            {
                Click("ServicesReviewButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
                check(Text("ConsentTitle") == "Change 1 service?" && Text("ConsentText").Contains("Fax") && ((Button)window.FindName("ConsentConfirmButton")).Content?.ToString() == "Restart as administrator"
                    && pc.Calls.Count == 0, "Review lists exactly what will change and, without administrator rights, offers Restart as administrator");
                Click("ConsentConfirmButton"); UiLayoutChecks.PumpUntil(() => launched.Count == 2);
                check(launched.SequenceEqual(["--services", "exit"]) && pc.Calls.Count == 0, "Restart as administrator relaunches Hush for the Services card and changes nothing itself");
            }
            finally { MainWindow.ElevatedLauncher = oldLauncher; MainWindow.ExitForRestart = oldExit; }
            // With administrator rights: confirm, change, report, Undo.
            pc.Admin = true;
            Click("ServicesReviewButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(((Button)window.FindName("ConsentConfirmButton")).Content?.ToString() == "Change them", "With administrator rights the action is Change them");
            Click("ConsentCancelButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Collapsed);
            check(pc.Calls.Count == 0 && pc.Read("Fax")!.Start == 2, "Cancel changes nothing");
            Click("ServicesReviewButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            Click("ConsentConfirmButton");
            UiLayoutChecks.PumpUntil(() => pc.Read("Fax")!.Start == 4 && ((TextBlock)window.FindName("ServicesStatus")).Visibility == Visibility.Visible && window.Services.HasChanges);
            UiLayoutChecks.PumpUntil(() => ((Button)window.FindName("ServicesUndoButton")).Visibility == Visibility.Visible);
            check(Text("ServicesStatus").Contains("1 service changed and checked") && Text("ServicesStatus").Contains("Undo puts them back") && Text("ServicesSummary").Contains("1 changed by Hush")
                && window.ServiceChoices.All(choice => choice.Name != "Fax"), "The result is reported exactly and the card now says how many Hush changed");
            Click("ServicesUndoButton");
            UiLayoutChecks.PumpUntil(() => pc.Read("Fax")!.Start == 2 && !window.Services.HasChanges);
            UiLayoutChecks.PumpUntil(() => ((Button)window.FindName("ServicesUndoButton")).Visibility == Visibility.Collapsed);
            check(Text("ServicesStatus").Contains("put back"), "Undo puts the service back and says so");
        }
        finally { window.Close(); }
    }
}
