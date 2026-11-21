using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Hush;

// The This PC card on Home (Phase D, owner, November 19, 2026). It took over the Boost chip that used to sit beside the wordmark: the same figures (memory in use,
// processor use, system drive used, the Safe temporary files ready to clean) as four slim bars, one line that says how the PC is doing, and the Boost button, which
// shows the amount ready to clean beside its name ("Boost · 1.4 GB").
//
// How it stays cheap (the owner's choice: "every 5 seconds, with a nice ease"): the figures come from two Windows counters (ResourceMonitor, its own instance), read on
// a 5 s DispatcherTimer that runs ONLY while the real app window is active and not minimized (stopped on Deactivated / Minimized / Closed). Each change eases: the bar
// slides to its new height and the number rolls to its new value; a reading that barely moved is not redrawn at all. The MB figure is one count-only,
// background-priority read of the Safe junk (the same one Boost uses), shortly after start and after a clean or a Boost. Test windows never start the timer or the read
// by themselves (SampleBoostChip / UpdateBoostChip are the seams).
public partial class MainWindow
{
    internal static readonly TimeSpan BoostChipInterval = TimeSpan.FromSeconds(5);
    private readonly ResourceMonitor chipMonitor = new();
    private DispatcherTimer? chipTimer;
    private ResourceSnapshot chipSample;
    private bool chipSampled;
    private JunkReading? chipReading;

    // The figures in one line (tests and screen readers): "RAM 62% · CPU 14% · 1.4 GB".
    internal string BoostChipLabel => BoostChipFigures();
    internal bool BoostChipTimerRunning => chipTimer?.IsEnabled == true;

    private void InitializeBoostChip()
    {
        UpdateBoostChip(null);
        Activated += (_, _) => ResumeBoostChip();
        Deactivated += (_, _) => chipTimer?.Stop();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) chipTimer?.Stop(); else if (IsActive) ResumeBoostChip(); };
        Closed += (_, _) => chipTimer?.Stop();
    }

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
        ShowMeter(CpuFill, CpuText, chipSample.CpuPercent, $"{chipSample.CpuPercent:0}%", ResourceSnapshot.CpuAmber, ResourceSnapshot.CpuRed, minChange: 4);
        ShowMeter(MemoryFill, MemoryText, chipSample.MemoryPercent, $"{chipSample.MemoryPercent:0}%", ResourceSnapshot.MemoryAmber, ResourceSnapshot.MemoryRed, minChange: 2);
        MemoryText.ToolTip = $"{Sizes.Format(chipSample.MemoryUsed)} of {Sizes.Format(chipSample.MemoryTotal)} in use";
        ShowMeter(DiskFill, DiskText, chipSample.DiskUsedPercent, $"{chipSample.DiskUsedPercent:0}%", ResourceSnapshot.DiskAmber, ResourceSnapshot.DiskRed);
        DiskText.ToolTip = $"{Sizes.Format(chipSample.DiskFree)} free of {Sizes.Format(chipSample.DiskTotal)} on the system drive";
        if (DateTime.UtcNow >= infoNoteUntilUtc)
        {
            var reading = chipSample.Assess(lastReading is { } junk ? TempFraction(junk.Bytes) * 100 : -1);
            string brush = BrushFor(reading.Level);
            if (InfoSummary.Text != reading.Text || InfoSummary.Tag as string != brush)
            {
                InfoSummary.Text = reading.Text; InfoSummary.Tag = brush;
                InfoSummary.SetResourceReference(TextBlock.ForegroundProperty, brush);
            }
            InfoSummary.ToolTip = $"Figures come from Windows and update every 5 seconds while Hush is in front. Hush itself uses {Sizes.Format(chipSample.HushWorkingSet)} of memory.";
        }
        UpdateBoostChip(null);
    }

    private string BoostChipFigures()
    {
        string ram = chipSampled ? $"{chipSample.MemoryPercent:0}%" : "–", cpu = chipSampled ? $"{chipSample.CpuPercent:0}%" : "–";
        string clean = chipReading == null ? "—" : chipReading.Files == 0 ? "0 MB" : Sizes.Format(chipReading.Bytes);
        return $"RAM {ram} · CPU {cpu} · {clean}";
    }

    // Called whenever a junk reading arrives (a Boost, the quiet read, a scan) and whenever the Boost button's stage text changes: the Boost button shows the amount
    // ready to clean beside its name while it rests, and the Junk tile follows.
    internal void UpdateBoostChip(JunkReading? reading)
    {
        if (reading != null)
        {
            chipReading = reading;
            ShowTempMeter(reading);
        }
        string suffix = boosting || chipReading == null || chipReading.Files == 0 ? "" : " · " + Sizes.Format(chipReading.Bytes);
        if (!Equals(BoostButton.Tag, suffix)) BoostButton.Tag = suffix;
        UpdateHomeTiles();
    }

    private void SyncBoostChip() => UpdateBoostChip(null);

    // Numbers roll to their new value while their bar eases (owner, November 19, 2026): one tween per text, retargeted from where it is.
    private static readonly ConditionalWeakTable<TextBlock, NumberTween> numberRolls = new();
    private static void RollPercent(TextBlock text, double from, double to)
    {
        var tween = numberRolls.GetValue(text, block => new NumberTween { ValueChanged = value => block.Text = $"{value:0}%" });
        _ = tween.RunAsync(from, to, 400);
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
