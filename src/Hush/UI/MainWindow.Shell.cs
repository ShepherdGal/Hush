using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hush;

// The Frost + Prism shell (Phase D, owner, November 19, 2026; the design notes "Next look", the plan step 5): five tabs in the header instead of
// the old icons and Back buttons. Home, Clean (the Review page), Tools, Tweaks and Settings; Simple mode shows Home and Settings. Until the Tweaks page is built
// (owner's choice) the Tweaks tab opens today's Settings page at its Privacy card. One accent "selected" pill moves under the chosen tab by a transform
// (280 ms ease-in-out); from the keyboard (Ctrl+1 to Ctrl+5) it moves at once.
public sealed partial class MainWindow
{
    internal const double TabWidth = 76, ModeSegmentWidth = 62, CompactModeSegmentWidth = 54, CompactHeaderBelow = 880, ThemeThumbTravel = 30;

    // Below 880 wide the mode pill is compact (owner's spec: the tabs stay exactly centered and the left group never touches them at 800 wide).
    private bool compactHeader;
    private double ModeSegmentNow => compactHeader ? CompactModeSegmentWidth : ModeSegmentWidth;

    private void HeaderBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width + HeaderBar.Margin.Left + HeaderBar.Margin.Right < CompactHeaderBelow;
        if (compact == compactHeader && e.PreviousSize.Width > 0) return;
        compactHeader = compact;
        SimpleModeButton.Width = ProModeButton.Width = ModeThumb.Width = ModeSegmentNow;
        ModePill.Margin = new Thickness(compact ? 12 : 18, 0, 0, 0);
        MoveModeThumb(animate: false);
    }

    internal string ActiveTab { get; private set; } = "Home";
    private bool keyboardNavigation;

    private Button[] TabButtons => [HomeTabButton, CleanTabButton, ToolsButton, TweaksTabButton, SettingsButton];
    private static readonly string[] TabNames = ["Home", "Clean", "Tools", "Tweaks", "Settings"];

    // Marks the chosen tab (accent label; no text glow, owner, November 19, 2026: glows made text look blurry) and moves the pill under it. A tab that is hidden
    // (Simple mode) shows no pill at all.
    internal void SelectTab(string name, bool? animate = null)
    {
        ActiveTab = name;
        var tabs = TabButtons;
        int visibleIndex = -1, position = 0;
        for (int index = 0; index < tabs.Length; index++)
        {
            bool chosen = TabNames[index] == name;
            tabs[index].Tag = chosen ? "On" : "Off";
            if (tabs[index].Visibility != Visibility.Visible) continue;
            if (chosen) visibleIndex = position;
            position++;
        }
        TabPill.Visibility = visibleIndex >= 0 ? Visibility.Visible : Visibility.Hidden;
        if (visibleIndex >= 0) Motion.Glide(TabPillShift, visibleIndex * TabWidth, animate ?? (!keyboardNavigation && IsLoaded));
    }

    // Leaving Settings stops what Settings reads live (as its old Back button did).
    private void LeaveSettings()
    {
        if (SettingsPage.Visibility != Visibility.Visible) return;
        settingsVisit++;
        StopResourceIndicators();
    }

    private void HomeTab_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        LeaveSettings();
        ShowReviewPage(false);
    }

    private void CleanTab_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !IsProMode) return;
        LeaveSettings();
        ShowReviewPage(true);
    }

    private void TweaksTab_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !IsProMode) return;
        OpenTweaksPage("Overview");   // the header tab always leads to the Overview, like Tools
    }

    // Settings opens at the top, or at one of its own sections.
    private void ScrollSettingsTo(bool tweaks, FrameworkElement? section = null)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!tweaks && section == null) { WindowsSettingsScroll.ScrollToVerticalOffset(0); return; }
            if (section == null) return;
            var target = section;
            if (!target.IsVisible) return;
            var top = target.TranslatePoint(new Point(0, 0), WindowsSettingsScroll).Y + WindowsSettingsScroll.VerticalOffset;
            WindowsSettingsScroll.ScrollToVerticalOffset(Math.Max(0, top - 8));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Ctrl+1 to Ctrl+5 (owner, November 19, 2026): the tabs in order; the pill jumps at once. Ignored while a popup is open.
    private bool HandleTabShortcut(KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return false;
        int index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0, Key.D2 or Key.NumPad2 => 1, Key.D3 or Key.NumPad3 => 2, Key.D4 or Key.NumPad4 => 3, Key.D5 or Key.NumPad5 => 4, _ => -1
        };
        if (index < 0 || !AppSurface.IsEnabled) return false;
        var tab = TabButtons[index];
        if (tab.Visibility != Visibility.Visible) return false;
        keyboardNavigation = Motion.InstantPageChange = true;   // from the keyboard the tab changes at once (no page fade either)
        try { tab.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
        finally { keyboardNavigation = Motion.InstantPageChange = false; }
        return true;
    }

    // The Simple | Expert pill: the accent thumb sits under the chosen half and slides (280 ms ease-in-out).
    private void MoveModeThumb(bool animate) => Motion.Glide(ModeThumbShift, prefs.ProMode ? ModeSegmentNow : 0, animate && IsLoaded);

    // The sun / full-moon slider: the thumb under the current theme's icon, that icon in the accent, the other faint.
    private void ApplyThemeSwitch(bool animate)
    {
        Motion.Glide(ThemeThumbShift, light ? 0 : ThemeThumbTravel, animate && IsLoaded);
        ThemeSunIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, light ? "TealTextBrush" : "FaintBrush");
        string moon = light ? "FaintBrush" : "TealTextBrush";
        ThemeMoonDisc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, moon);
        // Two craters off the middle (a third made the disc read as a face at 16 DIP).
        foreach (var crater in new[] { ThemeMoonCrater1, ThemeMoonCrater2 }) crater.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, moon);
    }
}
