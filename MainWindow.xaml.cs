using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private readonly Func<IProgress<ScanProgress>?, CancellationToken, ScanResult> scan;
    private readonly Func<IUpdateClient> updateFactory;
    private readonly Action<string, AppRelease> installUpdate;
    private readonly ObservableCollection<CleanupItem> items = [];
    internal ObservableCollection<ReviewCategory> Categories { get; } = ReviewCategory.Create();
    internal IReadOnlyList<CleanupItem> ReviewItems => items;
    private CancellationTokenSource? cancellation;
    private bool busy, light, closing;
    private bool settingsReturnToReview;
    internal bool IsPageTransitioning => Motion.IsEntering(MainContent);
    internal enum ScanState { Ready, Scanning, Completed, Stopped, Limited, Failed }
    internal ScanState CurrentScanState { get; private set; }
    private bool scanned => CurrentScanState is ScanState.Completed or ScanState.Stopped or ScanState.Limited;
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
    internal static readonly DependencyProperty GaugePercentProperty = DependencyProperty.Register(
        nameof(GaugePercent), typeof(double), typeof(MainWindow), new PropertyMetadata(0.0, GaugePercentChanged));
    internal double GaugePercent { get => (double)GetValue(GaugePercentProperty); set => SetValue(GaugePercentProperty, value); }
    private CleanupItem[] confirmedItems = [];
    private readonly string settingsPath;

    public MainWindow() : this(new CleanupEngine()) { }

    internal MainWindow(CleanupEngine engine, Func<IProgress<ScanProgress>?, CancellationToken, ScanResult>? scan = null, string? preferencesPath = null,
        Func<IUpdateClient>? updateFactory = null, Action<string, AppRelease>? installUpdate = null,
        WindowsSettingsService? windowsSettings = null)
    {
        this.engine = engine;
        this.scan = scan ?? engine.Scan;
        this.updateFactory = updateFactory ?? (() => new UpdateService());
        this.installUpdate = installUpdate ?? UpdateInstaller.Start;
        this.windowsSettings = windowsSettings ?? WindowsSettingsService.Create();
        settingsPath = preferencesPath ?? Path.Combine(CleanupEngine.DataDirectory, "settings.json");
        InitializeComponent();
        var categoryView = CollectionViewSource.GetDefaultView(Categories);
        categoryView.Filter = value => value is ReviewCategory { HasFiles: true };
        CategoriesList.ItemsSource = categoryView;
        foreach (var category in Categories)
        {
            category.PropertyChanged += CategoryChanged;
            category.SelectionBatchState = active => { batchSelection = active; if (!active) RefreshSelection(); };
        }
        try
        {
            if (File.Exists(settingsPath)) light = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(settingsPath))?.Light ?? false;
        }
        catch { }
        ApplyTheme();
        ResetGauge();
        RefreshSelection();
        AboutVersion.Text = $"Version {UpdateService.DisplayVersion}";
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape && ModalOverlay.Visibility == Visibility.Visible && ModalOverlay.IsEnabled)
            { e.Handled = true; await HideConfirmation(); }
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
        if (edges.Contains('R')) Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        if (edges.Contains('B')) Height = Math.Max(MinHeight, Height + e.VerticalChange);
        if (edges.Contains('L'))
        {
            double width = Math.Max(MinWidth, Width - e.HorizontalChange);
            Left += Width - width;
            Width = width;
        }
        if (edges.Contains('T'))
        {
            double height = Math.Max(MinHeight, Height - e.VerticalChange);
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
        Motion.EnterPage(MainContent, () =>
        {
            ScanPage.Visibility = ReviewPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
        });
        SettingsLightBox.Focus();
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e)
    {
        ShowReviewPage(settingsReturnToReview);
        SettingsButton.Focus();
    }

    private void SettingsTheme_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsLightBox.IsChecked != light) Theme_Click(sender, e);
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
        if (CanClean && items.Any(x => x.Selected)) ShowConfirmation();
        else await StartScan();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await StartScan();

    private async Task StartScan()
    {
        if (busy) return;
        CurrentScanState = ScanState.Scanning;
        scanSkippedLocations = false;
        LoadReviewItems([]);
        LiveFileCounter.Reset();
        ReviewHint.Text = "Selected files only. Confirmation required.";
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
        });
        try
        {
            var result = await Task.Run(() => scan(progress, cancellation!.Token));
            acceptingProgress = false;
            LoadReviewItems(result.Items);
            LiveFileCounter.SetCount(result.Items.Count(item => item.Kind is not (CleanupKind.Registry or CleanupKind.RecycleBin)), !result.Stopped);
            ReviewScroll.ScrollToTop();
            if (!result.Stopped && !result.Limited)
            {
                await Task.WhenAll(CompleteGauge(), LiveFileCounter.CompleteAsync());
            }
            else
            {
                FreezeGauge();
                if (!result.Stopped) await LiveFileCounter.CompleteAsync();
            }
            bool stopped = result.Stopped || cancellation?.IsCancellationRequested == true;
            CurrentScanState = stopped ? ScanState.Stopped : result.Limited ? ScanState.Limited : ScanState.Completed;
            if (stopped || result.Limited) FreezeIncompleteScanGauge();
            scanSkippedLocations = result.BusyCaches > 0 || result.Inaccessible > 0;
            GaugeLabel.Text = "Click to scan again";
            StatusTitle.Text = stopped ? "Scan stopped. Review partial results." : result.Limited ? "Scan limit reached. Review partial results." : items.Count == 0 ? (scanSkippedLocations ? "No cleanup items found." : "Everything looks clean.") : "Scan complete. Take a look.";
            ReviewHint.Text = stopped ? "Partial scan. Scan again before cleaning." : result.Limited ? "Scan limit reached. Scan again before cleaning." : "Selected files only. Confirmation required.";
            var notes = new List<string>();
            if (result.BusyCaches > 0) notes.Add("open browser caches skipped");
            if (result.Inaccessible > 0) notes.Add($"{result.Inaccessible:N0} inaccessible paths skipped");
            if (items.Any(x => x.Kind == CleanupKind.Registry)) notes.Add("registry entries are opt-in");
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
            ReviewHint.Text = "Partial scan. Scan again before cleaning.";
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
            LiveFileCounter.Settle();
            SetBusy(false);
            if (CurrentScanState != ScanState.Completed)
            {
                bool previousBatch = batchSelection;
                batchSelection = true;
                try { foreach (var item in items) item.Selected = false; }
                finally { batchSelection = previousBatch; }
            }
            RefreshSelection();
        }
    }

    private void HomeClean_Click(object sender, RoutedEventArgs e)
    { if (CanClean) ShowConfirmation(); }

    private void ShowConfirmation()
    {
        if (!CanClean) return;
        confirmedItems = items.Where(x => x.Selected).ToArray();
        if (confirmedItems.Length == 0) return;
        int registry = confirmedItems.Count(x => x.Kind == CleanupKind.Registry);
        ConfirmSummary.Text = $"Clean {confirmedItems.Length:N0} selected items and free {Sizes.Format(confirmedItems.Sum(x => x.Bytes))}?";
        ConfirmDetail.Text = "Selected files will be permanently deleted, bypassing the Recycle Bin. Locked or changed files will be skipped."
            + (registry > 0 ? $"\n\n{registry} app registry entries are included. Each is backed up to a .reg file before removal." : "")
            + (confirmedItems.Any(x => x.Kind == CleanupKind.RecycleBin) ? "\n\nThe Recycle Bin is included. Emptying it permanently removes everything in it on every drive and cannot be undone." : "")
            + (confirmedItems.Any(x => ReviewCategory.CategoryFor(x) is "Update" or "Delivery" or "Setup") ? "\n\nWindows Update, Delivery Optimization or setup files are included. Windows can download or recreate them, but updates may need to download again." : "")
            + "\n\nProtected servicing and rollback files, including previous Windows installations, stay untouched.";
        confirmationReturnFocus = Keyboard.FocusedElement;
        AppSurface.IsEnabled = false;
        BackgroundBlur.BeginAnimation(BlurEffect.RadiusProperty, Motion.Tween(BackgroundBlur.Radius, 7, 280));
        ModalOverlay.Visibility = Visibility.Visible;
        Motion.Reveal(ModalOverlay);
        CancelDeleteButton.Focus();
        // Keep keyboard navigation within the confirmation until it closes.
        KeyboardNavigation.SetTabNavigation(ModalOverlay, KeyboardNavigationMode.Cycle);
    }

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
            var removed = selected.Where(x => x.Outcome == "Deleted").ToArray();
            foreach (var item in removed) { item.PropertyChanged -= ItemChanged; items.Remove(item); }
            foreach (var item in selected.Except(removed)) item.Selected = false;
            if (!result.Stopped) await CompleteGauge();
            else FreezeGauge();
            GaugeLabel.Text = "space freed";
            StatusTitle.Text = $"{(result.Stopped ? "Stopped safely" : "Cleanup complete")} · {Sizes.Format(result.Bytes)} freed · {result.Deleted:N0} cleaned · {result.Skipped:N0} skipped";
            StatusDetail.Text = FoundSummary();
        }
        catch (Exception ex)
        {
            FreezeGauge();
            StatusTitle.Text = "Cleanup couldn't finish.";
            StatusDetail.Text = ex.Message;
            // If logging failed after an operation, reflect all recorded outcomes in the view.
            foreach (var item in selected.Where(x => x.Outcome == "Deleted").ToArray()) items.Remove(item);
            foreach (var item in items) item.Selected = false;
        }
        finally
        {
            ReviewHint.Text = StatusTitle.Text;
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

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupItem.Selected) && !batchSelection) RefreshSelection();
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
        bool showClean = CanClean && items.Any(item => item.Selected);
        bool revealClean = showClean && HomeCleanButton.Visibility != Visibility.Visible;
        HomeCleanButton.IsEnabled = showClean;
        HomeCleanButton.Visibility = showClean ? Visibility.Visible : Visibility.Hidden;
        if (revealClean) HomeCleanButton.BeginAnimation(OpacityProperty, Motion.Tween(0, HomeCleanButton.IsEnabled ? 1 : 0.45, 500));
    }

    internal void LoadReviewItems(IEnumerable<CleanupItem> files)
    {
        var snapshot = files.ToArray();
        foreach (var item in items) item.PropertyChanged -= ItemChanged;
        items.Clear();
        foreach (var item in snapshot) { item.PropertyChanged += ItemChanged; items.Add(item); }
        RebuildCategories();
        RefreshSelection();
        if (scanned && !busy) StatusDetail.Text = FoundSummary();
    }

    private void RebuildCategories()
    {
        var expanded = Categories.Where(category => category.Expanded).Select(category => category.Id).ToHashSet();
        foreach (var category in Categories) category.Replace(items.Where(item => ReviewCategory.CategoryFor(item) == category.Id));
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
        int selected = items.Count(item => item.Selected);
        SelectionSummary.Text = CanSelectCandidates
            ? $"{selected:N0} selected · {Sizes.Format(items.Where(item => item.Selected).Sum(item => item.Bytes))}"
            : "Read-only results";
        ResultCount.Text = FoundSummary();
        bool destructive = CanClean && selected > 0;
        PrimaryButton.Content = destructive ? "Clean selected" : CurrentScanState == ScanState.Ready ? "Start scan" : "Scan again";
        ApplyPrimaryAppearance(destructive);
        // Keep the rescan companion only while the primary action is Clean.
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
            ScanState.Stopped or ScanState.Limited => "No results to review.",
            ScanState.Failed => "The scan couldn't finish.",
            _ => "Your results will appear here."
        };
        EmptyStateDetail.Text = CurrentScanState switch
        {
            ScanState.Completed => scanSkippedLocations ? "Some locations were skipped. Scan again when they are available." : "No eligible files or registry leftovers were found.",
            ScanState.Stopped or ScanState.Limited => "The scan was incomplete. Scan again before cleaning.",
            ScanState.Failed => "Scan again to check for cleanup items.",
            _ => "Run a scan to check for cleanup items."
        };
        UpdateSectionToggle();
        UpdateLandingStatus();
    }

    private string FoundSummary()
    {
        int files = items.Count(item => item.Kind is not (CleanupKind.Registry or CleanupKind.RecycleBin));
        int registry = items.Count(item => item.Kind == CleanupKind.Registry);
        bool recycleBin = items.Any(item => item.Kind == CleanupKind.RecycleBin);
        return $"{files:N0} {(files == 1 ? "file" : "files")} found · {Sizes.Format(items.Sum(item => item.Bytes))}"
            + (registry > 0 ? $" · {registry:N0} app {(registry == 1 ? "entry" : "entries")}" : "")
            + (recycleBin ? " · Recycle Bin" : "");
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
        Motion.SetAccent(PrimaryButton, destructive ? (Color)ColorConverter.ConvertFromString("#FF795C") : (Color)Application.Current.Resources["PrimaryAccentColor"]);
        Motion.SetGlow(PrimaryButton, destructive ? 0.13 : 0);
        Motion.SetOuterGlow(PrimaryButton, destructive);
        Motion.SetHoverForeground(PrimaryButton, destructive ? null : (Brush)Application.Current.Resources["TextBrush"]);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool selected = SelectAllBox.IsChecked == true;
        batchSelection = true;
        try { foreach (var item in items.ToArray()) item.Selected = selected; }
        finally { batchSelection = false; }
        RefreshSelection();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ToggleTheme();
        try
        {
            string directory = Path.GetDirectoryName(settingsPath)!;
            Directory.CreateDirectory(directory);
            if (CleanupEngine.SafePath(directory, directory))
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Preferences(light)));
        }
        catch { }
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
        Set("SafetyGreenBrush", SafetyPalette.Text(SafetyLevel.Safe, light));
        Set("CleanTextBrush", light ? "#B54428" : "#FF795C");
        Set("CleanFillBrush", "#08FF795C");
        foreach (var category in Categories) category.SetTheme(light);
        ThemeSunCore.Opacity = light ? 1 : 0.3;
        ThemeSunRays.Opacity = light ? 1 : 0.55;
        ThemeButton.ToolTip = light ? "Switch to dark theme" : "Switch to light theme";
        ThemeButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, light ? "Light theme. Switch to dark theme" : "Dark theme. Switch to light theme");
        SettingsLightBox.IsChecked = light;
        AppearanceSummary.Text = light ? "Light theme" : "Dark theme";
        // One primary accent per theme (Cyan in Dark, soft blue in Light). The gauge, accent text, glows and Safe accents all read it,
        // so they stay identical to each other; TealBrush/TealTextBrush ease with the theme like every other palette brush.
        string accent = SafetyPalette.Accent(light);
        Application.Current.Resources["PrimaryAccentColor"] = (Color)ColorConverter.ConvertFromString(accent);
        Set("TealBrush", accent);
        Set("TealTextBrush", accent);
        Set("AccentHoverBrush", light ? "#3A66AE" : "#80E0E6");
        Set("ActionFillBrush", light ? "#E4E8F0" : "#252C38");
        if (!busy) ApplyPrimaryAppearance(CanClean && items.Any(item => item.Selected));
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (busy || AboutOverlay.Visibility == Visibility.Visible) return;
        ClearUpdateSession();
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
        UpdateStatus.Text = "Check for the latest release.";
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

    private bool IsCurrentAbout(int session) => session == aboutSession && AboutOverlay.Visibility == Visibility.Visible;

    private void SetUpdateBusy(bool value)
    {
        updateBusy = value;
        if (value) UpdateSweep.Progress = double.NaN; // Indeterminate until a download reports real progress.
        UpdateSweep.IsRunning = value;
        CancelUpdateButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        AboutUpdatesButton.IsEnabled = !value;
        CheckUpdateButton.IsEnabled = !value;
        CheckUpdateButton.Content = availableRelease == null ? "Check for updates" : "Download & install";
        CheckUpdateButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, availableRelease == null ? "Check for updates" : "Download and install the update");
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
                UpdateStatus.Text = "Looking for a newer version…";
                var release = await service.CheckAsync(operation.Token);
                if (!IsCurrentAbout(session) || operation.IsCancellationRequested) return;
                availableRelease = release;
                if (release == null) UpdateStatus.Text = $"You're up to date. Installed version {UpdateService.DisplayVersion}.";
                else
                {
                    UpdateStatus.Text = $"Version {UpdateService.Display(release.Version)} is available.";
                    UpdateHint.Text = "Download & install fetches it securely, checks it and restarts Hush to finish. Your current version is kept as a backup.";
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
                UpdateStatus.Text = "Downloading the update…";
                var progress = new Progress<double>(fraction =>
                {
                    if (!IsCurrentAbout(session) || request != updateRequest || !updateBusy || operation.IsCancellationRequested) return;
                    UpdateSweep.Progress = Math.Clamp(fraction, 0, 1);
                    UpdateStatus.Text = $"Downloading the update · {Math.Clamp(fraction, 0, 1):P0}";
                });
                string staged = await service.DownloadAsync(release, progress, operation.Token);
                if (!IsCurrentAbout(session) || operation.IsCancellationRequested) return;
                // The download is verified (size, SHA-256, Windows x64 identity and version) before anything is replaced.
                UpdateSweep.Progress = double.NaN;
                UpdateStatus.Text = "Verified. Restarting to finish the update…";
                try
                {
                    installUpdate(staged, release);
                    Close(); // Success is established by the new process's startup receipt, not this view.
                }
                catch
                {
                    UpdateStatus.Text = "Couldn't start the installation. Your installed app is unchanged.";
                }
            }
        }
        catch (OperationCanceledException) { if (IsCurrentAbout(session)) UpdateStatus.Text = "Update request canceled. Your installed app is unchanged."; }
        catch (InvalidDataException ex) { if (IsCurrentAbout(session)) UpdateStatus.Text = ex.Message; }
        catch (System.Net.Http.HttpRequestException) { if (IsCurrentAbout(session)) UpdateStatus.Text = "Couldn't reach the update server. Check your internet connection and try again."; }
        catch { if (IsCurrentAbout(session)) UpdateStatus.Text = "Couldn't prepare the update. Your installed app is unchanged."; }
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
        if (!busy || closing) return;
        e.Cancel = true;
        cancellation?.Cancel();
        StatusTitle.Text = "Stopping safely before closing…";
        while (busy) await Task.Delay(50);
        closing = true;
        Close();
    }

    private sealed record Preferences(bool Light);
}
