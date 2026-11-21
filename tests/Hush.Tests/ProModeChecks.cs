using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Hush;

// Simple mode and Pro mode (owner, November 11, 2026): everyone starts in Simple mode (Safe-only scan, a short Settings page); the header's "Pro" button asks before
// turning Pro on, highlights with the shared accent glow while on, and switches back without a question. Real window, fixture files, no real cleaning.
internal static class ProModeChecks
{
    internal static void RunUi(string fixture, Action<bool, string> check)
    {
        string temp = Path.Combine(fixture, "promode-temp"), logs = Path.Combine(fixture, "promode-logs");
        Directory.CreateDirectory(temp); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(temp, "a.tmp"), new string('x', 2048)); File.WriteAllText(Path.Combine(logs, "b.log"), new string('y', 2048));
        foreach (string file in new[] { Path.Combine(temp, "a.tmp"), Path.Combine(logs, "b.log") }) File.SetLastWriteTime(file, DateTime.Now.AddDays(-30));
        var engine = new CleanupEngine([new(temp, "User temp", CleanupKind.Temp, TimeSpan.Zero), new(logs, "App logs", CleanupKind.Cache, TimeSpan.Zero, "*.log", Category: "AppLogs")],
            dataDirectory: Path.Combine(fixture, "promode-audit"));
        string preferences = Path.Combine(fixture, "promode-preferences.json");
        new Preferences { TermsAccepted = Terms.Version, RunAsAdmin = false }.Save(preferences);   // an existing user's file: no Pro mode value in it

        var oldForce = MainWindow.TestProMode; MainWindow.TestProMode = null;   // the product's own default, not the test runner's
        var window = new MainWindow(engine, preferencesPath: preferences, serviceSystem: ServiceChecks.Pc()) { ShowActivated = false, ShowInTaskbar = false, ReviewShowsAllCategories = false };   // the product's own Review (groups first), not the layout suites' show-everything view
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            var pro = (Button)window.FindName("ProModeButton");
            void Click(string name) => ((ButtonBase)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            bool Shown(string name) => ((UIElement)window.FindName(name)).Visibility == Visibility.Visible;
            bool OverlayShown() => Shown("ConsentOverlay");
            string Text(string name) => ((TextBlock)window.FindName(name)).Text;

            // 1. Everyone starts in Simple mode, old and new users; the header button is the plain caption look, with a clear name.
            string Label(Button button) => ((Grid)button.Content).Children.OfType<TextBlock>().Single().Text;
            check(!window.IsProMode && (string)pro.Tag == "Off" && System.Windows.Automation.AutomationProperties.GetName(pro) == "Expert mode, off" && Label(pro) == "Expert",
                "Everyone starts in Simple mode (also with an existing settings file), and the Expert half of the mode pill has a spoken name");
            var settings = (Button)window.FindName("SettingsButton");
            // Phase D (owner, November 19, 2026): the mode pill sits beside the wordmark on the left; Simple mode shows only the Home and Settings tabs.
            check(pro.IsDescendantOf((DependencyObject)window.FindName("HeaderLeft"))
                && ((TranslateTransform)window.FindName("ModeThumbShift")).X == 0,
                "The Simple | Expert pill sits beside the wordmark; in Simple mode the accent thumb is on Simple");
            check(Shown("HomeTabButton") && Shown("SettingsButton") && !Shown("CleanTabButton") && !Shown("ToolsButton") && !Shown("TweaksTabButton"),
                "Simple mode shows only the Home and Settings tabs");

            // 2. Settings in Simple mode: the Info card (bars and Boost), the update line and Appearance only.
            Click("SettingsButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            ((Expander)window.FindName("SettingsSizeSection")).IsExpanded = true; UiLayoutChecks.Wait(400); UiLayoutChecks.Layout(root, new Size(920, 820));
            string[] hidden = ["SettingsColumns", "SettingsProcessSection", "SettingsAdminSection", "SettingsBoostSection", "SettingsBrowserSection", "SettingsServicesSection", "SettingsTasksSection",
                "SettingsPrivacySection", "SettingsImpactSection", "SettingsUpdateSection", "SettingsFooter"];
            check(Shown("SettingsSizeSection") && ((UIElement)window.FindName("AccentPanel")).IsVisible
                && hidden.All(name => !((UIElement)window.FindName(name)).IsVisible),
                "Simple Settings shows only the Info card (system bars, Boost) and Appearance (size and main color); every other card and the footer are hidden");
            ThemeMotionChecks.Capture(root, "promode-settings-simple-dark");
            Click("SettingsBackButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);

            // 3. Scanning in Simple mode looks for Safe items only (the Review log file is not even found), and Take a look opens the Safe list directly.
            Click("ScanCircleButton");
            UiLayoutChecks.PumpUntil(() => window.CurrentScanState == MainWindow.ScanState.Completed);
            check(window.ReviewItems.Count == 1 && window.ReviewItems.All(item => item.Safety == SafetyLevel.Safe), "A Simple scan finds Safe items only");
            // The Clean popup in Simple mode is only the amount, one calm line and two buttons.
            Click("HomeCleanButton"); UiLayoutChecks.PumpUntil(() => Shown("ModalOverlay")); UiLayoutChecks.Wait(340);
            check(Text("ConfirmTitle").StartsWith("Clean ") && Text("ConfirmTitle").EndsWith("?") && Text("ConfirmSummary").Contains("safe") && Text("ConfirmSummary").Length < 70
                && !Shown("ConfirmScroll") && !Shown("ConfirmHomeNote") && !Shown("AdvancedConsentBox")
                && (string)((Button)window.FindName("ConfirmDeleteButton")).Content == "Clean" && (string)((Button)window.FindName("CancelDeleteButton")).Content == "Not now",
                $"In Simple mode the Clean popup is the amount, one calm line and Clean / Not now: no breakdown and no list of what happens ({Text("ConfirmTitle")} / {Text("ConfirmSummary")})");
            ThemeMotionChecks.Capture(root, "promode-clean-popup-simple-dark");
            Click("CancelDeleteButton"); UiLayoutChecks.PumpUntil(() => !Shown("ModalOverlay"));
            Click("CompletionLinkButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            check(window.OpenGroup == SafetyLevel.Safe && !Shown("GroupBackButton") && ((CheckBox)window.FindName("SelectAllBox")).Content as string == "Select all" && !Shown("ReviewGroupsView"),
                "Take a look opens the Safe list directly: no groups, no chevron, a plain Select all");
            UiLayoutChecks.Layout(root, new Size(920, 820)); ThemeMotionChecks.Capture(root, "promode-review-simple-dark");
            Click("BackButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            check(Shown("ScanPage") && !Shown("ReviewPage"), "Back from the Safe list leaves Review (there is only one group)");

            // 4. Turning Pro on asks first, in the shared popup: the action on the left, the way out on the right; Stay changes nothing.
            Click("ProModeButton"); UiLayoutChecks.PumpUntil(OverlayShown); UiLayoutChecks.Wait(340);
            var confirm = (Button)window.FindName("ConsentConfirmButton"); var cancel = (Button)window.FindName("ConsentCancelButton");
            check(Text("ConsentTitle") == "Switch to Expert mode?" && Text("ConsentText").Contains("know what they're changing") && Text("ConsentText").Contains("switch back whenever you like")
                && (string)confirm.Content == "Switch to Expert mode" && (string)cancel.Content == "Stay in Simple mode"
                && confirm.TranslatePoint(new Point(), root).X < cancel.TranslatePoint(new Point(), root).X,
                "Turning Pro on opens a plain, warm warning with the action on the left and Stay in Simple mode on the right");
            ThemeMotionChecks.Capture(root, "promode-popup-dark");
            Click("ConsentCancelButton"); UiLayoutChecks.PumpUntil(() => !OverlayShown());
            check(!window.IsProMode && !Preferences.Load(preferences).ProMode && window.CurrentScanState == MainWindow.ScanState.Completed, "Staying in Simple mode changes nothing");

            // 5. Agreeing switches: saved, highlighted with the shared accent glow, old results cleared (a Safe-only scan does not fit Pro), everything visible again.
            Click("ProModeButton"); UiLayoutChecks.PumpUntil(OverlayShown); UiLayoutChecks.Wait(340);
            Click("ConsentConfirmButton"); UiLayoutChecks.PumpUntil(() => !OverlayShown()); UiLayoutChecks.Wait(700);   // the thumb springs for 600 ms
            check(window.IsProMode && Preferences.Load(preferences).ProMode && (string)pro.Tag == "On" && pro.Effect == null && ((Button)window.FindName("AboutButton")).Effect == null
                && !Motion.GetOuterGlow(pro) && pro.BorderThickness.Left == 0
                && Math.Abs(((TranslateTransform)window.FindName("ModeThumbShift")).X - MainWindow.ModeSegmentWidth) < 0.5
                && Shown("CleanTabButton") && Shown("ToolsButton") && Shown("TweaksTabButton")
                && System.Windows.Automation.AutomationProperties.GetName(pro) == "Expert mode, on",
                "Expert mode is saved, the accent thumb slides under Expert, Expert and the wordmark get their letter glow (no effect), all five tabs show, and the button says 'on'");
            check(window.ReviewItems.Count == 0 && window.CurrentScanState == MainWindow.ScanState.Ready, "Switching mode clears the earlier results (nothing was deleted); a new scan fills them");
            ThemeMotionChecks.Capture(root, "promode-home-pro-dark");
            Click("SettingsButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            // Phase D (owner, November 20, 2026): the Windows cards and the footer moved to the Tweaks page; Settings keeps the rest.
            check(hidden.Take(5).All(name => Shown(name)), "Expert mode shows every Settings card again");
            Click("TweaksTabButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            check(Shown("SettingsPrivacySection") && Shown("SettingsImpactSection") && Shown("SettingsUpdateSection") && Shown("SettingsFooter") && window.OpenTweaksTab == "Privacy",
                "The Tweaks tab opens the Tweaks page on Privacy (with More impact and Windows Update) and its bar");
            Click("HomeTabButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            Click("ScanCircleButton");
            UiLayoutChecks.PumpUntil(() => window.CurrentScanState == MainWindow.ScanState.Completed);
            check(window.ReviewItems.Count == 2 && window.ReviewItems.Any(item => item.Safety == SafetyLevel.Review), "A Pro scan is the full scan: it finds the Review file as well");
            // Pro mode keeps the breakdown, laid out on shared columns: the names start at one x, the amounts end at one x.
            foreach (var category in window.Categories) category.Selected = true;
            Click("HomeCleanButton"); UiLayoutChecks.PumpUntil(() => Shown("ModalOverlay")); UiLayoutChecks.Wait(340); UiLayoutChecks.Layout(root, new Size(920, 820));
            var rows = ((Panel)window.FindName("ConfirmGroups")).Children.OfType<Grid>().ToList();
            var names = rows.Select(row => row.Children.OfType<TextBlock>().First(block => !(block.FontFamily.Source ?? "").Contains("Cascadia"))).ToList();
            var amounts = rows.Select(row => row.Children.OfType<TextBlock>().First(block => (block.FontFamily.Source ?? "").Contains("Cascadia"))).ToList();
            double Left(FrameworkElement element) => element.TranslatePoint(new Point(0, 0), root).X;
            double Right(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth, 0), root).X;
            check(Shown("ConfirmScroll") && rows.Count >= 4 && names.All(name => Math.Abs(Left(name) - Left(names[0])) < 1) && amounts.All(amount => Math.Abs(Right(amount) - Right(amounts[0])) < 1)
                && Text("ConfirmTitle").StartsWith("Clean ") && Text("ConfirmSummary").Contains("selected") && Text("ConfirmDetail").Contains("permanently deleted") && !Text("ConfirmDetail").Contains("Defender")
                && (string)((Button)window.FindName("ConfirmDeleteButton")).Content == "Clean selected",
                $"In Pro mode the popup keeps the breakdown by group and category on shared columns (names at one x, amounts at one right edge), with a shorter list of what happens ({rows.Count} rows)");
            ThemeMotionChecks.Capture(root, "promode-clean-popup-pro-dark");
            Click("CancelDeleteButton"); UiLayoutChecks.PumpUntil(() => !Shown("ModalOverlay"));
            Click("CompletionLinkButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            check(window.OpenGroup == null && Shown("ReviewGroupsView"), "In Pro mode Take a look opens on the three groups, as before");
            Click("BackButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);

            // 6. Turning Pro off asks nothing and goes straight back; a new window remembers the last choice.
            Click("SimpleModeButton"); UiLayoutChecks.Wait(300);
            check(!window.IsProMode && !OverlayShown() && (string)pro.Tag == "Off" && !Preferences.Load(preferences).ProMode, "Turning Expert mode off needs no question");
            Click("ProModeButton"); UiLayoutChecks.PumpUntil(OverlayShown); UiLayoutChecks.Wait(340); Click("ConsentConfirmButton"); UiLayoutChecks.PumpUntil(() => !OverlayShown());

            // 7. The This PC card on Home (Phase D: it took over the Boost chip): memory, processor, drive and Safe junk as eased bars, and the Boost button shows the
            //    amount ready to clean beside its name. Shown in both modes; the status tiles only in Expert mode.
            Click("HomeTabButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            check(Shown("ThisPcCard") && Shown("StatusTiles") && window.FindName("BoostChipButton") == null, "Home shows the This PC card and the status tiles; the Boost chip is gone from the header");
            window.UpdateBoostChip(new JunkReading(1_500_000_000, 900, DateTime.UtcNow)); window.SampleBoostChip(); UiLayoutChecks.Wait(600);
            var boost = (Button)window.FindName("BoostButton");
            check(System.Text.RegularExpressions.Regex.IsMatch(window.BoostChipLabel, @"^RAM \d+% · CPU \d+% · 1\.[34] GB$") && (string)boost.Content == "Boost" && ((string)boost.Tag).StartsWith(" · 1.")
                && ((TextBlock)window.FindName("CpuText")).Text.EndsWith("%") && ((TextBlock)window.FindName("MemoryText")).Text.EndsWith("%"),
                $"The card reads memory and processor, and Boost shows the amount ready to clean ({window.BoostChipLabel}, Boost{boost.Tag})");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string homeShots) { Directory.CreateDirectory(homeShots); UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(homeShots, "home-thispc-dark.png")); }
            window.UpdateBoostChip(new JunkReading(0, 0, DateTime.UtcNow)); UiLayoutChecks.Wait(60);
            check((string)boost.Tag == "" && window.BoostChipLabel.EndsWith("· 0 MB"), $"With nothing to clean Boost is just 'Boost' ({window.BoostChipLabel})");
            // A percentage rolls to its new value (no jump): right after a new reading the text is on its way, then it lands exactly.
            var cpu = (TextBlock)window.FindName("CpuText");
            cpu.Tag = 10.0; ((System.Windows.Controls.Border)window.FindName("CpuFill")).Tag = null; window.SampleBoostChip();
            check(cpu.Text.EndsWith("%"), $"The processor figure rolls and ends as a percentage ({cpu.Text})");
            // The tiles: what Hush already knows, never a read of the PC by themselves.
            window.SetStartupItemCount(12); window.RecordAppUpdates(3);
            check(((System.Windows.Documents.Run)window.FindName("StartupTileValue")).Text == "12" && ((System.Windows.Documents.Run)window.FindName("UpdatesTileValue")).Text == "3"
                && ((System.Windows.Documents.Run)window.FindName("DiskTileValue")).Text == "Check" && Preferences.Load(preferences).AppUpdatesCount == 3,
                "Startup shows its count, App updates its last result (kept in the settings file), Disk health 'Check' until its tool ran once");
            window.RecordDiskHealth([new DriveHealth("Disk 0", "SSD", "Healthy")]);
            check(((System.Windows.Documents.Run)window.FindName("DiskTileValue")).Text == "Good" && Preferences.Load(preferences).DiskHealth == "Good", "A healthy drive reads 'Good' and is kept");
            // 8. The support link in the About window opens the Ko-fi page (through the test seam, never a real browser).
            static IEnumerable<object> Logical(object item) { yield return item; if (item is DependencyObject node) foreach (object child in LogicalTreeHelper.GetChildren(node)) foreach (object inner in Logical(child)) yield return inner; }
            string? opened = null; var previousOpener = MainWindow.LinkOpener; MainWindow.LinkOpener = url => opened = url;
            try { Click("SupportButton"); } finally { MainWindow.LinkOpener = previousOpener; }
            check(opened == "https://ko-fi.com/trueshepherdmusic" && Logical(((Button)window.FindName("SupportButton")).Content).OfType<TextBlock>().Any(text => text.Text == "Enjoying Hush? Support it on Ko-fi"), "The About window's support link says 'Enjoying Hush? Support it on Ko-fi' and opens the Ko-fi page");
            check(!window.BoostChipTimerRunning, "Test windows never start the This PC card's 5-second timer; only the real app window does, and only while it is in front");
            Click("SimpleModeButton"); UiLayoutChecks.Wait(300);
            check(!window.IsProMode && Shown("ThisPcCard") && !Shown("StatusTiles"), "Simple mode keeps the This PC card and hides the status tiles");
            Click("ProModeButton"); UiLayoutChecks.PumpUntil(OverlayShown); UiLayoutChecks.Wait(340); Click("ConsentConfirmButton"); UiLayoutChecks.PumpUntil(() => !OverlayShown());
        }
        finally { window.Close(); }
        var again = new MainWindow(engine, preferencesPath: preferences, serviceSystem: ServiceChecks.Pc()) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            again.Show();
            check(again.IsProMode && (string)((Button)again.FindName("ProModeButton")).Tag == "On", "A new window starts in the mode the person last chose");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") != null)
            {
                var back = (FrameworkElement)again.Content;
                again.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(back, new Size(920, 820)); ThemeMotionChecks.Capture(back, "promode-home-pro-light");
                again.SetProMode(false); UiLayoutChecks.Wait(400); UiLayoutChecks.Layout(back, new Size(920, 820)); ThemeMotionChecks.Capture(back, "promode-home-simple-light");
            }
        }
        finally { again.Close(); MainWindow.TestProMode = oldForce; }
    }
}
