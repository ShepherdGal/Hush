using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Hush;

// The Boost button (Info card), the "Temporary files" bar, and the Settings > Browser data flow.
public partial class MainWindow
{
    private IMemoryBooster memoryBooster = new WindowsMemoryBooster();
    private bool boosting, tempMeasuring, browserDataPending;
    internal bool IsBoosting => boosting;
    // How long each Boost stage stays on screen at least (so the animation reads as a calm sequence even when the work is instant) and how
    // long "Done" is held. Tests set both to zero.
    internal TimeSpan BoostStageMinimum { get; set; } = TimeSpan.FromMilliseconds(800);
    internal TimeSpan BoostDoneHold { get; set; } = TimeSpan.FromMilliseconds(1500);
    // The few seconds after a Boost (or an automatic one) during which the Info card's single line reports it instead of the usual reading.
    private DateTime infoNoteUntilUtc = DateTime.MinValue;
    private string infoNoteDetail = "";

    // ----- Temporary files bar -----
    private async void RefreshTempFiles()
    {
        if (tempMeasuring) return;
        tempMeasuring = true;
        try
        {
            var reading = await Task.Run(() => boostController.Measure(CancellationToken.None));
            lastReading = reading;
            ShowTempFiles(reading);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { TempText.Text = "—"; }
        finally { tempMeasuring = false; }
    }

    private double TempFraction(long bytes) => Math.Min(1.0, bytes / (Boost.ThresholdMb * 1048576.0));

    private void ShowTempFiles(JunkReading reading, int milliseconds = 400)
    {
        // The bar is full when the Safe junk reaches the cleanup threshold chosen in Smart Boost.
        ShowMeter(TempFill, TempText, TempFraction(reading.Bytes) * 100, reading.Files == 0 ? "0 B" : Sizes.Format(reading.Bytes), ResourceSnapshot.TempAmber, ResourceSnapshot.TempRed, milliseconds);
        TempText.ToolTip = (reading.Files == 0 ? "No Safe temporary files are ready to clean." : $"{reading.Files:N0} Safe temporary files ({Sizes.Format(reading.Bytes)}) are ready to clean.");
    }

    // The Info card's one line normally reads the PC ("Everything looks light."); for a few seconds after a Boost it reports the result.
    // The longer explanation (including that memory can refill) is the tooltip.
    private void ShowInfoNote(string text, string detail)
    {
        infoNoteUntilUtc = DateTime.UtcNow.AddSeconds(12);
        infoNoteDetail = detail;
        InfoSummary.Text = text; InfoSummary.Tag = null; InfoSummary.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        InfoSummary.ToolTip = detail.Length == 0 ? null : detail;
    }

    // ----- Boost -----
    // Like Microsoft PC Manager's Boost: clean temporary files and ask Windows to release idle memory. Both are explained and confirmed first.
    // Only Safe temporary files are cleaned (re-verified and logged by the engine); memory release never closes a program.
    // The two stages play as one calm sequence: a soft arc sweeps across the card while a stage runs, then the bar and number for what
    // was cleaned ease down together, then the memory bar does the same, and the button settles on "Done".
    private async void Boost_Click(object sender, RoutedEventArgs e)
    {
        if (boosting || busy) return;
        boosting = true;
        BoostButton.IsEnabled = false;
        try
        {
            var reading = await Task.Run(() => boostController.Measure(CancellationToken.None));
            string files = reading.Files == 0
                ? "No Safe temporary files were found right now."
                : $"Clean {Sizes.Format(reading.Bytes)} in {reading.Files:N0} Safe temporary files. Each file is checked again before it is removed, and every removal is logged.";
            bool agreed = await AskConsent("Boost now?",
                files + "\n\nAsk Windows to release idle memory held by your running programs. Nothing is closed, and a program may pause for a moment while it loads back what it needs. Memory can fill up again as you work."
                + "\n\nBoost never changes Windows services and makes no promise of a faster PC.", "Boost", "Not now");
            if (!agreed) return;
            boostCancellation?.Dispose();
            boostCancellation = new();
            var token = boostCancellation.Token;
            // One bar for the whole Boost: the first half follows the files really cleaned, the second half fills over the memory stage's minimum time.
            BoostSweep.Progress = 0;
            BoostSweep.IsRunning = true;

            // Stage 1: temporary files.
            BoostButton.Content = "Cleaning…";
            var clock = Stopwatch.StartNew();
            var cleanProgress = new Progress<double>(fraction => BoostSweep.Progress = 0.5 * fraction);
            var cleaned = await Task.Run(() => boostController.RunAutomaticBoost(token, cleanProgress));
            BoostSweep.Progress = 0.5;
            var after = await Task.Run(() => boostController.Measure(token));
            await Pace(clock);
            var tempText = new NumberTween { ValueChanged = value => TempText.Text = Sizes.Format((long)Math.Max(0, value)) };
            double drain = BoostStageMinimum == TimeSpan.Zero ? 0 : 900;
            ShowTempFiles(after, (int)drain);
            await tempText.RunAsync(reading.Bytes, after.Files == 0 ? 0 : after.Bytes, (int)drain);
            ShowTempFiles(after);
            lastReading = after;

            // Stage 2: memory.
            BoostButton.Content = "Freeing memory…";
            BoostSweep.Progress = double.NaN;
            BoostSweep.FillOver(BoostStageMinimum == TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : BoostStageMinimum + TimeSpan.FromMilliseconds(400), 0.95);
            clock.Restart();
            var memory = await Task.Run(() => memoryBooster.Release(token));
            await Pace(clock);
            double total = lastSample.MemoryTotal > 0 ? lastSample.MemoryTotal : new ResourceMonitor().Sample().MemoryTotal;
            double beforePercent = total <= 0 ? 0 : 100.0 * memory.UsedBefore / total, afterPercent = total <= 0 ? 0 : 100.0 * memory.UsedAfter / total;
            var memoryText = new NumberTween { ValueChanged = value => MemoryText.Text = $"{value:0}%" };
            ShowMeter(MemoryFill, MemoryText, afterPercent, $"{afterPercent:0}%", ResourceSnapshot.MemoryAmber, ResourceSnapshot.MemoryRed, (int)drain);
            await memoryText.RunAsync(beforePercent, afterPercent, (int)drain);
            MemoryText.Text = $"{afterPercent:0}%";

            // Done: the bar arrives at full, then a short, soft glow on the button and a one-line result.
            await BoostSweep.CompleteAsync();
            BoostSweep.IsRunning = false;
            BoostButton.Content = "Done";
            if (BoostDoneHold > TimeSpan.Zero) PulseBoostButton();
            string tempPart = cleaned == null ? "nothing to clean" : $"{Sizes.Format(cleaned.Bytes)} cleaned";
            string memoryPart = memory.ProgramsTrimmed == 0 ? "no memory released" : $"{Sizes.Format(memory.Released)} memory freed";
            string detail = $"Boosted at {DateTime.Now:t}: {tempPart} of temporary files; {memoryPart} from {memory.ProgramsTrimmed:N0} programs. Windows may refill memory as programs run. Hush itself uses {Sizes.Format(lastSample.HushWorkingSet)}.";
            ShowInfoNote($"Boosted · {memoryPart} · {tempPart}", detail);
            WriteBoostLog(cleaned, memory);
            // Cleaning behind the Review list's back would leave it stale, so earlier results are cleared and need a new scan.
            if (items.Count > 0 || CurrentScanState != ScanState.Ready)
            {
                LoadReviewItems([]); CurrentScanState = ScanState.Ready; ResetGauge();
                GaugeLabel.Text = "Click to scan"; RefreshSelection();
            }
            UpdateBoostSummary();
            if (BoostDoneHold > TimeSpan.Zero) await Task.Delay(BoostDoneHold);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowInfoNote("Boost couldn't finish", "Boost couldn't finish. Nothing else was changed.");
            UpdateBoostSummary();
        }
        finally
        {
            BoostSweep.IsRunning = false;
            boosting = false;
            BoostButton.IsEnabled = true;
            BoostButton.Content = "Boost";
        }
    }

    private async Task Pace(Stopwatch clock)
    {
        var remaining = BoostStageMinimum - clock.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
    }

    // A soft rise and fall of the button's glow (its own glow, not a new color or a size change).
    private void PulseBoostButton()
    {
        double rest = Motion.GetGlow(BoostButton);
        var pulse = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(Math.Max(0.34, rest * 4), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new SineEase { EasingMode = EasingMode.EaseOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        BoostButton.BeginAnimation(Motion.GlowProperty, pulse);
    }

    private void WriteBoostLog(CleanupResult? cleaned, MemoryBoostResult memory)
    {
        try
        {
            string directory = Path.Combine(CleanupEngine.DataDirectory, "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "boost.jsonl"), JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, temporaryBytes = cleaned?.Bytes ?? 0, temporaryFiles = cleaned?.Deleted ?? 0, cleanupLog = cleaned?.LogPath,
                memoryReleasedBytes = memory.Released, programsTrimmed = memory.ProgramsTrimmed
            }) + Environment.NewLine);
        }
        catch { }
    }

    // ----- Browser data (Settings) -----
    // History and cookies are never in a normal scan. They are picked here (cookies with a warning), scanned on demand and confirmed as usual.
    private async void BrowserChoice_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, BrowserCookiesBox) && BrowserCookiesBox.IsChecked == true)
        {
            bool agreed = await AskConsent("Include cookies and site data?",
                "Removing cookies signs you out of websites and clears the small settings those sites saved, in Chromium browsers and Firefox.\n\n"
                + "Your saved passwords, bookmarks and open tabs are not touched. You will still see exactly what was found, and confirm again, before anything is removed.",
                "Include", "Not now");
            if (!agreed) BrowserCookiesBox.IsChecked = false;
        }
        UpdateBrowserState();
    }

    private void UpdateBrowserState()
    {
        bool history = BrowserHistoryBox.IsChecked == true, cookies = BrowserCookiesBox.IsChecked == true;
        BrowserCleanButton.IsEnabled = history || cookies;
        BrowserSummary.Text = "Kept out of scans · " + (history && cookies ? "history and cookies selected" : history ? "history selected" : cookies ? "cookies selected" : "nothing selected");
        BrowserStatus.Text = history || cookies ? "Close your browsers first. Hush shows what it found before removing anything." : "";
    }

    private async void BrowserClean_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var chosen = new HashSet<string>();
        if (BrowserHistoryBox.IsChecked == true) chosen.Add("BrowserHistory");
        if (BrowserCookiesBox.IsChecked == true) chosen.Add("BrowserCookies");
        if (chosen.Count == 0) return;
        browserDataPending = true;
        ShowReviewPage(false);
        await StartScan(new ScanOptions(target => chosen.Contains(target.Category ?? ""), ReportIntervalMs: LowResourceMode.ReportIntervalMs, Background: LowResourceMode.IsActive));
    }

    // After the on-demand scan: select what the person asked for and show the normal confirmation (with the Advanced consent box for cookies).
    private void OfferBrowserDataConfirmation()
    {
        if (!browserDataPending) return;
        browserDataPending = false;
        if (CurrentScanState != ScanState.Completed) return;
        if (items.Count == 0)
        {
            StatusTitle.Text = scanSkippedLocations ? "Nothing found. Close your browsers and try again." : "No browser data found.";
            return;
        }
        foreach (var category in Categories) if (category.HasFiles) category.Selected = true;
        ShowConfirmation();
    }
}
