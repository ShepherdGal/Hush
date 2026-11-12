using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            check(!window.IsProMode && (string)pro.Tag == "Off" && !((UIElement)window.FindName("ProGlow")).IsVisible && !((UIElement)window.FindName("WordmarkGlow")).IsVisible && System.Windows.Automation.AutomationProperties.GetName(pro) == "Pro mode, off" && (string)pro.Content == "Pro",
                "Everyone starts in Simple mode (also with an existing settings file), and the Pro button is the plain header look with a spoken name");
            var settings = (Button)window.FindName("SettingsButton");
            check(pro.IsDescendantOf((DependencyObject)window.FindName("CaptionControls")) && ((StackPanel)window.FindName("CaptionControls")).Children.IndexOf((UIElement)window.FindName("ProModeSlot")) < ((StackPanel)window.FindName("CaptionControls")).Children.IndexOf(settings),
                "The Pro button sits next to the Settings icon, on its left");

            // 2. Settings in Simple mode: the Info card (bars and Boost), the update line and Appearance only.
            Click("SettingsButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            ((Expander)window.FindName("SettingsSizeSection")).IsExpanded = true; UiLayoutChecks.Wait(400); UiLayoutChecks.Layout(root, new Size(920, 820));
            string[] hidden = ["SettingsColumns", "SettingsProcessSection", "SettingsAdminSection", "SettingsBoostSection", "SettingsBrowserSection", "SettingsServicesSection", "SettingsTasksSection",
                "SettingsPrivacySection", "SettingsImpactSection", "SettingsUpdateSection", "SettingsFooter"];
            check(Shown("SettingsInfoCard") && Shown("SettingsSizeSection") && ((UIElement)window.FindName("AccentPanel")).IsVisible
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
            check(Text("ConsentTitle") == "Switch to Pro mode?" && Text("ConsentText").Contains("know what they're changing") && Text("ConsentText").Contains("switch back whenever you like")
                && (string)confirm.Content == "Switch to Pro mode" && (string)cancel.Content == "Stay in Simple mode"
                && confirm.TranslatePoint(new Point(), root).X < cancel.TranslatePoint(new Point(), root).X,
                "Turning Pro on opens a plain, warm warning with the action on the left and Stay in Simple mode on the right");
            ThemeMotionChecks.Capture(root, "promode-popup-dark");
            Click("ConsentCancelButton"); UiLayoutChecks.PumpUntil(() => !OverlayShown());
            check(!window.IsProMode && !Preferences.Load(preferences).ProMode && window.CurrentScanState == MainWindow.ScanState.Completed, "Staying in Simple mode changes nothing");

            // 5. Agreeing switches: saved, highlighted with the shared accent glow, old results cleared (a Safe-only scan does not fit Pro), everything visible again.
            Click("ProModeButton"); UiLayoutChecks.PumpUntil(OverlayShown); UiLayoutChecks.Wait(340);
            Click("ConsentConfirmButton"); UiLayoutChecks.PumpUntil(() => !OverlayShown()); UiLayoutChecks.Wait(400);
            check(window.IsProMode && Preferences.Load(preferences).ProMode && (string)pro.Tag == "On" && ((UIElement)window.FindName("ProGlow")).IsVisible && ((UIElement)window.FindName("WordmarkGlow")).IsVisible && pro.Effect == null && ((Button)window.FindName("AboutButton")).Effect == null
                && !Motion.GetOuterGlow(pro) && pro.BorderThickness.Left == 0
                && System.Windows.Automation.AutomationProperties.GetName(pro) == "Pro mode, on",
                "Pro mode is saved, the word Pro and the Hush wordmark get a tight glow per letter (no border, no fill, no effect) and the button has a spoken 'on'");
            check(window.ReviewItems.Count == 0 && window.CurrentScanState == MainWindow.ScanState.Ready, "Switching mode clears the earlier results (nothing was deleted); a new scan fills them");
            ThemeMotionChecks.Capture(root, "promode-home-pro-dark");
            Click("SettingsButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            check(hidden.All(name => Shown(name)), "Pro mode shows every Settings card and the footer again");
            Click("SettingsBackButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
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
            Click("ProModeButton"); UiLayoutChecks.Wait(300);
            check(!window.IsProMode && !OverlayShown() && (string)pro.Tag == "Off" && !((UIElement)window.FindName("ProGlow")).IsVisible && !((UIElement)window.FindName("WordmarkGlow")).IsVisible && !Preferences.Load(preferences).ProMode, "Turning Pro off needs no question and removes the glow from the button and the wordmark");
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

        // The letter glow picture: made once from the text, bright at the letters, nothing at the far edge, and SMOOTH (no stepped, jagged edge: the neighbouring pixels of
        // the picture never jump), and it is a frozen picture, so showing it costs nothing per frame.
        var sample = new LetterGlow { Text = "Hush", FontSize = 33, FontWeight = FontWeights.Medium, FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };
        sample.Measure(new Size(400, 200));
        var picture = sample.Bake();
        check(picture != null && picture.IsFrozen && picture.PixelWidth > sample.DesiredSize.Width * 3 && picture.PixelHeight > sample.DesiredSize.Height * 3, "The letter glow is one frozen picture, a little larger than the text");
        if (picture != null)
        {
            int w = picture.PixelWidth, h = picture.PixelHeight; var px = new byte[w * h * 4]; picture.CopyPixels(px, w * 4, 0);
            byte Alpha(int x, int y) => px[(y * w + x) * 4 + 3];
            int maxStep = 0, bright = 0;
            for (int y = 0; y < h; y++) for (int x = 1; x < w; x++) { maxStep = Math.Max(maxStep, Math.Abs(Alpha(x, y) - Alpha(x - 1, y))); if (Alpha(x, y) > 200) bright++; }
            check(Alpha(0, 0) < 4 && Alpha(w - 1, h - 1) < 4 && Alpha(w / 2, h / 2) > 60 && bright > 0 && maxStep < 40,
                $"The letter glow is bright at the letters, gone at the edge and smooth (corner {Alpha(0, 0)}, middle {Alpha(w / 2, h / 2)}, biggest jump between neighbouring pixels {maxStep} of 255)");
        }
    }
}
