using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Hush;

// The ONE "Review changes" button at the bottom of Settings (0.9, user direction): it works with everything the person has staged on the page as a whole, the
// Windows choices, the services and the scheduled tasks, instead of every card having a Review button of its own. Only Windows changes: the usual Windows popup
// (restore point option and all). Anything else: one confirmation that lists every group, one administrator restart if one is needed, one report.
public partial class MainWindow
{
    internal const string ReviewAllArgument = "--review-all";

    private int PendingWindows => adjustmentChoices.Count(choice => choice.CanEdit && choice.Selected != choice.Baseline.Enabled);
    internal int PendingChanges => PendingWindows + serviceChoices.Count(choice => choice.Selected) + taskChoices.Count(choice => choice.Selected);

    // The button's label counts everything staged: "Review changes" while nothing is, "Review 7 changes" otherwise.
    private void UpdateReviewButton()
    {
        int count = PendingChanges;
        ApplyWindowsSettingsButton.IsEnabled = count > 0 && !servicesBusy && !tasksBusy;
        ApplyWindowsSettingsButton.Content = count == 0 ? "Review changes" : $"Review {count} change{(count == 1 ? "" : "s")}";
    }

    private sealed record ReviewPayload(List<string> W, List<string> S, List<string> T);

    private async void ReviewChanges_Click(object sender, RoutedEventArgs e)
    {
        if (busy || windowsSettingsBusy || servicesBusy || tasksBusy) return;
        var services = serviceChoices.Where(choice => choice.Selected).ToList();
        var tasks = taskChoices.Where(choice => choice.Selected).ToList();
        if (services.Count == 0 && tasks.Count == 0) { ApplyWindowsSettings_Click(sender, e); return; } // only Windows choices: the usual popup

        AdjustmentPlan? plan = null;
        if (PendingWindows > 0)
        {
            try { plan = windowsSettings.Prepare(adjustmentChoices.Select(choice => choice.Baseline).ToArray(), adjustmentChoices.Where(choice => choice.CanEdit).ToDictionary(choice => choice.Id, choice => choice.Selected)); }
            catch { LoadWindowsSettings(); WindowsSettingsStatus.Text = "A setting changed or is unavailable. Review the refreshed choices."; return; }
            if (plan.Changes.Count == 0) plan = null;
        }
        bool admin = (plan == null || !windowsSettings.NeedsAdministrator(plan)) && (services.Count == 0 || serviceTuning.CanChange) && (tasks.Count == 0 || taskTuning.CanChange);
        int total = (plan?.Changes.Count ?? 0) + services.Count + tasks.Count;

        var body = new StringBuilder();
        void Group(string heading, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            if (list.Count == 0) return;
            body.Append(heading).Append(" (").Append(list.Count).Append(")\n");
            foreach (string line in list.Take(10)) body.Append("• ").Append(line).Append('\n');
            if (list.Count > 10) body.Append("… and ").Append(list.Count - 10).Append(" more\n");
            body.Append('\n');
        }
        if (plan != null) Group("Windows settings", plan.Changes.Select(change => WindowsSettingsService.Catalog.Single(item => item.Id == change.Id).Title));
        Group("Services to turn off", services.Where(choice => !choice.WasOff).Select(choice => choice.Title));
        Group("Services to turn back on", services.Where(choice => choice.WasOff).Select(choice => choice.Title));
        Group("Scheduled tasks to turn off", tasks.Where(choice => !choice.WasOff).Select(choice => choice.Title));
        Group("Scheduled tasks to turn back on", tasks.Where(choice => choice.WasOff).Select(choice => choice.Title));
        body.Append("Nothing is uninstalled or deleted. Hush saves every original setting first, and Undo puts them back.");
        if (plan != null) body.Append(" A restore point is made first for the Windows settings.");
        if (!admin) body.Append("\n\nThis needs administrator rights. Hush restarts with Windows' own permission prompt and comes back to this page with the same choices; nothing has been changed yet.");

        bool agreed = await AskConsent($"Apply {total} change{(total == 1 ? "" : "s")}?", body.ToString().TrimEnd(), admin ? "Apply changes" : "Restart as administrator", "Cancel");
        if (!agreed) return;
        if (!admin)
        {
            var payload = new ReviewPayload(plan?.Changes.Select(change => (change.Target.Exists ? "+" : "-") + change.Id).ToList() ?? [], services.Select(choice => choice.Name).ToList(), tasks.Select(choice => choice.Path).ToList());
            string argument = ReviewAllArgument + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            if (await Task.Run(() => ElevatedLauncher(argument))) { ExitForRestart(); return; }
            WindowsSettingsStatus.Text = "Windows' permission prompt was declined or could not open, so nothing was changed. Your choices are still staged.";
            return;
        }
        await ApplyEverythingAsync(plan, services, tasks);
    }

    private async Task ApplyEverythingAsync(AdjustmentPlan? plan, List<ServiceChoice> services, List<TaskChoice> tasks)
    {
        string restoreNote = "";
        if (plan != null)
        {
            string? error = await Task.Run(() => RestorePoints.Create("Hush: before changing Windows settings"));
            if (error != null)
            {
                bool carryOn = await AskConsent("No restore point", error + "\n\nNothing has been changed yet. Continue without a restore point?", "Continue without one", "Cancel");
                if (!carryOn) return;
                restoreNote = "No restore point was made (you chose to continue without one).";
            }
            else restoreNote = "A restore point was created first.";
        }
        windowsSettingsBusy = servicesBusy = tasksBusy = true; // closing the window waits for this (with the usual limit)
        UpdateReviewButton();
        var report = new List<string>();
        string servicesText = "", tasksText = "";
        bool problems = false;
        try
        {
            if (plan != null)
            {
                try
                {
                    await Task.Run(() => windowsSettings.Execute(plan));
                    report.Add($"Windows settings: {plan.Changes.Count} applied and verified. {restoreNote}".TrimEnd());
                }
                catch (Exception) { problems = true; report.Add("Windows settings could not be completed. Review the refreshed state; any saved history is kept for recovery."); }
            }
            if (services.Count > 0) servicesText = await RunGroupAsync(report, () => ChangeGroup(services.Where(choice => !choice.WasOff).Select(choice => choice.Name).ToList(), services.Where(choice => choice.WasOff).Select(choice => choice.Name).ToList(),
                names => serviceTuning.Apply(names).Text(false), names => serviceTuning.Undo(names).Text(true)));
            if (tasks.Count > 0) tasksText = await RunGroupAsync(report, () => ChangeGroup(tasks.Where(choice => !choice.WasOff).Select(choice => choice.Path).ToList(), tasks.Where(choice => choice.WasOff).Select(choice => choice.Path).ToList(),
                names => taskTuning.Apply(names).Text(false), names => taskTuning.Undo(names).Text(true)));
        }
        finally { windowsSettingsBusy = servicesBusy = tasksBusy = false; }
        problems |= report.Any(line => line.Contains("could not", StringComparison.OrdinalIgnoreCase) || line.Contains("did not", StringComparison.OrdinalIgnoreCase));
        if (plan != null) LoadWindowsSettings();
        RefreshWindowsUpdateState();
        if (servicesText.Length > 0) { ServicesStatus.Text = servicesText; ServicesStatus.Visibility = Visibility.Visible; }
        if (tasksText.Length > 0) { TasksStatus.Text = tasksText; TasksStatus.Visibility = Visibility.Visible; }
        if (services.Count > 0 || serviceChoices.Count > 0) await RefreshServicesAsync();
        if (tasks.Count > 0 || taskChoices.Count > 0) await RefreshTasksAsync();
        UpdateReviewButton();
        WindowsSettingsStatus.Text = string.Join(" ", report);
        string advice = plan != null ? "\n\n" + RestartAdvice(plan).Text : "";
        await AskConsent(problems ? "Some changes were not made" : "Changes applied", string.Join("\n\n", report) + advice, "OK", "Close");
    }

    // Turns off the first list and turns the second list back on; each part is skipped when empty. Returns the plain report lines joined.
    private static string ChangeGroup(List<string> turnOff, List<string> turnOn, Func<List<string>, string> off, Func<List<string>, string> on)
    {
        var parts = new List<string>();
        if (turnOff.Count > 0) parts.Add(off(turnOff));
        if (turnOn.Count > 0) parts.Add(on(turnOn));
        return string.Join(" ", parts);
    }

    private static async Task<string> RunGroupAsync(List<string> report, Func<string> work)
    {
        string text;
        try { text = await Task.Run(work); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { text = ex.Message; }
        report.Add(text);
        return text;
    }

    // Reached only from Hush's own administrator restart (App.xaml.cs): stages the same choices again (each name is checked against what this copy offers)
    // and opens the same confirmation. The person confirms once more; nothing was changed before.
    internal async void ResumeReviewAll(string payload)
    {
        if (busy || payload.Length > 40000) return;
        ReviewPayload? data;
        try { data = JsonSerializer.Deserialize<ReviewPayload>(Encoding.UTF8.GetString(Convert.FromBase64String(payload))); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return; }
        if (data == null) return;
        Settings_Click(this, new RoutedEventArgs());
        await RefreshServicesAsync();
        await RefreshTasksAsync();
        foreach (string token in (data.W ?? []).Take(500))
        {
            if (token.Length < 2 || token[0] is not ('+' or '-')) continue;
            var choice = adjustmentChoices.FirstOrDefault(item => item.Id == token[1..] && item.CanEdit);
            if (choice != null) choice.Selected = token[0] == '+';
        }
        foreach (string name in (data.S ?? []).Take(500)) if (serviceChoices.FirstOrDefault(choice => choice.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } service) service.Selected = true;
        foreach (string path in (data.T ?? []).Take(500)) if (taskChoices.FirstOrDefault(choice => choice.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } task) task.Selected = true;
        ReviewChanges_Click(this, new RoutedEventArgs());
    }
}
