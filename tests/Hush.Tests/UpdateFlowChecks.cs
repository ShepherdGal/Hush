using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hush;

internal static class UpdateFlowChecks
{
    internal static void Run(CleanupEngine engine, string fixture)
    {
        Finish(ProtocolChecks(fixture));
        var client = new FakeClient();
        int installs = 0;
        var window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "update-preferences.json"),
            updateFactory: () => client,
            installUpdate: (_, _) => { installs++; throw new IOException("Synthetic install-start failure"); });
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.MinimumUpdateCheck = TimeSpan.Zero; // the timing rule has its own test below; the rest of the suite should not wait
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            var about = (FrameworkElement)window.FindName("AboutOverlay");
            var card = (FrameworkElement)window.FindName("AboutCard");
            var check = (Button)window.FindName("CheckUpdateButton");
            var status = (TextBlock)window.FindName("UpdateStatus");
            var hint = (TextBlock)window.FindName("UpdateHint");
            var notes = (TextBlock)window.FindName("PatchNotes");
            var sweep = (UpdateActivity)window.FindName("UpdateSweep");
            void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Open() { Click("AboutButton"); Wait(360); }
            void Close() { Click("CloseAboutButton"); Until(() => about.Visibility == Visibility.Collapsed); }
            void Layout(Size size) { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
            void Capture(string name)
            {
                if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is not string directory) return;
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
            }
            byte[] SweepPixels()
            {
                int height = (int)Math.Ceiling(sweep.ActualHeight);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(sweep), null, new Rect(0, 0, sweep.ActualWidth, height));
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(sweep.ActualWidth), height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var pixels = new byte[bitmap.PixelWidth * height * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
            }
            Open(); Layout(new Size(800, 740));
            Require(window.FindName("UpdateToken") == null && window.FindName("ConnectGitHubButton") == null && window.FindName("UpdateInstallPanel") == null
                && check.IsVisible && card.ActualHeight < 600,
                "About has no token, GitHub sign-in or second install step, and exposes Check for updates immediately under the version");
            Require(!((FrameworkElement)window.FindName("AppSurface")).IsEnabled, "About still blocks background input");
            var title = TextBlocks(card).First(text => text.Text == "Hush");
            Require(title.FontStyle == FontStyles.Normal && title.FontWeight == FontWeights.Medium
                && ((SolidColorBrush)title.Foreground).Color == (Color)Application.Current.Resources["PrimaryAccentColor"],
                "The About title is the upright Hush wordmark in the accent color");
            var pending = new TaskCompletionSource<AppRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Check = _ => pending.Task;
            Click("CheckUpdateButton"); Wait(360); Layout(new Size(800, 740));
            Require(window.IsUpdateBusy && sweep.IsRunning && !check.IsEnabled,
                "Checking shows progress and never asks for credentials");
            // One bar only: sample the running track several times; at most one bright run may ever exist (nothing follows the bar), and the track is 5 px thick.
            int RunsAcross(byte[] pixels)
            {
                int width = (int)Math.Ceiling(sweep.ActualWidth), row = (int)Math.Ceiling(sweep.ActualHeight) / 2, runs = 0; bool inside = false;
                for (int x = 0; x < width; x++) { bool on = pixels[(row * width + x) * 4 + 3] > 150; if (on && !inside) runs++; inside = on; }
                return runs;
            }
            int mostRuns = 0, framesWithBar = 0;
            for (int frame = 0; frame < 6; frame++) { int runs = RunsAcross(SweepPixels()); mostRuns = Math.Max(mostRuns, runs); if (runs > 0) framesWithBar++; Wait(300); }
            Require(mostRuns == 1 && framesWithBar >= 2 && Math.Abs(sweep.ActualHeight - 5) < 0.01, $"The update search is one filling bar, 5 px thick, with nothing following it (max {mostRuns} bar in {framesWithBar} of 6 frames)");
            var before = SweepPixels(); Wait(260);
            Require(!before.SequenceEqual(SweepPixels()), "The checking bar fills: its actual rendered pixels change");
            Capture("about-checking-" + (window.IsLightTheme ? "light" : "dark"));
            Wait(500); Capture("about-checking-2-" + (window.IsLightTheme ? "light" : "dark"));
            Wait(500); Capture("about-checking-3-" + (window.IsLightTheme ? "light" : "dark"));
            pending.SetResult(null); Until(() => !window.IsUpdateBusy);
            Require(status.Text.Contains("latest version") && !sweep.IsRunning, "A current release stops progress and reports the installed version");
            client.Check = _ => throw new InvalidDataException("Updates can't be checked right now. Try again in a little while.");
            Click("CheckUpdateButton"); Until(() => !window.IsUpdateBusy);
            Require(status.Text.Contains("can't be checked") && !status.Text.Contains("latest version"), "Unavailable releases never claim a successful current check");
            var release = new AppRelease(new Version(9, 0, 0), "# Improvements\n**Clearer controls**\n[Details](https://example.invalid/)",
                new Uri($"https://api.github.com/repos/{UpdateService.Repository}/releases/assets/1"), 10, new string('0', 64));
            client.Check = _ => Task.FromResult<AppRelease?>(release);
            Click("CheckUpdateButton"); Until(() => !window.IsUpdateBusy);
            Require(check.Content.ToString() == "Update now" && hint.Visibility == Visibility.Visible && status.Text.Contains("is available")
                && notes.Text.Contains("Clearer controls") && !notes.Text.Contains("**") && !notes.Text.Contains("https://"),
                "An available update shows its version, plain release notes, what will happen and one Update now action");
            var accentColor = (Color)Application.Current.Resources["PrimaryAccentColor"];
            Require(((SolidColorBrush)status.Foreground).Color == ((SolidColorBrush)Application.Current.Resources["GoodBrush"]).Color && status.FontWeight == FontWeights.Medium && hint.Text.Contains("Update now"),
                "A new version is announced in green (Medium, never bold)");
            Layout(new Size(800, 740)); Capture("about-update-found-" + (window.IsLightTheme ? "light" : "dark"));
            var consent = (FrameworkElement)window.FindName("ConsentOverlay");
            void Ask() { Click("CheckUpdateButton"); Until(() => consent.Visibility == Visibility.Visible); }
            void Answer(string button) { Click(button); Until(() => consent.Visibility == Visibility.Collapsed); }
            int downloads = 0;
            client.Download = (_, _, _) => { downloads++; throw new InvalidDataException("Download verification failed; the current app was left unchanged."); };
            Ask(); Wait(360); Layout(new Size(800, 740)); Capture("about-update-confirm-" + (window.IsLightTheme ? "light" : "dark"));
            var panel = (Panel)consent.Parent;
            Require(panel.Children.IndexOf(consent) > panel.Children.IndexOf((UIElement)about) && ((TextBlock)window.FindName("ConsentTitle")).Text.Contains("9.0")
                && ((TextBlock)window.FindName("ConsentText")).Text.Contains("backup") && ((Button)window.FindName("ConsentConfirmButton")).Content.ToString() == "Update now"
                && downloads == 0 && installs == 0, "Clicking Update now first asks Are you sure, above the About card, naming the version and the backup, before anything downloads");
            Answer("ConsentCancelButton");
            Require(downloads == 0 && installs == 0 && check.Content.ToString() == "Update now" && status.Text.Contains("is available") && !window.IsUpdateBusy
                && !((FrameworkElement)window.FindName("AppSurface")).IsEnabled && about.Visibility == Visibility.Visible,
                "Answering Not now downloads nothing, keeps the update offered and keeps About open and blocking the background");
            Require(!TextBlocks(card).Concat(new[] { status, hint }).Any(text => text.Text.Contains("GitHub", StringComparison.OrdinalIgnoreCase)),
                "People never see the word GitHub in the update flow");
            Ask(); Answer("ConsentConfirmButton"); Until(() => downloads == 1 && !window.IsUpdateBusy && status.Text.Contains("verification failed"));
            Require(installs == 0 && status.Text.Contains("verification failed") && ((SolidColorBrush)status.Foreground).Color != accentColor && check.Content.ToString() == "Update now" && check.IsEnabled,
                "A failed verification cannot start an updater and leaves the download action available for a retry");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Download = async (_, progress, _) => { progress?.Report(0.4); await gate.Task; progress?.Report(1); return Path.Combine(fixture, "verified-client-contract.exe"); };
            Ask(); Answer("ConsentConfirmButton"); Until(() => !double.IsNaN(sweep.Progress)); Wait(320); Layout(new Size(800, 740));
            Require(window.IsUpdateBusy && Math.Abs(sweep.Progress - 0.4) < 0.001 && status.Text.Contains("40%") && installs == 0,
                "One click starts the download and the bar and text show the real fraction without installing yet");
            Capture("about-downloading-" + (window.IsLightTheme ? "light" : "dark"));
            gate.SetResult(); Until(() => !window.IsUpdateBusy); Wait(50);
            Require(installs == 1 && about.Visibility == Visibility.Visible && status.Text.Contains("Couldn't start the installation")
                && check.IsEnabled && check.Content.ToString() == "Update now" && !status.Text.Contains("Downloading"),
                "A verified download goes straight to the updater; a hand-over failure keeps the app open and unchanged without claiming success");
            foreach (bool light in new[] { false, true })
            {
                if (window.IsLightTheme != light) window.ToggleTheme();
                Wait(340); Layout(new Size(800, 740));
                Require(card.ActualHeight <= 650 && new Rect(root.RenderSize).Contains(card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize))),
                    "Expanded About with an available update fits the minimum window in both themes");
                Capture($"about-update-available-{(light ? "light" : "dark")}");
            }
            Close(); Require(!sweep.IsRunning, "Closing About stops activity");
            Open();
            // A check always shows its searching animation for at least 3.5 seconds, even when the answer is instant, and Cancel still stops it at once.
            window.MinimumUpdateCheck = TimeSpan.FromMilliseconds(3500);
            client.Check = _ => Task.FromResult<AppRelease?>(null);
            var searching = System.Diagnostics.Stopwatch.StartNew();
            Click("CheckUpdateButton"); Until(() => window.IsUpdateBusy); Wait(1500);
            Require(window.IsUpdateBusy && sweep.IsRunning && double.IsNaN(sweep.Progress) && status.Text.Contains("Looking"), "An instant answer still keeps the search animation going for its minimum time");
            double firstFill = sweep.Fill; Wait(700);
            Require(firstFill > 0.15 && sweep.Fill > firstFill && sweep.Fill < 0.99, $"The check's loading bar fills smoothly from empty and is not full before the answer is in ({firstFill:0.00} then {sweep.Fill:0.00})");
            Capture("about-checking-" + (window.IsLightTheme ? "light" : "dark"));
            Until(() => !window.IsUpdateBusy);
            Require(searching.ElapsedMilliseconds >= 3400 && searching.ElapsedMilliseconds < 6500 && status.Text.Contains("latest version"),
                $"The searching animation lasts at least 3.5 seconds before the result appears ({searching.ElapsedMilliseconds} ms)");
            searching.Restart();
            Click("CheckUpdateButton"); Until(() => window.IsUpdateBusy); Wait(700); Click("CancelUpdateButton"); Until(() => !window.IsUpdateBusy);
            Require(searching.ElapsedMilliseconds < 2500 && status.Text.Contains("canceled"), $"Cancel stops the minimum-time search immediately ({searching.ElapsedMilliseconds} ms)");
            window.MinimumUpdateCheck = TimeSpan.Zero;
            var late = new TaskCompletionSource<AppRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Check = _ => late.Task;
            Click("CheckUpdateButton"); Close(); Open(); late.SetResult(release); Wait(80);
            Require(check.Content.ToString() == "Check for updates" && status.Text == "",
                "A closed session's late response cannot modify a reopened About popup");
            client.Check = async token => { await Task.Delay(Timeout.Infinite, token); return null; };
            Click("CheckUpdateButton"); Click("CancelUpdateButton"); Until(() => !window.IsUpdateBusy);
            Require(status.Text.Contains("canceled") && !sweep.IsRunning && check.IsEnabled, "Cancel stops a check and restores its action");
            Close();
            foreach (bool light in new[] { false, true })
            {
                if (window.IsLightTheme != light) window.ToggleTheme();
                Open(); Layout(new Size(920, 820)); Capture($"about-summary-{(light ? "light" : "dark")}");
                Close();
            }
            // ---- The quiet update notice beside the wordmark ----
            var notice = (Button)window.FindName("UpdateNoticeButton");
            Require(notice.Visibility == Visibility.Collapsed, "Nothing shows beside the wordmark before any check has found an update");
            client.Check = _ => Task.FromResult<AppRelease?>(null);
            var quiet = window.RunQuietUpdateCheck(); Until(() => quiet.IsCompleted);
            Require(notice.Visibility == Visibility.Collapsed, "Up to date: no notice");
            client.Check = _ => throw new System.Net.Http.HttpRequestException("offline");
            quiet = window.RunQuietUpdateCheck(); Until(() => quiet.IsCompleted);
            Require(notice.Visibility == Visibility.Collapsed, "Offline or failing: no notice and no error");
            int checks = 0, downloadsBefore = downloads, installsBefore = installs;
            client.Check = _ => { checks++; return Task.FromResult<AppRelease?>(release); };
            quiet = window.RunQuietUpdateCheck(); Until(() => quiet.IsCompleted); Wait(420);
            Require(notice.Visibility == Visibility.Visible && notice.Opacity > 0.95 && notice.Content.ToString() == "There is a new update!" && ((SolidColorBrush)notice.Foreground).Color == ((SolidColorBrush)Application.Current.Resources["GoodBrush"]).Color
                && notice.FontStyle == FontStyles.Normal && checks == 1 && downloads == downloadsBefore && installs == installsBefore, $"A newer version fades in a green, upright 'There is a new update!' beside the wordmark; nothing is downloaded or installed (vis {notice.Visibility}, opacity {notice.Opacity}, fg {((SolidColorBrush)notice.Foreground).Color} vs {((SolidColorBrush)Application.Current.Resources["GoodBrush"]).Color}, checks {checks}, downloads {downloads}, installs {installs})");
            Click("UpdateNoticeButton"); Until(() => about.Visibility == Visibility.Visible); Wait(100);
            Require(checks == 1 && !window.IsUpdateBusy && status.Text.Contains("is available") && ((SolidColorBrush)status.Foreground).Color == ((SolidColorBrush)Application.Current.Resources["GoodBrush"]).Color, "Clicking the notice opens the update popup, which already says in green that a version is available (no second check)");
            Until(() => !window.IsUpdateBusy);
            Require(status.Text.Contains("is available") && check.Content.ToString() == "Update now" && downloads == downloadsBefore && installs == installsBefore, "The popup then offers Update now without downloading anything yet");
            Close();
            var briefSource = string.Join("\n", "## What changed", "- long thing", "", "## In brief", "- **Cleans** system Temp files", "* One Hush window at a time", "- [Link](http://x) text", "- four", "- five", "- six is cut", "", "## Other", "- not this");
            var brief = ReleaseNotes.InBrief(briefSource);
            Require(brief.Split("\n").Length == 5 && brief.StartsWith("\u2022 Cleans system Temp files") && brief.Contains("\u2022 Link text") && !brief.Contains("six") && !brief.Contains("not this"),
                "The short release notes come from the 'In brief' section: at most five one-line bullets without markdown");
            Require(ReleaseNotes.InBrief("nothing useful").Length > 0 && ReleaseNotes.InBrief("- " + new string('x', 200)).Length <= 80, "Release notes without bullets fall back to a plain line and long bullets are cut to one line");
            Console.WriteLine("PASS: About without any sign-in, aqua italic title, smooth check animation, one-click download with real progress, verified hand-over, failures, cancellation, late responses and both-theme layouts");
        }
        finally { window.Close(); }
    }

    private static async Task ProtocolChecks(string fixture)
    {
        byte[] executable = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Hush.exe"));
        // Releases carry a zip (GitHub does not accept .exe uploads); the updater unpacks Hush-Portable.exe from it.
        byte[] package = Zip((UpdateService.PackagedExecutable, executable), ("README.txt", [1, 2, 3]));
        var release = new AppRelease(UpdateService.CurrentVersion, "Fixture", new Uri($"https://api.github.com/repos/{UpdateService.Repository}/releases/assets/1"),
            package.Length, Convert.ToHexString(SHA256.HashData(package)));
        using var validClient = new UpdateService(new Handler(request =>
        {
            Require(request.Headers.Authorization == null, "Downloads are anonymous: no Authorization header is ever sent");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
        }));
        string verified = await validClient.DownloadAsync(release, null, CancellationToken.None, Path.Combine(fixture, "verified-download"));
        Require(File.Exists(verified) && File.ReadAllBytes(verified).SequenceEqual(executable), "Actual hash, size and x64 PE/product/version validation accept a known fixture executable unpacked from the release zip");
        Require(File.Exists(Path.Combine(Path.GetDirectoryName(verified)!, UpdateService.StagedHashName)) && !File.Exists(Path.Combine(Path.GetDirectoryName(verified)!, UpdateService.AssetName)),
            "The unpacked app's hash is recorded and the downloaded zip is removed");
        bool rejected = false;
        try { await validClient.DownloadAsync(release with { Version = new Version(9, 0, 0) }, null, CancellationToken.None, Path.Combine(fixture, "bad-version")); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected && !Directory.EnumerateFiles(Path.Combine(fixture, "bad-version"), "*.exe", SearchOption.AllDirectories).Any(),
            "Correctly hashed downloads with a wrong version are rejected and their partial executable is removed");
        byte[] wrongName = Zip(("Other.exe", executable));
        using var wrongClient = new UpdateService(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wrongName) }));
        rejected = false;
        try { await wrongClient.DownloadAsync(release with { Bytes = wrongName.Length, Sha256 = Convert.ToHexString(SHA256.HashData(wrongName)) }, null, CancellationToken.None, Path.Combine(fixture, "wrong-package")); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "A correctly hashed zip without Hush-Portable.exe is rejected");
        foreach (var code in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized })
        {
            using var service = new UpdateService(new Handler(_ => new HttpResponseMessage(code)));
            rejected = false;
            try { await service.CheckAsync(CancellationToken.None); } catch (InvalidDataException ex) { rejected = !ex.Message.Contains("GitHub", StringComparison.OrdinalIgnoreCase); }
            Require(rejected, "Unavailable releases are explained in plain words and not mistaken for an up-to-date result");
        }
        string metadata = JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v" + UpdateService.CurrentVersion, assets = Array.Empty<object>() });
        using var currentClient = new UpdateService(new Handler(request =>
        {
            Require(request.RequestUri!.AbsolutePath == $"/repos/{UpdateService.Repository}/releases/latest" && request.Headers.Authorization == null,
                "Checks anonymously request the pinned repository's latest release");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadata) };
        }));
        Require(await currentClient.CheckAsync(CancellationToken.None) == null, "A matching installed release is reported current");
        using var previewClient = new UpdateService(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadata.Replace("\"prerelease\":false", "\"prerelease\":true")) }));
        rejected = false;
        try { await previewClient.CheckAsync(CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "A prerelease response cannot produce a successful stable-channel check");
        Console.WriteLine("PASS: Anonymous update requests, known-PE download verification, wrong-version cleanup and unavailable/current/prerelease update responses");
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] files)
    {
        using var memory = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
            foreach (var (name, data) in files) { using var entry = archive.CreateEntry(name).Open(); entry.Write(data); }
        return memory.ToArray();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => Task.FromResult(respond(request));
    }
    private sealed class FakeClient : IUpdateClient
    {
        internal Func<CancellationToken, Task<AppRelease?>> Check = _ => Task.FromResult<AppRelease?>(null);
        internal Func<AppRelease, IProgress<double>?, CancellationToken, Task<string>> Download = (_, _, _) => throw new InvalidDataException("Fixture download unavailable");
        public Task<AppRelease?> CheckAsync(CancellationToken cancellation) => Check(cancellation);
        public Task<string> DownloadAsync(AppRelease release, IProgress<double>? progress, CancellationToken cancellation, string? stagingRoot = null) => Download(release, progress, cancellation);
        public void Dispose() { }
    }
    private static IEnumerable<TextBlock> TextBlocks(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); if (child is TextBlock text) yield return text;
            foreach (var descendant in TextBlocks(child)) yield return descendant;
        }
    }
    private static void Finish(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> complete)
    {
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || clock.ElapsedMilliseconds >= 6000) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Require(complete(), "Update fixture did not settle within six seconds");
    }
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false; timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
