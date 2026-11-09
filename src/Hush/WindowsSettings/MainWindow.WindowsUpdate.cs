using System.IO;
using System.Windows;

namespace Hush;

// Windows Update (0.9): the red "Turn off Windows Update" choice, the lasting red line while it is off, and the restore point offered before big changes.
// All real work is in WindowsUpdateSwitch and IRestorePoints; this file only asks, confirms and reports.
public partial class MainWindow
{
    private readonly WindowsUpdateSwitch updateSwitch;
    // Tests replace this so no real restore point is ever created.
    internal static IRestorePoints RestorePoints = new WindowsRestorePoints();
    private enum UpdateAction { None, TurnOff, TurnOn }
    private UpdateAction updateAction;
    private bool applyAllStaged, undoAlsoUpdate;
    private RestorePointStatus? restoreStatus;

    internal bool IsWindowsUpdateOff => updateSwitch.IsOff;
    internal bool IsWindowUpdateOffForTests() => updateSwitch.IsOff;
    internal bool WindowsOptionsShown => WindowsOptions.Visibility == Visibility.Visible;

    // The lasting red line at the top of Settings and the card's own summary follow the saved history (which only exists while Hush's change is in place).
    internal void RefreshWindowsUpdateState()
    {
        bool off = updateSwitch.IsOff;
        UpdateOffBanner.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
        TurnOffUpdateButton.Content = off ? "Turn back on…" : "Turn off…";
        TurnOffUpdateButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, off ? "Turn Windows Update back on" : "Turn off Windows Update");
        UpdateSwitchSummary.Text = off ? "Off · turned off by Hush" : "On";
    }

    private void TurnOffUpdate_Click(object sender, RoutedEventArgs e) => ShowUpdateConfirmation(updateSwitch.IsOff ? UpdateAction.TurnOn : UpdateAction.TurnOff);
    private void UpdateBackOn_Click(object sender, RoutedEventArgs e) => ShowUpdateConfirmation(UpdateAction.TurnOn);
    private void WindowsConsent_Click(object sender, RoutedEventArgs e)
    {
        if (updateAction == UpdateAction.TurnOff) ConfirmWindowsSettingsButton.IsEnabled = WindowsConsentBox.IsChecked == true;
    }

    // Reached from Hush's own administrator restart: it reopens the same confirmation (the person still has to confirm).
    internal void ResumeWindowsUpdate(bool turnOff)
    {
        if (busy) return;
        Settings_Click(this, new RoutedEventArgs());
        ShowUpdateConfirmation(turnOff ? UpdateAction.TurnOff : UpdateAction.TurnOn);
    }

    private void ShowUpdateConfirmation(UpdateAction action)
    {
        if (windowsSettingsBusy || WindowsSettingsOverlay.Visibility == Visibility.Visible) return;
        bool off = action == UpdateAction.TurnOff;
        updateAction = action; windowsPlan = null; applyAllStaged = false; undoAlsoUpdate = false;
        windowsPlanNeedsAdministrator = !updateSwitch.CanChange;
        WindowsConfirmTitle.Text = off ? "Turn off Windows Update?" : "Turn Windows Update back on?";
        ConfirmWindowsSettingsButton.Content = windowsPlanNeedsAdministrator ? "Restart as administrator" : off ? "Turn off Windows Update" : "Turn it back on";
        ConfirmWindowsSettingsButton.Visibility = Visibility.Visible;
        CancelWindowsSettingsButton.Content = "Cancel";
        WindowsConfirmSummary.Text = off
            ? "Hush will turn Windows Update off completely: its policies, the update services (Windows Update, Update Orchestrator, Update Medic and Background Intelligent Transfer) and the update scheduled tasks."
            : "Hush will put back every value it saved before it turned Windows Update off, and start the update services again.";
        WindowsConfirmDetail.Text = off
            ? "Your PC stops getting security fixes and Defender updates through Windows Update until you turn this back on. Every original value is saved first and Undo puts them all back. If Windows protects one of these items and refuses the change, Hush tells you exactly what could not be changed. Your files and programs are not touched."
            : "Updates resume after the services start again. If Windows refuses to put one item back, Hush tells you exactly which.";
        WindowsConfirmNote.Text = windowsPlanNeedsAdministrator
            ? "This needs administrator rights. Choose Restart as administrator: Windows shows its own permission prompt, Hush reopens on this page and asks you to confirm once more. Nothing has been changed yet."
            : "Restart your PC afterwards so everything takes full effect. Hush never restarts Windows for you.";
        WindowsConfirmNote.Visibility = Visibility.Visible;
        PrepareWindowsOptions(restore: off, alsoUpdate: false, consent: off);
        if (off) RefreshRestoreOptions(restore: true, alsoUpdate: false, consent: true);
        ConfirmWindowsSettingsButton.IsEnabled = !off; // turning it off stays disabled until the consent box is ticked
        OpenWindowsOverlay();
    }

    // The checkboxes in the confirmation: the restore point, turning System Protection on first, Apply all's Windows Update box and the consent box.
    private void PrepareWindowsOptions(bool restore, bool alsoUpdate, bool consent)
    {
        WindowsOptions.Visibility = restore || alsoUpdate || consent ? Visibility.Visible : Visibility.Collapsed;
        RestorePointBox.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
        RestorePointNote.Visibility = Visibility.Collapsed;
        EnableProtectionBox.Visibility = Visibility.Collapsed; EnableProtectionBox.IsChecked = false;
        AlsoUpdateOffBox.Visibility = alsoUpdate ? Visibility.Visible : Visibility.Collapsed; AlsoUpdateOffBox.IsChecked = false;
        WindowsConsentBox.Visibility = consent ? Visibility.Visible : Visibility.Collapsed; WindowsConsentBox.IsChecked = false;
        RestorePointBox.IsEnabled = true; RestorePointBox.IsChecked = true;
        if (!restore) return;
        var status = restoreStatus; // null while the quiet probe has not answered: assume the usual case
        bool canCreate = status?.CanCreate ?? true, protectionOn = status?.ProtectionOn ?? true, recent = status?.RecentPoint ?? false;
        string? note = null;
        if (!canCreate)
        {
            RestorePointBox.IsChecked = false; RestorePointBox.IsEnabled = false;
            note = "Creating a restore point needs administrator mode (Settings > Administrator), so none will be made.";
        }
        else if (!protectionOn)
        {
            RestorePointBox.IsChecked = false; RestorePointBox.IsEnabled = false;
            EnableProtectionBox.Visibility = Visibility.Visible;
            note = "System Protection is off on drive C:, so Windows cannot make a restore point. Tick the box below to turn it on first, or continue without one.";
        }
        else if (recent) note = "A restore point was made in the last 24 hours. Windows allows one a day, so Hush relies on that one instead of making another.";
        if (note != null) { RestorePointNote.Text = note; RestorePointNote.Visibility = Visibility.Visible; }
    }

    // The popup opens at once from what is known; a fresh read-only probe then corrects the restore point options if System Protection or the last point changed.
    private async void RefreshRestoreOptions(bool restore, bool alsoUpdate, bool consent)
    {
        try
        {
            var fresh = await Task.Run(() => RestorePoints.Probe());
            if (fresh == restoreStatus || WindowsSettingsOverlay.Visibility != Visibility.Visible || !WindowsOptionsShown) return;
            restoreStatus = fresh;
            bool ticked = WindowsConsentBox.IsChecked == true, also = AlsoUpdateOffBox.IsChecked == true;
            PrepareWindowsOptions(restore, alsoUpdate, consent);
            WindowsConsentBox.IsChecked = ticked; AlsoUpdateOffBox.IsChecked = also;
        }
        catch { /* keep what was shown */ }
    }

    // Creates the restore point the popup offered. Returns (error, note): an error stops the change before anything is written.
    private async Task<(string? Error, string Note)> EnsureRestorePointAsync()
    {
        if (!WindowsOptionsShown || RestorePointBox.Visibility != Visibility.Visible) return (null, "");
        bool enable = EnableProtectionBox.Visibility == Visibility.Visible && EnableProtectionBox.IsChecked == true;
        bool want = RestorePointBox.IsChecked == true || enable;
        if (!want) return (null, "No restore point was made (you chose to continue without one). ");
        if (enable)
        {
            string? failure = await Task.Run(() => RestorePoints.EnableProtection());
            if (failure != null) return ("System Protection could not be turned on: " + failure + ".", "");
        }
        else if (restoreStatus?.RecentPoint == true) return (null, "A restore point from the last 24 hours is used. ");
        string? error = await Task.Run(() => RestorePoints.Create("Hush: before changing Windows settings"));
        return error == null ? (null, "A restore point was created first. ") : ("No restore point could be created: " + error + ".", "");
    }

    // Runs the switch and returns the plain report. A failure is reported, never hidden, and never claims what was not verified.
    private async Task<string> RunUpdateSwitchAsync(bool turnOff)
    {
        try
        {
            if (!updateSwitch.CanChange) return turnOff ? "Windows Update was not turned off: it needs administrator mode." : "Windows Update was not turned back on: it needs administrator mode.";
            var report = await Task.Run(() => turnOff ? updateSwitch.TurnOff() : updateSwitch.TurnBackOn());
            return (turnOff ? "Windows Update: " : "Windows Update back on: ") + report.Text(turnOff);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { return "Windows Update could not be changed: " + ex.Message; }
    }

    private async Task ConfirmUpdateActionAsync()
    {
        var action = updateAction; updateAction = UpdateAction.None;
        bool off = action == UpdateAction.TurnOff;
        windowsSettingsBusy = true;
        ConfirmWindowsSettingsButton.IsEnabled = CancelWindowsSettingsButton.IsEnabled = false;
        string message; bool done = false;
        try
        {
            if (windowsPlanNeedsAdministrator)
            {
                if (await Task.Run(() => ElevatedLauncher(off ? "--windows-update-off" : "--windows-update-on"))) { windowsSettingsBusy = false; ConfirmWindowsSettingsButton.IsEnabled = CancelWindowsSettingsButton.IsEnabled = true; ExitForRestart(); return; }
                message = "Windows' permission prompt was declined or could not open, so nothing was changed.";
            }
            else
            {
                string restoreNote = "";
                if (off)
                {
                    var (error, note) = await EnsureRestorePointAsync();
                    if (error != null)
                    {
                        // The popup STAYS OPEN: the person sees why, the restore point box is unticked, and the same button then continues without one
                        // (or Cancel leaves everything as it was). It used to close and tell them to untick a box they could no longer reach.
                        updateAction = action;
                        windowsSettingsBusy = false;
                        RestorePointBox.IsChecked = false;
                        ConfirmWindowsSettingsButton.IsEnabled = !off || WindowsConsentBox.IsChecked == true;
                        CancelWindowsSettingsButton.IsEnabled = true;
                        WindowsConfirmNote.Text = error + " Nothing was changed. The restore point box is now unticked: press the button again to continue without one, or Cancel.";
                        WindowsConfirmNote.Visibility = Visibility.Visible;
                        return;
                    }
                    restoreNote = note;
                }
                message = restoreNote + await RunUpdateSwitchAsync(off);
                done = true;
            }
        }
        catch (Exception) { message = "Windows Update could not be changed. Nothing further was done."; }
        windowsSettingsBusy = false;
        ConfirmWindowsSettingsButton.IsEnabled = CancelWindowsSettingsButton.IsEnabled = true;
        RefreshWindowsUpdateState();
        WindowsSettingsStatus.Text = done ? (off ? "Windows Update is off. Turn it back on from the red line at the top of this page." : "Windows Update is back on.") : message;
        if (!done) { await HideWindowsSettingsConfirmation(); return; }
        // The same popup tells exactly what happened.
        WindowsOptions.Visibility = Visibility.Collapsed;
        WindowsConfirmTitle.Text = off ? "Windows Update turned off" : "Windows Update turned back on";
        WindowsConfirmSummary.Text = message;
        WindowsConfirmDetail.Text = off ? "Hush saved every original value first. The red line at the top of Settings stays until you turn Windows Update back on." : "Hush put back what it saved. Updates resume once the services are running.";
        WindowsConfirmNote.Text = "Restart your PC afterwards so everything takes full effect. Hush never restarts Windows for you.";
        WindowsConfirmNote.Visibility = Visibility.Visible;
        ConfirmWindowsSettingsButton.Visibility = Visibility.Collapsed;
        CancelWindowsSettingsButton.Content = "Close";
        CancelWindowsSettingsButton.Focus();
    }
}
