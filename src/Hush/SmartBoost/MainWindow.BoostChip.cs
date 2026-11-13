using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Hush;

// The Boost chip beside the Hush wordmark (owner, November 13, 2026). It is ALWAYS there, in both Simple and Pro mode, and never hides. It reads
// "RAM 62% · CPU 14% · 1.4 GB": memory in use, processor use and the Safe temporary files ready to clean, with a heartbeat line in front instead of an icon. Clicking it runs
// the same flow as the Settings Boost button (the "Boost now?" popup first; with 0 MB to clean it still opens and says so, because Boost also frees idle memory), and while
// that runs the chip shows the stage ("Cleaning…", "Freeing memory…", "Done").
//
// How it stays cheap (owner allowed one timer for this, "every 5 seconds, only while Hush is in front"): the RAM and CPU figures come from two Windows counters
// (ResourceMonitor, its own instance so Settings' readings never disturb the CPU difference), read on a 5 s DispatcherTimer that runs ONLY while the real app window is
// active and not minimized (stopped on Deactivated / Minimized / Closed, so it costs nothing in the background), and the text is only rewritten when a figure changed. The MB
// figure is one count-only, background-priority read of the Safe junk (the same one Boost uses): shortly after start and after a clean or a Boost. Test windows never start the
// timer or the read by themselves (SampleBoostChip / UpdateBoostChip are the seams).
public partial class MainWindow
{
    internal static readonly TimeSpan BoostChipInterval = TimeSpan.FromSeconds(5);
    private readonly ResourceMonitor chipMonitor = new();
    private DispatcherTimer? chipTimer;
    private ResourceSnapshot chipSample;
    private bool chipSampled;
    private JunkReading? chipReading;

    // The text the chip shows or is sliding to (tests and screen readers read this, not the half-way picture).
    internal string BoostChipLabel => chipTarget;
    internal bool BoostChipTimerRunning => chipTimer?.IsEnabled == true;

    private void InitializeBoostChip()
    {
        BoostSweep.Mirror = BoostChipBorder;   // the chip's own border draws itself while Boost runs (the same run as the Boost bar in Settings)
        UpdateBoostChip(null);
        Activated += (_, _) => ResumeBoostChip();
        Deactivated += (_, _) => chipTimer?.Stop();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) chipTimer?.Stop(); else if (IsActive) ResumeBoostChip(); };
        Closed += (_, _) => chipTimer?.Stop();
    }

    private void BoostChip_Click(object sender, RoutedEventArgs e) => Boost_Click(BoostButton, e);

    // Only the real app window runs the timer, and only while it is in front.
    private void ResumeBoostChip()
    {
        if (Application.Current?.MainWindow != this || WindowState == WindowState.Minimized || closing) return;
        if (chipTimer == null)
        {
            chipTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
            chipTimer.Tick += (_, _) => { chipTimer.Interval = BoostChipInterval; if (WindowState != WindowState.Minimized) SampleBoostChip(); };
        }
        chipMonitor.Sample();               // primes the CPU difference; the first full reading follows 1.5 s later, then every 5 s
        chipTimer.Interval = TimeSpan.FromMilliseconds(1500);
        chipTimer.Start();
    }

    internal void SampleBoostChip()
    {
        chipSample = chipMonitor.Sample(); chipSampled = true;
        UpdateBoostChip(null);
    }

    private string BoostChipFigures()
    {
        string ram = chipSampled ? $"{chipSample.MemoryPercent:0}%" : "–", cpu = chipSampled ? $"{chipSample.CpuPercent:0}%" : "–";
        string clean = chipReading == null ? "—" : chipReading.Files == 0 ? "0 MB" : Sizes.Format(chipReading.Bytes);
        return $"RAM {ram} · CPU {cpu} · {clean}";
    }

    // Called whenever a junk reading arrives (the Settings meter, a Boost, the quiet read), after each sample and whenever the Boost button's stage text changes.
    internal void UpdateBoostChip(JunkReading? reading)
    {
        if (reading != null) chipReading = reading;
        string label = boosting ? (BoostButton.Content as string is { Length: > 0 } stage ? stage : "Boost") : BoostChipFigures();
        SetChipText(label);
        // During a Boost the chip keeps its width (the stage words are shorter than the figures), so it never jumps and the thin line keeps its length.
        if (boosting && BoostChipButton.MinWidth == 0) BoostChipButton.MinWidth = BoostChipButton.ActualWidth;
        else if (!boosting && BoostChipButton.MinWidth != 0) BoostChipButton.MinWidth = 0;
        BoostChipButton.IsEnabled = !boosting && !busy;
        string tip = boosting ? "Boost is working." : "Memory in use, processor use and the temporary files ready to clean. Click to Boost.";
        if (!Equals(BoostChipButton.ToolTip, tip)) BoostChipButton.ToolTip = tip;
    }

    private void SyncBoostChip() => UpdateBoostChip(null);

    // ----- Slide up (owner, November 13, 2026): when the text changes (a Boost stage, new figures) the old line moves up and fades while the new one rises into place from
    // below, 300 ms, eased out. It always ends complete: the old line is replaced by the new one in place, nothing stops half way, nothing loops. Only transforms and opacity.
    private string chipTarget = "";
    private string? chipPending;
    private bool chipSliding;

    private void SetChipText(string text)
    {
        if (text == chipTarget) return;
        chipTarget = text;
        if (!BoostChipText.IsLoaded || !BoostChipButton.IsVisible) { BoostChipText.Text = text; BoostChipNext.Text = ""; chipPending = null; return; }
        if (chipSliding) { chipPending = text; return; }   // plays right after the one in progress
        Slide(text);
    }

    private void Slide(string text)
    {
        chipSliding = true;
        BoostChipNext.Text = text;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var time = TimeSpan.FromMilliseconds(300);
        BoostChipTextShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(0, -7, time) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        BoostChipText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, time) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        BoostChipNextShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(7, 0, time) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        var arrive = new DoubleAnimation(0, 1, time) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        arrive.Completed += (_, _) => FinishSlide(text);
        BoostChipNext.BeginAnimation(OpacityProperty, arrive);
        // Animations only tick while something is drawn; a minimized window must still end on the right text, so a short watchdog finishes the move.
        _ = Task.Delay(900).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (chipSliding && BoostChipNext.Text == text) FinishSlide(text); }), TaskScheduler.Default);
    }

    private void FinishSlide(string text)
    {
        if (!chipSliding || BoostChipNext.Text != text) return;
        BoostChipTextShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null); BoostChipTextShift.Y = 0;
        BoostChipText.BeginAnimation(OpacityProperty, null); BoostChipText.Opacity = 1;
        BoostChipNextShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null); BoostChipNextShift.Y = 7;
        BoostChipNext.BeginAnimation(OpacityProperty, null); BoostChipNext.Opacity = 0;
        BoostChipText.Text = text; BoostChipNext.Text = "";
        chipSliding = false;
        if (chipPending is { } next) { chipPending = null; if (next != text) Slide(next); }
    }

    // The quiet read of the Safe junk. Skipped while a Boost or another reading is running.
    internal async Task RefreshBoostChipAsync()
    {
        if (boosting || tempMeasuring || closing) return;
        tempMeasuring = true;
        try
        {
            var reading = await Task.Run(() => boostController.Measure(CancellationToken.None));
            lastReading = reading;
            UpdateBoostChip(reading);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* keep the last figure */ }
        finally { tempMeasuring = false; }
    }
}
