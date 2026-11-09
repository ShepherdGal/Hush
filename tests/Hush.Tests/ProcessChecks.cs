using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hush;

// The Process card (list and End), the process service, and the Temp meter's "held back" numbers. Real apps are never ended: the service is tested
// only on a child process this test starts itself, and the window is tested with a fake manager.
internal static class ProcessChecks
{
    private sealed class FakeProcesses : IProcessManager
    {
        internal readonly List<AppProcess> Apps = [];
        internal readonly List<string> Ended = [];
        internal EndOutcome Outcome = new(1, 0, 0);
        public IReadOnlyList<AppProcess> List() => Apps.ToList();
        public EndOutcome End(AppProcess app, CancellationToken token) { Ended.Add(app.Name); Apps.RemoveAll(item => item.Path == app.Path); return Outcome; }
    }

    internal static void RunEngine(string fixture, Action<bool, string> check)
    {
        // Read-only listing of this PC: invariants only (which apps are open is not ours to assume).
        var manager = new WindowsProcessManager();
        var apps = manager.List();
        string? own = Environment.ProcessPath;
        check(apps.All(app => app.MemoryBytes > 0 && app.ProcessCount > 0 && app.Name.Length > 0 && File.Exists(app.Path)
                && !WindowsProcessManager.Protected.Contains(Path.GetFileNameWithoutExtension(app.Path))
                && !app.Path.Equals(own, StringComparison.OrdinalIgnoreCase) && app.Members.All(member => member.Pid != Environment.ProcessId))
            && apps.Select(app => app.Path.ToLowerInvariant()).Distinct().Count() == apps.Count,
            "The app list holds only programs with a window: never Windows' own processes, security software or Hush, one entry per program");

        // Ending, on a harmless child process of this test (ping waits about a minute and has no window).
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        Process Start() => Process.Start(new ProcessStartInfo(ping, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        var child = Start(); var bystander = Start(); var wrongStart = Start();
        try
        {
            var quick = new WindowsProcessManager(TimeSpan.FromMilliseconds(300));
            var app = new AppProcess("Ping test", ping, 1, [(child.Id, child.StartTime)]);
            var outcome = quick.End(app, CancellationToken.None);
            check(child.WaitForExit(3000) && outcome.Closed + outcome.Ended == 1 && outcome.StillRunning == 0 && !bystander.HasExited,
                "Ending closes exactly the listed process and leaves another process of the same program alone");
            var stale = new AppProcess("Ping test", ping, 1, [(wrongStart.Id, wrongStart.StartTime.AddHours(1))]);
            var refused = quick.End(stale, CancellationToken.None);
            check(refused == new EndOutcome(0, 0, 0) && !wrongStart.HasExited, "A process whose start time no longer matches (a reused id) is never touched");
            var ownAsApp = new AppProcess("Hush test", ping, 1, [(Environment.ProcessId, Process.GetCurrentProcess().StartTime)]);
            check(quick.End(ownAsApp, CancellationToken.None) == new EndOutcome(0, 0, 0), "Ending never touches the running program itself");
        }
        finally
        {
            foreach (var process in new[] { child, bystander, wrongStart }) { try { if (!process.HasExited) process.Kill(); } catch { } process.Dispose(); }
        }

        // Apps that run elevated are listed with a note instead of hidden; the start time is read through a limited handle (what elevated apps allow).
        using (var self = Process.GetCurrentProcess())
            check(WindowsProcessManager.StartTimeOf(self.Id) is { } started && Math.Abs((started - self.StartTime).TotalSeconds) < 2, "A process start time can be read through a limited-rights handle");
        check(new ProcessRow(new AppProcess("Elevated", @"C:\Fake\e.exe", 1, [], NeedsAdministrator: true), null).NeedsAdministrator && !new ProcessRow(new AppProcess("Plain", @"C:\Fake\p.exe", 1, []), null).NeedsAdministrator,
            "An app that runs elevated is marked 'Needs administrator to end' instead of being hidden");
        // The Temp meter counts every Safe temp file that is ready to clean, of any age (0.9: no "held back" split any more).
        string root = Path.Combine(fixture, "meter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string File_(string name, int bytes, int daysOld)
            {
                string path = Path.Combine(root, name);
                File.WriteAllBytes(path, new byte[bytes]);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld)); File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
                return path;
            }
            File_("old.tmp", 10, 3); File_("fresh1.tmp", 100, 0); File_("fresh2.tmp", 100, 0);
            var temp = new CleanupTarget(root, "User temp", CleanupKind.Temp, TimeSpan.Zero, Category: "Temp");
            var engine = new CleanupEngine([temp], includeRegistry: false, dataDirectory: Path.Combine(fixture, "meter-data"));
            var controller = new SmartBoostController(engine);
            var reading = controller.Measure(CancellationToken.None);
            check(reading.Files == 3 && reading.Bytes == 210, $"The meter counts old and brand-new temp files alike: {reading.Files}/{reading.Bytes}");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        var fake = new FakeProcesses();
        fake.Apps.Add(new AppProcess("Zen", @"C:\Fake\zen.exe", 1_900_000_000, [(1, DateTime.UtcNow), (2, DateTime.UtcNow), (3, DateTime.UtcNow)]));
        fake.Apps.Add(new AppProcess("Claude", @"C:\Fake\claude.exe", 850_000_000, [(4, DateTime.UtcNow)]));
        fake.Apps.Add(new AppProcess("Notepad", @"C:\Fake\notepad.exe", 45_000_000, [(5, DateTime.UtcNow)]));
        string logs = Path.Combine(fixture, "process-logs");
        MainWindow.ProcessLogDirectory = logs;
        var window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "process-preferences.json"), processManager: fake) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            void Pump(Func<bool> done, string what) { var clock = Stopwatch.StartNew(); while (!done() && clock.ElapsedMilliseconds < 6000) { UiLayoutChecks.Wait(30); } UiLayoutChecks.Require(done(), "Timed out waiting for " + what); }
            var summary = (TextBlock)window.FindName("ProcessSummary"); var note = (TextBlock)window.FindName("ProcessNote");
            Click("SettingsButton"); UiLayoutChecks.Wait(350);
            Pump(() => summary.Text.StartsWith("In progress"), "the app count");
            check(summary.Text.StartsWith("In progress · 3 apps · ") && ((Expander)window.FindName("SettingsProcessSection")).IsExpanded == false,
                $"The Process card says how many apps are open before it is opened ({summary.Text})");
            ((Expander)window.FindName("SettingsProcessSection")).IsExpanded = true;
            Pump(() => window.ProcessRowsShown.Count == 3, "the app list");
            UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Wait(380); UiLayoutChecks.Layout(root, new Size(920, 820));
            var list = (ItemsControl)window.FindName("ProcessList");
            var ends = Descendants<Button>(list).Where(button => button.Tag is ProcessRow).ToList();
            check(ends.Count == 3 && ends.All(button => button.Content?.ToString() == "End") && window.ProcessRowsShown[0].Name == "Zen" && window.ProcessRowsShown[0].Detail.Contains("1.8 GB")
                && window.ProcessRowsShown[0].Detail.Contains("3 processes"), "Opening the card lists each app with its memory and an End button");
            var scroll = (ScrollViewer)window.FindName("WindowsSettingsScroll");
            var card = (FrameworkElement)window.FindName("SettingsProcessSection");
            scroll.ScrollToVerticalOffset(Math.Max(0, card.TransformToAncestor((Visual)scroll.Content).Transform(new Point()).Y - 10)); UiLayoutChecks.Wait(120);
            UiLayoutChecks.Layout(root, new Size(920, 820));
            Capture(root, "process-card-dark");

            // Cancel: nothing is ended. Confirm: that app only, logged, list refreshed, plain result line.
            ends[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); UiLayoutChecks.Wait(350);
            var consent = (FrameworkElement)window.FindName("ConsentOverlay");
            check(consent.Visibility == Visibility.Visible && ((TextBlock)window.FindName("ConsentTitle")).Text == "End Zen?"
                && ((TextBlock)window.FindName("ConsentText")).Text.Contains("anything unsaved in it is lost") && ((Button)window.FindName("ConsentConfirmButton")).Content.ToString() == "End",
                "End asks first and says what will happen");
            Click("ConsentCancelButton"); UiLayoutChecks.Wait(350);
            check(fake.Ended.Count == 0, "Keeping the app open ends nothing");
            ends[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); UiLayoutChecks.Wait(350);
            Click("ConsentConfirmButton");
            Pump(() => fake.Ended.Count == 1 && window.ProcessRowsShown.Count == 2, "the app to be ended and the list to refresh");
            check(fake.Ended.SequenceEqual(["Zen"]) && window.ProcessRowsShown.All(row => row.Name != "Zen") && note.Text.Contains("Zen was closed")
                && File.ReadAllText(Path.Combine(logs, "process-ends.jsonl")).Contains("\"app\":\"Zen\""), "Confirming ends only that app, refreshes the list, says so plainly and logs it");
            fake.Outcome = new EndOutcome(0, 0, 3);
            Pump(() => Descendants<Button>(list).Count(button => button.Tag is ProcessRow) == 2, "the refreshed rows");
            Descendants<Button>(list).First(button => button.Tag is ProcessRow).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); UiLayoutChecks.Wait(350);
            Click("ConsentConfirmButton");
            Pump(() => fake.Ended.Count == 2, "the second end");
            Pump(() => note.Text.Contains("still running"), "the still-running note");
            check(note.Text.Contains("waiting for you to save"), "An app that stays open (waiting to save) is reported honestly, not as ended");
        }
        finally { window.Close(); MainWindow.ProcessLogDirectory = null; }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }

    private static void Capture(FrameworkElement root, string name)
    {
        if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is not string directory) return;
        Directory.CreateDirectory(directory); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
}
