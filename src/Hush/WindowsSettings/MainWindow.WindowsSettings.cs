using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Effects;

namespace Hush;

public partial class MainWindow
{
    private readonly WindowsSettingsService windowsSettings;
    private List<AdjustmentChoice> adjustmentChoices = [];
    private AdjustmentPlan? windowsPlan;
    private bool windowsSettingsBusy;
    private bool windowsPlanNeedsAdministrator;
    // Device-wide choices need administrator rights. Hush then restarts itself with Windows' own permission prompt and comes back to this review.
    // Tests replace both of these so no real permission prompt or shutdown ever happens.
    internal static Func<string, bool> ElevatedLauncher = LaunchElevated;
    internal static Action ExitForRestart = () => Application.Current.Shutdown();
    private static bool LaunchElevated(string arguments)
    {
        string? self = Environment.ProcessPath;
        if (self == null) return false;
        try { Process.Start(new ProcessStartInfo(self, arguments) { UseShellExecute = true, Verb = "runas" }); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; } // the person declined Windows' permission prompt
    }
    internal IReadOnlyList<AdjustmentChoice> AdjustmentChoices => adjustmentChoices;

    private void LoadWindowsSettings()
    {
        try
        {
            var state = windowsSettings.Read();
            adjustmentChoices = state.Settings.Select(setting => new AdjustmentChoice(setting)).ToList();
            foreach (var choice in adjustmentChoices) choice.PropertyChanged += (_, _) => UpdateWindowsChanges();
            PrivacyAdjustments.ItemsSource = Grouped(adjustmentChoices.Where(choice => !choice.Advanced));
            AdvancedAdjustments.ItemsSource = Grouped(adjustmentChoices.Where(choice => choice.Advanced));
            WindowsSettingsSummary.Text = state.Summary;
            UndoWindowsSettingsButton.IsEnabled = state.CanUndo;
            WindowsSettingsStatus.Text = state.UndoHint;
            UpdateWindowsChanges();
        }
        catch
        {
            adjustmentChoices = [];
            PrivacyAdjustments.ItemsSource = AdvancedAdjustments.ItemsSource = null;
            WindowsSettingsSummary.Text = "Windows settings could not be read safely. Changes are disabled.";
            WindowsSettingsStatus.Text = "Check access and saved history, then Refresh. Existing history is kept for recovery.";
            UndoWindowsSettingsButton.IsEnabled = ApplyWindowsSettingsButton.IsEnabled = false;
            PrivacySummary.Text = ImpactSummary.Text = "Unavailable right now";
        }
    }
    // Rows are grouped under small topic headings (Telemetry & diagnostics, Search & Cortana, Microsoft Edge ...), in catalog order.
    private static System.ComponentModel.ICollectionView Grouped(IEnumerable<AdjustmentChoice> rows)
    {
        var view = new System.Windows.Data.ListCollectionView(rows.ToList());
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(AdjustmentChoice.Topic)));
        return view;
    }
    private void UpdateWindowsChanges()
    {
        int count = adjustmentChoices.Count(choice => choice.CanEdit && choice.Selected != choice.Baseline.Enabled);
        ApplyWindowsSettingsButton.IsEnabled = count > 0;
        ApplyWindowsSettingsButton.Content = count == 0 ? "Review changes" : $"Review {count} change{(count == 1 ? "" : "s")}";
        PrivacySummary.Text = Describe(adjustmentChoices.Where(choice => !choice.Advanced));
        ImpactSummary.Text = Describe(adjustmentChoices.Where(choice => choice.Advanced));
    }
    // The collapsed section header summarizes its switches: how many are on and what waits for review.
    private static string Describe(IEnumerable<AdjustmentChoice> group)
    {
        var choices = group.ToList();
        if (choices.Count == 0) return "Unavailable right now";
        string text = $"{choices.Count(choice => choice.Selected)} of {choices.Count} turned on";
        int pending = choices.Count(choice => choice.CanEdit && choice.Selected != choice.Baseline.Enabled);
        if (pending > 0) text += $" · {pending} waiting for review";
        return text;
    }
    private void RefreshWindowsSettings_Click(object sender, RoutedEventArgs e) => LoadWindowsSettings();
    private void ApplyWindowsSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var plan = windowsSettings.Prepare(adjustmentChoices.Select(choice => choice.Baseline).ToArray(),
                adjustmentChoices.Where(choice => choice.CanEdit).ToDictionary(choice => choice.Id, choice => choice.Selected));
            if (plan.Changes.Count > 0) ShowWindowsSettingsConfirmation(plan);
        }
        catch { LoadWindowsSettings(); WindowsSettingsStatus.Text = "A setting changed or is unavailable. Review the refreshed choices."; }
    }
    private void UndoWindowsSettings_Click(object sender, RoutedEventArgs e)
    {
        try { ShowWindowsSettingsConfirmation(windowsSettings.PrepareUndo()); }
        catch { LoadWindowsSettings(); WindowsSettingsStatus.Text = "Undo is unavailable: a value changed, is managed, or requires administrator access."; }
    }
    // What the person should do after the change: restart for device-wide settings, sign out and back in for per-user ones.
    private static (bool Restart, string Text) RestartAdvice(AdjustmentPlan plan)
    {
        bool deviceWide = plan.Changes.Any(change => WindowsSettingsService.Catalog.Single(item => item.Id == change.Id).Machine);
        return deviceWide
            ? (true, "Restart your PC afterwards so these changes take full effect. Save your work first; Hush never restarts Windows for you.")
            : (false, "Afterwards, sign out of Windows and back in (or restart) so your apps notice the change. Hush never does this for you.");
    }
    private void ShowWindowsSettingsConfirmation(AdjustmentPlan plan)
    {
        if (windowsSettingsBusy || WindowsSettingsOverlay.Visibility == Visibility.Visible) return;
        windowsPlan = plan;
        windowsPlanNeedsAdministrator = windowsSettings.NeedsAdministrator(plan);
        WindowsConfirmTitle.Text = plan.Undo ? "Undo Windows changes?" : "Apply Windows changes?";
        ConfirmWindowsSettingsButton.Content = windowsPlanNeedsAdministrator ? "Restart as administrator" : plan.Undo ? "Undo changes" : "Apply changes";
        ConfirmWindowsSettingsButton.Visibility = Visibility.Visible;
        CancelWindowsSettingsButton.Content = "Cancel";
        WindowsConfirmSummary.Text = string.Join("\n\n", plan.Changes.Select(change =>
        {
            var setting = WindowsSettingsService.Catalog.Single(item => item.Id == change.Id);
            return (plan.Undo ? "Restore previous: " : "Apply: ") + setting.Title + "\n" + setting.Description;
        }));
        WindowsConfirmDetail.Text = plan.Undo
            ? "Hush puts back only the values it saved before it changed them. If another tool changed a value afterwards, Undo stops instead of overwriting it."
            : "These are Windows policy values, the same ones an administrator sets in Group Policy. Hush saves the current values first, so Undo changes can put them back. Your files and programs are not touched.";
        WindowsConfirmNote.Text = windowsPlanNeedsAdministrator
            ? "These changes need administrator rights. Choose Restart as administrator: Windows shows its own permission prompt, Hush reopens on this page and asks you to confirm once more. Nothing has been changed yet, and scan results on screen will be cleared."
            : RestartAdvice(plan).Text;
        WindowsConfirmNote.Visibility = Visibility.Visible;
        confirmationReturnFocus = Keyboard.FocusedElement;
        AppSurface.IsEnabled = false;
        BackgroundBlur.BeginAnimation(BlurEffect.RadiusProperty, Motion.Tween(BackgroundBlur.Radius, 7, 280));
        WindowsSettingsOverlay.Visibility = Visibility.Visible;
        Motion.Reveal(WindowsSettingsOverlay);
        CancelWindowsSettingsButton.Focus();
    }
    // After a successful change the same popup tells what happened and whether to restart, so the advice cannot be missed in a status line.
    private void ShowWindowsResult(AdjustmentPlan plan)
    {
        var advice = RestartAdvice(plan);
        WindowsConfirmTitle.Text = plan.Undo ? "Windows changes undone" : "Windows changes applied";
        WindowsConfirmSummary.Text = string.Join("\n", plan.Changes.Select(change =>
        {
            var setting = WindowsSettingsService.Catalog.Single(item => item.Id == change.Id);
            return (plan.Undo ? "Restored: " : "Applied: ") + setting.Title;
        }));
        WindowsConfirmDetail.Text = "Hush checked each value after writing it. Undo changes can put things back whenever you like.";
        WindowsConfirmNote.Text = advice.Text;
        WindowsConfirmNote.Visibility = Visibility.Visible;
        ConfirmWindowsSettingsButton.Visibility = Visibility.Collapsed;
        CancelWindowsSettingsButton.Content = "Close";
        CancelWindowsSettingsButton.Focus();
    }
    // Reached only from Hush's own administrator restart (App.xaml.cs): it stages the choices and opens the normal confirmation. Ids are checked
    // against the fixed catalog by the rows themselves, and the person still has to confirm.
    internal void ResumeWindowsReview(string[] args)
    {
        if (busy) return;
        Settings_Click(this, new RoutedEventArgs());
        SettingsPrivacySection.IsExpanded = SettingsImpactSection.IsExpanded = true;
        if (args is ["--windows-undo"]) { UndoWindowsSettings_Click(this, new RoutedEventArgs()); return; }
        if (args is not ["--windows-review", var list]) return;
        foreach (string token in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 2 || token[0] is not ('+' or '-')) continue;
            var choice = adjustmentChoices.FirstOrDefault(item => item.Id == token[1..] && item.CanEdit);
            if (choice != null) choice.Selected = token[0] == '+';
        }
        ApplyWindowsSettings_Click(this, new RoutedEventArgs());
    }
    private async Task HideWindowsSettingsConfirmation()
    {
        windowsPlan = null;
        WindowsSettingsOverlay.IsEnabled = false;
        BackgroundBlur.BeginAnimation(BlurEffect.RadiusProperty, Motion.Tween(BackgroundBlur.Radius, 0, 160));
        await Motion.FadeOut(WindowsSettingsOverlay);
        WindowsSettingsOverlay.Visibility = Visibility.Collapsed;
        WindowsSettingsOverlay.IsEnabled = true;
        AppSurface.IsEnabled = true;
        if (confirmationReturnFocus != null) Keyboard.Focus(confirmationReturnFocus); else SettingsBackButton.Focus();
    }
    private async void CancelWindowsSettings_Click(object sender, RoutedEventArgs e)
    { if (!windowsSettingsBusy) await HideWindowsSettingsConfirmation(); }
    private async void ConfirmWindowsSettings_Click(object sender, RoutedEventArgs e)
    {
        if (windowsSettingsBusy || windowsPlan == null || WindowsSettingsOverlay.Visibility != Visibility.Visible) return;
        var plan = windowsPlan;
        windowsPlan = null; // One confirmation authorizes one immutable batch only.
        windowsSettingsBusy = true;
        ConfirmWindowsSettingsButton.IsEnabled = CancelWindowsSettingsButton.IsEnabled = false;
        string outcome;
        bool applied = false, keepStaged = false;
        try
        {
            if (windowsPlanNeedsAdministrator)
            {
                // Nothing is written here. The administrator copy of Hush re-stages the same choices and asks for this confirmation again.
                string arguments = plan.Undo ? "--windows-undo"
                    : "--windows-review " + string.Join(",", plan.Changes.Select(change => (change.Target.Exists ? "+" : "-") + change.Id));
                if (await Task.Run(() => ElevatedLauncher(arguments))) { ExitForRestart(); return; }
                outcome = "Windows' permission prompt was declined or could not open, so nothing was changed. Your choices are still staged.";
                keepStaged = true;
            }
            else
            {
                await Task.Run(() => windowsSettings.Execute(plan));
                applied = true;
                outcome = (plan.Undo ? "Tracked Windows values restored and verified. " : "Windows policy values saved and verified. Undo changes is available. ") + RestartAdvice(plan).Text;
            }
        }
        catch (Exception)
        { outcome = "Windows changes could not be completed. Review the refreshed state; any saved history is kept for recovery."; }
        finally
        {
            windowsSettingsBusy = false;
            ConfirmWindowsSettingsButton.IsEnabled = CancelWindowsSettingsButton.IsEnabled = true;
        }
        if (applied)
        {
            LoadWindowsSettings();
            WindowsSettingsStatus.Text = outcome;
            ShowWindowsResult(plan);
            return;
        }
        await HideWindowsSettingsConfirmation();
        if (!keepStaged) LoadWindowsSettings();
        WindowsSettingsStatus.Text = outcome;
    }
}
