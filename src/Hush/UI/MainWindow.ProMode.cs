using System.Windows;
using System.Windows.Controls;

namespace Hush;

// Simple mode and Pro mode (owner, November 11, 2026). Simple is the default for everyone: scanning looks only for Safe items, Take a look shows only them, and Settings
// keeps just the Info card (CPU, memory, drive, temporary files, Boost) and Appearance (size and main color). Pro mode is the whole app as it was. The header's "Pro"
// button switches; turning Pro ON asks first (a plain, warm warning), turning it OFF needs no question. Switching only changes what is shown and what the next scan
// looks for: nothing is deleted or undone, and anything already turned on (Automatic Boost, tray, start with Windows, services, tasks) keeps working while hidden.
public sealed partial class MainWindow
{
    // Test seam only: the layout suites exercise every Settings card, so the test runner starts every window in Pro mode. The product never sets it.
    internal static bool? TestProMode;

    internal bool IsProMode => prefs.ProMode;

    // The donation link in the About window (owner, November 13, 2026). Tests replace the opener so no browser is ever started.
    internal const string SupportUrl = "https://ko-fi.com/trueshepherdmusic";
    internal static Action<string> LinkOpener = url =>
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser available: nothing to do */ }
    };

    private void Support_Click(object sender, RoutedEventArgs e) => LinkOpener(SupportUrl);

    private void InitializeProMode()
    {
        if (TestProMode is bool forced) prefs.ProMode = forced;
        ApplyProMode();
    }

    private void ApplyProMode()
    {
        bool pro = prefs.ProMode;
        ProModeButton.Tag = pro ? "On" : "Off";
        ProModeButton.ToolTip = pro ? "Pro mode is on." : "Pro mode is off. Turn it on to see everything Hush can do.";
        System.Windows.Automation.AutomationProperties.SetName(ProModeButton, pro ? "Pro mode, on" : "Pro mode, off");
        SimpleModeButton.Tag = pro ? "Off" : "On";
        SimpleModeButton.ToolTip = pro ? "Simple mode is off. Click to go back to the simple view." : "Simple mode is on.";
        System.Windows.Automation.AutomationProperties.SetName(SimpleModeButton, pro ? "Simple mode, off" : "Simple mode, on");
        MoveModeUnderline(animate: modeUnderlineReady);
        // The chosen word has no glow (owner, November 13, 2026: just the normal accent color and the line); only the Hush wordmark keeps its glow while Pro is on.
        WordmarkGlow.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
        // Settings: only the Info card (above) and Appearance stay in Simple mode; the red "Windows Update is off" line stays too, whatever the mode.
        var proOnly = new UIElement[] { SettingsColumns, SettingsServicesSection, SettingsTasksSection, SettingsPrivacySection, SettingsImpactSection, SettingsUpdateSection, SettingsFooter };
        foreach (var element in proOnly) element.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
        // Review: one Safe list in Simple mode (no groups, no chevron), the three groups in Pro mode.
        if (items.Count > 0 || openGroup != null) { if (!pro && openGroup == null) OpenReviewGroup(SafetyLevel.Safe); else ApplyReviewView(); }
    }

    // What an ordinary scan looks for: Simple mode is Safe items only (the same filter as Quick Boost, so the registry, Recycle Bin and system tools are not touched
    // either); Pro mode is the full scan. Low-resource mode applies in both.
    private ScanOptions? DefaultScanOptions()
    {
        if (IsProMode) return LowResourceMode.IsActive ? new ScanOptions(ReportIntervalMs: LowResourceMode.ReportIntervalMs, Background: true) : null;
        return new ScanOptions(SmartBoostController.IsSafeTarget, ReportIntervalMs: LowResourceMode.IsActive ? LowResourceMode.ReportIntervalMs : 40, Background: LowResourceMode.IsActive);
    }

    // The line under the chosen word. It is one pre-built rectangle moved by transforms only (position and length), on WPF's render thread; the spring is an
    // ElasticEase that overshoots the target once and settles. No timer, no effect, nothing runs when the mode is not changing.
    private bool modeUnderlineReady;

    private void SimpleMode_Click(object sender, RoutedEventArgs e)
    {
        if (busy || BoostWorking || !IsProMode) return;
        SetProMode(false);
    }

    private void ProModeSlot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MoveModeUnderline(animate: false);
        modeUnderlineReady = true;
    }

    private void MoveModeUnderline(bool animate)
    {
        var target = prefs.ProMode ? ProModeButton : SimpleModeButton;
        if (target.ActualWidth <= 0 || ProModeSlot.ActualWidth <= 0) return;
        double inset = target.Padding.Left;
        double x = target.TranslatePoint(new Point(0, 0), ProModeSlot).X + inset;
        double scale = Math.Max(1, target.ActualWidth - inset - target.Padding.Right) / ModeUnderline.Width;
        if (!animate)
        {
            ModeUnderlineShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            ModeUnderlineScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            ModeUnderlineShift.X = x; ModeUnderlineScale.ScaleX = scale;
            return;
        }
        var spring = new System.Windows.Media.Animation.ElasticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Oscillations = 1, Springiness = 5 };
        var duration = TimeSpan.FromMilliseconds(620);
        ModeUnderlineShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(x, duration) { EasingFunction = spring, FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd });
        ModeUnderlineScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(scale, duration) { EasingFunction = spring, FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd });
    }

    private async void ProMode_Click(object sender, RoutedEventArgs e)
    {
        if (busy || BoostWorking || IsProMode) return;
        bool agreed = await AskConsent("Switch to Pro mode?",
            "Pro mode shows everything Hush can do: every kind of file it can clean, plus Windows tweaks, services and scheduled tasks.\n\n"
            + "These tools are powerful. A wrong choice can make Windows or a program behave differently, so Pro mode is meant for people who know what they're changing. "
            + "If you're not sure, Simple mode is the safest place to stay.\n\n"
            + "Hush still asks before it changes anything, and you can switch back whenever you like.",
            "Switch to Pro mode", "Stay in Simple mode");
        if (agreed) SetProMode(true);
    }

    internal void SetProMode(bool on)
    {
        if (prefs.ProMode == on) return;
        prefs.ProMode = on;
        SavePreferences();
        // Results from the other mode do not fit this one (a Safe-only scan and a full scan differ), so they are cleared; nothing was deleted, a new scan fills them again.
        if (items.Count > 0 || CurrentScanState != ScanState.Ready)
        {
            OpenReviewGroup(null);
            LoadReviewItems([]); CurrentScanState = ScanState.Ready; ResetGauge();
            GaugeLabel.Text = "Click to scan"; RefreshSelection(); UpdateLandingStatus();
            StatusTitle.ToolTip = null; StatusTitle.Text = "Your files stay yours."; StatusDetail.Text = "Personal files and passwords are left alone."; ResultCount.ToolTip = null;   // the home text of a fresh start
            if (ReviewPage.Visibility == Visibility.Visible) ShowReviewPage(false);
        }
        ApplyProMode();
        // Opening Pro mode while Settings is showing: read what Settings reads in Pro mode, as opening Settings would have.
        if (on && SettingsPage.Visibility == Visibility.Visible) { LoadWindowsSettings(); LoadStartupApps(); RefreshProcesses(rebuildList: SettingsProcessSection.IsExpanded); }
    }
}
