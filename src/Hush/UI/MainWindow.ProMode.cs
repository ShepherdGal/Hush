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

    private void InitializeProMode()
    {
        if (TestProMode is bool forced) prefs.ProMode = forced;
        ApplyProMode();
    }

    private void ApplyProMode()
    {
        bool pro = prefs.ProMode;
        ProModeButton.Tag = pro ? "On" : "Off";
        ProModeButton.ToolTip = pro ? "Pro mode is on. Click to go back to Simple mode." : "Pro mode is off. Turn it on to see everything Hush can do.";
        System.Windows.Automation.AutomationProperties.SetName(ProModeButton, pro ? "Pro mode, on" : "Pro mode, off");
        // A tight glow around each letter of "Pro" and of the Hush wordmark while Pro is on (a picture drawn once, see LetterGlow; both are hidden in Simple mode).
        ProGlow.Visibility = WordmarkGlow.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
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

    private async void ProMode_Click(object sender, RoutedEventArgs e)
    {
        if (busy || BoostWorking) return;
        if (IsProMode) { SetProMode(false); return; }
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
