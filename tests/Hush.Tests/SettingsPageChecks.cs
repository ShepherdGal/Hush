using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hush;

// The Settings page (Phase D, owner, November 19, 2026: "no accordions"; owner's picks of November 20, 2026, revised the same day): one open page with a section
// index; Open apps shows its list straight away; no Windows Update section (its switch is in Tweaks); About Hush checks for updates in its Version row; the
// Smart Boost threshold and interval show only while Watch for junk is on.
internal static class SettingsPageChecks
{
    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        string preferences = Path.Combine(fixture, "settings-page-preferences.json");
        new Preferences { TermsAccepted = Terms.Version, RunAsAdmin = false, ProMode = true }.Save(preferences);
        var updates = new UpdateFlowChecks.FakeClient();
        var window = new MainWindow(engine, preferencesPath: preferences, serviceSystem: ServiceChecks.Pc(), taskSystem: TaskChecks.Pc(), updateFactory: () => updates) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            T Find<T>(string name) where T : class => (T)window.FindName(name);
            void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Click("SettingsButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            UiLayoutChecks.Layout(root, new Size(920, 820));
            var index = window.SettingsIndexItems;
            check(index.Select(item => item.Name).SequenceEqual(["Appearance", "Administrator", "Smart Boost", "Browser data", "Open apps", "About Hush"])
                && index.All(item => item.Shown == Visibility.Visible) && index.Count(item => item.Selected) == 1 && index[0].Selected,
                "Expert Settings lists its six sections in the index (no Windows Update: its switch is in Tweaks), the first one lit");
            check(new[] { "SettingsSizeSection", "SettingsAdminSection", "SettingsBoostSection", "SettingsBrowserSection", "SettingsProcessSection", "SettingsAboutGroup" }
                .All(name => Find<FrameworkElement>(name).IsVisible) && window.FindName("SettingsUpdateGroup") == null && window.FindName("SettingsUpdateButton") == null,
                "Every section is on the page without opening anything, and nothing sends you to another page");

            // Open apps (owner, November 20, 2026): the list is there straight away, read once the page has faded in.
            UiLayoutChecks.Wait(700);
            check(Find<FrameworkElement>("ProcessListPanel").IsVisible && window.FindName("ShowAppsButton") == null && Find<Expander>("SettingsProcessSection").IsExpanded
                && Find<TextBlock>("ProcessSummary").Text != "Reading open apps…", "Open apps shows its list at once, without a Show apps step");

            // Smart Boost: the threshold and interval rows appear only with Watch for junk.
            bool watching = Find<CheckBox>("MonitorJunkBox").IsChecked == true;
            check(Find<FrameworkElement>("ThresholdRow").Visibility == (watching ? Visibility.Visible : Visibility.Collapsed)
                && Find<FrameworkElement>("IntervalRow").Visibility == Find<FrameworkElement>("ThresholdRow").Visibility
                && Find<FrameworkElement>("ThresholdDivider").Visibility == Find<FrameworkElement>("ThresholdRow").Visibility,
                "Cleanup threshold and Check every show only while Watch for junk is on (no dimmed rows that do nothing)");

            // An index click lights its entry and jumps there (owner, November 20, 2026, remade): one ~350 ms slow-fast-slow glide, the highlight never leaves
            // the clicked entry on the way or after landing, and it lands exactly (re-clamped to the page's end).
            var about = index.Single(item => item.Key == "about");
            var scroll = Find<ScrollViewer>("WindowsSettingsScroll");
            var aboutButton = UiLayoutChecks.Descendants<Button>(Find<ItemsControl>("SettingsIndex")).Single(button => button.Tag == about);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool leftClicked = false; var offsets = new List<double>();
            aboutButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            bool jumping = SmoothScroll.IsJumping(scroll);
            UiLayoutChecks.PumpUntil(() => { offsets.Add(scroll.VerticalOffset); leftClicked |= !about.Selected || index.Count(item => item.Selected) != 1; return !SmoothScroll.IsJumping(scroll); });
            double took = clock.Elapsed.TotalMilliseconds;
            UiLayoutChecks.Wait(200);
            check((jumping || !Motion.Animates) && !leftClicked && about.Selected && index.Count(item => item.Selected) == 1 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1
                && took < 900 && offsets.Zip(offsets.Skip(1)).All(pair => pair.Second >= pair.First - 0.5),
                $"Clicking an index entry jumps to its section in one steady glide ({took:0} ms), its entry stays lit the whole way and after landing");
            check(SmoothScroll.JumpEase(0) == 0 && SmoothScroll.JumpEase(1) == 1 && Math.Abs(SmoothScroll.JumpEase(0.5) - 0.5) < 1e-9 && SmoothScroll.JumpEase(0.1) < 0.01
                && Enumerable.Range(0, 101).Select(step => SmoothScroll.JumpEase(step / 100.0)).All(value => value is >= 0 and <= 1),
                "The jump eases in and out with no overshoot (no lurch at the start, no bounce at the end)");
            var appearance = index.Single(item => item.Key == "appearance");
            UiLayoutChecks.Descendants<Button>(Find<ItemsControl>("SettingsIndex")).Single(button => button.Tag == appearance).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiLayoutChecks.PumpUntil(() => !SmoothScroll.IsJumping(scroll)); UiLayoutChecks.Wait(100);
            check(appearance.Selected && scroll.VerticalOffset < 1, "Clicking back to the first section jumps to the top and lights it");

            // About Hush > Version: the check runs in the row (owner: "check updates in place"), no popup.
            check(Find<TextBlock>("SettingsVersionText").Text.StartsWith("Version "), "About Hush shows the version");
            Click("SettingsCheckUpdateButton");
            check(Find<Button>("SettingsCheckUpdateButton").Content?.ToString() == "Checking…" && Find<FrameworkElement>("AboutOverlay").Visibility != Visibility.Visible,
                "Check for updates checks right in the row, without opening the About popup");
            UiLayoutChecks.PumpUntil(() => Find<Button>("SettingsCheckUpdateButton").IsEnabled);
            check(Find<TextBlock>("SettingsVersionText").Text.Contains("latest version") && Find<Button>("SettingsCheckUpdateButton").Content?.ToString() == "Check for updates",
                "Up to date: the row says so and the button stays Check for updates");
            updates.Check = _ => Task.FromResult<AppRelease?>(new AppRelease(new Version(99, 0, 0), "", new Uri("https://example.invalid/Hush.zip"), 1, new string('0', 64)));
            Click("SettingsCheckUpdateButton"); UiLayoutChecks.PumpUntil(() => Find<Button>("SettingsCheckUpdateButton").IsEnabled);
            check(Find<TextBlock>("SettingsVersionText").Text.Contains("is available") && Find<Button>("SettingsCheckUpdateButton").Content?.ToString() == "Update now",
                "A newer version: the row names it and the button becomes Update now");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots)
            {
                Find<ScrollViewer>("WindowsSettingsScroll").ScrollToTop(); UiLayoutChecks.Wait(300); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "settings-page-dark.png"));
                window.ToggleTheme(); window.Width = 800; window.Height = 740; UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(800, 740));
                UiLayoutChecks.Save(root, new Size(800, 740), Path.Combine(shots, "settings-page-light-800.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
            }

            // Simple mode: the index shows Appearance and About only.
            window.SetProMode(false); UiLayoutChecks.Wait(200);
            check(index.Where(item => item.Shown == Visibility.Visible).Select(item => item.Key).SequenceEqual(["appearance", "about"]), "Simple Settings indexes only Appearance and About Hush");
        }
        finally { window.Close(); }
    }
}
