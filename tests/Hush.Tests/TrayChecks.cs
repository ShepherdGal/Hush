using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hush;

// Settings > Administrator: Minimize to tray and Start with Windows (0.9, user request), and the Services card's Actions menu.
// Only fakes: no real icon next to the clock, no real Run key.
internal static class TrayChecks
{
    internal sealed class FakeTray : ITrayIcon
    {
        public bool Visible { get; set; }
        public bool Disposed;
        public void Dispose() { Disposed = true; Visible = false; }
    }

    internal sealed class FakeStartup : IStartupRegistration
    {
        internal string? Command;
        internal bool Fail;
        internal int Writes;
        public string? Current() => Command;
        public void Enable(string exe) { if (Fail) throw new IOException("Windows refused the change."); Command = RunKeyStartup.CommandFor(exe); Writes++; }
        public void Disable() { if (Fail) throw new IOException("Windows refused the change."); Command = null; Writes++; }
    }

    internal static void Run(string fixture, Action<bool, string> check)
    {
        // The Run entry: Hush's own value, quoted path plus --startup, rewritten only when the path moved.
        var fake = new FakeStartup();
        check(RunKeyStartup.CommandFor(@"C:\Program Files\Hush\Hush.exe") == "\"C:\\Program Files\\Hush\\Hush.exe\" --startup", "The Start-with-Windows command quotes the path and adds --startup");
        check(RunKeyStartup.Sync(fake, true, @"C:\Hush\Hush.exe") && fake.Command == "\"C:\\Hush\\Hush.exe\" --startup" && fake.Writes == 1
            && !RunKeyStartup.Sync(fake, true, @"c:\hush\hush.exe") && fake.Writes == 1, "The entry is written when it is missing and left alone when it already points at this copy");
        check(RunKeyStartup.Sync(fake, true, @"D:\Moved\Hush.exe") && fake.Command!.Contains(@"D:\Moved") && !RunKeyStartup.Sync(fake, false, @"D:\Moved\Hush.exe") && !RunKeyStartup.Sync(fake, true, null),
            "A moved or updated copy re-points the entry; with the option off nothing is ever written");

        // The one-line note that carries "open quietly" through the administrator task.
        string directory = Path.Combine(fixture, "startup-hint");
        Directory.CreateDirectory(directory);
        check(!StartupHint.Take(directory), "Without a note nothing starts quietly");
        StartupHint.Leave(directory);
        check(StartupHint.Take(directory) && !StartupHint.Take(directory) && !File.Exists(Path.Combine(directory, "startup-hint.txt")), "A fresh note is read once and removed");
        File.WriteAllText(Path.Combine(directory, "startup-hint.txt"), DateTime.UtcNow.AddMinutes(-10).Ticks.ToString());
        check(!StartupHint.Take(directory), "An old note is ignored");
        File.WriteAllText(Path.Combine(directory, "startup-hint.txt"), "not a time");
        check(!StartupHint.Take(directory), "A note that is not a time is ignored");

        // Uninstall removes the Start-with-Windows entry (through the fake: never the real Run key).
        var loc = new SetupLocations(Path.Combine(fixture, "tray-install"), Path.Combine(fixture, "tray-start"), Path.Combine(fixture, "tray-desktop"), @"Software\Hush.TestFixtures\" + Guid.NewGuid().ToString("N"), Path.Combine(fixture, "tray-data"));
        var startup = new FakeStartup { Command = "x" };
        HushSetup.Uninstall(loc, false, null, null, startup);
        check(startup.Command == null, "Uninstalling Hush removes its Start-with-Windows entry");
    }

    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        string preferences = Path.Combine(fixture, "tray-ui-preferences.json");
        new Preferences { TermsAccepted = Terms.Version, RunAsAdmin = false }.Save(preferences);
        var startup = new FakeStartup();
        var oldStartup = MainWindow.StartupRegistration; MainWindow.StartupRegistration = startup;
        FakeTray? created = null;
        var oldFactory = MainWindow.TrayFactory; MainWindow.TrayFactory = (_, _, _) => created = new FakeTray();
        var pc = ServiceChecks.Pc();
        var window = new MainWindow(engine, preferencesPath: preferences, serviceSystem: pc) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string Text(string name) => ((TextBlock)window.FindName(name)).Text;
            // A real switch click: the box takes the new state, then its Click event fires (what the mouse does).
            void Flip(string name, bool on) { var box = (CheckBox)window.FindName(name); box.IsChecked = on; box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
            Click("SettingsButton"); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            var tray = (CheckBox)window.FindName("MinimizeToTrayBox"); var start = (CheckBox)window.FindName("StartWithWindowsBox");
            var admin = (Expander)window.FindName("SettingsAdminSection");
            admin.IsExpanded = true; UiLayoutChecks.Wait(400); UiLayoutChecks.Layout(root, new Size(920, 820));
            check(tray.IsChecked == false && start.IsChecked == false && created == null && !window.TrayIconVisible && Text("AdminSummary") == "Off",
                "Both new options are off by default, and no tray icon exists until the person asks for it");
            check(tray.IsDescendantOf(admin) && start.IsDescendantOf(admin) && ((FrameworkElement)window.FindName("AdminModeBox")).IsDescendantOf(admin), "Minimize to tray and Start with Windows sit in the Administrator card");

            // Minimize to tray.
            Flip("MinimizeToTrayBox", true);
            check(created is { Visible: true } && window.TrayIconVisible && Preferences.Load(preferences).MinimizeToTray && Text("AdminSummary").Contains("Minimize to tray"), "Turning it on shows the tray icon, remembers the choice and says so in the card");
            window.WindowState = WindowState.Minimized; UiLayoutChecks.Wait(150);
            check(!window.IsVisible && window.IsInTray, "Minimizing hides Hush in the tray: the window has no taskbar button while it is hidden");
            window.RestoreFromTray(); UiLayoutChecks.Wait(150);
            check(window.IsVisible && window.WindowState == WindowState.Normal && !window.IsInTray, "Using the tray icon (or a second start) brings the window back, in its normal size");
            Flip("MinimizeToTrayBox", false); UiLayoutChecks.Wait(50);
            window.WindowState = WindowState.Minimized; UiLayoutChecks.Wait(150);
            check(created!.Visible == false && !Preferences.Load(preferences).MinimizeToTray && window.IsVisible, "With the option off, minimizing is the normal minimize and the tray icon is gone");
            window.WindowState = WindowState.Normal; UiLayoutChecks.Wait(100);
            // Turning the option off while Hush is hidden must never leave it without a way back.
            Flip("MinimizeToTrayBox", true); window.WindowState = WindowState.Minimized; UiLayoutChecks.Wait(150);
            bool hidden = !window.IsVisible;
            Flip("MinimizeToTrayBox", false); UiLayoutChecks.Wait(150);
            check(hidden && window.IsVisible, "Switching the option off while Hush is hidden shows the window again");

            // Start with Windows.
            Flip("StartWithWindowsBox", true);
            check(startup.Command != null && startup.Command.EndsWith("--startup") && Preferences.Load(preferences).StartWithWindows && Text("AdminSummary").Contains("Starts with Windows"), "Turning it on writes Hush's own Run entry (with --startup), remembers it and says so");
            Flip("StartWithWindowsBox", false);
            check(startup.Command == null && !Preferences.Load(preferences).StartWithWindows, "Turning it off removes the entry");
            startup.Fail = true; Flip("StartWithWindowsBox", true);
            UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(start.IsChecked == false && !Preferences.Load(preferences).StartWithWindows && Text("ConsentText").Contains("nothing was changed"), "When Windows refuses, the switch goes back and the person is told nothing changed");
            Click("ConsentCancelButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility != Visibility.Visible);
            startup.Fail = false;
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots)
            {
                Flip("MinimizeToTrayBox", true); Flip("StartWithWindowsBox", true); UiLayoutChecks.Wait(300); UiLayoutChecks.Layout(root, new Size(920, 820));
                Directory.CreateDirectory(shots); UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "admin-card-dark.png"));
                Flip("MinimizeToTrayBox", false); Flip("StartWithWindowsBox", false);
            }
            admin.IsExpanded = false; UiLayoutChecks.Wait(300);

            // The Services card is a full-width card with its own Actions menu.
            var services = (Expander)window.FindName("SettingsServicesSection");
            var left = (FrameworkElement)window.FindName("SettingsLeftColumn"); var right = (FrameworkElement)window.FindName("SettingsRightColumn");
            services.IsExpanded = true; UiLayoutChecks.PumpUntil(() => window.ServiceChoices.Count > 0); UiLayoutChecks.Wait(360); UiLayoutChecks.Layout(root, new Size(920, 820));
            Rect Bounds(FrameworkElement e) => e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize));
            check(!services.IsDescendantOf(left) && !services.IsDescendantOf(right) && Math.Abs(Bounds(services).Left - Bounds(left).Left) < 1 && Math.Abs(Bounds(services).Right - Bounds(right).Right) < 1
                && Bounds(services).Top >= Math.Max(Bounds(left).Bottom, Bounds(right).Bottom) - 0.5 && Math.Abs(Bounds(left).Bottom - Bounds(right).Bottom) < 30,
                "Services is a big card: full width under the two columns, which still end at the same height");
            var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("ServicesActionsPopup");
            Click("ServicesActionsButton");
            check(popup.IsOpen, "The Services Actions button opens its menu");
            Click("ServicesActionsButton");
            check(!popup.IsOpen, "A second click closes it again");

            int rare = window.ServiceChoices.Count(choice => choice.Candidate.Level == ServiceLevel.Recommended);
            int readFirst = window.ServiceChoices.Count(choice => choice.Candidate.Level != ServiceLevel.LeftBehind), all = window.ServiceChoices.Count;
            check(rare > 0 && readFirst > rare && all > readFirst, $"The fixture has services of every level (rarely needed {rare}, plus read-first {readFirst}, all {all})");
            Click("ServicesActionsButton"); Click("ServicesActionRareButton");
            UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(!popup.IsOpen && window.ServiceChoices.Count(choice => choice.Selected) == rare && window.ServiceChoices.Where(choice => choice.Selected).All(choice => choice.Candidate.Level == ServiceLevel.Recommended)
                && Text("ConsentTitle") == $"Apply {rare} change{(rare == 1 ? "" : "s")}?" && pc.Calls.Count == 0,
                "Actions > rarely needed switches exactly those services ON and opens the usual review; nothing is changed yet");
            Click("ConsentCancelButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility != Visibility.Visible);
            check(window.ServiceChoices.Count(choice => choice.Selected) == rare && pc.Calls.Count == 0, "Cancelling the review keeps the switches where they are and changes nothing");
            Click("ServicesActionsButton"); Click("ServicesActionClearButton");
            check(window.ServiceChoices.All(choice => !choice.Selected) && !popup.IsOpen, "Actions > Clear my choices switches everything back OFF");
            Click("ServicesActionsButton"); Click("ServicesActionReadFirstButton");
            UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(window.ServiceChoices.Count(choice => choice.Selected) == readFirst, "Actions > rarely needed and read-first adds the amber ones");
            Click("ConsentCancelButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility != Visibility.Visible);
            Click("ServicesActionsButton"); Click("ServicesActionAllButton");
            UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(window.ServiceChoices.Count(choice => choice.Selected) == all, "Actions > every service switches the whole list ON, left-behind ones included");
            Click("ConsentCancelButton"); UiLayoutChecks.PumpUntil(() => ((FrameworkElement)window.FindName("ConsentOverlay")).Visibility != Visibility.Visible);
            Click("ServicesActionsButton"); Click("ServicesActionClearButton");
            Click("ServicesActionsButton"); Click("ServicesActionUndoButton");
            check(Text("ServicesStatus").StartsWith("Nothing to undo") && pc.Calls.Count == 0, "Actions > Undo says so when Hush has not changed any service");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string menuShots)
            {
                Click("ServicesActionsButton"); UiLayoutChecks.Wait(300);
                ((FrameworkElement)window.FindName("ServicesActionsButton")).BringIntoView(); UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Wait(120);
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(menuShots, "services-actions-dark.png"));
                Click("ServicesActionsButton");
            }
            window.Close();
            check(created is { Disposed: true } || created == null, "Closing Hush removes the tray icon");
        }
        finally { if (window.IsLoaded) window.Close(); MainWindow.StartupRegistration = oldStartup; MainWindow.TrayFactory = oldFactory; }
    }
}
