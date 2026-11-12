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
        WindowsSettingsService? windowsSettings = null, StartupAppsService? startupApps = null, IMemoryBooster? memoryBooster = null, IProcessManager? processManager = null, WindowsUpdateSwitch? windowsUpdate = null, IServiceSystem? serviceSystem = null, ITaskSystem? taskSystem = null)
    {
        // Histories and logs of system changes live beside the settings file: the real app uses %LOCALAPPDATA%\Hush, and a window given its own settings path
        // (every test window) keeps them in that folder, so a test can never write a record of a service change into the person's real data (it did: 43 fake log lines).
        string dataFolder = preferencesPath != null ? Path.GetDirectoryName(Path.GetFullPath(preferencesPath))! : CleanupEngine.DataDirectory;
        serviceTuning = new ServiceTuning(serviceSystem ?? ServiceSystemFactory(), Path.Combine(dataFolder, "Windows settings"));
        taskTuning = new TaskTuning(taskSystem ?? TaskSystemFactory(), TaskHistoryDirectory ?? Path.Combine(dataFolder, "Windows settings"));
        this.memoryBooster = memoryBooster ?? new WindowsMemoryBooster();
        this.processManager = processManager ?? new WindowsProcessManager();
        updateSwitch = windowsUpdate ?? new WindowsUpdateSwitch(new RealWindowsUpdateSystem(), Path.Combine(dataFolder, "Windows settings"));
        this.engine = engine;
        this.scan = scan == null ? engine.Scan : (progress, token, _) => scan(progress, token);
        this.updateFactory = updateFactory ?? (() => new UpdateService());
        this.installUpdate = installUpdate ?? UpdateInstaller.Start;
        this.windowsSettings = windowsSettings ?? WindowsSettingsService.Create();
        this.startupApps = startupApps ?? new StartupAppsService(StartupLocations.Default);
        boostController = new SmartBoostController(engine);
        settingsPath = preferencesPath ?? Path.Combine(CleanupEngine.DataDirectory, "settings.json");
        InitializeComponent();
        // Windows 11: per-pixel transparency from the compositor instead of a layered window (default; --layered goes back). See UI/WindowGlass.cs.
        WindowGlass.Apply(this, WindowGlass.ChooseForThisProcess());
        AppSurface.Effect = null; // the popup blur is attached only while a popup is open (see SetBackdropBlur)
        var categoryView = CollectionViewSource.GetDefaultView(Categories);
        categoryView.Filter = value => value is ReviewCategory { HasFiles: true } category && InOpenGroup(category);
        // Safe, then Review, then Advanced; inside each risk level the category holding the most comes first (ties keep the catalog order).
        categoryView.SortDescriptions.Add(new SortDescription(nameof(ReviewCategory.Safety), ListSortDirection.Ascending));
        categoryView.SortDescriptions.Add(new SortDescription(nameof(ReviewCategory.TotalBytes), ListSortDirection.Descending));
        categoryView.SortDescriptions.Add(new SortDescription(nameof(ReviewCategory.CatalogOrder), ListSortDirection.Ascending));
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
        InitializeAccentChoices();
        InitializeProMode();
        ResetGauge();
        RefreshSelection();
        InitializeBoost();
        InitializeMemoryTrim();
        InitializeTray();
        // The gauge's own hover (before a scan, and a different one after it); off while Hush works.
        ScanCircleButton.MouseEnter += (_, _) => RefreshGaugeHover();
        ScanCircleButton.MouseLeave += (_, _) => RefreshGaugeHover();
        ScanCircleButton.IsEnabledChanged += (_, _) => RefreshGaugeHover();
        ScanCircleButton.IsVisibleChanged += (_, _) => RefreshGaugeHover();
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
            else if (e.Key == Key.Escape && (ActionsPopup.IsOpen || ServicesActionsPopup.IsOpen))
            { e.Handled = true; CloseActionMenus(); }
        };
        PreviewMouseDown += CloseActionsOnOutsidePress;
        Deactivated += (_, _) => { if (CloseActionsOnDeactivate) CloseActionMenus(); };
        ActionsButton.IsVisibleChanged += (_, e) => { if (e.NewValue is false) ActionsPopup.IsOpen = false; }; // leaving Settings closes the menu
        ServicesActionsButton.IsVisibleChanged += (_, e) => { if (e.NewValue is false) ServicesActionsPopup.IsOpen = false; }; // so does closing the Services card
    }

    // The soft blur behind a popup. A window-wide effect makes WPF push every frame of this transparent window through the effect pipeline, even at
    // radius 0 (measured: a frame went from about 11 ms of CPU to under 0.2 ms without it), so it exists only while a popup is open, switches on
    // and off at once (no animated radius) and is detached from the tree the rest of the time.
    // 0.9 audit (performance on weak PCs): the blur is OFF by default. Opening a popup with it cost 80% of a core and frames of up to 150 ms with software
    // rendering (an Intel i3 class machine); the darker scrim (ModalShadeBrush) separates the popup from the page instead. The radius is still tracked so
    // the popup code is unchanged, and BackdropBlurEnabled = true brings the soft blur back (measure with --ui-perf before turning it on).
    internal static bool BackdropBlurEnabled = false;
    private void SetBackdropBlur(double radius)
    {
        BackgroundBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        BackgroundBlur.Radius = radius;
        AppSurface.Effect = BackdropBlurEnabled && radius > 0.01 ? BackgroundBlur : null;
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
        // The finished-clean look ends with the next scan or clean: the "100%" goes back to its normal color and loses its glow (and the effect is detached).
        if (GaugeValue.Effect != null)
        {
            var glow = (System.Windows.Media.Effects.DropShadowEffect)FindResource("GaugeValueGlowEffect");
            glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null); glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
            glow.Opacity = 0; glow.BlurRadius = 0;
            GaugeValue.Effect = null;
        }
        GaugeValue.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
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

    // mission: the end of a clean. Besides the ring's bloom, the "100%" turns to the accent color and glows, and both settle into a lasting finished look (until the next
    // scan or clean): a sense of a finished mission. A scan's end keeps the calmer bloom.
    // after: something to wait for before the finished look (the cleaning cue's final smash), so the lasting glow starts when the smash is over and nothing overrides it.
    internal async Task CompleteGauge(bool mission = false, Task? after = null)
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
        if (after != null) await after;
        if (GaugePercent >= 100 && cancellation?.IsCancellationRequested != true)
        {
            // One soft bloom settles into a visible completed state until the next scan.
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            DoubleAnimationUsingKeyFrames Frames(double start, double peak, double rest, int peakAt = 650, int restAt = 1800)
            {
                var frames = new DoubleAnimationUsingKeyFrames();
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(peakAt)), ease));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(restAt)), ease));
                return frames;
            }
            // The ring: one soft bloom that settles into a lasting glow (stronger and wider for a finished clean).
            GaugeFinishGlow.BeginAnimation(DropShadowEffect.OpacityProperty, mission ? Frames(0.3, 1.0, 0.9, 700, 2000) : Frames(0.22, 0.72, 0.50));
            GaugeFinishGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, mission ? Frames(12, 36, 28, 700, 2000) : Frames(12, 20, 16));
            if (mission)
            {
                // The "100%": the accent color and its own glow, attached only now (an effect is attached only while it is visible) and removed again by ResetGauge.
                GaugeValue.SetResourceReference(TextBlock.ForegroundProperty, "TealTextBrush");
                var glow = (DropShadowEffect)FindResource("GaugeValueGlowEffect");
                GaugeValue.Effect = glow;
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, Frames(0, 1.0, 0.75, 700, 2000));
                glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, Frames(0, 22, 16, 700, 2000));
            }
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
        OpenReviewGroup(IsProMode ? null : SafetyLevel.Safe); // Pro: Take a look starts from the three groups; Simple has only the Safe items, so it opens them directly
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
            _ = Task.Run(UpdateInstaller.PruneAll);
            _ = RunQuietUpdateCheck();
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
        if (IsProMode) LoadWindowsSettings();   // the Windows choices are only read when they can be seen (Simple mode hides them)
        RefreshWindowsUpdateState();
        // (The restore-point probe is not run here any more: it asks Windows Management, which costs about a second of CPU, and the confirmation that needs it
        // probes by itself when it opens. 0.9 audit.)
        RefreshAdminSwitch();
        if (IsProMode) LoadStartupApps();
        Motion.EnterPage(MainContent, () =>
        {
            ScanPage.Visibility = ReviewPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
        });
        SettingsBackButton.Focus();
        BeginSettingsLiveData();
    }

    // The live readings (meter bars, Temporary files, open apps) start once the page has finished fading in. The page fades as one cached picture, and
    // anything that animates inside it during the fade makes WPF redraw that whole picture on every frame (measured: over a second of CPU on opening).
    private int settingsVisit;
    private async void BeginSettingsLiveData()
    {
        int visit = ++settingsVisit;
        await Task.Delay((int)Motion.PageFadeMilliseconds + 60);
        if (visit != settingsVisit || SettingsPage.Visibility != Visibility.Visible) return;
        StartResourceIndicators();
        RefreshTempFiles();
        if (IsProMode) RefreshProcesses(rebuildList: SettingsProcessSection.IsExpanded);
        // The Services list is read when its card is opened (or refreshed), not every time Settings opens: reading it walks every Windows service and starts sc.exe.
        if (IsProMode && SettingsServicesSection.IsExpanded) _ = RefreshServicesAsync();
        if (IsProMode && SettingsTasksSection.IsExpanded) _ = RefreshTasksAsync(); // likewise the scheduled tasks (a walk of every task file)
    }

    // The Settings sections sit in two columns side by side; in a narrow window they stack into one.
    private void SettingsScroll_SizeChanged(object sender, SizeChangedEventArgs e) => ApplySettingsColumns(e.NewSize.Width);

    internal void ApplySettingsColumns(double width)
    {
        bool wide = width >= 700;
        Grid.SetColumnSpan(SettingsLeftColumn, wide ? 1 : 2);
        Grid.SetColumn(SettingsRightColumn, wide ? 1 : 0);
        Grid.SetRow(SettingsRightColumn, wide ? 0 : 1);
        Grid.SetColumnSpan(SettingsRightColumn, wide ? 1 : 2);
        SettingsRightColumn.Margin = wide ? new Thickness(16, 0, 0, 0) : new Thickness(0);
    }

    internal bool SettingsInTwoColumns => Grid.GetColumn(SettingsRightColumn) == 1;

    private void SettingsBack_Click(object sender, RoutedEventArgs e)
    {
        settingsVisit++; // a reading that was about to start must not start behind the person's back
        StopResourceIndicators();
        ShowReviewPage(settingsReturnToReview);
        SettingsButton.Focus();
    }

    private async void Circle_Click(object sender, RoutedEventArgs e) => await ActivateGaugeAsync();

    // Decides which hover the gauge shows: none while Hush works, "ready" before a scan, "complete" after one. Called when the pointer moves on or off the
    // gauge and whenever the gauge's state changes (a scan that ends under a resting pointer starts the "complete" hover by itself).
    private void RefreshGaugeHover()
    {
        if (busy || !ScanCircleButton.IsEnabled || !ScanCircleButton.IsVisible) { GaugeHover.Stop(); return; }
        if (!ScanCircleButton.IsMouseOver) { GaugeHover.Leave(); return; }
        GaugeHover.Enter(scanned ? GaugeHoverStyle.Complete : GaugeHoverStyle.Ready);
    }

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

    // ----- Review: three color groups first, then the categories of the one that was opened (0.9) -----
    private SafetyLevel? openGroup;
    private readonly List<ReviewGroupSummary> reviewGroups = [];
    internal SafetyLevel? OpenGroup => openGroup;
    internal IReadOnlyList<ReviewGroupSummary> ReviewGroups => reviewGroups;
    // Test seam only: the layout checks that inspect every category card at once show the whole list instead of the overview. The product never sets it.
    internal static bool ReviewShowsAllByDefault;
    internal bool ReviewShowsAllCategories { get; set; } = ReviewShowsAllByDefault;
    private bool InOpenGroup(ReviewCategory category) => ReviewShowsAllCategories || openGroup == null || category.Safety == openGroup;
    private void ApplyReviewView()
    {
        bool list = items.Count > 0 && (ReviewShowsAllCategories || openGroup != null);
        CategoryListView.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        ReviewGroupsView.Visibility = items.Count > 0 && !list ? Visibility.Visible : Visibility.Collapsed;
        // Inside a group: chevron back, "Select all in <group>" and Expand all. On the overview: "Select all categories" (all three groups) only.
        bool inGroup = openGroup != null && !ReviewShowsAllCategories;
        GroupBackButton.Visibility = inGroup && IsProMode ? Visibility.Visible : Visibility.Collapsed; // Simple has one group only: Back leaves Review
        OverviewDivider.Visibility = items.Count > 0 && !list ? Visibility.Visible : Visibility.Collapsed;
        CollapseAllButton.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        string label = !IsProMode ? "Select all" : inGroup ? $"Select all in {openGroup switch { SafetyLevel.Safe => "Safe", SafetyLevel.Review => "Review", _ => "Advanced" }}" : "Select all categories";
        if (!ReferenceEquals(SelectAllBox.Content, label) && !Equals(SelectAllBox.Content, label)) SelectAllBox.Content = label;
    }

    // The chevron beside Select all: one step up, from an opened group back to the three groups.
    private void GroupBack_Click(object sender, RoutedEventArgs e)
    {
        if (busy || openGroup == null) return;
        OpenReviewGroup(null);
        Motion.Reveal(ReviewGroupsView);
    }
    private IEnumerable<ReviewCategory> VisibleCategories => Categories.Where(category => category.HasFiles && InOpenGroup(category));

    internal void OpenReviewGroup(SafetyLevel? level)
    {
        openGroup = level;
        CollectionViewSource.GetDefaultView(Categories).Refresh();
        ApplyReviewView();
        ReviewScroll.ScrollToTop();
        RefreshSelection();
    }

    // The checkbox beside a group (overview): ticks or clears every category of that group, leaving the other groups as they are.
    private void GroupSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ReviewGroupSummary group } box) return; // the box itself is disabled while results are partial
        e.Handled = true; // a Click bubbles: without this the row's own button would also open the group
        bool selected = box.IsChecked == true;
        batchSelection = true;
        try { foreach (var category in Categories.Where(category => category.Safety == group.Level && category.HasFiles).ToList()) category.Selected = selected; }
        finally { batchSelection = false; }
        RefreshSelection();
    }

    private void ReviewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is CheckBox) return; // the checkbox only selects; the rest of the row opens the group
        if (sender is Button { Tag: ReviewGroupSummary group } && !busy) { OpenReviewGroup(group.Level); Motion.Reveal(CategoryListView); }
    }

    // Three rows at most; recomputed from the categories' own running totals (never by looping the files).
    private void UpdateReviewGroups()
    {
        if (reviewGroups.Count == 0)
            foreach (var level in new[] { SafetyLevel.Safe, SafetyLevel.Review, SafetyLevel.Advanced }) reviewGroups.Add(new ReviewGroupSummary(level));
        foreach (var group in reviewGroups) group.Update(Categories.Where(category => category.Safety == group.Level && category.HasFiles).ToList(), light);
        var shown = reviewGroups.Where(group => group.HasItems).ToList();
        for (int index = 0; index < shown.Count; index++) shown[index].DividerAbove = index > 0;
        if (ReviewGroupsList.ItemsSource is not List<ReviewGroupSummary> current || !current.SequenceEqual(shown)) ReviewGroupsList.ItemsSource = shown;
        // A group that no longer has anything (after cleaning) closes back to the overview.
        if (openGroup != null && !reviewGroups.Any(group => group.Level == openGroup && group.HasItems)) OpenReviewGroup(null);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        // Inside a group, Back returns to the three groups; from the groups it leaves Review.
        if (IsProMode && openGroup != null && ReviewPage.Visibility == Visibility.Visible) { OpenReviewGroup(null); Motion.Reveal(ReviewGroupsView); return; }
        Headline.Text = "Make more room to breathe.";
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

    // Boost (the Info card's button, or the automatic one) scans and cleans with the same engine on its own: a scan or a clean started meanwhile would
    // list files that are about to disappear and run two cleans over the same folders, so they wait until it is done (0.9 audit).
    private bool BoostWorking => boosting || boostRunning;

    private async Task StartScan(ScanOptions? options = null)
    {
        if (busy || BoostWorking) return;
        // Low-resource mode scans at background CPU and disk priority and reports progress less often.
        options ??= DefaultScanOptions();
        pendingScanOptions = options;
        CurrentScanState = ScanState.Scanning;
        scanSkippedLocations = false;
        LoadReviewItems([]);
        LiveFileCounter.Reset();
        ShowReviewPage(false);
        SetBusy(true, "scan");
        ResetGauge();
        StatusDetail.Text = "Looking through safe cleanup locations…";
        StatusTitle.Text = "Searching files…"; StatusTitle.ToolTip = null;
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
            StatusTitle.Text = stopped ? "Scan stopped. Review partial results." : items.Count == 0 ? (scanSkippedLocations ? "No cleanup items found." : "Everything looks clean.") : "Scan Completed";
            var notes = new List<string>();
            if (result.BusyCaches > 0) notes.Add("locations in use (an open app, or Windows installing an update) skipped");
            // System-protected files that qualify but cannot be opened for deletion: administrator mode reaches most of them; the rest are simply in use.
            if (result.ProtectedFiles > 0)
                notes.Add(AdminMode.Elevation.IsElevated ? $"{result.ProtectedFiles:N0} system files are in use right now and were left alone"
                    : $"{result.ProtectedFiles:N0} system files can only be cleaned in administrator mode (Settings > Administrator) or are in use");
            if (result.Inaccessible > 0) notes.Add($"{result.Inaccessible:N0} inaccessible paths skipped");
            if (result.Stalled > 0) notes.Add($"{result.Stalled:N0} location{(result.Stalled == 1 ? "" : "s")} did not respond and {(result.Stalled == 1 ? "was" : "were")} skipped (see Logs\\scan-issues.log)");
            if (CleanupEngine.UserLocationsSkipped) notes.Add("Hush is running as a different Windows account than yours, so only system locations were scanned; your own folders are cleaned only when Hush runs as you");
            if (foundRegistry > 0) notes.Add("registry entries are opt-in");
            StatusDetail.Text = FoundSummary() + (result.ProtectedFiles > 0 && !AdminMode.Elevation.IsElevated ? $" · {result.ProtectedFiles:N0} more need administrator mode" : "");
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
            ReleaseOldResults();
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
    { if (CanClean) ShowConfirmation(fromHome: true); }

    private void ShowConfirmation(bool fromHome = false)
    {
        if (!CanClean || BoostWorking) return;
        confirmedItems = items.Where(x => x.Selected).ToArray();
        if (confirmedItems.Length == 0) return;
        // Simple mode: just what will be freed and one calm line. Pro mode: the breakdown by group and category and the short list of what will happen.
        bool simple = !IsProMode;
        long freed = confirmedItems.Sum(x => x.Bytes);
        ConfirmTitle.Text = $"Clean {Sizes.Format(freed)}?";
        ConfirmSummary.Text = simple ? "Only safe temporary files. Your own files are never touched."
            : $"{confirmedItems.Length:N0} selected {(confirmedItems.Length == 1 ? "item" : "items")}";
        ConfirmDetail.Text = ConfirmationText(confirmedItems);
        // From Home, Clean only touches what was selected by default (the green Safe items); say so once, plainly (Pro mode only: Simple mode is Safe only by definition).
        ConfirmHomeNote.Text = confirmedItems.All(x => x.Safety == SafetyLevel.Safe)
            ? "Cleans only the green, recommended items. Use Take a look to choose more."
            : "Includes the extra items you chose in Take a look.";
        ConfirmHomeNote.Visibility = fromHome && !simple ? Visibility.Visible : Visibility.Collapsed;
        ConfirmScroll.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        ConfirmDeleteButton.Content = simple ? "Clean" : "Clean selected";
        CancelDeleteButton.Content = simple ? "Not now" : "Keep my files";
        if (!simple) BuildConfirmGroups(confirmedItems);
        // Advanced (red) items are irreversible or recovery-related: the clean button stays off until the person ticks the consent box.
        bool advanced = confirmedItems.Any(x => x.Safety == SafetyLevel.Advanced);
        AdvancedConsentBox.IsChecked = false;
        AdvancedConsentBox.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        ConfirmDeleteButton.IsEnabled = !advanced;
        confirmationReturnFocus = Keyboard.FocusedElement;
        AppSurface.IsEnabled = false;
        SetBackdropBlur(7);
        ModalOverlay.Visibility = Visibility.Visible;
        Motion.Reveal(ModalOverlay);
        CancelDeleteButton.Focus();
        // Keep keyboard navigation within the confirmation until it closes.
        KeyboardNavigation.SetTabNavigation(ModalOverlay, KeyboardNavigationMode.Cycle);
    }

    // The Pro popup lists what will be cleaned as up to three colored groups (green Safe, amber Review, red Advanced): a header per group, then one row per category.
    // Every row is one grid with the same columns, so the colored lines, the names and the amounts line up (the amounts share one right edge and use the same
    // monospace figures as the sizes elsewhere).
    private void BuildConfirmGroups(IReadOnlyCollection<CleanupItem> selected)
    {
        ConfirmGroups.Children.Clear();
        foreach (var level in new[] { SafetyLevel.Safe, SafetyLevel.Review, SafetyLevel.Advanced })
        {
            var inLevel = selected.Where(item => item.Safety == level).ToList();
            if (inLevel.Count == 0) continue;
            string brush = level switch { SafetyLevel.Safe => "GoodBrush", SafetyLevel.Review => "MeterWarnBrush", _ => "CleanTextBrush" };
            string name = level switch { SafetyLevel.Safe => "Safe", SafetyLevel.Review => "Review", _ => "Advanced" };
            string Amount(IEnumerable<CleanupItem> group) { var list = group.ToList(); return $"{list.Count:N0} {(list.Count == 1 ? "item" : "items")} · {Sizes.Format(list.Sum(x => x.Bytes))}"; }
            Grid Row(string left, string rightText, bool header, string foreground, Thickness margin)
            {
                var row = new Grid { Margin = margin };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(header ? 12 : 12) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ConfirmAmount" });
                if (header)
                {
                    var line = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 0, 1) };
                    line.SetResourceReference(Border.BackgroundProperty, brush);
                    row.Children.Add(line);
                }
                TextBlock Text(string text, bool right)
                {
                    var block = new TextBlock { Text = text, FontSize = (double)FindResource("CompactFontSize"), FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
                    if (right) block.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                    block.SetResourceReference(TextBlock.ForegroundProperty, foreground);
                    return block;
                }
                var l = Text(left, false); var r = Text(rightText, true);
                Grid.SetColumn(l, 2); Grid.SetColumn(r, 4);
                row.Children.Add(l); row.Children.Add(r);
                return row;
            }
            ConfirmGroups.Children.Add(Row(name, Amount(inLevel), true, brush, new Thickness(0, ConfirmGroups.Children.Count == 0 ? 0 : 16, 0, 6)));
            foreach (var group in inLevel.GroupBy(item => ReviewCategory.CategoryFor(item)).OrderByDescending(g => g.Sum(x => x.Bytes)))
            {
                string title = ReviewCategory.Definitions.FirstOrDefault(d => d.Id == group.Key)?.Title ?? group.Key;
                ConfirmGroups.Children.Add(Row(title, Amount(group), false, "MutedBrush", new Thickness(0, 3, 0, 3)));
            }
        }
    }

    // What exactly will happen, written from the selected items in short lines: nothing is described that is not selected.
    internal static string ConfirmationText(IReadOnlyCollection<CleanupItem> selected)
    {
        bool Has(string category) => selected.Any(x => ReviewCategory.CategoryFor(x) == category);
        int registry = selected.Count(x => x.Kind == CleanupKind.Registry);
        var lines = new List<string> { "Files are permanently deleted, not sent to the Recycle Bin. Files in use are skipped." };
        if (registry > 0) lines.Add($"{registry} app registry entries are backed up first.");
        if (Has("RecycleBin")) lines.Add("The Recycle Bin is emptied on every drive.");
        if (Has("WindowsOld")) lines.Add("Windows.old is deleted, including the old Users folder (administrator mode). You can't go back to the old Windows.");
        if (ReviewCategory.AppDownloadIds.Any(Has)) lines.Add("Those apps download or reinstall what is removed; Unity Asset Store packages come back only from your account.");
        if (Has("BrowserCookies")) lines.Add("You will be signed out of websites. Passwords and bookmarks stay.");
        if (Has("BrowserHistory")) lines.Add("Browser history is cleared. Bookmarks, passwords and logins stay.");
        if (Has("Clipboard")) lines.Add("Clipboard history is cleared. Pinned clips stay.");
        if (Has("Update") || Has("Delivery") || Has("Setup")) lines.Add("Update and setup files can be downloaded again.");
        if (Has("BrowserEdge") || Has("BrowserChrome") || Has("BrowserFirefox") || Has("BrowserOther") || Has("DirectX") || Has("NvidiaShader") || Has("GpuShader"))
            lines.Add("Browser and shader caches rebuild; the next launch may be a little slower.");
        lines.Add("Security data and restore points are never touched.");
        return string.Join("\n", lines);
    }

    private void AdvancedConsent_Click(object sender, RoutedEventArgs e) => ConfirmDeleteButton.IsEnabled = AdvancedConsentBox.IsChecked == true;

    private async Task HideConfirmation()
    {
        confirmedItems = [];
        ModalOverlay.IsEnabled = false;
        SetBackdropBlur(0);
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
        if (!CanClean || selected.Length == 0 || BoostWorking) return;
        ShowReviewPage(false);
        SetBusy(true, "clean");
        ResetGauge();
        GaugeLabel.Text = "Click to stop";
        ScanCircleButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Stop cleaning");
        StatusTitle.Text = "Cleaning selected items…"; StatusTitle.ToolTip = null;
        var progress = new Progress<int>(count =>
        {
            if (cancellation?.IsCancellationRequested != true) AnimateGaugeTo(Math.Min(99, count * 100.0 / selected.Length));
            StatusDetail.Text = $"{count:N0} of {selected.Length:N0} items processed";
        });
        try
        {
            var toolNote = new Progress<string>(text => { if (busy) StatusDetail.Text = text; });
            var result = await Task.Run(() => engine.Clean(selected, progress, cancellation!.Token, toolNote));
            RemoveDeleted();
            foreach (var item in selected) if (item.Outcome != "Deleted") item.Selected = false;
            // A finished clean ends the Smash cue on purpose: the dust rushes in and one bigger final smash lands while the ring fills to 100% (a stopped or failed clean just stops it).
            // Only the real app window plays the finale; test windows share one process and must not wait for it.
            // The lasting glow of the whole ring starts after the final smash (it used to be overridden by a late pulse of the cue).
            if (!result.Stopped) await CompleteGauge(mission: true, after: TrimsMemory ? FileScanActivity.FinishAsync() : null);
            else FreezeGauge();
            GaugeLabel.Text = "space freed";
            // Sleek and minimal (owner, November 11, 2026): one short line with the result, and a second one only when something was left alone. The numbers
            // (cleaned, skipped and why, what the disk itself reports, how much is still found) are one hover away on the result line.
            StatusTitle.Text = $"{(result.Stopped ? "Stopped safely" : "Cleanup complete")} · {Sizes.Format(result.Bytes)} freed";
            StatusDetail.Text = result.Skipped > 0 ? $"{result.Skipped:N0} {(result.Skipped == 1 ? "file was" : "files were")} left alone" : "";
            // The claim comes from the items; the measurement comes from Windows' own free-space figure for the fixed drives.
            var more = new List<string> { $"{result.Deleted:N0} cleaned" };
            if (result.Skipped > 0) more.Add($"{result.Skipped:N0} skipped" + (result.SkipSummary.Length > 0 ? $" ({result.SkipSummary})" : ""));
            if (result.MeasuredFreed > 0) more.Add($"the disk reports {Sizes.Format(result.MeasuredFreed)} more free");
            if (items.Count > 0) more.Add(FoundSummary() + " still found");
            StatusTitle.ToolTip = string.Join(" · ", more);
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
            // The status line keeps what the try/catch above set ("Cleanup complete · X freed ..." or "Cleanup couldn't finish."): the result and any
            // failure must stay readable (0.9 audit). It used to be replaced here at once by "Review remaining files.".
            // The person stays on Home with the finished gauge (owner, November 11, 2026: no automatic jump to the Review page after a clean).
            RebuildCategories(); SetBusy(false); RefreshSelection(); ScanCircleButton.Focus();
            ReleaseOldResults();
        }
    }

    private void SetBusy(bool value, string mode = "")
    {
        busy = value;
        CanSelectCandidates = CanClean;
        if (value) currentOperation = mode;
        if (value) { cancellation?.Dispose(); cancellation = new(); }
        // The Hush logo (About button) stays enabled and unchanged while Hush works: it looks identical in every state, and About_Click ignores clicks while busy.
        CategoriesList.IsEnabled = CollapseAllButton.IsEnabled = GroupBackButton.IsEnabled = SelectAllBox.IsEnabled = BackButton.IsEnabled = SettingsButton.IsEnabled = ProModeButton.IsEnabled = !value;
        bool scanning = value && mode == "scan";
        ScanRipplesCue.IsRunning = scanning;
        FileScanActivity.IsRunning = value && !scanning;
        FileScanActivity.Visibility = value && !scanning ? Visibility.Visible : Visibility.Hidden;
        LiveFileCounter.Visibility = scanning ? Visibility.Visible : Visibility.Hidden;
        StatusDetail.Visibility = scanning ? Visibility.Hidden : Visibility.Visible;
        RescanButton.Visibility = Visibility.Collapsed;
        PrimaryButton.IsEnabled = ScanCircleButton.IsEnabled = true;
        RefreshGaugeHover();
        if (!value) Dispatcher.BeginInvoke(RefreshGaugeHover, System.Windows.Threading.DispatcherPriority.Loaded); // the scan state is set just after this call
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
        StatusTitle.Visibility = Visibility.Visible; // the status line stays under the gauge in every state (while cleaning the animation lives inside the gauge)
        // Once a scan has completed with results the Clean button stays on screen: dimmed while nothing is selected, so it never "disappears".
        bool showClean = CanClean && SelectedCount > 0;
        bool offerClean = CanClean && items.Count > 0;
        bool revealClean = offerClean && HomeCleanButton.Visibility != Visibility.Visible;
        HomeCleanButton.IsEnabled = showClean;
        HomeCleanButton.Visibility = offerClean ? Visibility.Visible : Visibility.Collapsed;
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
        foreach (var category in Categories) category.Replace(byCategory.TryGetValue(category.Id, out var list) ? ReviewCategory.OrderBySizeAndLocation(list) : []);
        foreach (var category in Categories) category.Expanded = expanded.Contains(category.Id) && category.Files.Count > 0;
        CollectionViewSource.GetDefaultView(Categories).Refresh();
    }

    private void CategoryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ReviewCategory.Expanded)) UpdateSectionToggle();
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        bool expand = !VisibleCategories.All(category => category.Expanded);
        foreach (var category in Categories) category.Expanded = category.HasFiles && InOpenGroup(category) && expand;
        UpdateSectionToggle();
    }

    private void UpdateSectionToggle()
    {
        bool expanded = VisibleCategories.Any() && VisibleCategories.All(category => category.Expanded);
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
        // Select all covers the categories on screen (the opened group), so a group can be ticked without touching the others.
        int scopeSelected = VisibleCategories.Sum(category => category.SelectedCount), scopeTotal = VisibleCategories.Sum(category => category.Files.Count);
        SelectAllBox.IsChecked = scopeTotal == 0 || scopeSelected == 0 ? false : scopeSelected == scopeTotal ? true : null;
        bool hasItems = items.Count > 0;
        UpdateReviewGroups();
        ApplyReviewView();
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
        try { foreach (var category in VisibleCategories.ToList()) category.Selected = selected; }
        finally { batchSelection = false; }
        RefreshSelection();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ToggleTheme();
        prefs.Light = light;
        SavePreferences();
    }

    internal void ToggleTheme() { light = !light; ApplyTheme(animate: true); RefreshAccentChoices(); }

    // The accent for a theme: the person's choice (Settings > Appearance) in its Dark shade or its slightly darker Light shade.
    private string AccentColor(bool forLight) => AccentPalette.Find(prefs.Accent).For(forLight);

    private void ApplyTheme(bool animate = false)
    {
        void Set(string key, string color) => ThemePalette.Set(Application.Current.Resources, key, color, animate);
        string[] keys = ["WindowBrush", "PanelBrush", "RaisedBrush", "BorderBrush", "TextBrush", "MutedBrush", "FaintBrush", "HeadingBrush"];
        string[] colors = light
            ? ["#EFF1F5", "#F9FAFC", "#E4E8F0", "#DBE1EA", "#48566C", "#627086", "#6C798D", "#344156"]
            : ["#11151B", "#1B212B", "#252C38", "#2C3442", "#C6CFDC", "#9BA8BA", "#8794A8", "#D8DFE9"];
        for (int i = 0; i < keys.Length; i++) Set(keys[i], colors[i]);
        Set("ModalShadeBrush", light ? "#65E4EAF0" : "#A00C1016");
        Set("SafetyGreenBrush", AccentColor(light));
        Set("GoodBrush", SafetyPalette.Good(light));
        Set("CleanTextBrush", SafetyPalette.Danger(light));
        Set("CleanFillBrush", "#08FF795C");
        Set("AccentFillBrush", "#08" + AccentColor(light).TrimStart('#'));
        foreach (var category in Categories) category.SetTheme(light);
        UpdateReviewGroups();
        ThemeSunCore.Opacity = light ? 1 : 0.3;
        ThemeSunRays.Opacity = light ? 1 : 0.55;
        ThemeButton.ToolTip = light ? "Switch to dark theme" : "Switch to light theme";
        ThemeButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, light ? "Light theme. Switch to dark theme" : "Dark theme. Switch to light theme");
        // One primary accent per theme (Cyan in Dark, soft blue in Light). The gauge, accent text, glows and Safe accents all read it,
        // so they stay identical to each other; TealBrush/TealTextBrush ease with the theme like every other palette brush.
        string accent = AccentColor(light);
        Application.Current.Resources["PrimaryAccentColor"] = (Color)ColorConverter.ConvertFromString(accent);
        Set("TealBrush", accent);
        Set("TealTextBrush", accent);
        Set("AccentHoverBrush", AccentPalette.Find(prefs.Accent).HoverFor(light));
        Set("MeterWarnBrush", SafetyPalette.Text(SafetyLevel.Review, light));
        Set("ActionFillBrush", light ? "#E4E8F0" : "#252C38");
        if (!busy) ApplyPrimaryAppearance(CanClean && items.Count > 0);
        // The letter glows are pictures in the main color: draw them again for the new color (only when they are on screen do they actually draw).
        WordmarkGlow?.Refresh(); ProGlow?.Refresh();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (busy || AboutOverlay.Visibility == Visibility.Visible) return;
        ClearUpdateSession();
        AboutTerms.Text = Terms.Notice;
        confirmationReturnFocus = Keyboard.FocusedElement;
        AppSurface.IsEnabled = false;
        SetBackdropBlur(7);
        AboutOverlay.Visibility = Visibility.Visible;
        PatchNotesHeading.Text = "What's new in this version";
        PatchNotes.Text = ReleaseNotes.Current;
        SetUpdateStatus("");
        if (knownRelease != null) { availableRelease = knownRelease; ShowAvailable(knownRelease); SetUpdateBusy(false); } // the start-up check already found one: say so at once
        Motion.Reveal(AboutOverlay);
        CloseAboutButton.Focus();
    }

    // ----- The quiet update notice beside the wordmark (0.9) -----
    // One silent check at start (anonymous, nothing downloaded). Only a newer version shows the green line; up to date, offline or any failure shows nothing.
    internal async Task RunQuietUpdateCheck()
    {
        try
        {
            using var service = updateFactory();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var release = await Task.Run(() => service.CheckAsync(timeout.Token), timeout.Token);
            knownRelease = release;
            if (release == null || closing) return;
            UpdateNoticeButton.Visibility = Visibility.Visible;
            UpdateNoticeButton.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 320));
        }
        catch { /* offline or unavailable: nothing to say */ }
    }

    // Clicking the notice opens the update popup and runs the check there (with the filling bar); nothing downloads before the person agrees.
    private void UpdateNotice_Click(object sender, RoutedEventArgs e)
    {
        if (busy || AboutOverlay.Visibility == Visibility.Visible) return;
        About_Click(sender, e);
        if (AboutOverlay.Visibility == Visibility.Visible && knownRelease == null) CheckUpdate_Click(CheckUpdateButton, e);
    }

    private async void CloseAbout_Click(object sender, RoutedEventArgs e) => await HideAbout();

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
        UpdateStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; // nothing to say yet: the row is just the version and the action
        UpdateStatus.SetResourceReference(TextBlock.ForegroundProperty, good ? "GoodBrush" : notice ? "TealTextBrush" : "TextBrush");
        UpdateStatus.FontWeight = FontWeights.Medium; // Medium (500) for every state, never bold; the color carries the meaning
    }

    // The newest release the start-up check (or a manual check) found; the update popup announces it the moment it opens.
    private AppRelease? knownRelease;

    // A newer version: green text beside the version, what Update now does, and what is new in it.
    private void ShowAvailable(AppRelease release)
    {
        SetUpdateStatus($"Version {UpdateService.Display(release.Version)} is available.", good: true);
        UpdateHint.Text = "Update now downloads it securely, checks it and restarts Hush to finish. Your current version is kept as a backup.";
        UpdateHint.Visibility = Visibility.Visible;
        UpdateHint.BeginAnimation(OpacityProperty, Motion.Tween(0, 1));
        PatchNotesHeading.Text = $"What's new in {UpdateService.Display(release.Version)}";
        PatchNotes.Text = ReleaseNotes.InBrief(release.Notes);
        PatchNotesPanel.BeginAnimation(OpacityProperty, Motion.Tween(0, 1));
    }

    private bool IsCurrentAbout(int session) => session == aboutSession && AboutOverlay.Visibility == Visibility.Visible;

    private void SetUpdateBusy(bool value)
    {
        updateBusy = value;
        if (value) UpdateSweep.Progress = double.NaN; // Indeterminate until a download reports real progress.
        UpdateSweep.IsRunning = value;
        CancelUpdateButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = !value;
        CheckUpdateButton.Content = availableRelease == null ? "Check for updates" : "Update now";
        CheckUpdateButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, availableRelease == null ? "Check for updates" : "Update now: download and install the update");
    }

    private async Task HideAbout()
    {
        ClearUpdateSession();
        AboutOverlay.IsEnabled = false;
        SetBackdropBlur(0);
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
                UpdateSweep.FillOver(MinimumUpdateCheck < TimeSpan.FromMilliseconds(3500) ? TimeSpan.FromMilliseconds(3500) : MinimumUpdateCheck); // no real progress exists: the bar fills over the minimum check time and completes with the answer
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
                await UpdateSweep.CompleteAsync(); // the answer is really in: the bar arrives at full
                if (!IsCurrentAbout(session) || operation.IsCancellationRequested) return;
                availableRelease = release;
                if (release == null) knownRelease = null; // a fresh check says it is up to date
                if (release == null) SetUpdateStatus("Updated to latest version.", good: true);
                else
                {
                    ShowAvailable(release);
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
                UpdateSweep.Progress = 1;
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
    // How long a close request waits for a running scan/clean to stop, or for a Windows change to finish, before Hush closes anyway (0.9 audit: it used
    // to wait without limit, so one stuck file or Windows tool made the window impossible to close). Every clean writes its intent to the audit log in
    // batches before it acts, and a Windows change saves its originals first, so closing after the wait never loses the way back.
    internal static int CloseGraceMilliseconds = 8000, WindowsChangeCloseGraceMilliseconds = 30000;
    private bool closeRequested;

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        ClearUpdateSession();
        StopBoostWork();
        if (closing || (!busy && !windowsSettingsBusy)) return;
        e.Cancel = true;
        if (closeRequested) return; // already waiting: a second click on the close button changes nothing
        closeRequested = true;
        bool windowsChange = windowsSettingsBusy && !busy;
        cancellation?.Cancel();
        StatusTitle.Text = windowsChange ? "Finishing the Windows change before closing…" : "Stopping safely before closing…";
        var waited = Stopwatch.StartNew();
        int limit = windowsChange ? WindowsChangeCloseGraceMilliseconds : CloseGraceMilliseconds;
        while ((busy || windowsSettingsBusy) && waited.ElapsedMilliseconds < limit) await Task.Delay(50);
        closing = true;
        Close();
    }

}
