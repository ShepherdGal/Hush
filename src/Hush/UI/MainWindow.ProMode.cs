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
        // Phase D (owner, November 19, 2026): the person sees "Expert mode" (code names stay ProMode until the rename reaches them).
        ProModeButton.Tag = pro ? "On" : "Off";
        ProModeButton.ToolTip = pro ? "Expert mode is on." : "Expert mode is off. Turn it on to see everything Hush can do.";
        System.Windows.Automation.AutomationProperties.SetName(ProModeButton, pro ? "Expert mode, on" : "Expert mode, off");
        SimpleModeButton.Tag = pro ? "Off" : "On";
        SimpleModeButton.ToolTip = pro ? "Simple mode is off. Click to go back to the simple view." : "Simple mode is on.";
        System.Windows.Automation.AutomationProperties.SetName(SimpleModeButton, pro ? "Simple mode, off" : "Simple mode, on");
        // The chosen half sits on the accent thumb (the one "selected" look of the top bar). No text glows anywhere (owner, November 19, 2026: they looked blurry).
        MoveModeThumb(animate: true);
        // Tabs: Simple mode shows Home and Settings only.
        CleanTabButton.Visibility = TweaksTabButton.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
        StatusTiles.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;   // Home: the status tiles are Expert mode only (the This PC card stays in both)
        // Settings: only Appearance stays in Simple mode (the Info card moved to Home as This PC); the red "Windows Update is off" line stays too, whatever the mode.
        var proOnly = new UIElement[] { SettingsColumns };   // the Windows cards moved to the Tweaks page (Phase D), which Simple mode does not show
        foreach (var element in proOnly) element.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
        RefreshSettingsIndex();   // Simple mode's index shows Appearance and About only
        // Tools (owner, November 19, 2026) is Pro only: the header icon goes away in Simple mode, and so does the page if it is showing.
        ToolsButton.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
        if (!pro && ToolsPage.Visibility == Visibility.Visible) ShowReviewPage(false);
        if (!pro && TweaksPage.Visibility == Visibility.Visible) ShowReviewPage(false);
        if (!pro && ActiveTab == "Tweaks") SelectTab("Settings", animate: false);
        UpdateLayout();
        SelectTab(ActiveTab, animate: false);
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

    private void SimpleMode_Click(object sender, RoutedEventArgs e)
    {
        if (busy || BoostWorking || !IsProMode) return;
        SetProMode(false);
    }

    private async void ProMode_Click(object sender, RoutedEventArgs e)
    {
        if (busy || BoostWorking || IsProMode) return;
        bool agreed = await AskConsent("Switch to Expert mode?",
            "Expert mode shows everything Hush can do: every kind of file it can clean, plus Windows tweaks, services and scheduled tasks.\n\n"
            + "These tools are powerful. A wrong choice can make Windows or a program behave differently, so Expert mode is meant for people who know what they're changing. "
            + "If you're not sure, Simple mode is the safest place to stay.\n\n"
            + "Hush still asks before it changes anything, and you can switch back whenever you like.",
            "Switch to Expert mode", "Stay in Simple mode");
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
        if (on && SettingsPage.Visibility == Visibility.Visible) { LoadWindowsSettings(); RefreshProcesses(rebuildList: SettingsProcessSection.IsExpanded); }
    }
}
