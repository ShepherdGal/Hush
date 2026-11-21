using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hush;

// The Tweaks page (Phase D, owner's picks of November 20, 2026): five sub-tabs whose counts fill in from a quiet background read, one search that shows matches
// from every tab, the open tab's own Actions in the one bar, staged dots and the "Staged: …" summary, and the tab used last remembered. The test runner gives
// every window fake services, tasks and tweaks, so nothing real is read or changed.
internal static class TweaksPageChecks
{
    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        string preferences = Path.Combine(fixture, "tweaks-page-preferences.json");
        new Preferences { TermsAccepted = Terms.Version, RunAsAdmin = false, ProMode = true }.Save(preferences);
        var window = new MainWindow(engine, preferencesPath: preferences, serviceSystem: ServiceChecks.Pc(), taskSystem: TaskChecks.Pc()) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            T Find<T>(string name) where T : class => (T)window.FindName(name);
            void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Click("TweaksTabButton");
            UiLayoutChecks.PumpUntil(() => window.ServiceChoices.Count > 0 && window.TaskChoices.Count > 0 && window.TweakChoices.Count > 0);
            UiLayoutChecks.Wait(400);
            var tabs = window.TweaksTabsShown;
            // One screen (owner, November 21, 2026): the rail (Overview first) is always on the left and Tweaks opens on the Overview, whose at most three
            // suggestions come from the quiet background read; with nothing staged the bar under the card stays hidden there.
            check(Find<Grid>("TweaksPage").IsVisible && window.OpenTweaksTab == "Overview" && tabs.Select(tab => tab.Label).SequenceEqual(["Overview", "Privacy", "Windows tweaks", "Services", "Tasks", "Startup"])
                && tabs.Where(tab => tab.Key is "Privacy" or "Tweaks" or "Services" or "Tasks").All(tab => tab.Count.Length > 0) && tabs[0].Open && Find<ItemsControl>("TweaksTabs").IsVisible
                && Find<FrameworkElement>("TweaksOverview").IsVisible && window.TweakSuggestions.Count is > 0 and <= 3 && !Find<FrameworkElement>("TweaksFooterBar").IsVisible
                && !Find<Expander>("SettingsPrivacySection").IsVisible && window.MaxWidth <= 921,
                $"Tweaks opens on the Overview with the rail at the left and {window.TweakSuggestions.Count} suggestions ({string.Join("; ", window.TweakSuggestions.Select(s => s.Title))}), and no bar while nothing is staged");
            var privacyEntry = UiLayoutChecks.Descendants<Button>(Find<ItemsControl>("TweaksTabs")).Single(button => (button.Tag as TweaksTab)?.Key == "Privacy");
            privacyEntry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiLayoutChecks.Wait(200);
            check(window.OpenTweaksTab == "Privacy" && Find<Expander>("SettingsPrivacySection").IsVisible && !Find<FrameworkElement>("TweaksOverview").IsVisible && Find<FrameworkElement>("TweaksFooterBar").IsVisible,
                "A rail entry shows its cards in the card beside the rail, with the bar under it");
            // Owner, November 20, 2026: a badge says what is active out of the total, and the summary spells it out.
            var servicesTab = tabs.Single(tab => tab.Key == "Services");
            int enabled = window.ServiceChoices.Count(choice => !choice.WasOff), total = window.ServiceChoices.Count;
            check(servicesTab.Count == $"{enabled}/{total}" && servicesTab.SpokenName.StartsWith($"Services, {enabled} of {total}")
                && ((TextBlock)window.FindName("ServicesSummary")).Text.StartsWith($"{enabled} of {total} enabled")
                && tabs.Single(tab => tab.Key == "Tweaks").Count == $"{window.TweakChoices.Count(choice => choice.State.On)}/{window.TweakChoices.Count}",
                $"Tab badges read active/total (Services {servicesTab.Count}) and the summary says it in words: {((TextBlock)window.FindName("ServicesSummary")).Text}");

            // Staging one service marks its tab and the bar says where the staged changes are.
            window.ServiceChoices[0].On = true;
            check(tabs.Single(tab => tab.Key == "Services").Staged && Find<TextBlock>("TweaksStagedSummary").Text == "Staged: 1 Services"
                && Find<Button>("ApplyWindowsSettingsButton").Content?.ToString() == "Review 1 change", "A staged change puts a dot on its tab and the bar says where it is");

            // One search shows matches from every tab (owner's pick); clearing it returns to the open tab.
            string word = window.ServiceChoices[0].Title.Split(' ')[0];
            Find<TextBox>("SettingsSearch").Text = word; UiLayoutChecks.Wait(300);
            check(Find<Expander>("SettingsServicesSection").IsVisible && tabs.All(tab => !tab.Open), $"Searching shows matches from other tabs too (\"{word}\" finds the Services row while Privacy was open)");
            Find<TextBox>("SettingsSearch").Text = ""; UiLayoutChecks.Wait(300);
            check(window.OpenTweaksTab == "Privacy" && !Find<Expander>("SettingsServicesSection").IsVisible && Find<Expander>("SettingsPrivacySection").IsVisible, "Clearing the search returns to the open tab");

            // The bar's Actions are the open tab's own (owner's pick).
            check(Find<Button>("ActionsButton").IsVisible && !Find<Button>("ServicesActionsButton").IsVisible && !Find<Button>("TabActionsButton").IsVisible, "On Privacy the bar offers Privacy's level actions");
            window.ShowTweaksTab("Tasks"); UiLayoutChecks.Wait(200);
            window.TaskChoices[0].On = true;
            check(Find<Button>("TabActionsButton").IsVisible && !Find<Button>("ActionsButton").IsVisible && window.TaskChoices.Count(choice => choice.Selected) == 1, "On Tasks the bar offers Clear my choices and Undo");
            Click("TabActionClearButton");
            check(window.TaskChoices.All(choice => !choice.Selected) && window.ServiceChoices[0].Selected, "Clear my choices clears only the open tab's staged rows");
            window.ShowTweaksTab("Overview"); UiLayoutChecks.Wait(200);
            check(window.TweakSuggestions.FirstOrDefault()?.Key == "Review" && window.TweakSuggestions.First().Title.StartsWith("1 change is waiting") && Find<FrameworkElement>("TweaksFooterBar").IsVisible,
                "With a change staged, the Overview puts Review first and shows the bar");
            UiLayoutChecks.Layout(root, new Size(920, 820));
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots)
            {
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "tweaks-overview-dark.png"));
                window.ShowTweaksTab("Services"); UiLayoutChecks.Wait(300); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "tweaks-page-services-dark.png"));
                window.ShowTweaksTab("Privacy"); UiLayoutChecks.Wait(300);
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "tweaks-page-privacy-light.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500);
                window.ShowTweaksTab("Tasks");
            }
            check(Preferences.Load(preferences).LastTweaksTab == "Tasks", "The tab used last is remembered");
        }
        finally { window.Close(); }

        // Tweaks opens on the Overview again, also in a new window (the entry used last is only remembered).
        var again = new MainWindow(engine, preferencesPath: preferences, serviceSystem: ServiceChecks.Pc(), taskSystem: TaskChecks.Pc()) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            again.Show();
            again.OpenTweaksPage();
            UiLayoutChecks.Wait(300);
            check(again.OpenTweaksTab == "Overview" && !((Expander)again.FindName("SettingsTasksSection")).IsVisible, "Tweaks opens on the Overview again after a restart");
            again.OpenTweaksPage("Startup"); UiLayoutChecks.Wait(200);
            check(again.OpenTweaksTab == "Startup" && ((Expander)again.FindName("SettingsStartupSection")).IsVisible, "Opening Tweaks at an entry (Home's Startup tile) still lands on it");
        }
        finally { again.Close(); }
    }
}
