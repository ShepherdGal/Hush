using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hush;

// Phase C (owner, November 19, 2026): software updates, duplicate and large files, disk check, registry size, new junk categories and the new tweaks.
// Fakes for winget, the disk check, the registry measurement and the Recycle Bin; temp folders only.
internal static class SystemCareChecks
{
    internal sealed class FakeUpdater : ISoftwareUpdater
    {
        public readonly List<string> Updated = [];
        public bool Available => true;
        public Task<IReadOnlyList<AppUpdate>> ListAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<AppUpdate>>(
            [new("Contoso Editor", "Contoso.Editor", "1.0", "2.0", "winget"), new("Fabrikam Player", "Fabrikam.Player", "3.1", "3.2", "winget")]);
        // Hold: a test keeps an update running until it lets go (to see the rows progress bar mid-update).
        public TaskCompletionSource? Hold;
        public async Task<(bool Ok, string Message)> UpdateAsync(AppUpdate update, CancellationToken token) { if (Hold != null) await Hold.Task; Updated.Add(update.Id); return (true, "Updated to " + update.Available + "."); }
    }

    // Administrator mode as a test says it is; launching never happens (no permission prompt in a test).
    private sealed class FixedElevation(bool elevated) : IElevation
    {
        public bool IsElevated => elevated;
        public bool IsSignedInUser => true;
        public bool Launch(string exe, string arguments, bool wait, out int exitCode) { exitCode = 0; return false; }
    }

    // Driver updates: a Bluetooth driver with a known installed version and a graphics driver without one; nothing real is searched or installed.
    internal sealed class FakeDrivers : IDriverUpdates
    {
        public readonly List<string> Installed = [];
        public bool UpdateOff;
        public Task<IReadOnlyList<DriverUpdate>> SearchAsync(CancellationToken token) => UpdateOff ? Task.FromException<IReadOnlyList<DriverUpdate>>(new WindowsUpdateOffException())
            : Task.FromResult<IReadOnlyList<DriverUpdate>>(
            [
                new("bt-1", "Contoso - Bluetooth - 23.40.0.2", "Contoso Wireless Bluetooth", "Bluetooth", "Contoso", "23.40.0.2", "22.150.0.6", new DateTime(2026, 10, 3), 12_400_000),
                new("gpu-1", "Fabrikam - Display - 32.0.101.6", "Fabrikam Graphics", "Graphics", "Fabrikam", "32.0.101.6", null, new DateTime(2026, 9, 18), 640_000_000),
            ]);
        public Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdate> chosen, IProgress<string>? progress, CancellationToken token)
        {
            Installed.AddRange(chosen.Select(driver => driver.UpdateId));
            return Task.FromResult(new DriverInstallResult(chosen.Count, 0, true, chosen.ToDictionary(driver => driver.UpdateId, _ => "Installed")));
        }
    }

    internal sealed class FakeDisk : IDiskCheck
    {
        public readonly List<string> Repairs = [];
        public Task<DiskCheckResult> CheckAsync(string drive, CancellationToken token) => Task.FromResult(new DiskCheckResult(true, true, "Windows found problems on this drive. A repair at the next restart can fix them.", ""));
        public Task<string?> ScheduleRepairAsync(string drive) { Repairs.Add(drive); return Task.FromResult<string?>(null); }
        public Task<IReadOnlyList<DriveHealth>> HealthAsync() => Task.FromResult<IReadOnlyList<DriveHealth>>([new("Test SSD", "SSD", "Healthy")]);
    }

    internal sealed class FakeCompactor : IRegistryCompactor
    {
        public Task<IReadOnlyList<HiveSize>> MeasureAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<HiveSize>>([new("This account", 8 << 20, 6 << 20)]);
    }

    internal static void RunEngine(string fixture, Action<bool, string> check)
    {
        // Owner, November 20, 2026 (Codex CLI: "the requested operation is not permitted"): an update refused with 0x8A15007D in an elevated Hush is run again as
        // the person; fakes only, nothing is updated.
        var oldRunner = WingetUpdater.Runner; var oldUser = WingetUpdater.UserRunner; var oldElevated = WingetUpdater.IsElevated;
        try
        {
            var asUser = new List<string>();
            WingetUpdater.Runner = (args, _, _) => Task.FromResult((WingetUpdater.AdminContextRefused, "The requested operation is not permitted"));
            WingetUpdater.UserRunner = (exe, args, _, _) => { asUser.Add(args); return Task.FromResult((0, "Successfully installed")); };
            WingetUpdater.IsElevated = () => true;
            var winget = new WingetUpdater();
            if (winget.Available)
            {
                var (ok, message) = winget.UpdateAsync(new AppUpdate("Codex CLI", "OpenAI.Codex", "0.161.0", "0.171.0", "winget"), CancellationToken.None).GetAwaiter().GetResult();
                check(ok && asUser.Count == 1 && asUser[0].Contains("--id \"OpenAI.Codex\""), "An update Windows refuses from administrator mode (a program installed for your account) is run again as you: " + message);
                WingetUpdater.IsElevated = () => false; asUser.Clear();
                var (notOk, refused) = winget.UpdateAsync(new AppUpdate("Codex CLI", "OpenAI.Codex", "0.161.0", "0.171.0", "winget"), CancellationToken.None).GetAwaiter().GetResult();
                check(!notOk && asUser.Count == 0 && refused.Contains("not permitted"), "Without administrator mode there is nothing to retry: the refusal is reported as it is");
            }
        }
        finally { WingetUpdater.Runner = oldRunner; WingetUpdater.UserRunner = oldUser; WingetUpdater.IsElevated = oldElevated; }

        // winget's table.
        string table = "Name              Id                 Version  Available Source\r\n----------------------------------------------------------------\r\nContoso Editor    Contoso.Editor     1.0      2.0       winget\r\nFabrikam Player   Fabrikam.Player    3.1.4    3.2       winget\r\n2 upgrades available.\r\n";
        var parsed = WingetUpdater.ParseTable("   - \r\n" + table);
        check(parsed.Count == 2 && parsed[0] is { Name: "Contoso Editor", Id: "Contoso.Editor", Version: "1.0", Available: "2.0", Source: "winget" } && parsed[1].Version == "3.1.4",
            $"winget's update table is read into name, id, versions and source ({parsed.Count})");
        check(WingetUpdater.ParseTable("No installed package found matching input criteria.").Count == 0, "No table means no updates");

        // Programs are grouped by what they are, not by the first letter (owner, November 20, 2026).
        var kinds = new (string Name, string Id, string Kind)[]
        {
            ("Google Chrome", "Google.Chrome", ProgramKinds.Browsers), ("Mozilla Firefox", "Mozilla.Firefox", ProgramKinds.Browsers), ("Microsoft Edge", "Microsoft.Edge", ProgramKinds.Browsers),
            ("Discord", "Discord.Discord", ProgramKinds.Chat), ("Zoom Workplace", "Zoom.Zoom", ProgramKinds.Chat), ("VLC media player", "VideoLAN.VLC", ProgramKinds.Media),
            ("Spotify", "Spotify.Spotify", ProgramKinds.Media), ("Steam", "Valve.Steam", ProgramKinds.Games), ("Visual Studio Code", "Microsoft.VisualStudioCode", ProgramKinds.Developer),
            ("Git", "Git.Git", ProgramKinds.Developer), ("7-Zip", "7zip.7zip", ProgramKinds.Utilities), ("Contoso Editor", "Contoso.Editor", ProgramKinds.Other),
            ("Barcode Tool", "Acme.Barcode", ProgramKinds.Other),   // "code" inside another word is not a developer tool
        };
        var wrongKinds = kinds.Where(k => ProgramKinds.Of(k.Name, k.Id) != k.Kind).Select(k => $"{k.Name} is {ProgramKinds.Of(k.Name, k.Id)}").ToList();
        check(wrongKinds.Count == 0 && UpdateIcons.Of("Bluetooth") != null && UpdateIcons.Of("Graphics") != null && UpdateIcons.Of("Audio") != null && UpdateIcons.Of("Network") != null
            && UpdateIcons.Of("Other devices") != null && ProgramKinds.Order.All(kind => UpdateIcons.Of(kind) != null) && DriverCategories.Order.All(category => UpdateIcons.Of(category) != null),
            "Programs are recognized by kind and every device type and kind has an icon" + (wrongKinds.Count > 0 ? ": " + string.Join(", ", wrongKinds) : ""));

        // Duplicates: equal content in two folders, a same-size different file, a large file.
        string root = Path.Combine(fixture, "systemcare");
        string a = Path.Combine(root, "a"), b = Path.Combine(root, "b");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        var data = new byte[200_000]; new Random(5).NextBytes(data);
        File.WriteAllBytes(Path.Combine(a, "photo.jpg"), data); File.WriteAllBytes(Path.Combine(b, "photo copy.jpg"), data); File.WriteAllBytes(Path.Combine(b, "third.jpg"), data);
        var other = (byte[])data.Clone(); other[^1] ^= 1; File.WriteAllBytes(Path.Combine(b, "almost.jpg"), other);
        File.WriteAllBytes(Path.Combine(a, "big.bin"), new byte[600_000]);
        var result = FileFinder.Search([a, b], minimumDuplicate: 100_000, largeFile: 500_000, null, CancellationToken.None);
        check(result.Duplicates.Count == 1 && result.Duplicates[0].Files.Count == 3 && result.Duplicates[0].Wasted == 400_000 && result.Large.Single().Path.EndsWith("big.bin"),
            $"Exact copies are found by content (a same-size different file is not); the large file is listed ({result.Duplicates.Count} sets)");
        var set = new List<FoundFileRow>();
        foreach (var file in result.Duplicates[0].Files) set.Add(new FoundFileRow(file, set));
        set[0].Checked = true; set[1].Checked = true; set[2].Checked = true;
        check(set.Count(row => row.Checked) == 2 && !set[2].Checked, "Every copy but one can be ticked: the last copy always stays");
        var recycled = new List<string>();
        var (moved, problems) = FileFinder.Recycle([Path.Combine(b, "third.jpg"), Path.Combine(b, "nope.jpg")], path => { recycled.Add(path); File.Delete(path); return true; });
        check(moved == 1 && problems.Count == 1 && recycled.Count == 1, "Removing goes through the Recycle Bin path and reports a missing file");
        check(FileFinder.Skipped(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) && FileFinder.Skipped(Path.Combine(Path.GetPathRoot(root)!, "$Recycle.Bin")) && !FileFinder.Skipped(a), "Windows and system folders are skipped, chosen folders are not");

        // New categories and tweaks.
        check(ReviewCategory.SafetyOf("FontCache") == SafetyLevel.Advanced && ReviewCategory.SafetyOf("ExplorerCaches") == SafetyLevel.Advanced && ReviewCategory.SafetyOf("DnsCache") == SafetyLevel.Review,
            "The font cache and Explorer caches are Advanced, the DNS cache is Review");
        check(RegistryTraces.Lists.Count(rule => rule.Category == "ExplorerCaches") == 5, "Five Explorer caches are offered as registry lists");
        check(WindowsTweaks.Catalog.Count(t => t.Topic == WindowsTweaks.TopicPerformance) == 3 && WindowsTweaks.Catalog.Count(t => t.Topic == WindowsTweaks.TopicNetwork) == 3, "Three performance and three gaming/network tweaks");
    }

    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        var updater = new FakeUpdater(); var disk = new FakeDisk(); var drivers = new FakeDrivers();
        var oldUpdater = MainWindow.SoftwareUpdaterFactory; var oldDisk = MainWindow.DiskCheckFactory; var oldDrives = MainWindow.DiskDrivesFactory; var oldMin = MainWindow.DuplicateMinimumBytes; var oldRecycle = MainWindow.RecycleForTests; var oldDrivers = MainWindow.DriverUpdatesFactory; var oldElevation = AdminMode.Elevation;
        MainWindow.SoftwareUpdaterFactory = () => updater; MainWindow.DriverUpdatesFactory = () => drivers; MainWindow.DiskCheckFactory = () => disk; MainWindow.DiskDrivesFactory = () => [new("Z:\\", "Z: Test")]; MainWindow.DuplicateMinimumBytes = 1000;
        MainWindow.RecycleForTests = path => { File.Delete(path); return true; };
        string folder = Path.Combine(fixture, "systemcare-ui", "files");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "one.txt"), new string('x', 5000)); File.WriteAllText(Path.Combine(folder, "two.txt"), new string('x', 5000));
        var window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "systemcare-ui", "preferences.json")) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            T Find<T>(string name) where T : class => (T)window.FindName(name);
            void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string Text(string name) => Find<TextBlock>(name).Text;
            Click("ToolsButton");
            UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            // The four system-care tools are rail entries until one is opened (only the open tool is read), and nothing has run.
            var tileKeys = window.ToolRail.Select(entry => entry.Key).ToList();
            check(window.ToolsHomeShown && new[] { "Updates", "Duplicates", "Disk", "RegistrySize" }.All(tileKeys.Contains)
                && new[] { "ToolsUpdatesSection", "ToolsDuplicatesSection", "ToolsDiskSection", "ToolsRegistrySizeSection" }.All(name => Find<Expander>(name) is { IsVisible: false, IsExpanded: false }) && updater.Updated.Count == 0,
                "The four system-care tools are in the rail, not yet read, and nothing has run");

            // Owner, November 21, 2026: update rows show the program's own icon, matched by name from the installed programs (a longer installed name counts,
            // a name shorter than four letters never matches, and a program without a readable icon keeps its letter tile).
            var installedFake = new ToolsChecks.FakePrograms();
            installedFake.Installed.Add(new InstalledProgram("k1", "k1", "Mozilla Firefox (x64 en-US)", "Mozilla", "1", 300, null, null, "u", false, false, "C:\\fx.exe,0"));
            installedFake.Installed.Add(new InstalledProgram("k2", "k2", "Zed", "Zed", "1", 10, null, null, "u", false, false, "C:\\zed.exe"));
            var iconImage = new System.Windows.Media.DrawingImage(); iconImage.Freeze();
            var matched = MainWindow.MatchInstalled(installedFake, ["Mozilla Firefox", "Zed", "Unknown Tool"], path => path.StartsWith("C:\\fx.exe") ? iconImage : null);
            check(matched.TryGetValue("Mozilla Firefox", out var fox) && ReferenceEquals(fox.Icon, iconImage) && fox.Bytes == 300 && !matched.ContainsKey("Zed") && !matched.ContainsKey("Unknown Tool"),
                "An update finds its installed program's own icon and size by name; short or unknown names keep the letter tile");

            // Software updates (owner, November 20, 2026): Programs and one tab per device type that has a driver update, a smart summary, Hush installs.
            Find<Expander>("ToolsUpdatesSection").IsExpanded = true;
            UiLayoutChecks.PumpUntil(() => window.AppUpdateRows.Count == 2 && window.DriverUpdateRows.Count == 2);
            // Owner, November 20, 2026: never blank while searching (mockup B): named steps tick off with what each found, then the steps leave.
            var wait = Find<WaitSteps>("UpdatesWait");
            UiLayoutChecks.PumpUntil(() => wait.Shown.Count == 2 && wait.Shown.All(step => step.State == "done"));
            check(wait.Shown.Select(step => step.Title).SequenceEqual(["Your programs", "Drivers from Windows Update"])
                && wait.Shown[0].Detail == "2 updates found" && wait.Shown[1].Detail == "2 driver updates found" && !wait.IsSpinning,
                $"The search shows its steps ticking off with what each found ({string.Join(" | ", wait.Shown.Select(step => step.Title + ": " + step.Detail))})");
            UiLayoutChecks.PumpUntil(() => !wait.IsVisible);
            check(window.UpdateTabs.Select(tab => tab.Key).SequenceEqual(["Programs", "Graphics", "Bluetooth"]) && window.UpdateTabs.All(tab => tab.Count == (tab.Key == "Programs" ? "2" : "1"))
                && window.OpenUpdatesTab == "Programs" && !Find<Button>("UpdateSelectedButton").IsEnabled,
                "The tabs are Programs and only the device types that have an update (in their fixed order), each with its count; nothing is ticked");
            check(window.UpdatesSummaryShown == "4 updates ready · 2 programs, 2 drivers",
                "The summary says that updates are ready, then only a short split (owner: the longer line was too cluttered): " + window.UpdatesSummaryShown);
            var headlineRun = (System.Windows.Documents.Run)window.FindName("UpdatesHeadline"); var detailRun = (System.Windows.Documents.Run)window.FindName("UpdatesDetail");
            check(headlineRun.Text == "4 updates ready" && headlineRun.FontWeight == FontWeights.SemiBold && headlineRun.Foreground is SolidColorBrush ready
                && Find<TextBlock>("UpdatesSummary").Foreground is SolidColorBrush rest && ready.Color != rest.Color && detailRun.Text == " · 2 programs, 2 drivers",
                "\"4 updates ready\" stands out in the accent color; the rest of the line is muted");
            // The tabs have their own icon, and a count badge that is accent while something waits.
            check(window.UpdateTabs.All(tab => tab.IconGeometry != null && tab.HasUpdates) && window.UpdateTabs.Select(tab => tab.IconGeometry).Distinct().Count() == 3,
                "Every tab has an icon of its own (apps, monitor, Bluetooth) and an accent count badge");
            window.ShowTool("Updates"); UiLayoutChecks.Wait(250);
            // Programs by kind (owner: "by kind of program, in a switch like Size | Name"), not by letters.
            var programView = (System.ComponentModel.ICollectionView)Find<ItemsControl>("UpdatesList").ItemsSource;
            check(window.UpdatesSort == "kind" && programView.Groups?.Cast<System.Windows.Data.CollectionViewGroup>().Select(group => group.Name as string).SequenceEqual(["Media", "Other"]) == true
                && Find<FrameworkElement>("UpdatesSortBar").Visibility == Visibility.Visible,
                "Programs start grouped by kind (Media, Other), with the Kind / Size / Name switch above the list");
            // The sort-pill rule (owner, November 20, 2026): the switch is the top bar's pill at the LEFT of the list's top row, and Update selected sits on that
            // same row at the right, level with it, so the bulk action is always in view.
            {
                var sortPill = Find<Border>("UpdatesSortBar"); var updateButton = Find<Button>("UpdateSelectedButton"); var bar = Find<FrameworkElement>("UpdatesActionBar");
                UiLayoutChecks.Layout(root, new Size(920, 820));
                var pillAt = sortPill.TranslatePoint(new Point(0, sortPill.ActualHeight / 2), bar); var buttonAt = updateButton.TranslatePoint(new Point(0, updateButton.ActualHeight / 2), bar);
                check(sortPill.Style == (Style)Application.Current.FindResource("SortPill") && pillAt.X < 1 && buttonAt.X > bar.ActualWidth / 2
                    && Math.Abs(pillAt.Y - buttonAt.Y) < 1 && Math.Abs(buttonAt.X + updateButton.ActualWidth - bar.ActualWidth) < 1,
                    $"The sort pill sits at the left and Update selected at the right of one row, centered on the same line (pill {pillAt}, button {buttonAt}, row {bar.ActualWidth})");
                // The same rule everywhere: every sort button in the XAML is a SegmentChoice of the top-bar pill, and no sort pill is placed in a right-hand column.
                string sourceXaml = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Hush", "MainWindow.xaml")));
                var sortButtons = System.Text.RegularExpressions.Regex.Matches(sourceXaml, "<Button [^>]*Sort_Click\"[^>]*>").Select(match => match.Value).ToList();
                var pills = System.Text.RegularExpressions.Regex.Matches(sourceXaml, "<Border [^>]*SortPill[^>]*>").Select(match => match.Value).ToList();
                check(sortButtons.Count >= 6 && sortButtons.All(button => button.Contains("{StaticResource SegmentChoice}")) && pills.Count >= 2
                    && pills.All(pill => !pill.Contains("Grid.Column") && !pill.Contains("HorizontalAlignment=\"Right\"")),
                    $"Every sort switch is the top bar's pill at the left of its row ({sortButtons.Count} sort buttons, {pills.Count} pills)");
            }
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string programShots)
            {
                UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(programShots, "software-updates-programs-dark.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(programShots, "software-updates-programs-light.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500);
            }
            Click("UpdatesSortNameButton"); UiLayoutChecks.Wait(250);
            check(window.UpdatesSort == "name" && ((System.ComponentModel.ICollectionView)Find<ItemsControl>("UpdatesList").ItemsSource) is { Groups: null or { Count: 0 } } flat
                && flat.Cast<AppUpdateRow>().Select(row => row.Name).SequenceEqual(["Contoso Editor", "Fabrikam Player"]), "Name view is one flat A to Z list");
            Click("UpdatesSortKindButton"); UiLayoutChecks.Wait(250);
            var bluetoothTab = UiLayoutChecks.Descendants<Button>(Find<ItemsControl>("UpdateTabsList")).Single(button => (button.Tag as TweaksTab)?.Key == "Bluetooth");
            bluetoothTab.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); UiLayoutChecks.Wait(200);
            var shownDrivers = Find<ItemsControl>("UpdatesList").Items.Cast<object>().ToList();
            check(window.OpenUpdatesTab == "Bluetooth" && shownDrivers.Count == 1 && shownDrivers[0] is DriverUpdateRow bt && bt.Name == "Contoso Wireless Bluetooth"
                && bt.Detail.StartsWith("22.150.0.6 → 23.40.0.2 · Contoso · released 3 Oct 2026"),
                "The Bluetooth tab lists its driver with the installed and new versions, the maker and the release date");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string updatesShots)
            {
                UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(updatesShots, "software-updates-dark.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(updatesShots, "software-updates-light.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500);
            }
            // Drivers need administrator mode: without it, Update offers the restart and installs nothing.
            AdminMode.Elevation = new FixedElevation(false);
            window.DriverUpdateRows.Single(row => row.Driver.Category == "Bluetooth").Checked = true;
            check(Find<Button>("UpdateSelectedButton").IsEnabled, "Ticking a driver enables Update");
            Click("UpdateSelectedButton"); UiLayoutChecks.PumpUntil(() => Find<FrameworkElement>("ConsentOverlay").Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(Text("ConsentTitle") == "Restart as administrator?" && drivers.Installed.Count == 0, "Without administrator mode a driver install asks to restart as administrator first");
            Click("ConsentCancelButton"); UiLayoutChecks.PumpUntil(() => Find<FrameworkElement>("ConsentOverlay").Visibility != Visibility.Visible); UiLayoutChecks.Wait(200);
            // As administrator: one confirmation for the program and the driver together, then only the ticked ones.
            AdminMode.Elevation = new FixedElevation(true);
            window.AppUpdateRows[0].Checked = true;
            check(Find<Button>("UpdateSelectedButton").Content?.ToString() == "Update 2", "Update counts every ticked update across the tabs");
            Click("UpdateSelectedButton"); UiLayoutChecks.PumpUntil(() => Find<FrameworkElement>("ConsentOverlay").Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(Text("ConsentTitle") == "Update 1 program and 1 driver?", "Update asks first: " + Text("ConsentTitle"));
            Click("ConsentConfirmButton");
            UiLayoutChecks.PumpUntil(() => Text("UpdatesStatus").StartsWith("Updated 1 program"));
            check(updater.Updated.SequenceEqual(["Contoso.Editor"]) && drivers.Installed.SequenceEqual(["bt-1"]) && window.AppUpdateRows[0].Detail.Contains("Updated to 2.0")
                && Text("UpdatesStatus").Contains("Installed 1 driver") && Text("UpdatesStatus").Contains("Restart Windows"),
                "Only the ticked program and driver are installed; the lines and the status say the result and that a restart finishes it");
            // The Update button on a line (owner, November 20, 2026): a New chip on the right and a button of its own; it asks the same question and updates only that one.
            Click("RefreshUpdatesButton"); UiLayoutChecks.PumpUntil(() => Find<Button>("RefreshUpdatesButton").IsEnabled); UiLayoutChecks.Wait(300);
            UiLayoutChecks.Descendants<Button>(Find<ItemsControl>("UpdateTabsList")).Single(button => (button.Tag as TweaksTab)?.Key == "Programs")
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); UiLayoutChecks.Wait(250);
            var rowButtons = UiLayoutChecks.Descendants<Button>(Find<ItemsControl>("UpdatesList")).Where(button => button.Tag is AppUpdateRow).ToList();
            check(rowButtons.Count == 2 && rowButtons.All(button => button.Content as string == "Update" && button.IsEnabled) && window.AppUpdateRows.All(row => row.NewVisibility == Visibility.Visible),
                "Every program line has its own Update button and a New chip");
            int updatedBefore = updater.Updated.Count;
            rowButtons.Single(button => ((AppUpdateRow)button.Tag).Name == "Fabrikam Player").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiLayoutChecks.PumpUntil(() => Find<FrameworkElement>("ConsentOverlay").Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(Text("ConsentTitle") == "Update 1 program?", "A line's Update button asks first: " + Text("ConsentTitle"));
            updater.Hold = new TaskCompletionSource();
            Click("ConsentConfirmButton");
            // Owner, November 20, 2026: a line that downloads and updates shows a bar that eases toward full, and completes only when the update is done.
            var fabrikamRow = window.AppUpdateRows.Single(row => row.Name == "Fabrikam Player");
            UiLayoutChecks.PumpUntil(() => fabrikamRow.FillState == "working"); UiLayoutChecks.Wait(400);
            var updateBar = UiLayoutChecks.Descendants<Border>(Find<ItemsControl>("UpdatesList")).Single(border => border.Name == "UpdateProgress" && border.DataContext == fabrikamRow);
            double midway = EasedProgress.Shown(updateBar);
            var otherBar = UiLayoutChecks.Descendants<Border>(Find<ItemsControl>("UpdatesList")).Single(border => border.Name == "UpdateProgress" && border.DataContext is AppUpdateRow { Name: "Contoso Editor" });
            check(updateBar.IsVisible && midway is > 0 and < EasedProgress.WorkingCeiling && !otherBar.IsVisible && fabrikamRow.Detail.Contains("downloading and updating"),
                $"While a program updates, its line shows an easing bar that never claims to be finished ({midway:P0} after 0.4 s); other lines show none");
            updater.Hold.SetResult(); updater.Hold = null;
            UiLayoutChecks.PumpUntil(() => updater.Updated.Count > updatedBefore && Find<Button>("RefreshUpdatesButton").IsEnabled && Text("UpdatesStatus").StartsWith("Updated 1 program"));
            UiLayoutChecks.PumpUntil(() => !updateBar.IsVisible);
            check(fabrikamRow.FillState == "done" && Math.Abs(EasedProgress.Shown(updateBar) - 1) < 0.001, "When the update is done the bar completes, then fades away");   // (the status still holds the earlier run's text until this one ends)
            var fabrikam = window.AppUpdateRows.Single(row => row.Name == "Fabrikam Player"); var contoso = window.AppUpdateRows.Single(row => row.Name == "Contoso Editor");
            check(updater.Updated.Skip(updatedBefore).SequenceEqual(["Fabrikam.Player"]) && fabrikam.Succeeded && fabrikam.NewVisibility == Visibility.Collapsed && fabrikam.DoneVisibility == Visibility.Visible
                && !contoso.Succeeded && contoso.NewVisibility == Visibility.Visible && window.UpdatesSummaryShown.StartsWith("3 updates ready"),
                "Only that program was updated: its chip says Updated, the others still wait, and the summary counts what is left: " + window.UpdatesSummaryShown);
            Click("UpdatesSortNameButton"); Click("UpdatesSortKindButton");
            // Windows Update off: drivers can't be checked, and the card offers to turn it back on (no trip to another page).
            drivers.UpdateOff = true;
            Click("RefreshUpdatesButton"); UiLayoutChecks.PumpUntil(() => Find<Button>("RefreshUpdatesButton").IsEnabled); UiLayoutChecks.Wait(100);
            check(Text("UpdatesStatus").Contains("Windows Update is off") && Find<FrameworkElement>("DriversUpdateOnButton").Visibility == Visibility.Visible
                && window.UpdateTabs.Select(tab => tab.Key).SequenceEqual(["Programs"]), "With Windows Update off the card says so and offers to turn it back on right here");
            drivers.UpdateOff = false;

            // Duplicates.
            var dup = Find<Expander>("ToolsDuplicatesSection"); dup.IsExpanded = true; UiLayoutChecks.Wait(200);
            window.AddDuplicateFolders([folder]);
            _ = window.FindDuplicatesAsync();
            UiLayoutChecks.PumpUntil(() => window.FoundFileRows.Count == 2);
            check(window.FoundFileRows.All(row => !row.Checked), "Duplicates are listed with nothing ticked");
            check(window.PlaceChips.Single().Checked && Find<Button>("DuplicateSearchButton").Content as string == "Find in 1 place" && Find<FrameworkElement>("DuplicateSummaryTiles").IsVisible && Text("DuplicateCopiesValue").Length > 0,
                "Duplicate and large files starts friendly (an added folder is a ticked chip, the button says how many places) and shows a summary before the list (owner, November 20, 2026)");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string dupShots) { UiLayoutChecks.Wait(800); UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(dupShots, "duplicates-dark.png")); }
            window.FoundFileRows[0].Checked = true; window.FoundFileRows[1].Checked = true;
            check(window.FoundFileRows.Count(row => row.Checked) == 1, "The second copy cannot be ticked too");
            Click("DuplicateRecycleButton"); UiLayoutChecks.PumpUntil(() => Find<FrameworkElement>("ConsentOverlay").Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            Click("ConsentConfirmButton");
            UiLayoutChecks.PumpUntil(() => Text("DuplicatesStatus").StartsWith("Moved"));
            check(Directory.GetFiles(folder).Length == 1, "One copy went to the Recycle Bin, one stays");

            // Disk check.
            var diskCard = Find<Expander>("ToolsDiskSection"); diskCard.IsExpanded = true;
            UiLayoutChecks.PumpUntil(() => Text("DiskHealthText").Contains("Healthy"));
            check(Find<FrameworkElement>("DiskHealthRow").IsVisible && Text("DiskHealthChip") == "Healthy" && window.DiskRows.Count == 1 && !Find<WaitSteps>("DiskWait").IsVisible,
                "Disk check opens with drive health in friendly words and a chip, and one card per drive (owner, November 20, 2026)");
            var checkButton = UiLayoutChecks.Descendants<Button>(Find<ItemsControl>("DiskDrivesList")).First();
            checkButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiLayoutChecks.PumpUntil(() => Find<Button>("DiskRepairButton").Visibility == Visibility.Visible);
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string diskShots) { UiLayoutChecks.Wait(300); UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(diskShots, "disk-check-dark.png")); }
            check(Text("DiskStatus").Contains("found problems") && window.DiskRows[0].State == "problems" && window.DiskRows[0].Chip == "Problems found", "A check with problems offers a repair at the next restart");
            Click("DiskRepairButton"); UiLayoutChecks.PumpUntil(() => Find<FrameworkElement>("ConsentOverlay").Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            Click("ConsentConfirmButton");
            UiLayoutChecks.PumpUntil(() => disk.Repairs.Count == 1);
            check(disk.Repairs.Single() == "Z:\\", "The repair is scheduled only after asking");

            UiLayoutChecks.Layout(root, new Size(920, 820));
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots)
            {
                window.ShowTool("Updates"); UiLayoutChecks.Wait(150); UiLayoutChecks.Layout(root, new Size(920, 820));   // one tool at a time in the card (Phase D)
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "systemcare-dark.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "systemcare-light.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500);
            }
        }
        finally
        {
            window.Close();
            MainWindow.SoftwareUpdaterFactory = oldUpdater; MainWindow.DriverUpdatesFactory = oldDrivers; AdminMode.Elevation = oldElevation; MainWindow.DiskCheckFactory = oldDisk; MainWindow.DiskDrivesFactory = oldDrives; MainWindow.DuplicateMinimumBytes = oldMin; MainWindow.RecycleForTests = oldRecycle;
        }
    }
}
