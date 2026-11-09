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
        // Let go of the one-at-a-time lock first so the elevated copy can take it; it comes back if the person declines Windows' permission prompt.
        return App.HandOver(() => AdminMode.Elevation.Launch(self, arguments, false, out _));
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
    private System.Windows.Data.ListCollectionView Grouped(IEnumerable<AdjustmentChoice> rows)
    {
        var view = new System.Windows.Data.ListCollectionView(rows.ToList());
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(AdjustmentChoice.Topic)));
        view.Filter = SearchFilter();
        return view;
    }

    // ----- Compact Settings: collapsible topics, search, and the Actions menu -----
    private readonly HashSet<string> openTopics = new(StringComparer.OrdinalIgnoreCase);
    private bool topicsProgrammatic;
    private string settingsFilter = "";

    private Predicate<object>? SearchFilter()
    {
        string text = settingsFilter;
        if (text.Length == 0) return null;
        return row => row is AdjustmentChoice choice && (choice.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
            || choice.Description.Contains(text, StringComparison.OrdinalIgnoreCase) || choice.Topic.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private void Topic_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { DataContext: System.Windows.Data.CollectionViewGroup group } toggle) return;
        topicsProgrammatic = true;
        try { toggle.IsChecked = settingsFilter.Length > 0 || openTopics.Contains(group.Name?.ToString() ?? ""); }
        finally { topicsProgrammatic = false; }
    }

    private void Topic_Changed(object sender, RoutedEventArgs e)
    {
        if (topicsProgrammatic || settingsFilter.Length > 0) return; // a search opens everything without changing what the person left open
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { DataContext: System.Windows.Data.CollectionViewGroup group } toggle) return;
        string name = group.Name?.ToString() ?? "";
        if (toggle.IsChecked == true) openTopics.Add(name); else openTopics.Remove(name);
    }

    // A card is always opened with every sub-category inside it closed (0.9, user direction): closing a card forgets which of its topics were open, so opening it
    // again never shows anything pre-opened. (A search still opens everything while it is typed, and is not remembered.)
    private void TopicCard_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Expander card || !ReferenceEquals(e.OriginalSource, card) || settingsFilter.Length > 0) return;
        topicsProgrammatic = true;
        try
        {
            foreach (var toggle in TopicToggles(card))
            {
                if (toggle.DataContext is System.Windows.Data.CollectionViewGroup group) openTopics.Remove(group.Name?.ToString() ?? "");
                toggle.IsChecked = false;
            }
        }
        finally { topicsProgrammatic = false; }
    }

    private IEnumerable<System.Windows.Controls.Primitives.ToggleButton> TopicToggles(DependencyObject? within = null)
    {
        var found = new List<System.Windows.Controls.Primitives.ToggleButton>();
        void Walk(DependencyObject parent)
        {
            for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
                if (child is System.Windows.Controls.Primitives.ToggleButton { Uid: "topic" } toggle) found.Add(toggle);
                Walk(child);
            }
        }
        if (within != null) Walk(within); else { Walk(PrivacyAdjustments); Walk(AdvancedAdjustments); }
        return found;
    }

    // Opens or closes every topic (a search opens all; tests use it too). Opening for good is remembered like a click.
    internal void SetAllTopicsExpanded(bool expanded)
    {
        foreach (var toggle in TopicToggles()) toggle.IsChecked = expanded;
    }

    private void SettingsSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        settingsFilter = SettingsSearch.Text.Trim();
        foreach (var items in new[] { PrivacyAdjustments, AdvancedAdjustments })
            if (items.ItemsSource is System.Windows.Data.ListCollectionView view) view.Filter = SearchFilter();
        if (settingsFilter.Length > 0) { SettingsPrivacySection.IsExpanded = true; SettingsImpactSection.IsExpanded = true; }
        // Groups that already existed keep their state; open them all while searching and restore what the person had open afterwards.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            topicsProgrammatic = true;
            try { foreach (var toggle in TopicToggles()) toggle.IsChecked = settingsFilter.Length > 0 || (toggle.DataContext is System.Windows.Data.CollectionViewGroup group && openTopics.Contains(group.Name?.ToString() ?? "")); }
            finally { topicsProgrammatic = false; }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // The Actions button is a plain toggle (0.9 audit). The menu used to close itself (StaysOpen=False) when the mouse went down outside it, which includes the
    // Actions button, and then guess from timing whether the click that followed was "the same press": with a real mouse that guess failed and a click on the
    // button never closed the menu. Now the popup stays open until Hush closes it: the button toggles, and a press anywhere else in the window, the window
    // losing focus, Escape or choosing an action closes it (the chevron follows IsOpen both ways).
    private void Actions_Click(object sender, RoutedEventArgs e) => ActionsPopup.IsOpen = !ActionsPopup.IsOpen;

    // The menu closes when Hush loses focus. Test windows switch this off: they are never activated on purpose, and a person using another program while the
    // suite runs would otherwise close the menu under the test (one full run failed that way, three isolated runs passed).
    internal static bool CloseActionsOnDeactivate = true;

    private void CloseActionsOnOutsidePress(object sender, MouseButtonEventArgs e)
    {
        if (!ActionsPopup.IsOpen) return;
        if (e.OriginalSource is DependencyObject source && (IsWithin(source, ActionsButton) || (ActionsPopup.Child is { } menu && IsWithin(source, menu)))) return;
        ActionsPopup.IsOpen = false;
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node != null; node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    private enum BulkLevel { Recommended, Somewhat, All }

    // Switches on every choice of a level and opens the usual review (nothing is written until it is confirmed). The first two skip choices that only
    // take effect on Enterprise/Education while this PC runs another edition; "all" includes them.
    private void StageBulk(BulkLevel level)
    {
        ActionsPopup.IsOpen = false;
        applyAllStaged = level == BulkLevel.All && !updateSwitch.IsOff; // only "all" offers Windows Update, and only with its own checkbox in the confirmation
        foreach (var choice in adjustmentChoices)
        {
            if (!choice.CanEdit) continue;
            bool include = level switch { BulkLevel.Recommended => !choice.Advanced && choice.TakesEffect, BulkLevel.Somewhat => choice.TakesEffect, _ => true };
            if (include) choice.Selected = true;
        }
        if (adjustmentChoices.Count(choice => choice.CanEdit && choice.Selected != choice.Baseline.Enabled) == 0)
        { WindowsSettingsStatus.Text = "Nothing to change: those settings are already on."; applyAllStaged = false; return; }
        ApplyWindowsSettings_Click(this, new RoutedEventArgs());
    }
    private void ActionRecommended_Click(object sender, RoutedEventArgs e) => StageBulk(BulkLevel.Recommended);
    private void ActionSomewhat_Click(object sender, RoutedEventArgs e) => StageBulk(BulkLevel.Somewhat);
    private void ActionAll_Click(object sender, RoutedEventArgs e) => StageBulk(BulkLevel.All);
    private void ActionUndo_Click(object sender, RoutedEventArgs e)
    {
        ActionsPopup.IsOpen = false;
        if (UndoWindowsSettingsButton.IsEnabled) UndoWindowsSettings_Click(sender, e);
        else if (updateSwitch.IsOff) ShowUpdateConfirmation(UpdateAction.TurnOn); // only Windows Update was changed: undo that
        else WindowsSettingsStatus.Text = "Nothing to undo: Hush has not changed any Windows setting.";
    }
    private void UpdateWindowsChanges()
    {
        int count = adjustmentChoices.Count(choice => choice.CanEdit && choice.Selected != choice.Baseline.Enabled);
        ApplyWindowsSettingsButton.IsEnabled = count > 0;
        ApplyWindowsSettingsButton.Content = count == 0 ? "Review Windows changes" : $"Review {count} Windows change{(count == 1 ? "" : "s")}";
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
    // Reads the Windows choices (and the Windows Update state) again and SAYS so: the lists usually look the same, so without a word it seemed to do nothing.
    private void RefreshWindowsSettings_Click(object sender, RoutedEventArgs e)
    {
        LoadWindowsSettings();
        RefreshWindowsUpdateState();
        string stamp = $"Refreshed at {DateTime.Now:HH:mm:ss}: {adjustmentChoices.Count} Windows choices read again.";
        WindowsSettingsStatus.Text = string.IsNullOrWhiteSpace(WindowsSettingsStatus.Text) ? stamp : stamp + " " + WindowsSettingsStatus.Text;
    }
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
        // Before applying (not undoing) the popup offers a restore point; "Apply all settings" also offers its own checkbox for turning Windows Update off.
        PrepareWindowsOptions(restore: !plan.Undo, alsoUpdate: applyAllStaged && !plan.Undo, consent: false);
        if (!plan.Undo) RefreshRestoreOptions(restore: true, alsoUpdate: applyAllStaged, consent: false);
        if (plan.Undo && updateSwitch.IsOff)
        {
            undoAlsoUpdate = true;
            WindowsConfirmDetail.Text += "\n\nWindows Update is off because of Hush, so it will also be turned back on.";
        }
        OpenWindowsOverlay();
    }

    private void OpenWindowsOverlay()
    {
        confirmationReturnFocus = Keyboard.FocusedElement;
        AppSurface.IsEnabled = false;
        SetBackdropBlur(7);
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
        updateAction = UpdateAction.None; applyAllStaged = false; undoAlsoUpdate = false;
        WindowsOptions.Visibility = Visibility.Collapsed;
        WindowsSettingsOverlay.IsEnabled = false;
        SetBackdropBlur(0);
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
        if (windowsSettingsBusy || WindowsSettingsOverlay.Visibility != Visibility.Visible) return;
        if (updateAction != UpdateAction.None) { await ConfirmUpdateActionAsync(); return; }
        if (windowsPlan == null) return;
        var plan = windowsPlan;
        windowsPlan = null; // One confirmation authorizes one immutable batch only.
        bool alsoUpdateOff = AlsoUpdateOffBox.Visibility == Visibility.Visible && AlsoUpdateOffBox.IsChecked == true, alsoUpdateOn = undoAlsoUpdate && plan.Undo;
        windowsSettingsBusy = true;
        ConfirmWindowsSettingsButton.IsEnabled = CancelWindowsSettingsButton.IsEnabled = false;
        string outcome, extra = "";
        bool applied = false, keepStaged = false;
        try
        {
            if (windowsPlanNeedsAdministrator)
            {
                // Nothing is written here. The administrator copy of Hush re-stages the same choices and asks for this confirmation again.
                string arguments = plan.Undo ? "--windows-undo"
                    : "--windows-review " + string.Join(",", plan.Changes.Select(change => (change.Target.Exists ? "+" : "-") + change.Id));
                if (await Task.Run(() => ElevatedLauncher(arguments))) { windowsSettingsBusy = false; ConfirmWindowsSettingsButton.IsEnabled = CancelWindowsSettingsButton.IsEnabled = true; ExitForRestart(); return; }
                outcome = "Windows' permission prompt was declined or could not open, so nothing was changed. Your choices are still staged.";
                keepStaged = true;
            }
            else
            {
                string restoreNote = "";
                if (!plan.Undo)
                {
                    var (error, note) = await EnsureRestorePointAsync();
                    if (error != null) { outcome = error + " Nothing was changed. Untick \"Create a restore point first\" to continue without one."; keepStaged = true; goto finished; }
                    restoreNote = note;
                }
                await Task.Run(() => windowsSettings.Execute(plan));
                applied = true;
                if (alsoUpdateOff) extra = await RunUpdateSwitchAsync(turnOff: true);
                else if (alsoUpdateOn) extra = await RunUpdateSwitchAsync(turnOff: false);
                outcome = restoreNote + (plan.Undo ? "Tracked Windows values restored and verified. " : "Windows policy values saved and verified. Undo changes is available. ") + RestartAdvice(plan).Text;
            }
        }
        catch (Exception)
        { outcome = "Windows changes could not be completed. Review the refreshed state; any saved history is kept for recovery."; }
        finished:
        windowsSettingsBusy = false;
        ConfirmWindowsSettingsButton.IsEnabled = CancelWindowsSettingsButton.IsEnabled = true;
        RefreshWindowsUpdateState();
        if (applied)
        {
            LoadWindowsSettings();
            WindowsSettingsStatus.Text = outcome;
            ShowWindowsResult(plan);
            if (extra.Length > 0) WindowsConfirmDetail.Text = extra + "\n\n" + WindowsConfirmDetail.Text;
            return;
        }
        await HideWindowsSettingsConfirmation();
        if (!keepStaged) LoadWindowsSettings();
        WindowsSettingsStatus.Text = outcome;
    }
}
