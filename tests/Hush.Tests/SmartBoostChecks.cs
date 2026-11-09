using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Hush;

// Smart Boost: settings, startup apps, resource readings, low-resource mode, junk monitoring, Quick Boost and Automatic Boost.
// Fixtures only: a HKCU\Software\Hush.TestFixtures\<GUID> registry key, temp folders and synthetic files. The real startup list is never written.
internal static class SmartBoostChecks
{
    private static string Make(string folder, string name, int length = 100)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[length]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-30)); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        return path;
    }

    private sealed class FakeBooster : IMemoryBooster
    {
        public int Calls;
        public MemoryBoostResult Release(CancellationToken token) { Calls++; return new(7, 1000L * 1048576, 877L * 1048576); }
    }

    private sealed class StartupFixture : IDisposable
    {
        public readonly string KeyPath = @"Software\Hush.TestFixtures\" + Guid.NewGuid().ToString("N");
        public readonly string UserFolder, CommonFolder;
        public readonly StartupLocations Locations;
        public StartupFixture(string fixture)
        {
            UserFolder = Path.Combine(fixture, "startup-user"); CommonFolder = Path.Combine(fixture, "startup-common");
            Directory.CreateDirectory(UserFolder); Directory.CreateDirectory(CommonFolder);
            Locations = new(Registry.CurrentUser, KeyPath + @"\UserRun", Registry.CurrentUser, KeyPath + @"\MachineRun", KeyPath + @"\MachineRun32", KeyPath + @"\Approved", UserFolder, CommonFolder);
        }
        public void SetRun(string subKey, string name, string command)
        { using var key = Registry.CurrentUser.CreateSubKey(KeyPath + "\\" + subKey); key.SetValue(name, command); }
        public string? RunValue(string subKey, string name)
        { using var key = Registry.CurrentUser.OpenSubKey(KeyPath + "\\" + subKey); return key?.GetValue(name) as string; }
        public void Dispose() { try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false); } catch { } }
    }

    internal static void RunEngine(string fixture, Action<bool, string> check)
    {
        // Settings: round trip, hand-edited values snap to offered choices, Automatic Boost needs its recorded consent, theme and Smart Boost share one file.
        string file = Path.Combine(fixture, "boost-preferences.json");
        var prefs = new Preferences { Light = true };
        prefs.Boost.MonitorJunk = true; prefs.Boost.ThresholdMb = 2000; prefs.Boost.CheckEveryMinutes = 360;
        prefs.Save(file);
        var loaded = Preferences.Load(file);
        check(loaded.Light && loaded.Boost.MonitorJunk && loaded.Boost.ThresholdMb == 2000 && loaded.Boost.CheckEveryMinutes == 360 && !loaded.Boost.AutoBoost && !loaded.Boost.LowResource,
            "Smart Boost settings round-trip beside the theme and everything starts off");
        File.WriteAllText(file, "{\"Light\":false,\"Boost\":{\"AutoBoost\":true,\"AutoBoostConsent\":false,\"ThresholdMb\":3,\"CheckEveryMinutes\":1}}");
        var edited = Preferences.Load(file);
        check(!edited.Boost.AutoBoost && edited.Boost.ThresholdMb == 500 && edited.Boost.CheckEveryMinutes == 15,
            "Automatic Boost without recorded consent is off and out-of-range values snap to the nearest choice (never faster than 15 minutes)");
        File.WriteAllText(file, "not json at all");
        check(!Preferences.Load(file).Light && Directory.GetFiles(fixture, "boost-preferences.json.*.tmp").Length == 0, "A damaged settings file loads as defaults and saving leaves no temp files");

        // Startup apps: Task Manager's flag only; the original entry is never altered or deleted.
        using (var startup = new StartupFixture(fixture))
        {
            startup.SetRun("UserRun", "Alpha", @"C:\Tools\alpha.exe --tray");
            startup.SetRun("MachineRun", "Beta", @"C:\Tools\beta.exe");
            startup.SetRun("MachineRun32", "Gamma", @"C:\Tools\gamma.exe");
            Make(startup.UserFolder, "Delta.lnk"); File.WriteAllText(Path.Combine(startup.UserFolder, "desktop.ini"), "x");
            Make(startup.CommonFolder, "Epsilon.lnk");
            var service = new StartupAppsService(startup.Locations, Path.Combine(fixture, "startup-logs"));
            var listed = service.List();
            check(listed.Count == 5 && listed.All(entry => entry.Enabled) && !listed.Any(entry => entry.Name == "desktop.ini")
                && listed.Single(entry => entry.Name == "Beta").Scope == "All users" && listed.Single(entry => entry.Name == "Alpha").Scope == "Current user",
                "Startup apps from both Run keys and both Startup folders are listed with their scope");
            string alpha = listed.Single(entry => entry.Name == "Alpha").Id;
            check(service.SetEnabled(alpha, false) && !service.List().Single(entry => entry.Name == "Alpha").Enabled && service.List().Count(entry => entry.Enabled) == 4,
                "Turning a startup app off changes only that app");
            check(startup.RunValue("UserRun", "Alpha") == @"C:\Tools\alpha.exe --tray", "The original Run entry is untouched, so turning it on restores exactly what was there");
            check(service.SetEnabled(alpha, true) && service.List().Single(entry => entry.Name == "Alpha").Enabled, "Turning it back on works");
            foreach (string name in new[] { "Beta", "Gamma", "Delta.lnk", "Epsilon.lnk" })
            {
                string id = service.List().Single(entry => entry.Name == name).Id;
                check(service.SetEnabled(id, false) && !service.List().Single(entry => entry.Name == name).Enabled, $"{name} can be turned off without administrator rights");
            }
            check(File.Exists(Path.Combine(startup.UserFolder, "Delta.lnk")) && startup.RunValue("MachineRun", "Beta") != null, "Startup folder shortcuts and all-users entries are never deleted");
            check(!service.SetEnabled("UserRun|Not-listed", false), "Only an entry that is listed right now can be switched");
            check(File.ReadAllLines(Path.Combine(fixture, "startup-logs", "Logs", "startup-changes.jsonl")).Length == 6, "Every startup change is logged");
            check(StartupAppsService.IsDisabled(StartupAppsService.DisabledFlag()) && !StartupAppsService.IsDisabled(StartupAppsService.EnabledFlag())
                && !StartupAppsService.IsDisabled([6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]) && !StartupAppsService.IsDisabled(null), "The flag format matches Task Manager (2/6 enabled, 3 disabled)");
        }

        // The Info card's one-line reading names the most pressing thing first.
        check(new ResourceSnapshot(10, 40, 100, 50, 100, 1).Headline == "Everything looks light."
            && new ResourceSnapshot(10, 90, 100, 50, 100, 1).Headline == "Memory is busy right now."
            && new ResourceSnapshot(95, 10, 100, 50, 100, 1).Headline == "The processor is busy right now."
            && new ResourceSnapshot(95, 90, 100, 5, 100, 1).Headline == "The system drive is almost full.", "The Info summary reads the situation in plain words, most pressing first");

        // Resource readings and low-resource mode.
        var monitor = new ResourceMonitor();
        monitor.Sample(); Thread.Sleep(120);
        var reading = monitor.Sample();
        check(reading.CpuPercent is >= 0 and <= 100 && reading.MemoryTotal > 0 && reading.MemoryUsed > 0 && reading.MemoryUsed <= reading.MemoryTotal
            && reading.DiskTotal > 0 && reading.DiskFree >= 0 && reading.DiskFree <= reading.DiskTotal && reading.HushWorkingSet > 0
            && reading.MemoryPercent is > 0 and <= 100 && reading.DiskUsedPercent is >= 0 and <= 100, "Resource indicators read sensible processor, memory and drive figures");
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
        {
            var original = self.PriorityClass;
            LowResourceMode.Apply(true); self.Refresh();
            bool lowered = self.PriorityClass == System.Diagnostics.ProcessPriorityClass.BelowNormal && LowResourceMode.IsActive && LowResourceMode.ReportIntervalMs == 250;
            LowResourceMode.Apply(false); self.Refresh();
            check(lowered && self.PriorityClass == System.Diagnostics.ProcessPriorityClass.Normal && !LowResourceMode.IsActive && LowResourceMode.ReportIntervalMs == 40,
                "Low-resource mode lowers only Hush's own priority and fully restores it");
            if (original != System.Diagnostics.ProcessPriorityClass.Normal) self.PriorityClass = original;
        }

        // Junk monitoring and Automatic Boost: Safe only.
        string safeFolder = Path.Combine(fixture, "boost-safe"), reviewFolder = Path.Combine(fixture, "boost-review");
        var safeFiles = Enumerable.Range(0, 3).Select(i => Make(safeFolder, $"s{i}.tmp", 1024)).ToArray();
        var reviewFile = Make(reviewFolder, "cache.bin", 4096);
        CleanupEngine BoostEngine() => new([new(safeFolder, "User temp", CleanupKind.Temp, TimeSpan.FromDays(2), Category: "Temp"),
            new(reviewFolder, "Chrome cache", CleanupKind.Cache, TimeSpan.FromDays(2), Category: "BrowserChrome")], dataDirectory: Path.Combine(fixture, "audit-boost"));
        var controller = new SmartBoostController(BoostEngine());
        var junk = controller.Measure(CancellationToken.None);
        check(junk.Files == 3 && junk.Bytes == 3072, "Junk monitoring counts Safe files only (the Review cache is not counted)");
        var settings = new BoostSettings { ThresholdMb = 500 };
        check(!SmartBoostController.ThresholdReached(junk, settings) && SmartBoostController.ThresholdReached(new(600L * 1024 * 1024, 5, DateTime.UtcNow), settings)
            && !SmartBoostController.ThresholdReached(new(0, 0, DateTime.UtcNow), settings), "The cleanup threshold decides when Hush speaks up");
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            check(controller.RunAutomaticBoost(stop.Token) == null && safeFiles.All(File.Exists), "A stopped scan is never cleaned");
        }
        var automatic = controller.RunAutomaticBoost(CancellationToken.None);
        check(automatic is { Deleted: 3, Skipped: 0 } && safeFiles.All(path => !File.Exists(path)) && File.Exists(reviewFile),
            "Automatic Boost cleans exactly the Safe items and leaves Review items for the person");
        check(controller.RunAutomaticBoost(CancellationToken.None) == null && File.ReadAllText(automatic!.LogPath).Contains("Deleted"), "With nothing Safe left it does nothing, and its work is logged");
    }

    // `--verify-smart-boost`: the Settings section, junk monitoring switches, the consent dialog, Quick Boost and shared settings, with real controls.
    internal static void RunUi(string fixture)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Toggle(CheckBox box) { box.IsChecked = !(box.IsChecked == true); box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
        string safeFolder = Path.Combine(fixture, "ui-safe"), reviewFolder = Path.Combine(fixture, "ui-review");
        string safeFile = Make(safeFolder, "quick.tmp", 2048), reviewFile = Make(reviewFolder, "cache.bin", 2048);
        string cookieFolder = Path.Combine(fixture, "ui-cookies"); string cookieFile = Make(cookieFolder, "Cookies", 4096);
        var engine = new CleanupEngine([new(safeFolder, "User temp", CleanupKind.Temp, TimeSpan.FromDays(2), Category: "Temp"),
            new(reviewFolder, "Chrome cache", CleanupKind.Cache, TimeSpan.FromDays(2), Category: "BrowserChrome"),
            new(cookieFolder, "Chrome cookies", CleanupKind.Junk, TimeSpan.Zero, "Cookies;Cookies-journal", Recursive: false, Category: "BrowserCookies")], dataDirectory: Path.Combine(fixture, "audit-ui"));
        var booster = new FakeBooster();
        using var startup = new StartupFixture(fixture);
        startup.SetRun("UserRun", "Alpha", @"C:\Tools\alpha.exe");
        string preferences = Path.Combine(fixture, "boost-ui-preferences.json");
        var window = new MainWindow(engine, preferencesPath: preferences, startupApps: new StartupAppsService(startup.Locations, Path.Combine(fixture, "startup-ui-logs")), memoryBooster: booster);
        try
        {
            var root = (FrameworkElement)window.Content;
            UiLayoutChecks.Layout(root, new Size(920, 820));
            var settingsButton = (Button)window.FindName("SettingsButton");
            var backButton = (Button)window.FindName("SettingsBackButton");
            var monitorBox = (CheckBox)window.FindName("MonitorJunkBox");
            var autoBox = (CheckBox)window.FindName("AutoBoostBox");
            var lowBox = (CheckBox)window.FindName("LowResourceBox");
            var consent = (FrameworkElement)window.FindName("ConsentOverlay");
            UiLayoutChecks.Require(!window.IsBoostTimerRunning && !window.IsResourceTimerRunning && !((Expander)window.FindName("SettingsBoostSection")).IsExpanded && monitorBox.IsChecked != true && autoBox.IsChecked != true && lowBox.IsChecked != true,
                "Smart Boost starts fully off with no background timer");

            Click(settingsButton); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            UiLayoutChecks.PumpUntil(() => window.IsResourceTimerRunning && ((TextBlock)window.FindName("DiskText")).Text.EndsWith("%")); // live readings start just after the page has faded in
            UiLayoutChecks.Require(window.IsResourceTimerRunning && ((TextBlock)window.FindName("MemoryText")).Text != "…" && ((TextBlock)window.FindName("DiskText")).Text.EndsWith("%"),
                "Resource indicators fill in and sample only while Settings is open");
            // The live figures are in their own Info card, separate from the Smart Boost card; every Settings title is weight 500.
            UiLayoutChecks.Layout(root, new Size(920, 820));
            var boostCard = (Expander)window.FindName("SettingsBoostSection");
            var infoCard = (Border)window.FindName("SettingsInfoCard");
            UiLayoutChecks.Require(Descendants<FrameworkElement>(infoCard).Any(element => element.Name == "CpuFill") && !Descendants<FrameworkElement>(boostCard).Any(element => element.Name is "CpuFill" or "MemoryFill" or "DiskFill")
                && infoCard.TransformToAncestor(root).Transform(new Point(0, 0)).Y < boostCard.TransformToAncestor(root).Transform(new Point(0, 0)).Y,
                "Info is its own top card above Smart Boost and the three bars live only in it");
            UiLayoutChecks.Require(!boostCard.IsExpanded && ((FrameworkElement)window.FindName("CpuFill")).ActualWidth > 0 && ((FrameworkElement)window.FindName("MemoryText")).ActualWidth > 0
                && ((FrameworkElement)window.FindName("DiskText")).ActualHeight > 0 && ((FrameworkElement)window.FindName("BoostSummary")).ActualWidth > 0,
                $"The Info card shows the bars and details without opening anything (expanded {boostCard.IsExpanded}, cpuFill {((FrameworkElement)window.FindName("CpuFill")).ActualWidth > 0}, mem {((FrameworkElement)window.FindName("MemoryText")).ActualWidth}, disk {((FrameworkElement)window.FindName("DiskText")).ActualHeight}, summary {((FrameworkElement)window.FindName("BoostSummary")).ActualWidth > 0})");
            var titles = Descendants<TextBlock>(root).Where(block => block.Text is "Settings" or "Info" or "Smart Boost" or "Browser data" or "Privacy & telemetry" or "More impact").ToList();
            UiLayoutChecks.Require(titles.Count == 6 && titles.All(block => block.FontWeight == FontWeights.Medium && block.FontStyle == FontStyles.Normal),
                "The Settings page title and every card title are weight 500 and upright");
            // Page titles are one style: Review files and Settings match in size, weight, style, color and position.
            var settingsTitle = (TextBlock)window.FindName("SettingsTitle");
            var reviewTitle = (TextBlock)window.FindName("ReviewTitle");
            UiLayoutChecks.Require(ReferenceEquals(settingsTitle.Style, reviewTitle.Style) && settingsTitle.FontSize == reviewTitle.FontSize && settingsTitle.FontWeight == reviewTitle.FontWeight
                && settingsTitle.FontStyle == reviewTitle.FontStyle && ((SolidColorBrush)settingsTitle.Foreground).Color == ((SolidColorBrush)reviewTitle.Foreground).Color && settingsTitle.FontSize == 29,
                "Review files and Settings titles share one style: same size, weight, style and color");
            double settingsTop = settingsTitle.TransformToAncestor(root).Transform(new Point(0, 0)).Y;
            window.ShowReviewPage(true); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Layout(root, new Size(920, 820));
            double reviewTop = reviewTitle.TransformToAncestor(root).Transform(new Point(0, 0)).Y;
            Click(settingsButton); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Layout(root, new Size(920, 820));
            UiLayoutChecks.Require(Math.Abs(settingsTop - reviewTop) < 0.5 && reviewTop > 0, $"Review files and Settings titles sit at the same position ({reviewTop:0.#} and {settingsTop:0.#})");
            // Italic audit of everything that exists in the window: only the home heading may be italic (the logo is upright since 0.9).
            var italics = Descendants<TextBlock>(root).Where(block => block.FontStyle == FontStyles.Italic).Select(block => block.Text).Distinct().ToList();
            UiLayoutChecks.Require(italics.All(text => text is "Make more room to breathe.") && italics.Contains("Make more room to breathe."),
                "In the running window only the home heading is italic" + (italics.All(text => text is "Make more room to breathe.") ? "" : ": " + string.Join(" | ", italics)));
            UiLayoutChecks.Require(((ItemsControl)window.FindName("CategoriesList")).Items.Groups == null && Descendants<TextBlock>(root).All(block => block.Text != "Appearance"),
                "Review Files is a flat list again and Settings has no Appearance section");            UiLayoutChecks.Require(window.StartupChoices.Count == 1 && window.StartupChoices[0].Enabled, "The startup list shows this account's startup apps");
            ((Expander)window.FindName("SettingsBoostSection")).IsExpanded = true;
            foreach (var size in new[] { new Size(920, 820), new Size(800, 740) })
            {
                UiLayoutChecks.Layout(root, size); UiLayoutChecks.Wait(350); UiLayoutChecks.Layout(root, size);
                var section = (FrameworkElement)window.FindName("SettingsBoostSection");
                UiLayoutChecks.Require(section.ActualWidth > 300 && ((FrameworkElement)window.FindName("QuickBoostButton")).ActualWidth > 0, "The Smart Boost section lays out at every supported size");
            }

            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string previews)
            {
                // Look at the section itself: scroll it into view and save both themes.
                var boostScroll = (ScrollViewer)window.FindName("WindowsSettingsScroll");
                foreach (bool light in new[] { false, true })
                {
                    if (window.IsLightTheme != light) window.ToggleTheme();
                    UiLayoutChecks.Wait(400);
                    UiLayoutChecks.Layout(root, new Size(920, 820)); boostScroll.ScrollToVerticalOffset(130); UiLayoutChecks.Wait(60);
                    UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(previews, $"smart-boost-{(light ? "light" : "dark")}.png"));
                    boostScroll.ScrollToVerticalOffset(760); UiLayoutChecks.Wait(60);
                    UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(previews, $"smart-boost-startup-{(light ? "light" : "dark")}.png"));
                }
                if (window.IsLightTheme) window.ToggleTheme();
                boostScroll.ScrollToVerticalOffset(0);
            }

            // Startup switch writes only the flag, with Undo.
            var startupSwitch = Descendants<CheckBox>((DependencyObject)window.FindName("StartupList")).Single();
            Toggle(startupSwitch);
            UiLayoutChecks.Require(!window.StartupChoices[0].Enabled && startup.RunValue("UserRun", "Alpha") == @"C:\Tools\alpha.exe"
                && ((Button)window.FindName("UndoStartupButton")).Visibility == Visibility.Visible, "The startup switch turns the app off, keeps its entry and offers Undo");
            Click((Button)window.FindName("UndoStartupButton"));
            UiLayoutChecks.Require(window.StartupChoices[0].Enabled, "Undo restores the startup app");

            // Junk monitoring switch creates and removes the only timer; settings persist beside the theme.
            Toggle(monitorBox);
            UiLayoutChecks.Require(window.BoostSettings.MonitorJunk && window.IsBoostTimerRunning && File.ReadAllText(preferences).Contains("\"MonitorJunk\":true"), "Watching for junk starts one timer and is saved");
            Click((Button)window.FindName("ThemeButton"));
            UiLayoutChecks.PumpUntil(() => window.IsLightTheme);
            string saved = File.ReadAllText(preferences);
            UiLayoutChecks.Require(saved.Contains("\"Light\":true") && saved.Contains("\"MonitorJunk\":true"), "Changing the theme keeps the Smart Boost settings");
            var interval = (RadioButton)window.FindName("Interval360"); interval.IsChecked = true; interval.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiLayoutChecks.Require(window.BoostSettings.CheckEveryMinutes == 360, "Interval choice is applied");
            Toggle(monitorBox);
            UiLayoutChecks.Require(!window.BoostSettings.MonitorJunk && !window.IsBoostTimerRunning, "Turning monitoring off stops the timer");

            // Automatic Boost needs an explicit, recorded consent.
            Toggle(autoBox);
            UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Visible);
            UiLayoutChecks.Require(!window.BoostSettings.AutoBoost && !window.BoostSettings.AutoBoostConsent, "Automatic Boost asks first and does nothing until answered");
            Click((Button)window.FindName("ConsentCancelButton"));
            UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Collapsed);
            UiLayoutChecks.Require(autoBox.IsChecked != true && !window.BoostSettings.AutoBoost && !window.IsBoostTimerRunning, "Declining leaves Automatic Boost off");
            Toggle(autoBox);
            UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Visible);
            Click((Button)window.FindName("ConsentConfirmButton"));
            UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Collapsed && window.BoostSettings.AutoBoost);
            UiLayoutChecks.Require(window.BoostSettings.AutoBoostConsent && window.BoostSettings.MonitorJunk && window.IsBoostTimerRunning && File.ReadAllText(preferences).Contains("\"AutoBoostConsent\":true"),
                "Agreeing records the consent, turns monitoring on and saves");
            Toggle(autoBox);
            UiLayoutChecks.Require(!window.BoostSettings.AutoBoost && window.BoostSettings.AutoBoostConsent, "Automatic Boost can be turned off again at any time");
            Toggle(monitorBox);

            Toggle(lowBox);
            UiLayoutChecks.Require(LowResourceMode.IsActive && window.BoostSettings.LowResource, "Low-resource mode switches on");
            Toggle(lowBox);
            UiLayoutChecks.Require(!LowResourceMode.IsActive, "Low-resource mode switches off and restores Hush");

            UiLayoutChecks.Require(((Button)window.FindName("AboutButton")).FontWeight == FontWeights.Medium && ((Button)window.FindName("AboutButton")).FontStyle == FontStyles.Normal, "The Hush logo is weight 500 and upright");
            UiLayoutChecks.Require(((TextBlock)window.FindName("Headline")).FontStyle == FontStyles.Italic && ((TextBlock)window.FindName("StatusTitle")).FontStyle == FontStyles.Normal
                && ((Button)window.FindName("CompletionLinkButton")).BorderThickness.Left == 1 && ((Button)window.FindName("CompletionLinkButton")).Width == ((Button)window.FindName("HomeCleanButton")).Width
                && ((Button)window.FindName("CompletionLinkButton")).FontSize == ((Button)window.FindName("HomeCleanButton")).FontSize,
                "The main title stays italic; Scan Completed is upright plain text and Take a look is an outlined button exactly like Clean");
            foreach (bool light in new[] { window.IsLightTheme, !window.IsLightTheme })
            {
                if (window.IsLightTheme != light) window.ToggleTheme();
                UiLayoutChecks.Wait(420);
                var cleanRed = ((System.Windows.Media.SolidColorBrush)Application.Current.Resources["CleanTextBrush"]).Color;
                var advancedRed = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(SafetyPalette.Text(SafetyLevel.Advanced, light));
                var warn = ((System.Windows.Media.SolidColorBrush)Application.Current.Resources["MeterWarnBrush"]).Color;
                UiLayoutChecks.Require(cleanRed == advancedRed && cleanRed == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(SafetyPalette.Danger(light))
                    && !ColorRuleChecks.IsRedFamily($"{warn.R:X2}{warn.G:X2}{warn.B:X2}"), $"The running app's Advanced red is the Clean button red and the warning meters are not red ({(light ? "Light" : "Dark")})");
            }
            if (window.IsLightTheme) window.ToggleTheme();
            Click(backButton); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            UiLayoutChecks.Require(!window.IsResourceTimerRunning, "Leaving Settings stops the resource sampling");

            // Quick Boost: a Safe-only scan that ends in the confirmation; nothing is removed before Clean is clicked.
            Click(settingsButton); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            Click((Button)window.FindName("QuickBoostButton"));
            var modal = (FrameworkElement)window.FindName("ModalOverlay");
            UiLayoutChecks.PumpUntil(() => modal.Visibility == Visibility.Visible);
            UiLayoutChecks.Require(window.ReviewItems.Count == 1 && window.ReviewItems[0].Path == safeFile && window.ReviewItems[0].Selected
                && ((TextBlock)window.FindName("ConfirmSummary")).Text.Contains("1 selected") && File.Exists(safeFile) && File.Exists(reviewFile),
                "Quick Boost scans only Safe items and asks before removing anything");
            Click((Button)window.FindName("CancelDeleteButton"));
            UiLayoutChecks.PumpUntil(() => modal.Visibility == Visibility.Collapsed);
            UiLayoutChecks.Require(File.Exists(safeFile) && File.Exists(reviewFile), "Declining Quick Boost deletes nothing");
            // ---- Info card: slim vertical bars like Microsoft PC Manager, with a Boost button ----
            Click(settingsButton); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Layout(root, new Size(920, 820));
            var memoryTrack = (FrameworkElement)((FrameworkElement)window.FindName("MemoryFill")).Parent;
            var tempText = (TextBlock)window.FindName("TempText");
            UiLayoutChecks.PumpUntil(() => tempText.Text != "…");
            double BarX(string name) => ((FrameworkElement)window.FindName(name)).TransformToAncestor(root).Transform(new Point(0, 0)).X;
            UiLayoutChecks.Require(BarX("CpuFill") < BarX("MemoryFill") && BarX("MemoryFill") < BarX("DiskFill") && BarX("DiskFill") < BarX("TempFill"),
                "The Info bars run left to right: Processor, Memory, System drive used, then Temporary files (0.9, user direction)");
            Rect BoundsOf(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            var infoTexts = Descendants<TextBlock>(infoCard).Where(block => block.Text.Length > 0).ToList();
            var boostCardButton = (Button)window.FindName("BoostButton");
            var infoTitle = infoTexts.Single(block => block.Text == "Info"); var infoLine = (TextBlock)window.FindName("InfoSummary");
            double contentRight = BoundsOf(infoCard).Right - 28, buttonMiddle = BoundsOf(boostCardButton).Top + BoundsOf(boostCardButton).Height / 2;
            UiLayoutChecks.Require(window.FindName("ResourceNote") == null && window.FindName("BoostStatus") == null && window.FindName("SetUpSmartBoostButton") == null && infoTexts.Count <= 11,
                $"The Info card is minimal: a title, one short line, four values and four labels, and the Boost button ({infoTexts.Count} texts)");
            UiLayoutChecks.Require(Math.Abs(BoundsOf(boostCardButton).Right - contentRight) <= 1 && buttonMiddle > BoundsOf(infoTitle).Top && buttonMiddle < BoundsOf(infoLine).Bottom
                && BoundsOf(boostCardButton).Left > BoundsOf(infoLine).Left + 200,
                "The Boost button sits on the same row as the title, vertically centered on it, with its right edge on the card's content edge");
            UiLayoutChecks.Require(memoryTrack.ActualHeight >= memoryTrack.ActualWidth * 3 && ((TextBlock)window.FindName("MemoryText")).Text.EndsWith("%") && ((TextBlock)window.FindName("CpuText")).Text.EndsWith("%")
                && tempText.Text.Contains("B") && Descendants<TextBlock>(infoCard).Any(block => block.Text == "Temporary files") && Descendants<TextBlock>(infoCard).Any(block => block.Text == "Memory usage"),
                $"The Info card shows slim vertical bars with a value and label beside each, including Memory usage and Temporary files ({tempText.Text})");
            var boostButton = (Button)window.FindName("BoostButton");
            Click(boostButton); UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Visible);
            UiLayoutChecks.Require(((TextBlock)window.FindName("ConsentTitle")).Text == "Boost now?" && ((TextBlock)window.FindName("ConsentText")).Text.Contains("Safe temporary files")
                && ((TextBlock)window.FindName("ConsentText")).Text.Contains("Nothing is closed") && booster.Calls == 0 && File.Exists(safeFile) && File.Exists(reviewFile),
                "Boost explains what it will do and asks first; nothing is cleaned or released before the answer");
            Click((Button)window.FindName("ConsentCancelButton")); UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Collapsed);
            UiLayoutChecks.PumpUntil(() => !window.IsBoosting);
            UiLayoutChecks.Require(booster.Calls == 0 && File.Exists(safeFile) && boostButton.IsEnabled && boostButton.Content.ToString() == "Boost", "Answering Not now does nothing and leaves Boost ready");
            window.BoostStageMinimum = TimeSpan.FromMilliseconds(500); window.BoostDoneHold = TimeSpan.FromMilliseconds(500); // slow enough to watch each stage
            var boostSweep = (UpdateActivity)window.FindName("BoostSweep");
            UiLayoutChecks.Require(!boostSweep.IsRunning, "The Boost sweep is idle (just a hairline) until Boost runs");
            Click(boostButton); UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Visible);
            Click((Button)window.FindName("ConsentConfirmButton"));
            UiLayoutChecks.PumpUntil(() => boostButton.Content.ToString() == "Cleaning…");
            UiLayoutChecks.Require(window.IsBoosting && boostSweep.IsRunning && !boostButton.IsEnabled && booster.Calls == 0, "While Boost cleans, the sweep runs, the button reads Cleaning… and memory has not been touched yet");
            // 0.9 audit: a scan started while Boost is cleaning would race it over the same folders, so it waits.
            var scanDuringBoost = window.ActivateGaugeAsync(); UiLayoutChecks.PumpUntil(() => scanDuringBoost.IsCompleted);
            UiLayoutChecks.Require(window.IsBoosting && ((TextBlock)window.FindName("StatusTitle")).Text != "Searching files…" && window.CurrentScanState != MainWindow.ScanState.Scanning,
                "While Boost works, a scan cannot be started over it");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string boostShots) UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(boostShots, "boost-running-dark.png"));
            UiLayoutChecks.PumpUntil(() => boostButton.Content.ToString() == "Freeing memory…");
            UiLayoutChecks.Require(!File.Exists(safeFile) && boostSweep.IsRunning && File.Exists(reviewFile), "The temporary files are gone before the memory stage starts and the sweep keeps running");
            UiLayoutChecks.PumpUntil(() => boostButton.Content.ToString() == "Done");
            UiLayoutChecks.Require(!boostSweep.IsRunning && booster.Calls == 1 && !boostButton.IsEnabled, "Boost ends on Done: the sweep stops and the button rests for a moment");
            UiLayoutChecks.PumpUntil(() => !window.IsBoosting);
            window.BoostStageMinimum = TimeSpan.Zero; window.BoostDoneHold = TimeSpan.Zero;
            var infoSummary = (TextBlock)window.FindName("InfoSummary");
            UiLayoutChecks.Require(!File.Exists(safeFile) && File.Exists(reviewFile) && booster.Calls == 1 && infoSummary.Text.StartsWith("Boosted") && infoSummary.Text.Contains("123") && infoSummary.Text.Contains("cleaned")
                && infoSummary.ToolTip?.ToString()!.Contains("refill") == true && boostButton.IsEnabled && boostButton.Content.ToString() == "Boost" && tempText.Text == "0 B" && ((TextBlock)window.FindName("MemoryText")).Text.EndsWith("%")
                && window.ReviewItems.Count == 0,
                "Boost cleans only the Safe temporary files, releases memory once, reports the measured result in the one line (the refill note is its tooltip) and leaves Review files alone");

            // ---- Browser data: kept out of scans, chosen in Settings with a red mark and warnings ----
            ((Expander)window.FindName("SettingsBrowserSection")).IsExpanded = true; UiLayoutChecks.Wait(420); UiLayoutChecks.Layout(root, new Size(920, 820));
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots)
            {
                var scroll = (ScrollViewer)window.FindName("WindowsSettingsScroll");
                foreach (bool light in new[] { false, true })
                {
                    if (window.IsLightTheme != light) window.ToggleTheme();
                    UiLayoutChecks.Wait(420);
                    scroll.ScrollToTop(); UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Wait(80);
                    UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, $"info-card-{(light ? "light" : "dark")}.png"));
                    ((FrameworkElement)window.FindName("SettingsBrowserSection")).BringIntoView(); UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Wait(120);
                    scroll.ScrollToVerticalOffset(scroll.ScrollableHeight); UiLayoutChecks.Wait(120);
                    UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, $"browser-data-{(light ? "light" : "dark")}.png"));
                }
                if (window.IsLightTheme) window.ToggleTheme();
                scroll.ScrollToTop();
            }
            var historyBox = (CheckBox)window.FindName("BrowserHistoryBox"); var cookiesBox = (CheckBox)window.FindName("BrowserCookiesBox"); var browserClean = (Button)window.FindName("BrowserCleanButton");
            var red = ((SolidColorBrush)Application.Current.Resources["CleanTextBrush"]).Color;
            UiLayoutChecks.Require(historyBox.IsChecked != true && cookiesBox.IsChecked != true && !browserClean.IsEnabled && ((TextBlock)window.FindName("BrowserSummary")).Text.Contains("nothing selected")
                && ((SolidColorBrush)((TextBlock)window.FindName("BrowserCookiesTitle")).Foreground).Color == red && ((SolidColorBrush)cookiesBox.Tag).Color == red,
                "Browser data starts with nothing selected and the cookies row is marked in the Clean red");
            Toggle(cookiesBox); UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Visible);
            UiLayoutChecks.Require(((TextBlock)window.FindName("ConsentTitle")).Text.Contains("cookies") && ((TextBlock)window.FindName("ConsentText")).Text.Contains("signs you out"), "Choosing cookies shows a warning popup first");
            Click((Button)window.FindName("ConsentCancelButton")); UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Collapsed && cookiesBox.IsChecked != true);
            UiLayoutChecks.Require(cookiesBox.IsChecked != true && !browserClean.IsEnabled, "Declining the cookie warning leaves cookies unselected");
            Toggle(cookiesBox); UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Visible);
            Click((Button)window.FindName("ConsentConfirmButton")); UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Collapsed && browserClean.IsEnabled);
            UiLayoutChecks.Require(cookiesBox.IsChecked == true && browserClean.IsEnabled && ((TextBlock)window.FindName("BrowserSummary")).Text.Contains("cookies selected"), "Accepting the warning selects cookies and enables Review and clean");
            Click(browserClean); UiLayoutChecks.PumpUntil(() => modal.Visibility == Visibility.Visible);
            var advancedConsent = (CheckBox)window.FindName("AdvancedConsentBox");
            UiLayoutChecks.Require(window.ReviewItems.Count == 1 && window.ReviewItems[0].Path == cookieFile && window.ReviewItems[0].Selected && window.ReviewItems[0].Safety == SafetyLevel.Advanced
                && advancedConsent.Visibility == Visibility.Visible && !((Button)window.FindName("ConfirmDeleteButton")).IsEnabled && File.Exists(cookieFile),
                "Review and clean scans only the chosen browser data, then asks again with the Advanced consent box; nothing is removed yet");
            Click((Button)window.FindName("CancelDeleteButton")); UiLayoutChecks.PumpUntil(() => modal.Visibility == Visibility.Collapsed);
            UiLayoutChecks.Require(File.Exists(cookieFile), "Cancelling keeps the cookies");
            var fullScan = window.ActivateGaugeAsync(); UiLayoutChecks.PumpUntil(() => fullScan.IsCompleted);
            UiLayoutChecks.Require(!window.ReviewItems.Any(item => item.Path == cookieFile), "A normal scan never lists browser cookies");
            // ---- Terms of use: first run, About notice and the full text ----
            string termsFile = Path.Combine(fixture, "terms-preferences.json");
            var termsWindow = new MainWindow(engine, preferencesPath: termsFile, startupApps: new StartupAppsService(startup.Locations, Path.Combine(fixture, "startup-terms-logs")), memoryBooster: booster);
            try
            {
                var termsConsent = (FrameworkElement)termsWindow.FindName("ConsentOverlay");
                var declined = termsWindow.EnsureTermsAccepted(); UiLayoutChecks.PumpUntil(() => termsConsent.Visibility == Visibility.Visible);
                UiLayoutChecks.Require(((TextBlock)termsWindow.FindName("ConsentTitle")).Text == "Use Hush at your own risk" && ((TextBlock)termsWindow.FindName("ConsentText")).Text == Terms.Body
                    && ((Button)termsWindow.FindName("ConsentConfirmButton")).Content.ToString() == "I understand" && ((Button)termsWindow.FindName("ConsentCancelButton")).Content.ToString() == "Quit",
                    "First run shows the use-at-your-own-risk terms with I understand and Quit");
                ((Button)termsWindow.FindName("ConsentCancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UiLayoutChecks.PumpUntil(() => declined.IsCompleted);
                UiLayoutChecks.Require(!declined.Result && (!File.Exists(termsFile) || !File.ReadAllText(termsFile).Contains(Terms.Version)), "Declining the terms returns false and saves nothing (the real app then quits)");
                UiLayoutChecks.PumpUntil(() => termsConsent.Visibility == Visibility.Collapsed);
                var accepted = termsWindow.EnsureTermsAccepted(); UiLayoutChecks.PumpUntil(() => termsConsent.Visibility == Visibility.Visible);
                ((Button)termsWindow.FindName("ConsentConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UiLayoutChecks.PumpUntil(() => accepted.IsCompleted);
                UiLayoutChecks.Require(accepted.Result && File.ReadAllText(termsFile).Contains($"\"TermsAccepted\":\"{Terms.Version}\""), "Accepting saves the accepted version of the terms");
                UiLayoutChecks.PumpUntil(() => termsConsent.Visibility == Visibility.Collapsed);
                var again = termsWindow.EnsureTermsAccepted();
                UiLayoutChecks.Require(again.IsCompleted && again.Result && termsConsent.Visibility == Visibility.Collapsed, "Later starts do not ask again");
            }
            finally { termsWindow.Close(); }
            Click((Button)window.FindName("AboutButton")); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("AboutOverlay")).Visibility == Visibility.Visible);
            UiLayoutChecks.Require(((TextBlock)window.FindName("AboutTerms")).Text == Terms.Notice && ((Button)window.FindName("TermsButton")).Content.ToString() == "Terms of use", "About shows the use-at-your-own-risk notice and a Terms of use button");
            Click((Button)window.FindName("TermsButton")); UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Visible);
            UiLayoutChecks.Require(((TextBlock)window.FindName("ConsentText")).Text == Terms.Body && ((TextBlock)window.FindName("ConsentTitle")).Text == "Terms of use", "Terms of use opens the full text");
            Click((Button)window.FindName("ConsentConfirmButton")); UiLayoutChecks.PumpUntil(() => consent.Visibility == Visibility.Collapsed);
            Click((Button)window.FindName("CloseAboutButton")); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("AboutOverlay")).Visibility == Visibility.Collapsed);
            // ---- Popup buttons: every action on the left of the button that leaves ----
            UiLayoutChecks.Layout(root, new Size(920, 820));
            double LeftOf(string name) { var element = (FrameworkElement)window.FindName(name); return element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)).Left; }
            foreach (var (overlayName, action, exit) in new[] { ("ModalOverlay", "ConfirmDeleteButton", "CancelDeleteButton"), ("WindowsSettingsOverlay", "ConfirmWindowsSettingsButton", "CancelWindowsSettingsButton"),
                ("ConsentOverlay", "ConsentConfirmButton", "ConsentCancelButton"), ("AboutOverlay", "CheckUpdateButton", "CloseAboutButton") })
            {
                var overlay = (FrameworkElement)window.FindName(overlayName); var before = overlay.Visibility;
                overlay.Visibility = Visibility.Visible; UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Require(LeftOf(action) < LeftOf(exit) - 20, $"{overlayName}: the action ({action}) is on the left and the exit ({exit}) on the right");
                overlay.Visibility = before;
            }
            Console.WriteLine("PASS: Smart Boost section, off-by-default timers, resource indicators, startup apps with Undo, consent for Automatic Boost, shared settings file, low-resource mode and Quick Boost confirmation");
        }
        finally { window.Close(); }
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
