using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Hush;

public partial class MainWindow : Window
{
    private readonly CleanupEngine engine;
    private readonly Func<IProgress<ScanProgress>?, CancellationToken, ScanOptions?, ScanResult> scan;
    private readonly Func<IUpdateClient> updateFactory;
    private readonly Action<string, AppRelease> installUpdate;
    private readonly List<CleanupItem> items = [];
    // What the last scan found (files only, their bytes, registry entries, Recycle Bin), recomputed only when the item list changes
    // so that toggling one checkbox never loops hundreds of thousands of files.
    private int foundFiles, foundRegistry;
    private long foundBytes;
    private bool foundRecycleBin;
    private int SelectedCount => Categories.Sum(category => category.SelectedCount);
    private long SelectedBytes => Categories.Sum(category => category.SelectedBytes);
    internal ObservableCollection<ReviewCategory> Categories { get; } = ReviewCategory.Create();
    internal IReadOnlyList<CleanupItem> ReviewItems => items;
    private CancellationTokenSource? cancellation;
    private bool busy, light, closing;
    private bool settingsReturnToReview;
    internal bool IsPageTransitioning => Motion.IsEntering(MainContent);
    internal enum ScanState { Ready, Scanning, Completed, Stopped, Failed }
    internal ScanState CurrentScanState { get; private set; }
    private bool scanned => CurrentScanState is ScanState.Completed or ScanState.Stopped;
    private bool CanClean => !busy && CurrentScanState == ScanState.Completed;
    public static readonly DependencyProperty CanSelectCandidatesProperty = DependencyProperty.Register(
        nameof(CanSelectCandidates), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool CanSelectCandidates { get => (bool)GetValue(CanSelectCandidatesProperty); private set => SetValue(CanSelectCandidatesProperty, value); }
    private bool scanSkippedLocations;
    internal bool IsLightTheme => light;
    private bool batchSelection;
    private bool showGaugePercentage;
    private double gaugeTarget;
    private string currentOperation = "";
    private TaskCompletionSource? gaugeCompletion;
    private IInputElement? confirmationReturnFocus;
    private AppRelease? availableRelease;
    private CancellationTokenSource? updateCancellation;
    private int aboutSession, updateRequest;
    private bool updateBusy;
    internal bool IsUpdateBusy => updateBusy;
    // The shortest time a check for updates shows its searching animation.
    internal TimeSpan MinimumUpdateCheck { get; set; } = TimeSpan.FromMilliseconds(3500);
    internal static readonly DependencyProperty GaugePercentProperty = DependencyProperty.Register(
        nameof(GaugePercent), typeof(double), typeof(MainWindow), new PropertyMetadata(0.0, GaugePercentChanged));
    internal double GaugePercent { get => (double)GetValue(GaugePercentProperty); set => SetValue(GaugePercentProperty, value); }
    private CleanupItem[] confirmedItems = [];
    private readonly string settingsPath;

    public MainWindow() : this(new CleanupEngine()) { }

    internal MainWindow(CleanupEngine engine, Func<IProgress<ScanProgress>?, CancellationToken, ScanResult>? scan = null, string? preferencesPath = null,
        Func<IUpdateClient>? updateFactory = null, Action<string, AppRelease>? installUpdate = null,
        WindowsSettingsService? windowsSettings = null, StartupAppsService? startupApps = null, IMemoryBooster? memoryBooster = null)
    {
        this.memoryBooster = memoryBooster ?? new WindowsMemoryBooster();
        this.engine = engine;
        this.scan = scan == null ? engine.Scan : (progress, token, _) => scan(progress, token);
        this.updateFactory = updateFactory ?? (() => new UpdateService());
        this.installUpdate = installUpdate ?? UpdateInstaller.Start;
        this.windowsSettings = windowsSettings ?? WindowsSettingsService.Create();
        this.startupApps = startupApps ?? new StartupAppsService(StartupLocations.Default);
        boostController = new SmartBoostController(engine);
        settingsPath = preferencesPath ?? Path.Combine(CleanupEngine.DataDirectory, "settings.json");
        InitializeComponent();
        var categoryView = CollectionViewSource.GetDefaultView(Categories);
        categoryView.Filter = value => value is ReviewCategory { HasFiles: true };
        CategoriesList.ItemsSource = categoryView;
        foreach (var category in Categories)
        {
            category.PropertyChanged += CategoryChanged;
            category.SelectionBatchState = active => { batchSelection = active; };
            category.SelectionTotalsChanged += OnSelectionTotalsChanged;
        }
        prefs = Preferences.Load(settingsPath);
        light = prefs.Light;
        InitializeUiSize();
        ApplyTheme();
        ResetGauge();
        RefreshSelection();
        InitializeBoost();
        AboutVersion.Text = $"Version {UpdateService.DisplayVersion}";
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape && ModalOverlay.Visibility == Visibility.Visible && ModalOverlay.IsEnabled)
            { e.Handled = true; await HideConfirmation(); }
            else if (e.Key == Key.Escape && ConsentOverlay.Visibility == Visibility.Visible && ConsentOverlay.IsEnabled)
            { e.Handled = true; await CompleteConsent(false); }
            else if (e.Key == Key.Escape && AboutOverlay.Visibility == Visibility.Visible && AboutOverlay.IsEnabled)
            { e.Handled = true; await HideAbout(); }
            else if (e.Key == Key.Escape && WindowsSettingsOverlay.Visibility == Visibility.Visible && WindowsSettingsOverlay.IsEnabled && !windowsSettingsBusy)
            { e.Handled = true; await HideWindowsSettingsConfirmation(); }
        };
    }

    private static void GaugePercentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var window = (MainWindow)sender;
        if (window.GaugeArc == null) return;
        double value = Math.Clamp((double)args.NewValue, 0, 100);
        window.GaugeArc.Data = ProgressRing.Create(value);
        if (window.showGaugePercentage) window.GaugeValue.Text = $"{Math.Floor(value + 0.00001):0}%";
    }

    internal void ResetGauge()
    {
        gaugeCompletion?.TrySetResult();
        gaugeCompletion = null;
        showGaugePercentage = true;
        GaugeFinishGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
        GaugeFinishGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
        BeginAnimation(GaugePercentProperty, null);
        GaugePercent = gaugeTarget = 0;
        GaugeValue.Text = "0%";
        GaugeArc.Data = ProgressRing.Create(0);
    }

    internal void AnimateGaugeTo(double percent)
    {
        if (gaugeCompletion != null) return;
        gaugeTarget = Math.Max(gaugeTarget, Math.Clamp(percent, 0, 100));
        // Progress arrives every ~40 ms while files are found; a short ease keeps the ring tracking the real count without stepping.
        BeginAnimation(GaugePercentProperty, new DoubleAnimation(GaugePercent, gaugeTarget, TimeSpan.FromMilliseconds(260))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    internal async Task CompleteGauge()
    {
        gaugeTarget = 100;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gaugeCompletion = finished;
        var animation = new DoubleAnimation(GaugePercent, 100, TimeSpan.FromMilliseconds(Math.Max(500, 1400 * (1 - GaugePercent / 100))))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        animation.Completed += (_, _) =>
        {
            if (ReferenceEquals(gaugeCompletion, finished)) gaugeCompletion = null;
            finished.TrySetResult();
        };
        BeginAnimation(GaugePercentProperty, animation);
        await finished.Task;
        if (GaugePercent >= 100 && cancellation?.IsCancellationRequested != true)
        {
            // One soft bloom settles into a visible completed state until the next scan.
            var bloom = new DoubleAnimationUsingKeyFrames();
            bloom.KeyFrames.Add(new EasingDoubleKeyFrame(0.22, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bloom.KeyFrames.Add(new EasingDoubleKeyFrame(0.72, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            bloom.KeyFrames.Add(new EasingDoubleKeyFrame(0.50, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            GaugeFinishGlow.BeginAnimation(DropShadowEffect.OpacityProperty, bloom);
            var radius = new DoubleAnimationUsingKeyFrames();
            radius.KeyFrames.Add(new EasingDoubleKeyFrame(12, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            radius.KeyFrames.Add(new EasingDoubleKeyFrame(20, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            radius.KeyFrames.Add(new EasingDoubleKeyFrame(16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            GaugeFinishGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, radius);
        }
    }

    internal void FreezeGauge()
    {
        double value = GaugePercent;
        BeginAnimation(GaugePercentProperty, null);
        GaugePercent = gaugeTarget = value;
        gaugeCompletion?.TrySetResult();
        gaugeCompletion = null;
    }

    private void OpenResults_Click(object sender, RoutedEventArgs e)
    {
        if (!scanned || busy) return;
        ShowReviewPage(true);
        PrimaryButton.Focus();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
        // Only the real app (the application's main window) asks for the terms and tidies old update staging; test windows do neither.
        if (Application.Current?.MainWindow == this)
        {
            ShowTermsOnStart();
            _ = Task.Run(() => UpdateInstaller.PruneStaging(UpdateInstaller.StagingRoot));
        }
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e) =>
        WindowSurface.Clip = new RectangleGeometry(new Rect(e.NewSize), 24, 24);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source != sender)
        {
            if (source is ButtonBase) return;
            source = VisualTreeHelper.GetParent(source);
        }
        DragMove();
    }

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        string edges = (string)((Thumb)sender).Tag;
        // The thumbs sit inside the scaled content, so their drag distances are in content units; the window is measured in screen units.
        double dx = e.HorizontalChange * uiScale, dy = e.VerticalChange * uiScale;
        if (edges.Contains('R')) Width = Math.Max(MinWidth, Width + dx);
        if (edges.Contains('B')) Height = Math.Max(MinHeight, Height + dy);
        if (edges.Contains('L'))
        {
            double width = Math.Max(MinWidth, Width - dx);
            Left += Width - width;
            Width = width;
        }
        if (edges.Contains('T'))
        {
            double height = Math.Max(MinHeight, Height - dy);
            Top += Height - height;
            Height = height;
        }
    }

    internal void ShowReviewPage(bool review)
    {
        if (ScanPage.Visibility == (review ? Visibility.Collapsed : Visibility.Visible)
            && ReviewPage.Visibility == (review ? Visibility.Visible : Visibility.Collapsed)
            && SettingsPage.Visibility == Visibility.Collapsed) return;
        Motion.EnterPage(MainContent, () =>
        {
            ScanPage.Visibility = review ? Visibility.Collapsed : Visibility.Visible;
            ReviewPage.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
        });
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (busy || SettingsPage.Visibility == Visibility.Visible) return;
        settingsReturnToReview = ReviewPage.Visibility == Visibility.Visible;
        LoadWindowsSettings();
        LoadStartupApps();
        StartResourceIndicators();
        RefreshTempFiles();
        Motion.EnterPage(MainContent, () =>
        {
            ScanPage.Visibility = ReviewPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
        });
        SettingsBackButton.Focus();
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e)
    {
        StopResourceIndicators();
        ShowReviewPage(settingsReturnToReview);
        SettingsButton.Focus();
    }

    private async void Circle_Click(object sender, RoutedEventArgs e) => await ActivateGaugeAsync();

    internal async Task ActivateGaugeAsync()
    {
        if (busy) { StopOperation(); return; }
        await StartScan();
    }

    private void StopOperation()
    {
        cancellation?.Cancel();
        LiveFileCounter.Settle();
        FreezeGauge();
        if (currentOperation == "scan")
        {
            CurrentScanState = ScanState.Stopped;
            FreezeIncompleteScanGauge();
        }
        PrimaryButton.IsEnabled = ScanCircleButton.IsEnabled = false;
        StatusTitle.Text = "Stopping safely…";
        GaugeLabel.Text = "Stopping…";
    }

    private void FreezeIncompleteScanGauge()
    {
        FreezeGauge();
        // The gauge can finish before the live count; canceling that wait is still a partial scan.
        if (GaugePercent >= 100) GaugePercent = gaugeTarget = 99;
        GaugeFinishGlow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        GaugeFinishGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        GaugeFinishGlow.Opacity = 0.22;
        GaugeFinishGlow.BlurRadius = 12;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        Headline.Text = "Make more room to breathe";
        GaugeValue.Text = $"{Math.Floor(GaugePercent):0}%";
        GaugeLabel.Text = scanned ? "Click to scan again" : "Click to scan";
        ScanCircleButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, scanned ? "Scan again" : "Ready to scan");
        ShowReviewPage(false);
        ScanCircleButton.Focus();
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (busy) { StopOperation(); return; }
        if (CanClean && SelectedCount > 0) ShowConfirmation();
        else await StartScan();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await StartScan();

    // Options for the next scan only (Quick Boost restricts it to Safe categories); a normal scan uses none.
    private ScanOptions? pendingScanOptions;
    // When a scan has been in one location this long without any news, the status line names it ("Still looking in ..."), so a slow or stuck
    // location is visible instead of looking like a frozen gauge. Tests shorten it.
    internal static int StillLookingAfterMs = 2500;
    private string scanLocation = "";
    private long scanProgressTick;
    private bool stillLookingShown;

    private async Task StartScan(ScanOptions? options = null)
    {
        if (busy) return;
        // Low-resource mode scans at background CPU and disk priority and reports progress less often.
        options ??= LowResourceMode.IsActive ? new ScanOptions(ReportIntervalMs: LowResourceMode.ReportIntervalMs, Background: true) : null;
        pendingScanOptions = options;
        CurrentScanState = ScanState.Scanning;
        scanSkippedLocations = false;
        LoadReviewItems([]);
        LiveFileCounter.Reset();
        ShowReviewPage(false);
        SetBusy(true, "scan");
        ResetGauge();
        StatusDetail.Text = "Looking through safe cleanup locations…";
        StatusTitle.Text = "Searching files…";
        GaugeLabel.Text = "Click to stop";
        ScanCircleButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Stop scan");
        bool acceptingProgress = true;
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!acceptingProgress || cancellation?.IsCancellationRequested == true) return;
            AnimateGaugeTo(Math.Min(99, p.Fraction * 100));
            LiveFileCounter.SetCount(p.FilesFound);
            LiveFileCounter.ToolTip = p.Location == "Complete" ? null : p.Location;
            // Remember where the scan is. Scans move through most locations in a blink, so the name is only shown (below) when one takes a while.
            // The clock restarts only when the scan moves to a different location: a huge folder keeps reporting new files, but the person still
            // deserves to see which place is taking the time (the gauge share of one location is small).
            if (p.Location is not ("Complete" or "Preparing scan") && !string.Equals(scanLocation, p.Location.Replace("Discovering ", ""), StringComparison.OrdinalIgnoreCase))
            {
                scanLocation = p.Location.Replace("Discovering ", "");
                scanProgressTick = Environment.TickCount64;
                if (stillLookingShown) { stillLookingShown = false; StatusTitle.Text = "Searching files…"; }
            }
        });
        scanLocation = ""; scanProgressTick = Environment.TickCount64; stillLookingShown = false;
        var stillLooking = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        stillLooking.Tick += (_, _) =>
        {
            if (!acceptingProgress || scanLocation.Length == 0 || Environment.TickCount64 - scanProgressTick < StillLookingAfterMs) return;
            stillLookingShown = true;
            StatusTitle.Text = $"Still looking in {scanLocation}…";
        };
        stillLooking.Start();
        try
        {
            var result = await Task.Run(() => scan(progress, cancellation!.Token, pendingScanOptions));
            acceptingProgress = false;
            LoadReviewItems(result.Items);
            LiveFileCounter.SetCount(foundFiles, !result.Stopped);
            ReviewScroll.ScrollToTop();
            if (!result.Stopped)
            {
                await Task.WhenAll(CompleteGauge(), LiveFileCounter.CompleteAsync());
            }
            else
            {
                FreezeGauge();
            }
            bool stopped = result.Stopped || cancellation?.IsCancellationRequested == true;
            CurrentScanState = stopped ? ScanState.Stopped : ScanState.Completed;
            if (stopped) FreezeIncompleteScanGauge();
            scanSkippedLocations = result.BusyCaches > 0 || result.Inaccessible > 0 || result.Stalled > 0;
            GaugeLabel.Text = "Click to scan again";
            StatusTitle.Text = stopped ? "Scan stopped. Review partial results." : items.Count == 0 ? (scanSkippedLocations ? "No cleanup items found." : "Everything looks clean.") : "Scan complete. Take a look.";
            var notes = new List<string>();
            if (result.BusyCaches > 0) notes.Add("open browser caches skipped");
            if (result.Inaccessible > 0) notes.Add($"{result.Inaccessible:N0} inaccessible paths skipped");
            if (result.Stalled > 0) notes.Add($"{result.Stalled:N0} location{(result.Stalled == 1 ? "" : "s")} did not respond and {(result.Stalled == 1 ? "was" : "were")} skipped (see Logs\\scan-issues.log)");
            if (foundRegistry > 0) notes.Add("registry entries are opt-in");
            StatusDetail.Text = FoundSummary();
            ResultCount.ToolTip = notes.Count > 0 ? string.Join(" · ", notes) : null;
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
        {
            acceptingProgress = false;
            CurrentScanState = ScanState.Stopped;
            FreezeIncompleteScanGauge();
            StatusTitle.Text = "Scan stopped. Review partial results.";
            StatusDetail.Text = FoundSummary();
            GaugeLabel.Text = "Click to scan again";
        }
        catch (Exception ex)
        {
            acceptingProgress = false;
            CurrentScanState = ScanState.Failed;
            FreezeIncompleteScanGauge();
            showGaugePercentage = false;
            StatusTitle.Text = "The scan couldn't finish.";
            StatusDetail.Text = ex.Message;
            GaugeValue.Text = $"{Math.Floor(GaugePercent):0}%";
            GaugeLabel.Text = "Try again";
        }
        finally
        {
            stillLooking.Stop();
            LiveFileCounter.Settle();
            SetBusy(false);
            if (CurrentScanState != ScanState.Completed)
            {
                bool previousBatch = batchSelection;
                batchSelection = true;
                try { foreach (var category in Categories) if (category.HasFiles) category.Selected = false; }
                finally { batchSelection = previousBatch; }
            }
            RefreshSelection();
        }
        // Quick Boost ends in the usual confirmation; nothing is cleaned before the person clicks Clean.
        OfferQuickBoostConfirmation();
        quickBoostPending = false;
        OfferBrowserDataConfirmation();
        browserDataPending = false;
    }

    private void HomeClean_Click(object sender, RoutedEventArgs e)
    { if (CanClean) ShowConfirmation(); }

    private void ShowConfirmation()
    {
        if (!CanClean) return;
        confirmedItems = items.Where(x => x.Selected).ToArray();
        if (confirmedItems.Length == 0) return;
        ConfirmSummary.Text = $"Clean {confirmedItems.Length:N0} selected items and free {Sizes.Format(confirmedItems.Sum(x => x.Bytes))}?";
        ConfirmDetail.Text = ConfirmationText(confirmedItems);
        BuildConfirmGroups(confirmedItems);
        // Advanced (red) items are irreversible or recovery-related: the clean button stays off until the person ticks the consent box.
        bool advanced = confirmedItems.Any(x => x.Safety == SafetyLevel.Advanced);
        AdvancedConsentBox.IsChecked = false;
        AdvancedConsentBox.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        ConfirmDeleteButton.IsEnabled = !advanced;
        confirmationReturnFocus = Keyboard.FocusedElement;
        AppSurface.IsEnabled = false;
        BackgroundBlur.BeginAnimation(BlurEffect.RadiusProperty, Motion.Tween(BackgroundBlur.Radius, 7, 280));
        ModalOverlay.Visibility = Visibility.Visible;
        Motion.Reveal(ModalOverlay);
        CancelDeleteButton.Focus();
        // Keep keyboard navigation within the confirmation until it closes.
        KeyboardNavigation.SetTabNavigation(ModalOverlay, KeyboardNavigationMode.Cycle);
    }

    // The popup lists what will be cleaned as three tiny colored groups (green Safe, amber Review, red Advanced): one summary line per group,
    // then one line per category. Text is deliberately small; the window is not.
    private void BuildConfirmGroups(IReadOnlyCollection<CleanupItem> selected)
    {
        ConfirmGroups.Children.Clear();
        foreach (var level in new[] { SafetyLevel.Safe, SafetyLevel.Review, SafetyLevel.Advanced })
        {
            var inLevel = selected.Where(item => item.Safety == level).ToList();
            if (inLevel.Count == 0) continue;
            string brush = level switch { SafetyLevel.Safe => "GoodBrush", SafetyLevel.Review => "MeterWarnBrush", _ => "CleanTextBrush" };
            string name = level switch { SafetyLevel.Safe => "Safe", SafetyLevel.Review => "Review", _ => "Advanced" };
            TextBlock Line(string text, bool strong, bool right, string foreground)
            {
                var block = new TextBlock { Text = text, FontSize = (double)FindResource("CompactFontSize"), FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
                block.SetResourceReference(TextBlock.ForegroundProperty, foreground);
                return block;
            }
            Grid Row(string left, string rightText, bool strong, string foreground, Thickness margin)
            {
                var row = new Grid { Margin = margin };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var l = Line(left, strong, false, foreground); var r = Line(rightText, strong, true, foreground);
                r.Margin = new Thickness(14, 0, 0, 0);
                Grid.SetColumn(r, 1); row.Children.Add(l); row.Children.Add(r);
                return row;
            }
            string Amount(IEnumerable<CleanupItem> group) { var list = group.ToList(); return $"{list.Count:N0} {(list.Count == 1 ? "item" : "items")} · {Sizes.Format(list.Sum(x => x.Bytes))}"; }
            var header = Row(name, Amount(inLevel), true, brush, new Thickness(0, ConfirmGroups.Children.Count == 0 ? 0 : 10, 0, 2));
            ConfirmGroups.Children.Add(header);
            foreach (var group in inLevel.GroupBy(item => ReviewCategory.CategoryFor(item)).OrderByDescending(g => g.Sum(x => x.Bytes)))
            {
                string title = ReviewCategory.Definitions.FirstOrDefault(d => d.Id == group.Key)?.Title ?? group.Key;
                ConfirmGroups.Children.Add(Row(title, Amount(group), false, "MutedBrush", new Thickness(14, 0, 0, 0)));
            }
        }
    }

    // What exactly will happen, written from the selected items in short lines: nothing is described that is not selected.
    internal static string ConfirmationText(IReadOnlyCollection<CleanupItem> selected)
    {
        bool Has(string category) => selected.Any(x => ReviewCategory.CategoryFor(x) == category);
        int registry = selected.Count(x => x.Kind == CleanupKind.Registry);
        var lines = new List<string> { "Files are permanently deleted, bypassing the Recycle Bin. Locked or changed files are skipped." };
        if (registry > 0) lines.Add($"{registry} app registry entries are backed up to .reg files first.");
        if (Has("RecycleBin")) lines.Add("Recycle Bin: everything in it is removed on every drive, for good.");
        if (Has("WindowsOld")) lines.Add("Previous Windows installation: removed by Windows' Disk Cleanup (administrator prompt); you can't go back to the old Windows.");
        if (Has("ComponentStore")) lines.Add("Component cleanup: Windows (DISM) removes old versions (administrator prompt); installed updates can't be uninstalled afterwards.");
        if (Has("BrowserCookies")) lines.Add("Cookies: you will be signed out of websites. Passwords and bookmarks stay.");
        if (Has("BrowserHistory")) lines.Add("Browser history is cleared. Bookmarks, passwords and logins stay.");
        if (Has("Clipboard")) lines.Add("Clipboard history is cleared. Pinned clips stay.");
        if (Has("Update") || Has("Delivery") || Has("Setup")) lines.Add("Update, delivery and setup files can be downloaded again; updates may download twice.");
        if (Has("BrowserEdge") || Has("BrowserChrome") || Has("BrowserFirefox") || Has("BrowserOther") || Has("DirectX") || Has("NvidiaShader") || Has("GpuShader"))
            lines.Add("Browser and shader caches rebuild themselves; the next launch may be a little slower.");
        lines.Add("Defender history, certificates, security logs, restore points and recovery data are never touched.");
        return string.Join("\n", lines);
    }

    private void AdvancedConsent_Click(object sender, RoutedEventArgs e) => ConfirmDeleteButton.IsEnabled = AdvancedConsentBox.IsChecked == true;

    private async Task HideConfirmation()
    {
        confirmedItems = [];
        ModalOverlay.IsEnabled = false;
        BackgroundBlur.BeginAnimation(BlurEffect.RadiusProperty, Motion.Tween(BackgroundBlur.Radius, 0, 160));
        await Motion.FadeOut(ModalOverlay);
        ModalOverlay.Visibility = Visibility.Collapsed;
        ModalOverlay.IsEnabled = true;
        AppSurface.IsEnabled = true;
        if (confirmationReturnFocus != null) Keyboard.Focus(confirmationReturnFocus);
        else if (ScanPage.Visibility == Visibility.Visible) HomeCleanButton.Focus();
        else PrimaryButton.Focus();
    }

    private async void CancelDelete_Click(object sender, RoutedEventArgs e) => await HideConfirmation();

    private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = confirmedItems;
        // Advanced items need the consent box even if the button was enabled some other way.
        if (selected.Any(x => x.Safety == SafetyLevel.Advanced) && AdvancedConsentBox.IsChecked != true) return;
        await HideConfirmation();
        if (!CanClean || selected.Length == 0) return;
        ShowReviewPage(false);
        SetBusy(true, "clean");
        ResetGauge();
        GaugeLabel.Text = "Click to stop";
        ScanCircleButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Stop cleaning");
        StatusTitle.Text = "Cleaning selected items…";
        var progress = new Progress<int>(count =>
        {
            if (cancellation?.IsCancellationRequested != true) AnimateGaugeTo(Math.Min(99, count * 100.0 / selected.Length));
            StatusDetail.Text = $"{count:N0} of {selected.Length:N0} items processed";
        });
        try
        {
            var result = await Task.Run(() => engine.Clean(selected, progress, cancellation!.Token));
            RemoveDeleted();
            foreach (var item in selected) if (item.Outcome != "Deleted") item.Selected = false;
            if (!result.Stopped) await CompleteGauge();
            else FreezeGauge();
            GaugeLabel.Text = "space freed";
            // The claim comes from the items; the measurement comes from Windows' own free-space figure for the fixed drives.
            string measured = result.MeasuredFreed > 0 ? $" · disk reports {Sizes.Format(result.MeasuredFreed)} more free" : "";
            StatusTitle.Text = $"{(result.Stopped ? "Stopped safely" : "Cleanup complete")} · {Sizes.Format(result.Bytes)} freed{measured} · {result.Deleted:N0} cleaned · {result.Skipped:N0} skipped";
            StatusDetail.Text = FoundSummary();
            lastCleanResult = result;
        }
        catch (Exception ex)
        {
            FreezeGauge();
            StatusTitle.Text = "Cleanup couldn't finish.";
            StatusDetail.Text = ex.Message;
            // If logging failed after an operation, reflect all recorded outcomes in the view.
            RemoveDeleted();
            foreach (var category in Categories) if (category.HasFiles) category.Selected = false;
        }
        finally
        {
            StatusTitle.Text = "Review remaining files.";
            RebuildCategories(); SetBusy(false); RefreshSelection(); ShowReviewPage(true); PrimaryButton.Focus();
        }
    }

    private void SetBusy(bool value, string mode = "")
    {
        busy = value;
        CanSelectCandidates = CanClean;
        if (value) currentOperation = mode;
        if (value) { cancellation?.Dispose(); cancellation = new(); }
        CategoriesList.IsEnabled = CollapseAllButton.IsEnabled = SelectAllBox.IsEnabled = BackButton.IsEnabled = AboutButton.IsEnabled = SettingsButton.IsEnabled = !value;
        bool scanning = value && mode == "scan";
        ScanRipplesCue.IsRunning = scanning;
        FileScanActivity.IsRunning = value && !scanning;
        FileScanActivity.Visibility = value && !scanning ? Visibility.Visible : Visibility.Hidden;
        LiveFileCounter.Visibility = scanning ? Visibility.Visible : Visibility.Hidden;
        StatusDetail.Visibility = scanning ? Visibility.Hidden : Visibility.Visible;
        RescanButton.Visibility = Visibility.Collapsed;
        PrimaryButton.IsEnabled = ScanCircleButton.IsEnabled = true;
        UpdateLandingStatus();
        if (value)
        {
            ApplyPrimaryAppearance(false);
            PrimaryButton.Visibility = Visibility.Visible;
            PrimaryButton.Content = mode == "clean" ? "Stop cleaning" : "Stop scan";
            PrimaryButton.SetResourceReference(BackgroundProperty, "RaisedBrush");
            PrimaryButton.SetResourceReference(ForegroundProperty, "TextBrush");
            Motion.SetGlow(PrimaryButton, 0);

        }
        else
        {
            GaugeArc.BeginAnimation(OpacityProperty, null);
            cancellation?.Dispose(); cancellation = null;
            ScanCircleButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, scanned ? "Scan again" : "Ready to scan");
        }
    }

    private CleanupResult? lastCleanResult;
    internal CleanupResult? LastCleanResult => lastCleanResult;

    // One pass removes everything Windows reported as deleted (removing items one by one was quadratic for large scans).
    private void RemoveDeleted()
    {
        items.RemoveAll(item => item.Outcome == "Deleted");
        RecomputeFound();
    }

    private void OnSelectionTotalsChanged()
    {
        if (!batchSelection) RefreshSelection();
    }

    private void RecomputeFound()
    {
        int files = 0, registry = 0; long bytes = 0; bool bin = false;
        foreach (var item in items)
        {
            bytes += item.Bytes;
            switch (item.Kind)
            {
                case CleanupKind.Registry: registry++; break;
                case CleanupKind.RecycleBin: bin = true; break;
                case CleanupKind.SystemTool: break;
                default: files++; break;
            }
        }
        foundFiles = files; foundRegistry = registry; foundBytes = bytes; foundRecycleBin = bin;
    }

    internal void UpdateLandingStatus()
    {
        bool ready = !busy && scanned;
        if (ready && CompletionLinkButton.Visibility != Visibility.Visible)
        {
            CompletionLinkButton.Visibility = Visibility.Visible;
            CompletionLinkButton.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 500));
        }
        else if (!ready) CompletionLinkButton.Visibility = Visibility.Collapsed;
        StatusTitle.Visibility = ready || (busy && currentOperation != "scan") ? Visibility.Collapsed : Visibility.Visible;
        // Once a scan has completed with results the Clean button stays on screen: dimmed while nothing is selected, so it never "disappears".
        bool showClean = CanClean && SelectedCount > 0;
        bool offerClean = CanClean && items.Count > 0;
        bool revealClean = offerClean && HomeCleanButton.Visibility != Visibility.Visible;
        HomeCleanButton.IsEnabled = showClean;
        HomeCleanButton.Visibility = offerClean ? Visibility.Visible : Visibility.Hidden;
        HomeCleanButton.ToolTip = offerClean && !showClean ? "Select something to clean first" : null;
        if (revealClean) HomeCleanButton.BeginAnimation(OpacityProperty, Motion.Tween(0, HomeCleanButton.IsEnabled ? 1 : 0.45, 500));
    }

    internal void LoadReviewItems(IEnumerable<CleanupItem> files)
    {
        items.Clear();
        items.AddRange(files);
        RecomputeFound();
        RebuildCategories();
        RefreshSelection();
        if (scanned && !busy) StatusDetail.Text = FoundSummary();
    }

    // Splits the items into their categories in one pass (not one pass per category) and replaces each category in a single step.
    private void RebuildCategories()
    {
        var expanded = Categories.Where(category => category.Expanded).Select(category => category.Id).ToHashSet();
        var byCategory = new Dictionary<string, List<CleanupItem>>();
        foreach (var item in items)
        {
            string id = ReviewCategory.CategoryFor(item);
            if (!byCategory.TryGetValue(id, out var list)) byCategory[id] = list = [];
            list.Add(item);
        }
        foreach (var category in Categories) category.Replace(byCategory.TryGetValue(category.Id, out var list) ? list : []);
        foreach (var category in Categories) category.Expanded = expanded.Contains(category.Id) && category.Files.Count > 0;
        CollectionViewSource.GetDefaultView(Categories).Refresh();
    }

    private void CategoryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ReviewCategory.Expanded)) UpdateSectionToggle();
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        bool expand = !Categories.Where(category => category.HasFiles).All(category => category.Expanded);
        foreach (var category in Categories) category.Expanded = category.HasFiles && expand;
        UpdateSectionToggle();
    }

    private void UpdateSectionToggle()
    {
        bool expanded = Categories.Any(category => category.HasFiles) && Categories.Where(category => category.HasFiles).All(category => category.Expanded);
        SectionToggleLabel.Text = expanded ? "Collapse all" : "Expand all";
        CollapseAllButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, SectionToggleLabel.Text);
        SectionToggleRotation.BeginAnimation(RotateTransform.AngleProperty, Motion.Tween(SectionToggleRotation.Angle, expanded ? 180 : 0, 240));
    }

    private void CategoryFiles_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scroll = FindChild<ScrollViewer>((DependencyObject)sender);
        if (scroll == null) return;
        if ((e.Delta > 0 && scroll.VerticalOffset <= 0) || (e.Delta < 0 && scroll.VerticalOffset >= scroll.ScrollableHeight))
        { ReviewScroll.ScrollToVerticalOffset(ReviewScroll.VerticalOffset - e.Delta / 3.0); e.Handled = true; }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindChild<T>(child) is T descendant) return descendant;
        }
        return null;
    }

    private void RefreshSelection()
    {
        if (busy) return;
        CanSelectCandidates = CanClean;
        int selected = SelectedCount;
        SelectionSummary.Text = CanSelectCandidates
            ? $"{selected:N0} selected · {Sizes.Format(SelectedBytes)}"
            : "Partial results";
        ResultCount.Text = FoundSummary();
        // With results on screen the red Clean button is always there (dimmed until something is selected); the count and size sit in
        // ordinary text just below it (SelectionSummary). Without results the one button is a neutral Start scan / Scan again.
        bool destructive = CanClean && items.Count > 0;
        PrimaryButton.Content = destructive ? "Clean selected" : CurrentScanState == ScanState.Ready ? "Start scan" : "Scan again";
        PrimaryButton.IsEnabled = !destructive || selected > 0;
        PrimaryButton.ToolTip = destructive && selected == 0 ? "Select something to clean first" : null;
        ApplyPrimaryAppearance(destructive);
        // Scan again sits at the left while the primary action is Clean.
        RescanButton.Visibility = destructive ? Visibility.Visible : Visibility.Collapsed;
        SelectAllBox.IsEnabled = CanSelectCandidates && items.Count > 0;
        SelectAllBox.Visibility = CanSelectCandidates ? Visibility.Visible : Visibility.Hidden;
        ReadOnlyResultsLabel.Visibility = CanSelectCandidates ? Visibility.Collapsed : Visibility.Visible;
        SelectAllBox.IsChecked = items.Count == 0 || selected == 0 ? false : selected == items.Count ? true : null;
        bool hasItems = items.Count > 0;
        CategorySurface.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        ReviewEmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        SelectionDetails.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        ResultCount.Visibility = hasItems ? Visibility.Visible : Visibility.Hidden;
        EmptyStateTitle.Text = CurrentScanState switch
        {
            ScanState.Completed => scanSkippedLocations ? "No cleanup items found." : "Everything looks clean.",
            ScanState.Stopped => "No results to review.",
            ScanState.Failed => "The scan couldn't finish.",
            _ => "Your results will appear here."
        };
        EmptyStateDetail.Text = CurrentScanState switch
        {
            ScanState.Completed => scanSkippedLocations ? "Some locations were skipped. Scan again when they are available." : "No eligible files or registry leftovers were found.",
            ScanState.Stopped => "The scan was incomplete. Scan again before cleaning.",
            ScanState.Failed => "Scan again to check for cleanup items.",
            _ => "Run a scan to check for cleanup items."
        };
        UpdateSectionToggle();
        UpdateLandingStatus();
    }

    private string FoundSummary()
    {
        int files = foundFiles, registry = foundRegistry;
        return $"{files:N0} {(files == 1 ? "file" : "files")} found · {Sizes.Format(foundBytes)}"
            + (registry > 0 ? $" · {registry:N0} app {(registry == 1 ? "entry" : "entries")}" : "")
            + (foundRecycleBin ? " · Recycle Bin" : "");
    }

    internal void ApplyPrimaryAppearance(bool destructive)
    {
        PrimaryButton.MinWidth = destructive ? 156 : 0;
        PrimaryButton.Padding = destructive ? new Thickness(18, 9, 18, 9) : new Thickness(14, 10, 14, 10);
        if (destructive) PrimaryButton.SetResourceReference(BackgroundProperty, "CleanFillBrush");
        else PrimaryButton.Background = Brushes.Transparent;
        PrimaryButton.BorderThickness = new Thickness(destructive ? 1 : 0);
        if (destructive) PrimaryButton.SetResourceReference(ForegroundProperty, "CleanTextBrush");
        else PrimaryButton.SetResourceReference(ForegroundProperty, "MutedBrush");
        Motion.SetAccent(PrimaryButton, destructive ? (Color)ColorConverter.ConvertFromString(SafetyPalette.Danger(false)) : (Color)Application.Current.Resources["PrimaryAccentColor"]);
        Motion.SetGlow(PrimaryButton, destructive ? 0.13 : 0);
        Motion.SetOuterGlow(PrimaryButton, destructive);
        Motion.SetHoverForeground(PrimaryButton, destructive ? null : (Brush)Application.Current.Resources["TextBrush"]);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool selected = SelectAllBox.IsChecked == true;
        batchSelection = true;
        try { foreach (var category in Categories) if (category.HasFiles) category.Selected = selected; }
        finally { batchSelection = false; }
        RefreshSelection();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ToggleTheme();
        prefs.Light = light;
        SavePreferences();
    }

    internal void ToggleTheme() { light = !light; ApplyTheme(animate: true); }

    private void ApplyTheme(bool animate = false)
    {
        void Set(string key, string color) => ThemePalette.Set(Application.Current.Resources, key, color, animate);
        string[] keys = ["WindowBrush", "PanelBrush", "RaisedBrush", "BorderBrush", "TextBrush", "MutedBrush", "FaintBrush", "HeadingBrush"];
        string[] colors = light
            ? ["#EFF1F5", "#F9FAFC", "#E4E8F0", "#DBE1EA", "#48566C", "#627086", "#6C798D", "#344156"]
            : ["#11151B", "#1B212B", "#252C38", "#2C3442", "#C6CFDC", "#9BA8BA", "#8794A8", "#D8DFE9"];
        for (int i = 0; i < keys.Length; i++) Set(keys[i], colors[i]);
        Set("ModalShadeBrush", light ? "#65E4EAF0" : "#750C1016");
        Set("SafetyGreenBrush", SafetyPalette.Accent(light));
        Set("GoodBrush", SafetyPalette.Good(light));
        Set("CleanTextBrush", SafetyPalette.Danger(light));
        Set("CleanFillBrush", "#08FF795C");
        foreach (var category in Categories) category.SetTheme(light);
        ThemeSunCore.Opacity = light ? 1 : 0.3;
        ThemeSunRays.Opacity = light ? 1 : 0.55;
        ThemeButton.ToolTip = light ? "Switch to dark theme" : "Switch to light theme";
        ThemeButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, light ? "Light theme. Switch to dark theme" : "Dark theme. Switch to light theme");
        // One primary accent per theme (Cyan in Dark, soft blue in Light). The gauge, accent text, glows and Safe accents all read it,
        // so they stay identical to each other; TealBrush/TealTextBrush ease with the theme like every other palette brush.
        string accent = SafetyPalette.Accent(light);
        Application.Current.Resources["PrimaryAccentColor"] = (Color)ColorConverter.ConvertFromString(accent);
        Set("TealBrush", accent);
        Set("TealTextBrush", accent);
        Set("AccentHoverBrush", light ? "#3A66AE" : "#7FE3DD");
        Set("MeterWarnBrush", SafetyPalette.Text(SafetyLevel.Review, light));
        Set("ActionFillBrush", light ? "#E4E8F0" : "#252C38");
        if (!busy) ApplyPrimaryAppearance(CanClean && items.Count > 0);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (busy || AboutOverlay.Visibility == Visibility.Visible) return;
        ClearUpdateSession();
        AboutTerms.Text = Terms.Notice;
        confirmationReturnFocus = Keyboard.FocusedElement;
        AppSurface.IsEnabled = false;
        BackgroundBlur.BeginAnimation(BlurEffect.RadiusProperty, Motion.Tween(BackgroundBlur.Radius, 7, 280));
        AboutOverlay.Visibility = Visibility.Visible;
        AboutUpdatesBody.Visibility = Visibility.Collapsed;
        AboutUpdatesBody.Expanded = false;
        AboutUpdatesButton.Content = "Release notes";
        AboutUpdatesButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Show release notes");
        PatchNotesHeading.Text = $"Installed release {UpdateService.DisplayVersion}";
        PatchNotes.Text = ReleaseNotes.Current;
        SetUpdateStatus("Check for the latest release.");
        UpdateDetailsScroll.ScrollToTop();
        Motion.Reveal(AboutOverlay);
        CloseAboutButton.Focus();
    }

    private void RevealUpdateDetails()
    {
        AboutUpdatesBody.Visibility = Visibility.Visible;
        AboutUpdatesBody.Expanded = true;
        AboutUpdatesButton.Content = "Hide details";
        AboutUpdatesButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Hide release details");
        UpdateDetailsScroll.ScrollToTop();
    }

    private async void CloseAbout_Click(object sender, RoutedEventArgs e) => await HideAbout();

    private void AboutUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!AboutUpdatesBody.Expanded) RevealUpdateDetails();
        else
        {
            AboutUpdatesBody.Expanded = false;
            AboutUpdatesButton.Content = "Release notes";
            AboutUpdatesButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Show release notes");
        }
    }

    private void ClearUpdateSession()
    {
        aboutSession++;
        updateCancellation?.Cancel();
        updateCancellation = null; // Each in-flight operation owns/disposes its source.
        availableRelease = null;
        UpdateHint.Visibility = Visibility.Collapsed;
        SetUpdateBusy(false);
    }

    // A new version is announced in the main accent color (and semibold) so it cannot be missed; every other message is plain text.
    private void SetUpdateStatus(string text, bool notice = false, bool good = false)
    {
        UpdateStatus.Text = text;
        UpdateStatus.SetResourceReference(TextBlock.ForegroundProperty, good ? "GoodBrush" : notice ? "TealTextBrush" : "TextBrush");
        UpdateStatus.FontWeight = notice || good ? FontWeights.SemiBold : FontWeights.Medium;
    }

    private bool IsCurrentAbout(int session) => session == aboutSession && AboutOverlay.Visibility == Visibility.Visible;

    private void SetUpdateBusy(bool value)
    {
        updateBusy = value;
        if (value) UpdateSweep.Progress = double.NaN; // Indeterminate until a download reports real progress.
        UpdateSweep.IsRunning = value;
        CancelUpdateButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        AboutUpdatesButton.IsEnabled = !value;
        CheckUpdateButton.IsEnabled = !value;
        CheckUpdateButton.Content = availableRelease == null ? "Check for updates" : "Update now";
        CheckUpdateButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, availableRelease == null ? "Check for updates" : "Update now: download and install the update");
    }

    private async Task HideAbout()
    {
        ClearUpdateSession();
        AboutOverlay.IsEnabled = false;
        AboutUpdatesBody.Expanded = false;
        BackgroundBlur.BeginAnimation(BlurEffect.RadiusProperty, Motion.Tween(BackgroundBlur.Radius, 0, 160));
        await Motion.FadeOut(AboutOverlay);
        AboutOverlay.Visibility = Visibility.Collapsed;
        AboutOverlay.IsEnabled = true;
        AppSurface.IsEnabled = true;
        if (confirmationReturnFocus != null) Keyboard.Focus(confirmationReturnFocus); else AboutButton.Focus();
    }

    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => updateCancellation?.Cancel();

    // One continuous flow, no sign-in: Check -> "Version X is available" -> one click on Download & install ->
    // download with live progress, hash/size/identity verification, then the updater hands over and Hush restarts.
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (updateBusy) return;
        RevealUpdateDetails();
        // Installing replaces the running app and restarts it, so it is only ever done after an explicit "Are you sure".
        if (availableRelease != null)
        {
            int askedSession = aboutSession;
            string version = UpdateService.Display(availableRelease.Version);
            bool agreed = await AskConsent($"Update Hush to {version}?",
                $"Hush will download version {version}, check it against the published checksum, then close and restart to finish the update.\n\nYour current version is kept as a backup, and Hush returns to it by itself if the new version does not start.",
                "Update now", "Not now");
            if (!agreed || !IsCurrentAbout(askedSession) || availableRelease == null) return;
        }
        using var operation = new CancellationTokenSource();
        updateCancellation = operation;
        int session = aboutSession;
        int request = ++updateRequest;
        SetUpdateBusy(true);
        try
        {
            using var service = updateFactory();
            if (availableRelease == null)
            {
                UpdateHint.Visibility = Visibility.Collapsed;
                SetUpdateStatus("Looking for a newer version…");
                // Even when the answer is instant, the search animation plays for a calm, readable moment (cancel still stops it at once).
                var checkClock = Stopwatch.StartNew();
                AppRelease? release;
                try { release = await service.CheckAsync(operation.Token); }
                finally
                {
                    var remaining = MinimumUpdateCheck - checkClock.Elapsed;
                    if (remaining > TimeSpan.Zero && !operation.IsCancellationRequested) await Task.Delay(remaining, operation.Token);
                }
                if (!IsCurrentAbout(session) || operation.IsCancellationRequested) return;
                availableRelease = release;
                if (release == null) SetUpdateStatus($"You're up to date. Installed version {UpdateService.DisplayVersion}.", good: true);
                else
                {
                    SetUpdateStatus($"Version {UpdateService.Display(release.Version)} is available.", notice: true);
                    UpdateHint.Text = "Update now downloads it securely, checks it and restarts Hush to finish. Your current version is kept as a backup.";
                    UpdateHint.Visibility = Visibility.Visible;
                    UpdateHint.BeginAnimation(OpacityProperty, Motion.Tween(0, 1));
                    PatchNotesHeading.Text = $"Release notes - {UpdateService.Display(release.Version)}";
                    PatchNotes.Text = PlainReleaseNotes(release.Notes);
                    PatchNotesPanel.BeginAnimation(OpacityProperty, Motion.Tween(0, 1));
                }
            }
            else
            {
                var release = availableRelease;
                UpdateHint.Visibility = Visibility.Collapsed;
                SetUpdateStatus("Downloading the update…");
                var progress = new Progress<double>(fraction =>
                {
                    if (!IsCurrentAbout(session) || request != updateRequest || !updateBusy || operation.IsCancellationRequested) return;
                    UpdateSweep.Progress = Math.Clamp(fraction, 0, 1);
                    SetUpdateStatus($"Downloading the update · {Math.Clamp(fraction, 0, 1):P0}");
                });
                string staged = await service.DownloadAsync(release, progress, operation.Token);
                if (!IsCurrentAbout(session) || operation.IsCancellationRequested) return;
                // The download is verified (size, SHA-256, Windows x64 identity and version) before anything is replaced.
                UpdateSweep.Progress = double.NaN;
                SetUpdateStatus("Verified. Restarting to finish the update…");
                try
                {
                    installUpdate(staged, release);
                    Close(); // Success is established by the new process's startup receipt, not this view.
                }
                catch
                {
                    SetUpdateStatus("Couldn't start the installation. Your installed app is unchanged.");
                }
            }
        }
        catch (OperationCanceledException) { if (IsCurrentAbout(session)) SetUpdateStatus("Update request canceled. Your installed app is unchanged."); }
        catch (InvalidDataException ex) { if (IsCurrentAbout(session)) SetUpdateStatus(ex.Message); }
        catch (System.Net.Http.HttpRequestException) { if (IsCurrentAbout(session)) SetUpdateStatus("Couldn't reach the update server. Check your internet connection and try again."); }
        catch { if (IsCurrentAbout(session)) SetUpdateStatus("Couldn't prepare the update. Your installed app is unchanged."); }
        finally
        {
            if (IsCurrentAbout(session) && request == updateRequest)
            {
                updateCancellation = null; SetUpdateBusy(false);
                UpdateHint.Visibility = availableRelease != null ? Visibility.Visible : Visibility.Collapsed; // The explanation returns after a failed or canceled attempt.
            }
        }
    }

    private static string PlainReleaseNotes(string notes)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(notes, @"(?m)^\s{0,3}#{1,6}\s+", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text.Replace("**", "", StringComparison.Ordinal);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (windowsSettingsBusy) { e.Cancel = true; return; }
        ClearUpdateSession();
        StopBoostWork();
        if (!busy || closing) return;
        e.Cancel = true;
        cancellation?.Cancel();
        StatusTitle.Text = "Stopping safely before closing…";
        while (busy) await Task.Delay(50);
        closing = true;
        Close();
    }

}
