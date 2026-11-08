using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Hush;

// The Boost button (Info card), the "Temporary files" bar, and the Settings > Browser data flow.
public partial class MainWindow
{
    private IMemoryBooster memoryBooster = new WindowsMemoryBooster();
    private bool boosting, tempMeasuring, browserDataPending;
    internal bool IsBoosting => boosting;

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
            UpdateBoostSummary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { TempText.Text = "—"; }
        finally { tempMeasuring = false; }
    }

    private void ShowTempFiles(JunkReading reading)
    {
        // The bar is full when the Safe junk reaches the cleanup threshold chosen in Smart Boost.
        double fraction = Math.Min(1.0, reading.Bytes / (Boost.ThresholdMb * 1048576.0));
        ShowMeter(TempFill, TempText, fraction * 100, reading.Files == 0 ? "0 B" : Sizes.Format(reading.Bytes), 100);
        TempText.ToolTip = reading.Files == 0 ? "No Safe temporary files found" : $"{reading.Files:N0} Safe temporary files";
    }

    private void SetUpSmartBoost_Click(object sender, RoutedEventArgs e)
    {
        SettingsBoostSection.IsExpanded = true;
        SettingsBoostSection.BringIntoView();
    }

    // ----- Boost -----
    // Like Microsoft PC Manager's Boost: clean temporary files and ask Windows to release idle memory. Both are explained and confirmed first.
    // Only Safe temporary files are cleaned (re-verified and logged by the engine); memory release never closes a program.
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
            BoostButton.Content = "Boosting…";
            boostCancellation?.Dispose();
            boostCancellation = new();
            var token = boostCancellation.Token;
            var outcome = await Task.Run(() =>
            {
                var cleaned = boostController.RunAutomaticBoost(token);
                var memory = memoryBooster.Release(token);
                return (cleaned, memory);
            });
            string temp = outcome.cleaned == null ? "no Safe temporary files to clean" : $"{Sizes.Format(outcome.cleaned.Bytes)} of temporary files cleaned";
            string memoryText = outcome.memory.ProgramsTrimmed == 0 ? "no memory could be released" : $"{Sizes.Format(outcome.memory.Released)} of memory released from {outcome.memory.ProgramsTrimmed:N0} programs";
            boostActivity = $"Boosted at {DateTime.Now:t}: {temp}; {memoryText}. Windows may refill memory as programs run.";
            WriteBoostLog(outcome.cleaned, outcome.memory);
            // Cleaning behind the Review list's back would leave it stale, so earlier results are cleared and need a new scan.
            if (items.Count > 0 || CurrentScanState != ScanState.Ready)
            {
                LoadReviewItems([]); CurrentScanState = ScanState.Ready; ResetGauge();
                GaugeLabel.Text = "Click to scan"; RefreshSelection();
            }
            UpdateBoostSummary();
            ResourceTick(null, EventArgs.Empty);
            RefreshTempFiles();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            boostActivity = "Boost couldn't finish. Nothing else was changed.";
            UpdateBoostSummary();
        }
        finally
        {
            boosting = false;
            BoostButton.IsEnabled = true;
            BoostButton.Content = "Boost";
        }
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
