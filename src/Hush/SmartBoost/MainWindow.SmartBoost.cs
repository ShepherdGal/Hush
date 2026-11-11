using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Hush;

// One row of the startup-apps list. Switching it on or off only flips the Task Manager style flag; nothing is uninstalled.
internal sealed class StartupChoice(StartupEntry entry) : INotifyPropertyChanged
{
    public StartupEntry Entry { get; private set; } = entry;
    public string Name => Entry.Name;
    public string Detail => $"{Entry.Scope} · {Entry.Where} · {Entry.Command}";
    private bool enabled = entry.Enabled;
    public bool Enabled { get => enabled; set { if (enabled == value) return; enabled = value; Notify(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public partial class MainWindow
{
    private Preferences prefs = new();
    private BoostSettings Boost => prefs.Boost;
    private readonly StartupAppsService startupApps;
    private readonly SmartBoostController boostController;
    private readonly ResourceMonitor resourceMonitor = new();
    private DispatcherTimer? boostTimer, resourceTimer;
    private CancellationTokenSource? boostCancellation;
    private bool boostRunning, quickBoostPending, boostLoading;
    private JunkReading? lastReading;
    private List<StartupChoice> startupChoices = [];
    private (string Id, bool WasEnabled, string Name)? lastStartupChange;
    private TaskCompletionSource<bool>? consent;
    internal JunkReading? LastJunkReading => lastReading;
    internal BoostSettings BoostSettings => Boost;
    internal IReadOnlyList<StartupChoice> StartupChoices => startupChoices;
    internal bool IsBoostTimerRunning => boostTimer?.IsEnabled == true;
    internal bool IsResourceTimerRunning => resourceTimer?.IsEnabled == true;

    // Called once the window is built. Nothing runs in the background unless a switch is on: timers are only created when needed.
    private void InitializeBoost()
    {
        LowResourceMode.Apply(Boost.LowResource);
        ApplyBoostSettings();
    }

    private static int ChoiceValue(RadioButton chip) => int.TryParse(chip.Tag?.ToString(), out int value) ? value : 0;

    private void SavePreferences() => prefs.Save(settingsPath);

    // Brings the screen and the (optional) background check in line with the saved choices.
    private void ApplyBoostSettings()
    {
        boostLoading = true;
        try
        {
            MonitorJunkBox.IsChecked = Boost.MonitorJunk;
            AutoBoostBox.IsChecked = Boost.AutoBoost;
            LowResourceBox.IsChecked = Boost.LowResource;
            foreach (var chip in new[] { Threshold500, Threshold1000, Threshold2000, Threshold5000 }) chip.IsChecked = ChoiceValue(chip) == Boost.ThresholdMb;
            foreach (var chip in new[] { Interval15, Interval60, Interval360, Interval1440 }) chip.IsChecked = ChoiceValue(chip) == Boost.CheckEveryMinutes;
            ThresholdRow.IsEnabled = IntervalRow.IsEnabled = Boost.MonitorJunk;
            ThresholdRow.Opacity = IntervalRow.Opacity = Boost.MonitorJunk ? 1 : 0.5;
        }
        finally { boostLoading = false; }
        if (Boost.MonitorJunk)
        {
            if (boostTimer == null)
            {
                boostTimer = new DispatcherTimer();
                boostTimer.Tick += BoostTick;
            }
            // The first look happens shortly after switching on; later looks follow the chosen interval.
            boostTimer.Interval = TimeSpan.FromSeconds(20);
            boostTimer.Start();
        }
        else
        {
            boostTimer?.Stop();
            boostCancellation?.Cancel();
            lastReading = null;
        }
        UpdateBoostSummary();
    }

    private void UpdateBoostSummary()
    {
        string state = Boost.AutoBoost ? "Automatic Boost on" : Boost.MonitorJunk ? "Junk monitoring on" : "Manual only";
        if (Boost.LowResource) state += " · Low-resource mode";
        BoostSummary.Text = state;
    }

    // ----- Quick Boost -----
    // A scan restricted to Safe categories that ends in the usual confirmation. Nothing is deleted before the person clicks Clean.
    private async void QuickBoost_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        quickBoostPending = true;
        ShowReviewPage(false);
        await StartScan(new ScanOptions(SmartBoostController.IsSafeTarget, ReportIntervalMs: LowResourceMode.ReportIntervalMs, Background: LowResourceMode.IsActive));
    }

    private void OfferQuickBoostConfirmation()
    {
        if (!quickBoostPending) return;
        quickBoostPending = false;
        if (CurrentScanState == ScanState.Completed && SelectedCount > 0) ShowConfirmation();
    }

    // ----- Switches and choices -----
    private void MonitorJunk_Click(object sender, RoutedEventArgs e)
    {
        if (boostLoading) return;
        Boost.MonitorJunk = MonitorJunkBox.IsChecked == true;
        if (!Boost.MonitorJunk) Boost.AutoBoost = false;
        SavePreferences(); ApplyBoostSettings();
    }

    private async void AutoBoost_Click(object sender, RoutedEventArgs e)
    {
        if (boostLoading) return;
        if (AutoBoostBox.IsChecked != true) { Boost.AutoBoost = false; SavePreferences(); ApplyBoostSettings(); return; }
        // Cleaning without a click each time is the person's explicit, recorded choice and covers Safe items only.
        bool agreed = Boost.AutoBoostConsent || await AskConsent("Turn on Automatic Boost?",
            "While Hush is open (also minimized) it will check for Safe junk, and when it passes your threshold Hush will clean those Safe items by itself, without asking again.\n\n"
            + "Only Safe items are ever included (old temporary files and thumbnail caches). Browser caches, logs, traces and anything Advanced always wait for your own review. "
            + "Each file is re-checked before it is removed, every removal is logged, and Hush never closes programs, clears memory or changes Windows services.\n\nYou can turn this off at any time.",
            "Turn on");
        if (!agreed) { boostLoading = true; AutoBoostBox.IsChecked = false; boostLoading = false; return; }
        Boost.AutoBoostConsent = true; Boost.AutoBoost = true; Boost.MonitorJunk = true;
        SavePreferences(); ApplyBoostSettings();
    }

    private void Threshold_Click(object sender, RoutedEventArgs e)
    {
        if (boostLoading || sender is not RadioButton chip) return;
        Boost.ThresholdMb = ChoiceValue(chip); SavePreferences(); UpdateBoostSummary();
    }

    private void Interval_Click(object sender, RoutedEventArgs e)
    {
        if (boostLoading || sender is not RadioButton chip) return;
        Boost.CheckEveryMinutes = ChoiceValue(chip); SavePreferences(); ApplyBoostSettings();
    }

    private void LowResource_Click(object sender, RoutedEventArgs e)
    {
        if (boostLoading) return;
        Boost.LowResource = LowResourceBox.IsChecked == true;
        LowResourceMode.Apply(Boost.LowResource);
        SavePreferences(); UpdateBoostSummary();
    }

    // ----- Background junk monitoring and Automatic Boost -----
    private async void BoostTick(object? sender, EventArgs e)
    {
        if (boostTimer != null) boostTimer.Interval = TimeSpan.FromMinutes(Boost.CheckEveryMinutes);
        // Never compete with something the person is doing: skip this round and try again at the next interval.
        if (boostRunning || boosting || busy || closing || !Boost.MonitorJunk || ModalOverlay.Visibility == Visibility.Visible || AboutOverlay.Visibility == Visibility.Visible) return;
        boostRunning = true;
        boostCancellation?.Dispose();
        boostCancellation = new();
        var token = boostCancellation.Token;
        try
        {
            var reading = await Task.Run(() => boostController.Measure(token));
            if (token.IsCancellationRequested) return;
            lastReading = reading;
            bool reached = SmartBoostController.ThresholdReached(reading, Boost);
            // Results the person is still reviewing are never changed behind their back.
            bool idle = !busy && (CurrentScanState is ScanState.Ready or ScanState.Failed || items.Count == 0);
            if (reached && Boost.AutoBoost && Boost.AutoBoostConsent && idle)
            {
                var result = await Task.Run(() => boostController.RunAutomaticBoost(token));
                if (result != null)
                {
                    string measured = result.MeasuredFreed > 0 ? $" (disk reports {Sizes.Format(result.MeasuredFreed)} more free)" : "";
                    ShowInfoNote($"Automatic Boost cleaned {Sizes.Format(result.Bytes)}", $"Automatic Boost cleaned {Sizes.Format(result.Bytes)} of Safe temporary files at {DateTime.Now:t}{measured}.");
                    lastReading = new JunkReading(0, 0, DateTime.UtcNow);
                }
            }
            else if (reached && CurrentScanState == ScanState.Ready && !busy)
            {
                StatusDetail.Text = $"About {Sizes.Format(reading.Bytes)} of safe junk is waiting. Quick Boost can clean it.";
            }
            UpdateBoostSummary();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowInfoNote("The last junk check couldn't finish", "");
            UpdateBoostSummary();
        }
        finally { boostRunning = false; }
    }

    private void StopBoostWork()
    {
        boostTimer?.Stop();
        resourceTimer?.Stop();
        boostCancellation?.Cancel();
    }

    // ----- Resource indicators (sampled only while Settings is on screen) -----
    private void StartResourceIndicators()
    {
        resourceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        resourceTimer.Tick -= ResourceTick;
        resourceTimer.Tick += ResourceTick;
        ResourceTick(null, EventArgs.Empty);
        resourceMonitor.Sample();
        resourceTimer.Start();
    }

    private void StopResourceIndicators() => resourceTimer?.Stop();

    private ResourceSnapshot lastSample;
    private int processTick;

    private void ResourceTick(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) return; // nobody is looking: no reading, no redraw
        var sample = resourceMonitor.Sample();
        // A bar only moves for a change worth seeing (CPU jitters by a few points every reading): every redraw of this window costs real CPU, and much more on a weak PC.
        ShowMeter(CpuFill, CpuText, sample.CpuPercent, $"{sample.CpuPercent:0}%", ResourceSnapshot.CpuAmber, ResourceSnapshot.CpuRed, minChange: 4);
        ShowMeter(MemoryFill, MemoryText, sample.MemoryPercent, $"{sample.MemoryPercent:0}%", ResourceSnapshot.MemoryAmber, ResourceSnapshot.MemoryRed, minChange: 2);
        MemoryText.ToolTip = $"{Sizes.Format(sample.MemoryUsed)} of {Sizes.Format(sample.MemoryTotal)} in use";
        ShowMeter(DiskFill, DiskText, sample.DiskUsedPercent, $"{sample.DiskUsedPercent:0}%", ResourceSnapshot.DiskAmber, ResourceSnapshot.DiskRed);
        DiskText.ToolTip = $"{Sizes.Format(sample.DiskFree)} free of {Sizes.Format(sample.DiskTotal)} on the system drive";
        lastSample = sample;
        if (++processTick % 10 == 0) RefreshProcesses(rebuildList: false); // the app count follows along (every 20 s) while Settings is open
        if (DateTime.UtcNow >= infoNoteUntilUtc)
        {
            // The situation in one line, colored like the bars (green good, amber so-so, red bad); advice comes with the amber and red ones.
            var reading = sample.Assess(lastReading is { } junk ? TempFraction(junk.Bytes) * 100 : -1);
            string brush = BrushFor(reading.Level);
            if (InfoSummary.Text != reading.Text || InfoSummary.Tag as string != brush)
            {
                InfoSummary.Text = reading.Text; InfoSummary.Tag = brush;
                InfoSummary.SetResourceReference(TextBlock.ForegroundProperty, brush);
            }
            InfoSummary.ToolTip = $"Figures come from Windows and update only while this page is open. Hush itself uses {Sizes.Format(sample.HushWorkingSet)} of memory.";
        }
    }

    // The three colors we already have: green (good), amber (so-so), red (bad: the Clean red, the only red in Hush).
    private static string BrushFor(MeterLevel level) => level switch { MeterLevel.Red => "CleanTextBrush", MeterLevel.Amber => "MeterWarnBrush", _ => "GoodBrush" };

    // A bar is green while its reading is good, amber in the middle and red when it is bad (the same three colors as the Safe/Review/Advanced levels).
    private static void ShowMeter(Border fill, TextBlock text, double percent, string label, double amberAt, double redAt, int milliseconds = 400, double minChange = 0)
    {
        // Every redraw of this window costs real CPU, so a bar whose reading and color did not change (or moved by less than minChange points) is left alone.
        var level = ResourceSnapshot.LevelOf(percent, amberAt, redAt);
        if (fill.Tag is MeterLevel shownLevel && shownLevel == level)
        {
            if (text.Text == label) return;
            if (minChange > 0 && text.Tag is double shown && Math.Abs(shown - percent) < minChange) return;
        }
        text.Tag = percent; // the reading the bar currently shows
        fill.Tag = level;
        text.Text = label;
        fill.SetResourceReference(Border.BackgroundProperty, BrushFor(level));
        // The slim vertical bar fills from the bottom (transform animation, no layout work).
        var scale = (ScaleTransform)fill.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Tween(scale.ScaleY, Math.Clamp(percent, 0, 100) / 100, milliseconds));
    }

    // ----- Startup apps -----
    private void LoadStartupApps()
    {
        try
        {
            startupChoices = startupApps.List().Select(entry => new StartupChoice(entry)).ToList();
            StartupList.ItemsSource = startupChoices;
            int on = startupChoices.Count(choice => choice.Enabled);
            StartupSummary.Text = startupChoices.Count == 0 ? "No startup apps found for this account." : $"{on} of {startupChoices.Count} start with Windows";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            startupChoices = [];
            StartupList.ItemsSource = null;
            StartupSummary.Text = "Startup apps couldn't be read right now.";
        }
        UndoStartupButton.Visibility = lastStartupChange == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: StartupChoice choice } box) return;
        bool enable = box.IsChecked == true;
        bool applied = startupApps.SetEnabled(choice.Entry.Id, enable);
        if (!applied)
        {
            choice.Enabled = !enable;
            StartupStatus.Text = $"Windows didn't accept the change for {choice.Name}. Nothing was changed.";
            return;
        }
        lastStartupChange = (choice.Entry.Id, !enable, choice.Name);
        StartupStatus.Text = enable ? $"{choice.Name} will start with Windows again." : $"{choice.Name} won't start with Windows. Nothing was uninstalled.";
        UndoStartupButton.Visibility = Visibility.Visible;
        int on = startupChoices.Count(item => item.Enabled);
        StartupSummary.Text = $"{on} of {startupChoices.Count} start with Windows";
    }

    private void UndoStartup_Click(object sender, RoutedEventArgs e)
    {
        if (lastStartupChange is not { } change) return;
        if (startupApps.SetEnabled(change.Id, change.WasEnabled))
        {
            var choice = startupChoices.FirstOrDefault(item => item.Entry.Id == change.Id);
            if (choice != null) choice.Enabled = change.WasEnabled;
            StartupStatus.Text = $"Restored {change.Name}.";
            lastStartupChange = null;
            UndoStartupButton.Visibility = Visibility.Collapsed;
            StartupSummary.Text = $"{startupChoices.Count(item => item.Enabled)} of {startupChoices.Count} start with Windows";
        }
        else StartupStatus.Text = "Windows didn't accept the change. Nothing was changed.";
    }

    // ----- One-time consent dialog -----
    // The dialog can open above another popup (About), so it remembers and restores the blur, enabled state and focus it found.
    private bool consentSurfaceWasEnabled = true;
    private double consentBlurRadius;
    private IInputElement? consentReturnFocus;

    private Task<bool> AskConsent(string title, string body, string confirmLabel, string cancelLabel = "Not now")
    {
        consent?.TrySetResult(false);
        consent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsentTitle.Text = title; ConsentText.Text = body; ConsentConfirmButton.Content = confirmLabel; ConsentCancelButton.Content = cancelLabel;
        consentReturnFocus = Keyboard.FocusedElement;
        consentSurfaceWasEnabled = AppSurface.IsEnabled;
        consentBlurRadius = BackgroundBlur.Radius;
        AppSurface.IsEnabled = false;
        SetBackdropBlur(Math.Max(7, consentBlurRadius));
        ConsentOverlay.Visibility = Visibility.Visible;
        Motion.Reveal(ConsentOverlay);
        ConsentCancelButton.Focus();
        KeyboardNavigation.SetTabNavigation(ConsentOverlay, KeyboardNavigationMode.Cycle);
        return consent.Task;
    }

    private async Task CompleteConsent(bool agreed)
    {
        var pending = consent; consent = null;
        ConsentOverlay.IsEnabled = false;
        SetBackdropBlur(consentBlurRadius);
        await Motion.FadeOut(ConsentOverlay);
        ConsentOverlay.Visibility = Visibility.Collapsed;
        ConsentOverlay.IsEnabled = true;
        AppSurface.IsEnabled = consentSurfaceWasEnabled;
        if (consentReturnFocus != null) Keyboard.Focus(consentReturnFocus);
        pending?.TrySetResult(agreed);
    }

    private async void ConsentConfirm_Click(object sender, RoutedEventArgs e) => await CompleteConsent(true);
    private async void ConsentCancel_Click(object sender, RoutedEventArgs e) => await CompleteConsent(false);
}
