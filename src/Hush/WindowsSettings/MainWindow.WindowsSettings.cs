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
    internal IReadOnlyList<AdjustmentChoice> AdjustmentChoices => adjustmentChoices;

    private void LoadWindowsSettings()
    {
        try
        {
            var state = windowsSettings.Read();
            adjustmentChoices = state.Settings.Select(setting => new AdjustmentChoice(setting)).ToList();
            foreach (var choice in adjustmentChoices) choice.PropertyChanged += (_, _) => UpdateWindowsChanges();
            PrivacyAdjustments.ItemsSource = adjustmentChoices.Where(choice => !choice.Advanced);
            AdvancedAdjustments.ItemsSource = adjustmentChoices.Where(choice => choice.Advanced);
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
    private void UpdateWindowsChanges()
    {
        int count = adjustmentChoices.Count(choice => choice.CanEdit && choice.Selected != choice.Baseline.Enabled);
        ApplyWindowsSettingsButton.IsEnabled = count > 0;
        ApplyWindowsSettingsButton.Content = count == 0 ? "Review changes" : $"Review {count} change{(count == 1 ? "" : "s")}";
        PrivacySummary.Text = Describe(adjustmentChoices.Where(choice => !choice.Advanced));
        ImpactSummary.Text = Describe(adjustmentChoices.Where(choice => choice.Advanced));
    }
    // The collapsed section header summarizes its switches: how many are on, what waits for review, and whether they are read-only here.
    private static string Describe(IEnumerable<AdjustmentChoice> group)
    {
        var choices = group.ToList();
        if (choices.Count == 0) return "Unavailable right now";
        string text = $"{choices.Count(choice => choice.Selected)} of {choices.Count} turned on";
        int pending = choices.Count(choice => choice.CanEdit && choice.Selected != choice.Baseline.Enabled);
        if (pending > 0) text += $" · {pending} waiting for review";
        else if (choices.All(choice => !choice.CanEdit)) text += " · read-only on this PC";
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
    private void ShowWindowsSettingsConfirmation(AdjustmentPlan plan)
    {
        if (windowsSettingsBusy || WindowsSettingsOverlay.Visibility == Visibility.Visible) return;
        windowsPlan = plan;
        WindowsConfirmTitle.Text = plan.Undo ? "Undo Windows changes?" : "Apply Windows changes?";
        ConfirmWindowsSettingsButton.Content = plan.Undo ? "Undo changes" : "Apply changes";
        WindowsConfirmSummary.Text = string.Join("\n\n", plan.Changes.Select(change =>
        {
            var setting = WindowsSettingsService.Catalog.Single(item => item.Id == change.Id);
            return (change.Target.Exists ? "Apply: " : "Restore previous: ") + setting.Title + "\n" + setting.Description;
        }));
        WindowsConfirmDetail.Text = plan.Undo
            ? "Restore only Hush's tracked policies to their saved prior state, including removing a value that was originally absent. Other tools' changed values will block restoration."
            : "Only these Windows policies will change. Original values are saved before writing; you can restore them with Undo changes. Existing external policies are left alone.";
        WindowsConfirmDetail.Text += "\n\nWindows may show these options as managed. Sign out or restart later if needed; Hush will not do so automatically. File cleanup is separate.";
        confirmationReturnFocus = Keyboard.FocusedElement;
        AppSurface.IsEnabled = false;
        BackgroundBlur.BeginAnimation(BlurEffect.RadiusProperty, Motion.Tween(BackgroundBlur.Radius, 7, 280));
        WindowsSettingsOverlay.Visibility = Visibility.Visible;
        Motion.Reveal(WindowsSettingsOverlay);
        CancelWindowsSettingsButton.Focus();
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
        try
        {
            await Task.Run(() => windowsSettings.Execute(plan));
            outcome = plan.Undo ? "Tracked Windows values restored and verified. Sign out or restart later if needed."
                : "Windows policy values saved and verified. Sign out or restart later if needed. Undo changes is available.";
        }
        catch (Exception)
        { outcome = "Windows changes could not be completed. Review the refreshed state; any saved history is kept for recovery."; }
        finally
        {
            windowsSettingsBusy = false;
            ConfirmWindowsSettingsButton.IsEnabled = CancelWindowsSettingsButton.IsEnabled = true;
        }
        await HideWindowsSettingsConfirmation();
        LoadWindowsSettings();
        WindowsSettingsStatus.Text = outcome;
    }
}
