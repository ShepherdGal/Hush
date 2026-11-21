using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Hush;

// Settings > Startup (owner, November 14, 2026): every source of things that start with Windows, the locked ones, save-first changes with read-back, Undo, the
// allow-listed history and the card. Fakes throughout: a fixture registry key under HKCU\Software\Hush.TestFixtures, temp folders, FakeTasks and FakeServices.
// Nothing on this PC is changed.
internal static class StartupChecks
{
    private sealed class Fixture : IDisposable
    {
        public readonly string KeyPath = @"Software\Hush.TestFixtures\" + Guid.NewGuid().ToString("N");
        public readonly string UserFolder, CommonFolder, History;
        public readonly StartupAppsService Apps;
        public readonly StartupLockedPlaces Locked;
        public readonly TaskChecks.FakeTasks Tasks = new();
        public readonly ServiceChecks.FakeServices Services = new();
        public Fixture(string fixture, string name)
        {
            string root = Path.Combine(fixture, "startup-" + name);
            UserFolder = Path.Combine(root, "user"); CommonFolder = Path.Combine(root, "common"); History = Path.Combine(root, "Windows settings");
            Directory.CreateDirectory(UserFolder); Directory.CreateDirectory(CommonFolder);
            // Two hives (this account and all users) as two fixture keys, so a flag written to the wrong one is caught.
            Apps = new StartupAppsService(new StartupLocations(Registry.CurrentUser, KeyPath + @"\User\Run", Registry.CurrentUser, KeyPath + @"\Machine\Run", KeyPath + @"\Machine\Run32",
                KeyPath + @"\Approved", UserFolder, CommonFolder), root);
            Locked = new StartupLockedPlaces(Registry.CurrentUser, Registry.CurrentUser, KeyPath + @"\Policies", KeyPath + @"\RunOnce", KeyPath + @"\Winlogon", KeyPath + @"\Windows", KeyPath + @"\Packages");
        }
        public void Set(string sub, string name, object value) { using var key = Registry.CurrentUser.CreateSubKey(KeyPath + "\\" + sub); key.SetValue(name, value); }
        public object? Get(string sub, string name) { using var key = Registry.CurrentUser.OpenSubKey(KeyPath + "\\" + sub); return key?.GetValue(name); }
        public StartupTuning Tuning() => new(Apps, Tasks, Services, History, Locked);
        public void Dispose() { try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false); } catch { } }
    }

    // A little PC: two Run apps (one this account, one all users, one with a missing file), a folder shortcut, Windows Security's tray (never), Hush's own
    // entry (never), sign-in tasks (a vendor's, a Microsoft one, Hush's own), services (another program's, Windows' own, a catalog one, a driver, security).
    private static void Populate(Fixture pc)
    {
        pc.Set(@"User\Run", "Spotify", @"C:\Users\Me\AppData\Roaming\Spotify\Spotify.exe --autostart");
        pc.Set(@"User\Run", RunKeyStartup.ValueName, "\"C:\\Hush\\Hush.exe\" --startup");
        pc.Set(@"Machine\Run", "VendorTray", "\"C:\\Program Files\\Vendor\\tray.exe\"");
        pc.Set(@"Machine\Run", "OldTray", @"C:\Program Files\Removed\old.exe");
        pc.Set(@"Machine\Run", "SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe");
        File.WriteAllText(Path.Combine(pc.UserFolder, "Notes.lnk"), "x");
        pc.Services.Missing.Add(@"C:\Program Files\Removed\old.exe");
        pc.Tasks.All.AddRange([
            new(@"\Vendor\Vendor Agent at logon", true, @"C:\Program Files\Vendor\agent.exe", true, false),
            new(@"\Vendor\Weekly report", true, @"C:\Program Files\Vendor\report.exe", false, false),                       // no sign-in or start trigger: not startup
            new(@"\Microsoft\Windows\Shell\Something", true, @"%windir%\system32\x.exe", true, false),                        // Windows' own
            new(@"\" + AdminMode.TaskName, true, @"C:\Program Files\Hush\Hush.exe", true, false),                             // Hush's own
            new(@"\Other Vendor Security Agent", true, @"C:\Program Files\Security Vendor\agent.exe", true, false),           // security: never
        ]);
        pc.Services.All.AddRange([
            new ServiceInfo("VendorSvc", "Vendor Helper Service", 0x10, 2, true, "\"C:\\Program Files\\Vendor\\svc.exe\"", true, 40),
            new ServiceInfo("WinThing", "Windows Thing", 0x20, 2, false, @"C:\Windows\system32\svchost.exe -k netsvcs", true, 41),  // Windows' own
            new ServiceInfo("Fax", "Fax", 0x10, 2, false, @"C:\Program Files\Fax\fax.exe", true, 42),                          // the Services card owns it
            new ServiceInfo("VendorDriver", "Vendor Driver", 1, 2, false, @"C:\Program Files\Vendor\drv.sys", true, 0),      // a driver: never
            new ServiceInfo("AvSvc", "Contoso Antivirus Service", 0x10, 2, false, @"C:\Program Files\Contoso\av.exe", true, 43), // security: never
            new ServiceInfo("ManualSvc", "Vendor Manual", 0x10, 3, false, @"C:\Program Files\Vendor\manual.exe", false, 0),  // does not start with Windows
        ]);
        pc.Set(@"Packages\ContosoLtd.Sketchpad_fnn82j28hfe8t\SketchpadStartup", "State", 2);              // a Store app that starts at sign-in
        pc.Set(@"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\StartTerminalOnLoginTask", "State", 1); // turned off by the person
        pc.Set(@"Packages\Contoso.Managed_abc\App", "State", 3);                                          // set by a policy: locked
        pc.Set("Policies", "CorpAgent", @"C:\Corp\agent.exe");
        pc.Set("RunOnce", "FinishSetup", @"C:\Setup\finish.exe");
        pc.Set("Winlogon", "Shell", "explorer.exe");
        pc.Set("Winlogon", "Userinit", @"C:\Windows\system32\userinit.exe,");
    }

    internal static void RunEngine(string fixture, Action<bool, string> check)
    {
        using var pc = new Fixture(fixture, "engine");
        Populate(pc);
        var tuning = pc.Tuning();
        var found = tuning.Scan();
        StartupCandidate? Item(string title) => found.FirstOrDefault(item => item.Title == title);
        check(Item("Spotify") is { Where: StartupWhere.Registry, AllUsers: false, StartsNow: true, NeedsAdministrator: false } && Item("VendorTray") is { AllUsers: true, NeedsAdministrator: true }
            && Item("Notes") is { Where: StartupWhere.Folder } && Item("Vendor Agent at logon") is { Where: StartupWhere.Task } && Item("Vendor Helper Service") is { Where: StartupWhere.Service },
            "Startup lists Run apps (this account and all users), Startup folder shortcuts, sign-in tasks and services of other programs");
        check(Item("Sketchpad") is { Where: StartupWhere.StoreApp, StartsNow: true, NeedsAdministrator: false } && Item("Windows Terminal") is { StartsNow: false } && Item("Managed") is { Locked: true }
            && StartupTuning.StoreTitle("34791E63.CanonInkjetSmartConnect_6e5tt8cgb93ep", "CanonPRINT", several: true) == "Canon Inkjet Smart Connect · CanonPRINT",
            "Store apps' own startup tasks are listed with a plain name (Sketchpad, Windows Terminal); one set by a policy is locked");
        string storeId = Item("Sketchpad")!.Id;
        check(tuning.Change([storeId], off: true).Changed.Count == 1 && (int)pc.Get(@"Packages\ContosoLtd.Sketchpad_fnn82j28hfe8t\SketchpadStartup", "State")! == 1
            && tuning.Undo([storeId]).Changed.Count == 1 && (int)pc.Get(@"Packages\ContosoLtd.Sketchpad_fnn82j28hfe8t\SketchpadStartup", "State")! == 2,
            "A Store app's startup is turned off the way Task Manager does it (State 1) and Undo turns it back on (State 2), without administrator rights");
        check(Item("OldTray") is { FileMissing: true } && Item("OldTray")!.Status.Contains("missing"), "An entry whose program file is gone says so");
        check(Item(RunKeyStartup.ValueName) == null && Item("SecurityHealth") == null && Item("Weekly report") == null && Item("Something") == null && Item(AdminMode.TaskName) == null
            && Item("Other Vendor Security Agent") == null && Item("Windows Thing") == null && Item("Fax") == null && Item("Vendor Driver") == null && Item("Contoso Antivirus Service") == null && Item("Vendor Manual") == null,
            "Never listed: Hush's own entry and task, Windows Security, Windows' own tasks and services, the Services card's own, drivers, security software, tasks without a sign-in trigger, services that do not start by themselves");
        check(Item("CorpAgent") is { Locked: true } && Item("FinishSetup") is { Locked: true } && !found.Any(item => item.Title == "Windows shell"),
            "Policy and RunOnce entries are shown but locked; an unchanged Windows shell is not listed");
        pc.Set("Winlogon", "Shell", @"explorer.exe, C:\Evil\shell.exe");
        check(tuning.Scan().Any(item => item.Title == "Windows shell" && item.Locked), "A changed Windows shell is shown, locked");

        // Turning off: the Run flag in the right hive, the task disabled, the service set to Manual; originals saved first; read back; logged.
        bool historyFirst = false;
        pc.Tasks.OnBeforeChange = _ => historyFirst = File.Exists(Path.Combine(pc.History, "startup-changed.json"));
        string[] off = [found.Single(item => item.Title == "Spotify").Id, found.Single(item => item.Title == "VendorTray").Id, found.Single(item => item.Title == "Vendor Agent at logon").Id,
            found.Single(item => item.Title == "Vendor Helper Service").Id, found.First(item => item.Title == "CorpAgent").Id, "service|WinThing", "task|\\Microsoft\\Windows\\Shell\\Something"];
        var report = tuning.Change(off, off: true);
        check(report.Changed.Count == 4 && report.Failed.Count == 0 && historyFirst, $"Four items stop starting with Windows, the originals saved before the first change ({report.Text(false)})");
        check(StartupAppsService.IsDisabled(pc.Get(@"Approved\Run", "Spotify") as byte[]) && pc.Get(@"User\Run", "Spotify") != null && pc.Get(@"Machine\Run", "VendorTray") != null,
            "Run entries are turned off with Task Manager's flag; the entries themselves stay");
        check(pc.Tasks.Read(@"\Vendor\Vendor Agent at logon")!.Enabled == false && pc.Services.Read("VendorSvc")!.Start == 3 && pc.Services.Read("WinThing")!.Start == 2
            && pc.Tasks.Read(@"\Microsoft\Windows\Shell\Something")!.Enabled && pc.Get("Policies", "CorpAgent") != null,
            "The task is disabled and the service set to start only when needed (owner's choice); a locked or unoffered item is never changed");
        var after = tuning.Scan();
        check(after.Single(item => item.Title == "Spotify") is { StartsNow: false, ChangedByHush: true } && after.Single(item => item.Title == "Vendor Helper Service") is { StartsNow: false, ChangedByHush: true }
            && tuning.ChangedCount == 4, "What Hush turned off is still listed, as turned off by Hush, so it can be turned back on");
        check(File.ReadAllLines(Path.Combine(fixture, "startup-engine", "Logs", "startup-changes.jsonl")).Count(line => line.Contains("\"action\":\"off\"")) == 5, "Every change is logged (four here and the Store app above)");

        // Without administrator rights: this account's Run apps only; anything else refuses before changing anything.
        pc.Tasks.Admin = false;
        bool refused = false;
        try { tuning.Change([found.Single(item => item.Title == "Notes").Id, found.Single(item => item.Title == "OldTray").Id], off: true); } catch (IOException) { refused = true; }
        check(refused && !StartupAppsService.IsDisabled(pc.Get(@"Approved\StartupFolder", "Notes.lnk") as byte[]), "A change that needs administrator rights refuses as a whole and changes nothing");
        check(tuning.Change([found.Single(item => item.Title == "Notes").Id], off: true).Changed.Count == 1, "This account's own startup apps change without administrator rights");
        pc.Tasks.Admin = true;

        // A task Windows will not change is reported and not kept in the history.
        pc.Tasks.All.Add(new(@"\Vendor\Stubborn", true, @"C:\Program Files\Vendor\s.exe", true, false));
        pc.Tasks.Refuse.Add(@"\Vendor\Stubborn");
        var stubborn = tuning.Change(["task|\\Vendor\\Stubborn"], off: true);
        check(stubborn.Failed.Count == 1 && !tuning.Scan().Single(item => item.Title == "Stubborn").ChangedByHush, "A refused change is reported and leaves no history behind");

        // A hostile history (written by another program, read by an elevated Undo) can never aim Hush elsewhere.
        string historyFile = Path.Combine(pc.History, "startup-changed.json");
        string good = File.ReadAllText(historyFile);
        File.WriteAllText(historyFile, good.Replace("]}", ",{\"Id\":\"service|WinThing\",\"Original\":\"automatic\"},{\"Id\":\"task|\\\\Microsoft\\\\Windows\\\\Shell\\\\Something\",\"Original\":\"enabled\"},{\"Id\":\"service|AvSvc\",\"Original\":\"automatic\"},{\"Id\":\"service|VendorSvc2\",\"Original\":\"disabled\"}]}"));
        int before = pc.Services.Calls.Count;
        var undo = tuning.Undo();
        check(undo.Changed.Count == 5 && pc.Services.Read("WinThing")!.Start == 2 && !pc.Services.Calls.Skip(before).Any(call => call.Contains("WinThing") || call.Contains("AvSvc")),
            $"Undo puts back exactly what Hush changed; forged history lines (Windows' own, security, odd originals) are ignored ({undo.Text(true)})");
        check(!StartupAppsService.IsDisabled(pc.Get(@"Approved\Run", "Spotify") as byte[]) && pc.Get(@"Approved\Run", "Spotify") == null && pc.Tasks.Read(@"\Vendor\Vendor Agent at logon")!.Enabled
            && pc.Services.Read("VendorSvc") is { Start: 2, Delayed: true } && !tuning.HasChanges && !File.Exists(historyFile),
            "Undo restores the original flag (none), enables the task, puts the service back to automatic (delayed, as it was) and closes the history");

        // Turned off elsewhere (Task Manager) and turned back on here: Task Manager's "on" flag. An old Hush HKCU flag for an all-users entry is honored and cleared.
        pc.Set(@"Approved\Run", "VendorTray", StartupAppsService.DisabledFlag());
        var elsewhere = tuning.Scan().Single(item => item.Title == "VendorTray");
        check(!elsewhere.StartsNow && !elsewhere.ChangedByHush && tuning.Change([elsewhere.Id], off: false).Changed.Count == 1 && !StartupAppsService.IsDisabled(pc.Get(@"Approved\Run", "VendorTray") as byte[]),
            "Something turned off elsewhere can be let back in");
        var machineOnly = new StartupAppsService(new StartupLocations(Registry.CurrentUser, pc.KeyPath + @"\User\Run", Registry.CurrentUser, pc.KeyPath + @"\Machine\Run", pc.KeyPath + @"\Machine\Run32",
            pc.KeyPath + @"\Approved", pc.UserFolder, pc.CommonFolder));
        check(StartupAppsService.IsMachine(StartupSource.MachineRun) && !StartupAppsService.IsMachine(StartupSource.UserRun) && !StartupAppsService.IsMachine(StartupSource.UserFolder)
            && StartupAppsService.IsMachine(StartupSource.CommonFolder) && machineOnly.List().Count > 0,
            "All-users entries (Run, Run32, common Startup folder) use the all-users flag, like Task Manager");
    }

    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        using var pc = new Fixture(fixture, "ui");
        Populate(pc);
        var window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "startup-ui", "preferences.json"), startupApps: pc.Apps, serviceSystem: pc.Services, taskSystem: pc.Tasks, startupLocked: pc.Locked)
        { ShowActivated = false, ShowInTaskbar = false };
        var oldHistory = MainWindow.TaskHistoryDirectory;
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string Text(string name) => ((TextBlock)window.FindName(name)).Text;
            Click("SettingsButton");
            UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            var card = (Expander)window.FindName("SettingsStartupSection");
            // Phase D (owner, November 20, 2026): Startup is a tab of the Tweaks page; Settings no longer shows it, and nothing is read until Tweaks opens.
            check(!card.IsVisible && window.StartupChoices.Count == 0 && !card.IsExpanded, "Startup is not on Settings any more, and nothing is read until Tweaks is opened");
            window.OpenTweaksPage("Startup");
            UiLayoutChecks.PumpUntil(() => card.IsVisible && window.OpenTweaksTab == "Startup");
            UiLayoutChecks.PumpUntil(() => window.StartupChoices.Count > 5);
            // Owner, November 20, 2026 (the Software Updates layout): one icon tab per source, in a fixed order; registry and folder apps share the Apps tab under
            // their two headings; another tab is a plain list of its source only.
            var view = (System.Windows.Data.ListCollectionView)((ItemsControl)window.FindName("StartupList")).ItemsSource;
            var topics = view.Groups.Cast<System.Windows.Data.CollectionViewGroup>().Select(group => group.Name.ToString()).ToList();
            check(window.StartupTabs.Select(tab => tab.Key).SequenceEqual(["Apps", "Store", "Tasks", "Services", "Locked"]) && window.OpenStartupTab == "Apps"
                && window.StartupTabs.All(tab => tab.IconGeometry != null && int.Parse(tab.Count) > 0) && window.StartupTabs.Sum(tab => int.Parse(tab.Count)) == window.StartupChoices.Count
                && topics.SequenceEqual(StartupTuning.TopicOrder.Take(2)) && Text("StartupSummary").StartsWith("7 of ") && Text("StartupSummary").Contains("start with Windows"),
                $"Startup has an icon tab per source with its count, opening on Apps with the registry and folder headings ({string.Join(" | ", topics)}; {Text("StartupSummary")})");
            window.ShowStartupTab("Services");
            check(view.Groups is null or { Count: 0 } &&view.Cast<StartupItemChoice>().All(choice => StartupTuning.TopicOrder[4] == choice.Candidate.Topic) && view.Count > 0,
                "Another source tab is a plain list of that source only");
            window.ShowStartupTab("Apps");
            var locked = window.StartupChoices.First(choice => choice.Title == "CorpAgent"); // the fixture reads policies of both hives from one key, so it shows twice
            check(!locked.CanEdit && locked.Status.StartsWith("Shown only"), "A locked line cannot be switched and says why");
            UiLayoutChecks.Layout(root, new Size(920, 820));
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots)
            {
                var scroll = (ScrollViewer)window.FindName("TweaksScroll");
                foreach (var toggle in UiLayoutChecks.Descendants<System.Windows.Controls.Primitives.ToggleButton>(card).Where(toggle => toggle.Uid == "topic").Take(2)) toggle.IsChecked = true;
                UiLayoutChecks.Wait(400); UiLayoutChecks.Layout(root, new Size(920, 820));
                scroll.ScrollToVerticalOffset(Math.Max(0, card.TransformToAncestor((System.Windows.Media.Visual)scroll.Content).Transform(new Point()).Y - 10)); UiLayoutChecks.Wait(150);
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "startup-card-dark.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "startup-card-light.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500);
            }

            // Staged into the one Review changes button with everything else; confirmed; changed; reported; Undo.
            var spotify = window.StartupChoices.Single(choice => choice.Title == "Spotify");
            var service = window.StartupChoices.Single(choice => choice.Title == "Vendor Helper Service");
            spotify.On = true; service.On = true;
            var review = (Button)window.FindName("ApplyWindowsSettingsButton");
            check(spotify.Status.StartsWith("Will be turned off after you review") && review.IsEnabled && review.Content?.ToString() == "Review 2 changes",
                "Switching ON stages 'stop starting with Windows' and the one Review button counts it");
            Click("ApplyWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(Text("ConsentTitle") == "Apply 2 changes?" && Text("ConsentText").Contains("Stop starting with Windows (2)") && Text("ConsentText").Contains("Spotify"), "Review lists what will stop starting with Windows");
            Click("ConsentConfirmButton");
            UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ResultOverlay")).Visibility == Visibility.Visible && Text("ResultTitle") == "Changes applied");
            // Owner, November 20, 2026 (mockup A): a clear success mark, the checked list, and ONE centered OK.
            var ok = (Button)window.FindName("ResultOkButton");
            check(ok.Content as string == "OK" && ok.HorizontalAlignment == HorizontalAlignment.Center && ((FrameworkElement)window.FindName("ResultCheck")).Visibility == Visibility.Visible
                && window.ResultLinesShown.Count > 0 && window.ResultLinesShown.All(line => line.Ok) && Text("ResultSubtitle") == "2 changes made and checked",
                $"Applied changes end in the success popup: a green check, every change checked off, one centered OK ({Text("ResultSubtitle")})");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string resultShots)
            {
                UiLayoutChecks.Wait(800); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(resultShots, "changes-applied-dark.png"));
            }
            Click("ResultOkButton");
            UiLayoutChecks.PumpUntil(() => window.Startup.HasChanges && ((Button)window.FindName("StartupUndoButton")).Visibility == Visibility.Visible);
            check(StartupAppsService.IsDisabled(pc.Get(@"Approved\Run", "Spotify") as byte[]) && pc.Services.Read("VendorSvc")!.Start == 3 && Text("StartupStatus").Contains("no longer start with Windows"),
                "Confirming changes exactly those two and says so");
            Click("StartupUndoButton");
            UiLayoutChecks.PumpUntil(() => !window.Startup.HasChanges);
            check(pc.Get(@"Approved\Run", "Spotify") == null && pc.Services.Read("VendorSvc")!.Start == 2 && Text("StartupStatus").Contains("start with Windows again"), "Undo lets them start with Windows again");

            // Without administrator rights a task or service needs Windows' prompt; Hush restarts for the one review with the same choice.
            pc.Tasks.Admin = false;
            UiLayoutChecks.PumpUntil(() => window.StartupChoices.Any(choice => choice.Title == "Vendor Agent at logon" && !choice.WasOff));
            window.StartupChoices.Single(choice => choice.Title == "Vendor Agent at logon").On = true;
            var launched = new List<string>(); var oldLauncher = MainWindow.ElevatedLauncher; var oldExit = MainWindow.ExitForRestart;
            MainWindow.ElevatedLauncher = argument => { launched.Add(argument); return true; }; MainWindow.ExitForRestart = () => launched.Add("exit");
            try
            {
                Click("ApplyWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
                check(((Button)window.FindName("ConsentConfirmButton")).Content?.ToString() == "Restart as administrator", "A sign-in task needs administrator rights: Review offers Restart as administrator");
                Click("ConsentConfirmButton"); UiLayoutChecks.PumpUntil(() => launched.Count == 2);
                string payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(launched[0][(MainWindow.ReviewAllArgument.Length + 1)..]));
                check(payload.Contains("task|\\\\Vendor\\\\Vendor Agent at logon") && pc.Tasks.Read(@"\Vendor\Vendor Agent at logon")!.Enabled, "The restart carries the same choice and nothing changed before it");
            }
            finally { MainWindow.ElevatedLauncher = oldLauncher; MainWindow.ExitForRestart = oldExit; pc.Tasks.Admin = true; }
        }
        finally { window.Close(); MainWindow.TaskHistoryDirectory = oldHistory; }
    }
}
