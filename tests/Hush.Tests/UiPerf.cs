using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Hush;

// `Hush.Tests.exe --ui-perf`: shows a real Hush window for about half a minute (not activated, not in the taskbar) and measures what it costs:
// CPU share while idle on Home and in Settings, frame times while cards expand and collapse and while a popup opens and closes, and memory.
// Read-only: it uses a synthetic fixture folder, a fake update client and never cleans anything. Run it before and after touching animation or layout.
internal static class UiPerf
{
    private sealed class QuietClient : IUpdateClient
    {
        public Task<AppRelease?> CheckAsync(CancellationToken cancellation) => Task.FromResult<AppRelease?>(null);
        public Task<string> DownloadAsync(AppRelease release, IProgress<double>? progress, CancellationToken cancellation, string? stagingRoot = null) => throw new InvalidDataException("no downloads in a perf run");
        public void Dispose() { }
    }

    // Preferences with the terms accepted and the administrator question answered, so no first-run popup (which blurs the app) is open while measuring.
    private static string AcceptedPreferences(string fixture)
    {
        string path = Path.Combine(fixture, "preferences.json");
        new Preferences { TermsAccepted = Terms.Version, RunAsAdmin = false }.Save(path);
        return path;
    }

    internal static int Run()
    {
        int code = 0;
        var thread = new Thread(() => { try { code = RunOnWindowThread(); } catch (Exception error) { Console.Error.WriteLine(error); code = 1; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        return code;
    }

    private static string Memory()
    {
        using var me = Process.GetCurrentProcess();
        me.Refresh();
        return $"working set {me.WorkingSet64 / 1048576.0:N0} MB, private {me.PrivateMemorySize64 / 1048576.0:N0} MB, managed heap {GC.GetTotalMemory(false) / 1048576.0:N1} MB";
    }

    // Pumps the window's messages for a fixed time (UiLayoutChecks.Wait gives up after five seconds, which is too short for idle readings).
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static int uiThreadId;
    private static Dictionary<int, TimeSpan> ThreadTimes(Process me)
    {
        var result = new Dictionary<int, TimeSpan>();
        me.Refresh();
        foreach (ProcessThread thread in me.Threads) { try { result[thread.Id] = thread.TotalProcessorTime; } catch { } }
        return result;
    }

    private static int CountEffects(DependencyObject parent)
    {
        int count = 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { Effect: not null } lit)
            {
                count++;
                if (Environment.GetEnvironmentVariable("HUSH_PERF_EFFECTS") == "1")
                {
                    string where = ""; for (var up = VisualTreeHelper.GetParent(child); up != null && where.Length < 80; up = VisualTreeHelper.GetParent(up)) if (up is FrameworkElement { Name.Length: > 0 } named) where += named.Name + "<";
                    Console.WriteLine($"      effect on {child.GetType().Name} {(child as FrameworkElement)?.Name} ({lit.Effect.GetType().Name}) visible={lit.IsVisible} in {where}");
                }
            }
            count += CountEffects(child);
        }
        return count;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }

    // Listening to CompositionTarget.Rendering itself keeps WPF drawing 60 frames a second, so idle readings are taken WITHOUT it (frames = false).
    private static void Measure(string name, Action begin, int milliseconds, bool frames = true)
    {
        var intervals = new List<double>();
        TimeSpan last = TimeSpan.MinValue;
        void Rendering(object? sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == last) return; // WPF can raise the event twice for one frame
            if (last != TimeSpan.MinValue) intervals.Add((now - last).TotalMilliseconds);
            last = now;
        }
        using var me = Process.GetCurrentProcess();
        var cpuBefore = me.TotalProcessorTime;
        var threadsBefore = ThreadTimes(me);
        var clock = Stopwatch.StartNew();
        if (frames) CompositionTarget.Rendering += Rendering;
        begin();
        Pump(milliseconds);
        if (frames) CompositionTarget.Rendering -= Rendering;
        clock.Stop();
        me.Refresh();
        double cpu = (me.TotalProcessorTime - cpuBefore).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100;
        if (Environment.GetEnvironmentVariable("HUSH_PERF_THREADS") == "1")
        {
            var after = ThreadTimes(me);
            var top = after.Select(pair => (Id: pair.Key, Ms: (pair.Value - (threadsBefore.TryGetValue(pair.Key, out var b) ? b : TimeSpan.Zero)).TotalMilliseconds)).Where(x => x.Ms >= 5).OrderByDescending(x => x.Ms).Take(5);
            Console.WriteLine("      threads: " + string.Join(", ", top.Select(x => $"{(x.Id == uiThreadId ? "UI" : x.Id.ToString())} {x.Ms:0} ms")));
        }
        intervals.Sort();
        string frameText = !frames ? "" : intervals.Count == 0 ? "no frames drawn"
            : $"{intervals.Count} frames, median {intervals[intervals.Count / 2]:0.0} ms, worst {intervals[^1]:0.0} ms, {intervals.Count(f => f > 33.4)} slower than 33 ms";
        Console.WriteLine($"{name,-44} CPU {cpu,5:0.0}% of one core ({cpu / Environment.ProcessorCount,4:0.00}% of the PC)   {frameText}");
    }

    private static int RunOnWindowThread()
    {
        uiThreadId = (int)GetCurrentThreadId();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string testsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string fixture = Path.Combine(testsRoot, ".fixtures-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            string old = Path.Combine(fixture, "old.tmp");
            File.WriteAllText(old, "synthetic");
            File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-10)); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
            var engine = new CleanupEngine([new(fixture, "User temp", CleanupKind.Temp, TimeSpan.Zero)], dataDirectory: Path.Combine(fixture, "audit"));
            var app = new App(); app.InitializeComponent();
            // HUSH_PERF_CORES=n: run on n logical processors only (an Intel i3 has 4 threads; the cores are also slower, so read times as a lower bound).
            if (int.TryParse(Environment.GetEnvironmentVariable("HUSH_PERF_CORES"), out int cores) && cores is > 0 and < 32) { using var self = Process.GetCurrentProcess(); self.ProcessorAffinity = (IntPtr)((1L << cores) - 1); Console.WriteLine($"EXPERIMENT: limited to {cores} logical processors"); }
            if (Environment.GetEnvironmentVariable("HUSH_PERF_SW") == "1") RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            // HUSH_PERF_GLASS=1: measure the Glass window (a test process is Layered otherwise) (a test process is Layered unless it asks).
            WindowGlass.Forced = Environment.GetEnvironmentVariable("HUSH_PERF_GLASS") == "1" ? WindowLook.Glass : null;
            var window = new MainWindow(engine, preferencesPath: AcceptedPreferences(fixture), updateFactory: () => new QuietClient())
            { ShowActivated = false, ShowInTaskbar = false };
            WindowGlass.Forced = null;
            Console.WriteLine($"Window look: {WindowGlass.Active}");
            // HUSH_PERF_OPAQUE=1: the same window without per-pixel transparency (an experiment: how much of the cost is the layered window?).
            if (Environment.GetEnvironmentVariable("HUSH_PERF_OPAQUE") == "1") { window.AllowsTransparency = false; window.Background = Brushes.Black; Console.WriteLine("EXPERIMENT: window is NOT transparent"); }
            Console.WriteLine($"Machine: {Environment.ProcessorCount} logical processors. Rendering tier {RenderCapability.Tier >> 16} (2 = full hardware).");
            Console.WriteLine($"Before the window opens: {Memory()}");
            window.Show();
            Pump(1500);
            Console.WriteLine($"Window open, Home:       {Memory()}");
            // HUSH_PERF_CAPTURE=<script.ps1>|<png>: photograph the window's screen area (what Windows really composes, corners and shadow included).
            // The window is raised above other windows (not activated) for the second the photograph takes, then put back.
            void Capture(string tag)
            {
                if (Environment.GetEnvironmentVariable("HUSH_PERF_CAPTURE") is not string capture || !capture.Contains('|')) return;
                window.Topmost = true; Pump(500);
                var scale = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice.M11;
                var topLeft = window.PointToScreen(new Point(0, 0));
                int margin = 30, w = (int)(window.ActualWidth * scale) + margin * 2, h = (int)(window.ActualHeight * scale) + margin * 2;
                var parts = capture.Split('|');
                string png = parts[1].Replace(".png", $"-{tag}.png");
                using var shot = Process.Start(new ProcessStartInfo("pwsh", $"-NoProfile -File \"{parts[0]}\" {(int)topLeft.X - margin} {(int)topLeft.Y - margin} {w} {h} \"{png}\"") { UseShellExecute = false, CreateNoWindow = true })!;
                shot.WaitForExit(15000);
                window.Topmost = false;
                Console.WriteLine($"Captured the window area to {png}");
            }
            if (Environment.GetEnvironmentVariable("HUSH_PERF_LIGHT") == "1") { window.ToggleTheme(); Pump(700); } // photograph the light theme
            Capture("home");
            Click(window, "SettingsButton"); // Home -> Settings below; first a quiet Home reading
            if (Environment.GetEnvironmentVariable("HUSH_PERF_CAPTURE") != null) { Pump(1300); Capture("settings"); Click(window, "AboutButton"); Pump(700); Capture("about"); Click(window, "CloseAboutButton"); Pump(500); }
            Click(window, "SettingsBackButton"); Pump(600);

            Measure("Home, idle for 5 s", () => { }, 5000, frames: false);
            if (Environment.GetEnvironmentVariable("HUSH_PERF_TIMELINE") == "1")
            {
                // CPU per half second after Settings opens: a one-time warm-up burst decays, a polling cost repeats with the 2-second timer.
                using var me = Process.GetCurrentProcess();
                for (int round = 1; round <= 6; round++)
                {
                    if (round > 1) { Click(window, "SettingsBackButton"); Pump(1500); }
                    var threadsBefore = ThreadTimes(me);
                    var clickClock = Stopwatch.StartNew();
                    void Swap() { ((UIElement)window.FindName("ScanPage")).Visibility = Visibility.Collapsed; ((UIElement)window.FindName("SettingsPage")).Visibility = Visibility.Visible; }
                    if (round == 4) Swap(); // same page swapped in directly: no click handler (no reading of Windows, no timers) and no fade
                    else if (round == 5)
                    {
                        // the click handler's work, but no fade
                        foreach (var step in new[] { "LoadWindowsSettings", "RefreshWindowsUpdateState", "RefreshAdminSwitch", "LoadStartupApps", "StartResourceIndicators", "RefreshTempFiles" })
                            typeof(MainWindow).GetMethod(step, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
                        Swap();
                    }
                    else if (round == 6) Motion.EnterPage((FrameworkElement)window.FindName("MainContent"), Swap); // the fade only
                    else Click(window, "SettingsButton");
                    Console.WriteLine($"Settings visit {round}: click handler itself (UI thread, synchronous): {clickClock.ElapsedMilliseconds} ms");
                    int layoutPasses = 0, sizeChanges = 0;
                    EventHandler layout = (_, _) => layoutPasses++;
                    SizeChangedEventHandler resized = (_, _) => sizeChanges++;
                    window.LayoutUpdated += layout; ((FrameworkElement)window.FindName("WindowsSettingsScroll")).SizeChanged += resized;
                    var line = new List<string>(); var last = me.TotalProcessorTime;
                    for (int i = 0; i < 6; i++) { Pump(500); me.Refresh(); var now = me.TotalProcessorTime; line.Add($"{(now - last).TotalMilliseconds:0}"); last = now; }
                    var after = ThreadTimes(me);
                    window.LayoutUpdated -= layout; ((FrameworkElement)window.FindName("WindowsSettingsScroll")).SizeChanged -= resized;
                    Console.WriteLine("   CPU ms per 0.5 s: " + string.Join(" ", line) + $"   layout passes {layoutPasses}, Settings size changes {sizeChanges}");
                    Console.WriteLine("   threads: " + string.Join(", ", after.Select(pair => (Id: pair.Key, Ms: (pair.Value - (threadsBefore.TryGetValue(pair.Key, out var b) ? b : TimeSpan.Zero)).TotalMilliseconds)).Where(x => x.Ms >= 5).OrderByDescending(x => x.Ms).Select(x => $"{(x.Id == uiThreadId ? "UI" : x.Id.ToString())} {x.Ms:0} ms")));
                }
            }
            else Click(window, "SettingsButton");
            Pump(900);
            Measure("Settings page, idle for 6 s (meters running)", () => { }, 6000, frames: false);
            Console.WriteLine($"Settings open:           {Memory()}   effects attached: {CountEffects(window)}");
            // Where does the Settings idle cost come from? Stop the 2-second meter timer and read again, then time one process-list refresh on its own.
            var stop = typeof(MainWindow).GetMethod("StopResourceIndicators", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            stop.Invoke(window, null);
            Measure("Settings page, idle 6 s, meter timer stopped", () => { }, 6000, frames: false);
            var listClock = Stopwatch.StartNew();
            using (var me = Process.GetCurrentProcess())
            {
                var cpu0 = me.TotalProcessorTime;
                for (int i = 0; i < 3; i++) new WindowsProcessManager().List();
                me.Refresh();
                Console.WriteLine($"Process list x3: {listClock.ElapsedMilliseconds} ms wall, {(me.TotalProcessorTime - cpu0).TotalMilliseconds:0} ms CPU");
            }
            var start = typeof(MainWindow).GetMethod("StartResourceIndicators", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            start.Invoke(window, null);

            if (Environment.GetEnvironmentVariable("HUSH_PERF_EXPERIMENT") == "1")
            {
                stop.Invoke(window, null);
                var bar = (ScaleTransform)((Border)window.FindName("CpuFill")).RenderTransform;
                void Bars() { for (int i = 0; i < 5; i++) { bar.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(i % 2 == 0 ? 0.8 : 0.2, TimeSpan.FromMilliseconds(400))); Pump(2000); } }
                var tick = typeof(MainWindow).GetMethod("ResourceTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                Measure("EXPERIMENT real ResourceTick x5 in 10 s", () => { for (int i = 0; i < 5; i++) { tick.Invoke(window, [null, EventArgs.Empty]); Pump(2000); } }, 10, frames: false);
                Measure("EXPERIMENT one bar animating 5 times in 10 s, effects on", Bars, 10, frames: false);
                var removed = new List<(UIElement Element, System.Windows.Media.Effects.Effect Effect)>();
                void Strip(DependencyObject parent)
                {
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    {
                        var child = VisualTreeHelper.GetChild(parent, i);
                        if (child is UIElement element && element.Effect != null) { removed.Add((element, element.Effect)); element.Effect = null; }
                        Strip(child);
                    }
                }
                Strip(window);
                Console.WriteLine($"   (removed {removed.Count} effects in the visual tree)");
                Measure("EXPERIMENT the same with every effect removed", Bars, 10, frames: false);
                foreach (var (element, effect) in removed) element.Effect = effect;
            }
            var privacy = (Expander)window.FindName("SettingsPrivacySection");
            var boost = (Expander)window.FindName("SettingsBoostSection");
            Measure("Expand Privacy card", () => privacy.IsExpanded = true, 900);
            Measure("Collapse Privacy card", () => privacy.IsExpanded = false, 900);
            Measure("Expand Smart Boost card", () => boost.IsExpanded = true, 900);
            Measure("Collapse Smart Boost card", () => boost.IsExpanded = false, 900);
            Measure("Expand, collapse x3 quickly", () => { for (int i = 0; i < 3; i++) { privacy.IsExpanded = true; Pump(200); privacy.IsExpanded = false; Pump(200); } }, 400);
            // Smooth scrolling on a long page: three cards open, ten wheel notches 90 ms apart, down and back up. The glide is the thing the person feels.
            privacy.IsExpanded = true; boost.IsExpanded = true; ((Expander)window.FindName("SettingsImpactSection")).IsExpanded = true; Pump(1200);
            var settingsScroll = (ScrollViewer)window.FindName("WindowsSettingsScroll");
            void Wheel(int delta) => settingsScroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = settingsScroll });
            Console.WriteLine($"   (Settings page is {settingsScroll.ExtentHeight:0} DIP tall in a {settingsScroll.ViewportHeight:0} DIP window)");
            Measure("Wheel-scroll Settings down, 10 notches", () => { for (int i = 0; i < 10; i++) { Wheel(-120); Pump(90); } Pump(600); }, 10);
            Measure("Wheel-scroll Settings up, 10 notches", () => { for (int i = 0; i < 10; i++) { Wheel(120); Pump(90); } Pump(600); }, 10);
            privacy.IsExpanded = false; boost.IsExpanded = false; ((Expander)window.FindName("SettingsImpactSection")).IsExpanded = false; Pump(600);
            Click(window, "SettingsBackButton"); Pump(700);

            // The cleaning cue inside the gauge while progress ticks 20 times a second, as a real clean reports it: this is the scene that must feel like 60 fps.
            var setBusy = typeof(MainWindow).GetMethod("SetBusy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var cleaningDetail = (TextBlock)window.FindName("StatusDetail");
            var cue = (ScanActivity)window.FindName("FileScanActivity");
            Measure("Cleaning cue in the gauge, 5 s", () =>
            {
                setBusy.Invoke(window, [true, "clean"]);
                for (int i = 0; i < 100; i++) { window.AnimateGaugeTo(i); cleaningDetail.Text = $"{i * 50:N0} of 5,000 items processed"; Pump(50); }
            }, 10);
            if (Environment.GetEnvironmentVariable("HUSH_PERF_CUE_PARTS") == "1")
            {
                setBusy.Invoke(window, [true, "clean"]); Pump(300); Console.WriteLine($"   effects attached while cleaning: {CountEffects(window)}"); cue.IsRunning = true;
                Measure("  cue alone (progress still), 3 s", () => { setBusy.Invoke(window, [false, ""]); setBusy.Invoke(window, [true, "clean"]); }, 3000);
                Measure("  progress alone (cue stopped), 3 s", () => { cue.IsRunning = false; for (int i = 0; i < 60; i++) { window.AnimateGaugeTo(i); cleaningDetail.Text = $"{i * 50:N0} of 5,000 items processed"; Pump(50); } }, 10);
            }
            setBusy.Invoke(window, [false, ""]); Pump(600);
            Measure("Open About popup", () => Click(window, "AboutButton"), 900);
            Measure("About popup idle for 3 s", () => { }, 3000, frames: false);
            Measure("Close About popup", () => Click(window, "CloseAboutButton"), 900);
            Measure("Open and close About again", () => { Click(window, "AboutButton"); Pump(500); Click(window, "CloseAboutButton"); }, 900);

            Pump(3000); // let tiered JIT settle, then read Home again
            Measure("Home, idle for 8 s (warmed up)", () => { }, 8000, frames: false);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Console.WriteLine($"After a full collection: {Memory()}");
            MainWindow.TrimMemoryNow(releasePages: true); Pump(300);
            Console.WriteLine($"After Hush's own trim (what happens a few seconds after it loses focus): {Memory()}");
            Click(window, "SettingsButton"); Pump(900);
            Console.WriteLine($"...and back in use (Settings opened again):      {Memory()}");
            window.Close();
            return 0;
        }
        finally
        {
            string resolved = Path.GetFullPath(fixture);
            if (resolved.StartsWith(testsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith(".fixtures-perf-") && Directory.Exists(resolved))
            {
                foreach (string file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(resolved, true);
            }
        }
    }

    // `Hush.Tests.exe --preview-cleaning <folder>`: renders the Home page in the middle of a clean (dark and light) as PNGs, using a fixture and no real cleaning.
    internal static int PreviewCleaning(string directory)
    {
        int code = 0;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                string testsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
                string fixture = Path.Combine(testsRoot, ".fixtures-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fixture); Directory.CreateDirectory(directory);
                try
                {
                    var engine = new CleanupEngine([new(fixture, "User temp", CleanupKind.Temp, TimeSpan.Zero)], dataDirectory: Path.Combine(fixture, "audit"));
                    var app = new App(); app.InitializeComponent();
                    var window = new MainWindow(engine, preferencesPath: AcceptedPreferences(fixture), updateFactory: () => new QuietClient()) { ShowActivated = false, ShowInTaskbar = false };
                    window.Show(); Pump(700);
                    var setBusy = typeof(MainWindow).GetMethod("SetBusy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    setBusy.Invoke(window, [true, "clean"]);
                    window.GaugePercent = 42;
                    ((TextBlock)window.FindName("StatusTitle")).Text = "Cleaning selected items…";
                    ((TextBlock)window.FindName("StatusDetail")).Text = "2,104 of 5,000 items processed";
                    var root = (FrameworkElement)window.Content;
                    foreach (bool light in new[] { false, true })
                    {
                        if (window.IsLightTheme != light) { window.ToggleTheme(); }
                        Pump(1200);
                        var size = new Size(920, 820);
                        UiLayoutChecks.Layout(root, size);
                        UiLayoutChecks.Save(root, size, Path.Combine(directory, light ? "cleaning-light.png" : "cleaning-dark.png"));
                        for (int extra = 2; extra <= 4; extra++) { Pump(430); UiLayoutChecks.Save(root, size, Path.Combine(directory, $"cleaning-{(light ? "light" : "dark")}-{extra}.png")); }
                    }
                    // Every main color the person can choose, in both themes (the cue follows the accent and the muted text color by resource).
                    foreach (var swatch in AccentPalette.All.Skip(1))
                        foreach (bool light in new[] { false, true })
                        {
                            if (window.IsLightTheme != light) window.ToggleTheme();
                            window.ChooseAccent(swatch.Id); Pump(1300);
                            var size = new Size(920, 820);
                            UiLayoutChecks.Layout(root, size);
                            UiLayoutChecks.Save(root, size, Path.Combine(directory, $"cleaning-{swatch.Id}-{(light ? "light" : "dark")}.png"));
                        }
                    window.Close();
                }
                finally { try { Directory.Delete(fixture, true); } catch { } }
            }
            catch (Exception error) { Console.Error.WriteLine(error); code = 1; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        return code;
    }

    // `Hush.Tests.exe --preview-more <folder>`: Settings with Interface size and Services open, and the update popup after a check, in both themes.
    internal static int PreviewMore(string directory)
    {
        int code = 0;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                string testsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
                string fixture = Path.Combine(testsRoot, ".fixtures-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fixture); Directory.CreateDirectory(directory);
                try
                {
                    var engine = new CleanupEngine([new(fixture, "User temp", CleanupKind.Temp, TimeSpan.Zero)], dataDirectory: Path.Combine(fixture, "audit"));
                    var app = new App(); app.InitializeComponent();
                    var window = new MainWindow(engine, preferencesPath: AcceptedPreferences(fixture), updateFactory: () => new QuietClient(), serviceSystem: ServiceChecks.BigPc()) { ShowActivated = false, ShowInTaskbar = false, MinimumUpdateCheck = TimeSpan.FromMilliseconds(100) };
                    window.Show(); Pump(700);
                    var root = (FrameworkElement)window.Content;
                    var size = new Size(920, 820);
                    void Shot(string name, bool light) { if (window.IsLightTheme != light) window.ToggleTheme(); Pump(900); UiLayoutChecks.Layout(root, size); UiLayoutChecks.Save(root, size, Path.Combine(directory, $"{name}-{(light ? "light" : "dark")}.png")); }
                    Click(window, "SettingsButton"); Pump(900);
                    ((Expander)window.FindName("SettingsSizeSection")).IsExpanded = true; ((Expander)window.FindName("SettingsServicesSection")).IsExpanded = true; Pump(700);
                    foreach (bool light in new[] { false, true }) Shot("settings-open", light);
                    Click(window, "SettingsBackButton"); Pump(700);
                    Click(window, "AboutButton"); Pump(500); Click(window, "CheckUpdateButton"); Pump(1500);
                    foreach (bool light in new[] { false, true }) Shot("about-uptodate", light);
                    window.Close();
                }
                finally { try { Directory.Delete(fixture, true); } catch { } }
            }
            catch (Exception error) { Console.Error.WriteLine(error); code = 1; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        return code;
    }

    private static void Click(MainWindow window, string name) =>
        ((ButtonBase)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
}
