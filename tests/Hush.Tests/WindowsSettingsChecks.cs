using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Hush;

internal static class WindowsSettingsChecks
{
    internal static void Run(CleanupEngine engine, string fixture)
    {
        // Exercise the production read path and native probes without calling its Write method.
        var native = new RegistryPolicyStore();
        var environment = native.Environment;
        Require(environment.Build > 0 && environment.Edition.Length > 0 && environment.Owner != "unknown", "Native Windows environment can be read safely");
        foreach (var setting in WindowsSettingsService.Catalog) { _ = native.Read(setting); _ = native.Read(setting, machineOverride: true); }
        Console.WriteLine("PASS: Native Windows build/edition/management/account and policy probes are read-only");
        // The built-in enrollment entries every Windows 11 PC has (and a plain "not joined" answer) must never count as management.
        Require(!RegistryPolicyStore.IsRealEnrollment(1, "Local Authority", null) && !RegistryPolicyStore.IsRealEnrollment(1, "Cloud Authority", "")
            && !RegistryPolicyStore.IsRealEnrollment(1, "Deploy Authority", "") && !RegistryPolicyStore.IsRealEnrollment(1, "", null)
            && !RegistryPolicyStore.IsRealEnrollment(null, "MS DM Server", "person@example.com") && !RegistryPolicyStore.IsRealEnrollment(0, "MS DM Server", "person@example.com")
            && RegistryPolicyStore.IsRealEnrollment(1, "MS DM Server", "person@example.com") && RegistryPolicyStore.IsRealEnrollment(1, "AirWatch", null),
            "Only a real device-management enrollment counts as managed; Windows' built-in local/cloud/deploy authorities do not");
        // The catalog is the only source of registry destinations: ids are unique, every key is a Microsoft policy key, and the scope is deliberate.
        var catalog = WindowsSettingsService.Catalog;
        Require(catalog.Select(item => item.Id).Distinct().Count() == catalog.Count && catalog.All(item => item.Key.Contains(@"Policies\Microsoft", StringComparison.OrdinalIgnoreCase) && item.ValueName.Length > 0),
            "Every Windows setting has a unique id and sits under a Microsoft policy key");
        string[] perUser = ["diagnostics", "tailored", "suggestions", "location", "office-telemetry", "office-personal", "office-ceip", "search-web", "typing",
            "spotlight", "third-party-suggestions", "recall", "copilot"];
        var perDevice = catalog.Select(item => item.Id).Except(perUser).ToArray();
        Require(perUser.All(id => !Definition(id).Machine) && perDevice.All(id => Definition(id).Machine) && perUser.Length + perDevice.Length == catalog.Count && perDevice.Length >= 11,
            "Office and per-user choices write only the current user; every other choice is device-wide and marked administrator-only");
        // ShutUp10++-style levels: only recommended and somewhat recommended exist. Nothing that would be "not recommended" is offered.
        // "Turn off Windows Update" is deliberately NOT a catalog choice (0.9): it is its own red module (WindowsUpdateSwitch) with consent, saved originals and Undo.
        string[] never = ["camera", "microphone", "location-access", "defender", "smartscreen", "windows-update", "store", "onedrive", "biometrics", "firewall", "uac"];
        Require(never.All(word => !catalog.Any(item => item.Id.Contains(word, StringComparison.OrdinalIgnoreCase) || item.Key.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !item.Key.Contains("WindowsUpdate\\AU", StringComparison.OrdinalIgnoreCase))),
            "No camera, microphone, Defender, SmartScreen, Update, Store, OneDrive, sign-in or security choice is offered");
        Require(catalog.Count >= 60 && catalog.Select(item => (item.Key.ToLowerInvariant(), item.ValueName.ToLowerInvariant(), item.Machine)).Distinct().Count() == catalog.Count
            && catalog.All(item => item.Topic.Length > 0 && item.Title.Length > 0 && item.Description.Length > 20),
            "Every choice has a topic, a plain description and its own registry value");
        Require(Definition("office-telemetry").Value == 3 && Definition("office-personal").Value == 0 && Definition("office-ceip").Value == 0
            && Definition("advertising-id").ValueName == "DisabledByGroupPolicy" && Definition("delivery").Value == 0,
            "The Office, advertising ID and Delivery Optimization policies use the documented values");
        // Every registry write maps to this fresh test-only HKCU root, including simulated HKLM.
        string registryRoot = @"Software\Hush.TestFixtures\" + Guid.NewGuid().ToString("N");
        var store = new FixtureStore(registryRoot);
        try
        {
            Backend(store, fixture);
            Ui(engine, store, fixture);
            Console.WriteLine("PASS: Windows settings confirmation/cancel, exact fixture restoration, durable intent/recovery, conflict/managed/edition/admin guards and both-theme responsive UI; no real Windows policy writes");
        }
        finally
        {
            if (!registryRoot.StartsWith(@"Software\Hush.TestFixtures\", StringComparison.Ordinal)
                || !Guid.TryParseExact(registryRoot.Split('\\')[^1], "N", out _)) throw new Exception("Unsafe registry fixture cleanup.");
            Registry.CurrentUser.DeleteSubKeyTree(registryRoot, throwOnMissingSubKey: false);
        }
    }
    private static WindowsAdjustment Definition(string id) => WindowsSettingsService.Catalog.Single(item => item.Id == id);
    private static void Backend(FixtureStore store, string fixture)
    {
        string directory = Path.Combine(fixture, "windows-history");
        var service = new WindowsSettingsService(store, directory);
        var first = service.Read();
        Require(store.Writes == 0 && first.Settings.All(row => !row.Enabled) && !first.CanUndo, "Reading/previewing never changes registry values");
        var suggestions = first.Settings.Single(row => row.Definition.Id == "suggestions");
        Require(suggestions.CanEdit && suggestions.Status.Contains("Enterprise"), "Enterprise-only suggestions stay usable on Pro, with a note that they only take effect on Enterprise");
        Require(first.Settings.All(row => row.CanEdit) && !first.Summary.Contains("read-only"), "No setting is locked as read-only on an ordinary PC");
        var two = new Dictionary<string, bool> { ["diagnostics"] = true, ["tailored"] = true };
        var plan = service.Prepare(first.Settings, two);
        Require(plan.Changes.Count == 2 && store.Writes == 0, "Preparing a batch never applies it");
        store.BeforeWrite = () =>
        {
            using var intent = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "windows-settings.json")));
            Require(intent.RootElement.GetProperty("Pending").GetBoolean()
                && intent.RootElement.GetProperty("Values").GetArrayLength() == 2, "Originals and pending intent are durable before each write");
        };
        service.Execute(plan); store.BeforeWrite = null;
        Require(store.Read(Definition("diagnostics")).Is(1) && store.Read(Definition("tailored")).Is(1), "Confirmed changes retain verified DWORD values");
        var restarted = new WindowsSettingsService(store, directory);
        Require(restarted.Read().CanUndo && restarted.Read().Settings.Count(row => row.Enabled) == 2, "Undo survives a new service instance");
        restarted.Execute(restarted.PrepareUndo());
        Require(store.Read(Definition("diagnostics")) == PolicyValue.Missing && !restarted.Read().CanUndo, "Undo restores originally absent values instead of forcing defaults");
        using (var neighbor = Registry.CurrentUser.OpenSubKey(store.PathFor(Definition("diagnostics"))))
            Require(neighbor?.GetValue("neighbor") as string == "keep", "Undo preserves unrelated values and keys");

        store.Seed("diagnostics", new(true, 0));
        var strict = service.Read().Settings.Single(row => row.Definition.Id == "diagnostics");
        Require(strict.Enabled && strict.CanEdit && strict.Status.Contains("set outside Hush"), "An externally set value is shown as on, editable and explained");
        // Turning it off removes the outside value, and Undo puts that very value back (the original is whatever Hush first found).
        service.Execute(service.Prepare(service.Read().Settings, new Dictionary<string, bool> { ["diagnostics"] = false }));
        Require(!store.Read(Definition("diagnostics")).Exists && service.Read().CanUndo, "An outside value can be changed after confirmation, and the change is tracked");
        service.Execute(service.PrepareUndo());
        Require(store.Read(Definition("diagnostics")) == new PolicyValue(true, 0) && !service.Read().CanUndo, "Undo restores the outside value Hush found, then stops tracking it");
        store.Seed("diagnostics", new(true, 3));
        service.Execute(service.Prepare(service.Read().Settings, new Dictionary<string, bool> { ["diagnostics"] = true }));
        Require(store.Read(Definition("diagnostics")).Is(1), "A different outside value is replaced after confirmation");
        service.Execute(service.PrepareUndo());
        Require(store.Read(Definition("diagnostics")) == new PolicyValue(true, 3), "Undo restores a non-default original value exactly");
        store.Seed("diagnostics", PolicyValue.Missing);
        store.Seed("tailored", new(true, IsDword: false));
        Require(!service.Read().Settings.Single(row => row.Definition.Id == "tailored").CanEdit, "A value of an unusual registry type is left alone because it could not be restored exactly");
        store.Seed("tailored", PolicyValue.Missing);
        store.Seed("diagnostics", new(true, 3), machine: true);
        var overridden = service.Read().Settings.Single(row => row.Definition.Id == "diagnostics");
        Require(overridden.CanEdit && overridden.Status.Contains("device-wide policy"), "An existing device-wide policy is explained, not locked");
        store.Seed("diagnostics", PolicyValue.Missing, machine: true);

        var stale = service.Prepare(service.Read().Settings, two);
        store.Seed("diagnostics", new(true, 0));
        int writes = store.Writes;
        Reject(() => service.Execute(stale));
        Require(store.Writes == writes && store.Read(Definition("tailored")) == PolicyValue.Missing, "Stale confirmation rejects the whole batch before any mutation");
        store.Seed("diagnostics", PolicyValue.Missing);

        plan = service.Prepare(service.Read().Settings, two);
        int attempts = 0;
        store.AfterWrite = () => { if (++attempts >= 2) throw new IOException("Synthetic write/rollback failure"); };
        Reject(() => service.Execute(plan)); store.AfterWrite = null;
        var recovery = new WindowsSettingsService(store, directory);
        Require(recovery.Read().UndoHint.Contains("interrupted") && recovery.Read().Settings.All(row => !row.CanEdit), "Interrupted writes keep recovery history and block ordinary apply");
        recovery.Execute(recovery.PrepareUndo());
        Require(!recovery.Read().CanUndo && !store.Read(Definition("diagnostics")).Exists && !store.Read(Definition("tailored")).Exists,
            "Recovery restores all tracked values after partial writes and failed rollback");

        service.Execute(service.Prepare(service.Read().Settings, new Dictionary<string, bool> { ["diagnostics"] = true }));
        store.Seed("diagnostics", new(true, 0)); writes = store.Writes;
        Reject(() => service.PrepareUndo());
        Require(store.Writes == writes && !service.Read().CanUndo, "Undo never overwrites a later external edit");
        store.Seed("diagnostics", new(true, 1)); service.Execute(service.PrepareUndo());

        var widgetPlan = service.Prepare(service.Read().Settings, new Dictionary<string, bool> { ["widgets"] = true });
        store.Configuration = store.Configuration with { Administrator = false };
        writes = store.Writes; Reject(() => service.Execute(widgetPlan));
        var widgetsRow = service.Read().Settings.Single(row => row.Definition.Id == "widgets");
        Require(store.Writes == writes && widgetsRow.CanEdit && widgetsRow.NeedsAdministrator && service.NeedsAdministrator(widgetPlan), "Administrator access is rechecked after confirmation, and the device-wide choice stays usable (it asks for administrator rights)");
        store.Configuration = store.Configuration with { Administrator = true, Managed = true };
        Require(service.Read().Settings.All(row => row.CanEdit) && service.Read().Summary.Contains("managed"), "A managed PC gets a warning, not a lock");
        service.Execute(service.Prepare(service.Read().Settings, new Dictionary<string, bool> { ["tailored"] = true }));
        Require(store.Read(Definition("tailored")).Is(1), "Changes can be applied on a managed PC after confirmation");
        service.Execute(service.PrepareUndo());
        store.Configuration = store.Configuration with { Managed = false, Edition = "Core" };
        Require(service.Read().Settings.All(row => row.CanEdit) && service.Read().Summary.Contains("Home"), "Windows Home gets a warning that some policies are ignored, not a lock");
        store.Configuration = store.Configuration with { Edition = "Professional", Build = 28000 };
        Require(service.Read().Settings.All(row => row.CanEdit), "Newer Windows builds are not locked");
        store.Configuration = store.Configuration with { Edition = "Enterprise", Build = 26200 };
        Require(!service.Read().Settings.Single(row => row.Definition.Id == "suggestions").Status.Contains("Enterprise"), "The Enterprise note disappears on Enterprise");
        // History written before originals could be non-empty (no Applied field) still loads and undoes.
        var legacy = Path.Combine(fixture, "legacy-windows-history"); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "windows-settings.json"), "{\"Schema\":1,\"Owner\":\"synthetic-test-account\",\"Pending\":false,\"Values\":[{\"Id\":\"diagnostics\",\"Original\":{\"Exists\":false,\"Number\":0,\"IsDword\":true}}]}");
        store.Seed("diagnostics", new(true, 1));
        var old = new WindowsSettingsService(store, legacy);
        Require(old.Read().CanUndo && old.Read().Settings.Single(row => row.Definition.Id == "diagnostics").Status.Contains("Applied by Hush"), "An older history file still loads");
        old.Execute(old.PrepareUndo());
        Require(!store.Read(Definition("diagnostics")).Exists && !old.Read().CanUndo, "An older history file still undoes");
        store.Configuration = store.Configuration with { Edition = "Professional" };
        service.Execute(service.Prepare(service.Read().Settings, new Dictionary<string, bool> { ["widgets"] = true }));
        Require(store.Read(Definition("widgets")).Is(0), "Device-wide Widgets writes only its simulated machine-policy fixture");
        service.Execute(service.PrepareUndo());
        Require(!store.Read(Definition("widgets")).Exists, "Device policy Undo restores the originally absent fixture value");

        var corrupt = Path.Combine(fixture, "corrupt-windows-history"); Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "windows-settings.json"), "{broken");
        writes = store.Writes; Reject(() => new WindowsSettingsService(store, corrupt).Read());
        Require(writes == store.Writes, "Damaged history cannot cause mutations");
        var otherOwner = Path.Combine(fixture, "other-owner-history"); Directory.CreateDirectory(otherOwner);
        File.WriteAllText(Path.Combine(otherOwner, "windows-settings.json"), "{\"Schema\":1,\"Owner\":\"other-account\",\"Pending\":false,\"Values\":[]}");
        Reject(() => new WindowsSettingsService(store, otherOwner).Read());
        var unavailable = new WindowsSettingsService(store, Path.Combine(fixture, "unwritable-history"));
        plan = unavailable.Prepare(unavailable.Read().Settings, two);
        Directory.CreateDirectory(Path.Combine(fixture, "unwritable-history", "windows-settings.json"));
        writes = store.Writes; Reject(() => unavailable.Execute(plan));
        Require(store.Writes == writes, "History failure prevents all registry writes");
        Console.WriteLine("PASS: Synthetic HKCU-only policy roundtrip, durable originals, restart/rollback recovery, external conflicts, exact absence/neighbor preservation and unsupported/managed/admin/history rejection");
    }

    private static void Ui(CleanupEngine engine, FixtureStore store, string fixture)
    {
        store.Configuration = store.Configuration with { Administrator = false };
        var service = new WindowsSettingsService(store, Path.Combine(fixture, "ui-windows-history"));
        var window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "windows-ui-preferences.json"), windowsSettings: service)
        { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            var overlay = (FrameworkElement)window.FindName("WindowsSettingsOverlay");
            var scroll = (ScrollViewer)window.FindName("WindowsSettingsScroll");
            var apply = (Button)window.FindName("ApplyWindowsSettingsButton");
            void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Layout(Size size) { window.Width = size.Width; window.Height = size.Height; root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
            // After a confirmed change the popup stays up with the result and restart advice; Close dismisses it.
            void ConfirmAndClose(string expectAdvice, string? picture = null)
            {
                Click("ConfirmWindowsSettingsButton");
                Until(() => ((Button)window.FindName("CancelWindowsSettingsButton")).Content?.ToString() == "Close");
                Require(((TextBlock)window.FindName("WindowsConfirmNote")).Text.Contains(expectAdvice) && ((UIElement)window.FindName("ConfirmWindowsSettingsButton")).Visibility == Visibility.Collapsed,
                    $"After applying, the popup tells the person what to do next ({expectAdvice})");
                if (picture != null) { Wait(380); Capture(root, picture); }
                Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            }
            void Toggle(string id, bool value)
            {
                var box = Descendants<CheckBox>((ItemsControl)window.FindName(id is "location" or "widgets" ? "AdvancedAdjustments" : "PrivacyAdjustments"))
                    .Single(box => box.DataContext is AdjustmentChoice choice && choice.Id == id);
                box.IsChecked = value; box.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            Click("SettingsButton"); Wait(350); Layout(new Size(920, 820));
            var sections = new[] { "SettingsBoostSection", "SettingsPrivacySection", "SettingsImpactSection" }.Select(name => (Expander)window.FindName(name)).ToArray();
            Require(sections.All(section => !section.IsExpanded)
                && ((TextBlock)window.FindName("BoostSummary")).Text == "Manual only"
                && ((TextBlock)window.FindName("PrivacySummary")).Text == $"0 of {WindowsSettingsService.Catalog.Count(item => !item.Advanced)} turned on"
                && ((TextBlock)window.FindName("ImpactSummary")).Text.StartsWith($"0 of {WindowsSettingsService.Catalog.Count(item => item.Advanced)} turned on"),
                "Settings opens as three collapsed summary cards that state each section's current state");
            Capture(root, "settings-summary-dark");
            foreach (var section in sections) section.IsExpanded = true;
            Wait(380); Layout(new Size(920, 820));
            // Compact layout: topics start collapsed, every choice is one line with a "?" help popup and a level dot, and a search narrows the list.
            void ScrollToSearch()
            {
                var box = (FrameworkElement)window.FindName("SettingsSearch");
                scroll.ScrollToVerticalOffset(Math.Max(0, box.TransformToAncestor((Visual)scroll.Content).Transform(new Point()).Y - 12));
            }
            var topics = Descendants<ToggleButton>(root).Where(toggle => toggle.Uid == "topic").ToList();
            Require(topics.Count >= 10 && topics.All(toggle => toggle.IsChecked != true), "Windows choices start as a short list of collapsed topics");
            ScrollToSearch(); Wait(150); Layout(new Size(920, 820));
            Capture(root, "settings-topics-collapsed-dark");
            window.SetAllTopicsExpanded(true); Wait(420); Layout(new Size(920, 820));
            ScrollToSearch(); Wait(150); Layout(new Size(920, 820));
            Capture(root, "settings-topics-open-dark");
            // Only the Windows choices (other Settings sections may carry their own "?" popups too).
            var helps = Descendants<HelpMark>((DependencyObject)window.FindName("PrivacyAdjustments")).Concat(Descendants<HelpMark>((DependencyObject)window.FindName("AdvancedAdjustments"))).ToList();
            Require(helps.Count == WindowsSettingsService.Catalog.Count && helps.All(help => help.HelpText.Length > 20), $"Every choice has a \"?\" with its explanation ({helps.Count} found)");
            var firstHelp = helps[0];
            var invoke = (System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(firstHelp).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
            invoke.Invoke(); Wait(150);
            Require(firstHelp.IsPopupOpen && firstHelp.IsPinned, "Clicking the \"?\" opens and pins a popup with the explanation");
            if (firstHelp.Bubble != null) Capture(firstHelp.Bubble, "windows-help-bubble-dark");
            invoke.Invoke(); Wait(150);
            Require(!firstHelp.IsPopupOpen, "Clicking it again closes the popup");
            var search = (TextBox)window.FindName("SettingsSearch");
            int RowCount() => Descendants<CheckBox>((ItemsControl)window.FindName("PrivacyAdjustments")).Count() + Descendants<CheckBox>((ItemsControl)window.FindName("AdvancedAdjustments")).Count();
            int all = RowCount();
            search.Text = "edge"; Wait(450); Layout(new Size(920, 820));
            int expected = window.AdjustmentChoices.Count(choice => choice.Title.Contains("edge", StringComparison.OrdinalIgnoreCase)
                || choice.Description.Contains("edge", StringComparison.OrdinalIgnoreCase) || choice.Topic.Contains("edge", StringComparison.OrdinalIgnoreCase));
            Require(expected >= 10 && RowCount() == expected, $"Searching narrows the list to the matching choices ({RowCount()} of {all})");
            search.Text = ""; Wait(450); Layout(new Size(920, 820));
            Require(RowCount() == all, "Clearing the search brings every choice back");
            int before = store.Writes;
            Toggle("diagnostics", true); Toggle("tailored", true);
            Require(store.Writes == before && apply.IsEnabled && apply.Content.ToString() == "Review 2 changes", "Actual checkbox bindings stage choices without writing");
            Require(((TextBlock)window.FindName("PrivacySummary")).Text == $"2 of {WindowsSettingsService.Catalog.Count(item => !item.Advanced)} turned on · 2 waiting for review", "A section summary shows staged choices before anything is applied");
            Click("ApplyWindowsSettingsButton"); Wait(350);
            Require(overlay.Visibility == Visibility.Visible && !((UIElement)window.FindName("AppSurface")).IsEnabled
                && ((BlurEffect)window.FindName("BackgroundBlur")).Radius == 7 && ((UIElement)window.FindName("AppSurface")).Effect == null == !MainWindow.BackdropBlurEnabled && store.Writes == before
                && KeyboardNavigation.GetTabNavigation(overlay) == KeyboardNavigationMode.Cycle,
                "Windows confirmation blurs/blocks the app, traps focus and does not apply changes");
            Capture(root, "windows-confirm-dark");
            Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            Require(store.Writes == before, "Cancel preserves all Windows values");
            Click("ApplyWindowsSettingsButton");
            Require(((TextBlock)window.FindName("WindowsConfirmNote")).Text.Contains("sign out", StringComparison.OrdinalIgnoreCase)
                && ((Button)window.FindName("ConfirmWindowsSettingsButton")).Content.ToString() == "Apply changes", "The confirmation states up front that signing out or restarting is needed");
            ConfirmAndClose("sign out", "windows-result-dark");
            Require(store.Read(Definition("diagnostics")).Is(1) && store.Read(Definition("tailored")).Is(1)
                && ((Button)window.FindName("UndoWindowsSettingsButton")).IsEnabled, "Explicit confirmation applies and verifies only the fixture-backed choices");
            Toggle("tailored", false); before = store.Writes;
            Click("ApplyWindowsSettingsButton");
            Require(store.Writes == before, "Unchecking an applied choice also waits for confirmation");
            ConfirmAndClose("sign out");
            Require(!store.Read(Definition("tailored")).Exists && store.Read(Definition("diagnostics")).Is(1), "A confirmed uncheck restores only that choice's original value");
            Click("UndoWindowsSettingsButton"); Wait(340); Capture(root, "windows-undo-dark");
            before = store.Writes; Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            Require(store.Writes == before && store.Read(Definition("diagnostics")).Is(1), "Undo also requires confirmation and supports cancellation");
            Click("UndoWindowsSettingsButton"); ConfirmAndClose("sign out");
            Require(!store.Read(Definition("diagnostics")).Exists && !store.Read(Definition("tailored")).Exists, "Confirmed UI Undo restores the original absence");

            Toggle("diagnostics", true); Click("ApplyWindowsSettingsButton");
            store.Seed("diagnostics", new(true, 0)); before = store.Writes;
            Click("ConfirmWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            Require(store.Writes == before && store.Read(Definition("diagnostics")).Is(0)
                && ((TextBlock)window.FindName("WindowsSettingsStatus")).Text.Contains("could not"), "An edit while confirmation is open cannot be overwritten or reported successful");
            store.Seed("diagnostics", PolicyValue.Missing); Click("RefreshWindowsSettingsButton");

            foreach (bool light in new[] { false, true })
            {
                if (window.IsLightTheme != light) window.ToggleTheme();
                foreach (var size in new[] { new Size(920, 820), new Size(800, 740), new Size(1100, 900), new Size(800, 740) })
                {
                    Layout(size); scroll.ScrollToTop(); Wait(340);
                    foreach (string name in new[] { "SettingsBackButton", "RefreshWindowsSettingsButton", "UndoWindowsSettingsButton", "ApplyWindowsSettingsButton" })
                    {
                        var button = (Button)window.FindName(name);
                        Require(new Rect(root.RenderSize).Contains(button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize)))
                            && button.RenderTransform.Value.IsIdentity, "Settings actions stay visible and unscaled at every layout size");
                    }
                    if (size.Width is 920 or 800) Capture(root, $"windows-privacy-{(light ? "light" : "dark")}-{size.Width}");
                }
                scroll.ScrollToBottom(); Layout(new Size(800, 740));
                Capture(root, $"windows-advanced-{(light ? "light" : "dark")}");
                Require(window.AdjustmentChoices.Single(choice => choice.Id == "location").CanEdit && !window.AdjustmentChoices.Single(choice => choice.Id == "location").NeedsAdministrator
                    && window.AdjustmentChoices.Single(choice => choice.Id == "widgets").CanEdit && window.AdjustmentChoices.Single(choice => choice.Id == "widgets").NeedsAdministrator,
                    "Location and device-wide Widgets are separated: Widgets stays usable and is marked as needing administrator rights");
                Toggle("location", true); Click("ApplyWindowsSettingsButton"); Wait(340); Layout(new Size(800, 740));
                var card = (FrameworkElement)window.FindName("WindowsSettingsCard");
                Require(card.ActualHeight <= 650 && new Rect(root.RenderSize).Contains(card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize))), "Windows confirmation fits minimum layout in both themes");
                Capture(root, $"windows-location-confirm-{(light ? "light" : "dark")}");
                Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
                Click("RefreshWindowsSettingsButton");
            }
            // Actions menu (like ShutUp10++): recommended only, plus somewhat recommended, all; each stages the switches and opens the usual review.
            Click("RefreshWindowsSettingsButton"); Layout(new Size(920, 820)); before = store.Writes;
            Require(((TextBlock)window.FindName("WindowsSettingsStatus")).Text.StartsWith("Refreshed at ") && Motion.GetMouseOnly((Button)window.FindName("RefreshWindowsSettingsButton")), "Refresh says it refreshed (time and count) and does not keep a pressed look from keyboard focus");
            void Bulk(string menuButton)
            {
                Click("ActionsButton"); Wait(150);
                Require(((Popup)window.FindName("ActionsPopup")).IsOpen, "The Actions button opens its menu");
                Capture((FrameworkElement)((Popup)window.FindName("ActionsPopup")).Child, "windows-actions-menu-dark");
                // A second click on the Actions button closes the menu (it used to reopen it), and the chevron follows; the next click opens it again.
                Click("ActionsButton"); Wait(60);
                Require(!((Popup)window.FindName("ActionsPopup")).IsOpen, "Clicking the Actions button again closes its menu");
                Wait(700); Click("ActionsButton"); UiLayoutChecks.PumpUntil(() => ((Popup)window.FindName("ActionsPopup")).IsOpen);
                Require(((Popup)window.FindName("ActionsPopup")).IsOpen, "The Actions button opens the menu again afterwards");
                // A press anywhere else in the window closes it (the menu stays open until Hush closes it, so the button can toggle reliably with a real mouse).
                var elsewhere = (UIElement)window.FindName("WindowsSettingsStatus");
                elsewhere.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = elsewhere });
                Require(!((Popup)window.FindName("ActionsPopup")).IsOpen, "A press anywhere else in the window closes the Actions menu");
                Click("ActionsButton"); UiLayoutChecks.PumpUntil(() => ((Popup)window.FindName("ActionsPopup")).IsOpen);
                Click(menuButton); Wait(340);
            }
            IReadOnlyList<AdjustmentChoice> Choices() => window.AdjustmentChoices; // the list is replaced by every refresh
            var alsoUpdate = (FrameworkElement)window.FindName("AlsoUpdateOffBox"); var restoreBox = (FrameworkElement)window.FindName("RestorePointBox");
            Bulk("ActionRecommendedButton");
            Require(restoreBox.Visibility == Visibility.Visible && alsoUpdate.Visibility == Visibility.Collapsed, "Apply only recommended offers a restore point and never offers to turn Windows Update off");
            Require(overlay.Visibility == Visibility.Visible && store.Writes == before && !((Popup)window.FindName("ActionsPopup")).IsOpen
                && Choices().Where(choice => !choice.Advanced && choice.TakesEffect && choice.CanEdit).All(choice => choice.Selected)
                && Choices().Where(choice => choice.Advanced).All(choice => !choice.Selected), "Apply only recommended settings stages the green choices and asks for confirmation");
            Capture(root, "windows-bulk-recommended-dark");
            Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            Click("RefreshWindowsSettingsButton"); Layout(new Size(920, 820));
            Bulk("ActionSomewhatButton");
            Require(overlay.Visibility == Visibility.Visible && Choices().Where(choice => choice.TakesEffect && choice.CanEdit).All(choice => choice.Selected)
                && Choices().Where(choice => !choice.TakesEffect && choice.CanEdit).All(choice => !choice.Selected), "Recommended and somewhat recommended stages both levels but skips choices this edition ignores");
            Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            Click("RefreshWindowsSettingsButton"); Layout(new Size(920, 820));
            Bulk("ActionSomewhatButton");
            Require(alsoUpdate.Visibility == Visibility.Collapsed, "The amber action never offers Windows Update either");
            Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            Click("RefreshWindowsSettingsButton"); Layout(new Size(920, 820));
            Bulk("ActionAllButton");
            Require(!window.CanSelectCandidates && alsoUpdate.IsEnabled, "The \"Also turn off Windows Update\" box can be ticked even when no scan result is on screen");
            Require(alsoUpdate.Visibility == Visibility.Visible && ((CheckBox)alsoUpdate).IsChecked == false && ((CheckBox)alsoUpdate).Tag is SolidColorBrush clean && clean.Color == ((SolidColorBrush)Application.Current.Resources["CleanTextBrush"]).Color,
                "Apply all settings adds its own red, unticked checkbox for turning Windows Update off");
            Require(overlay.Visibility == Visibility.Visible && Choices().Where(choice => choice.CanEdit).All(choice => choice.Selected) && store.Writes == before, "Apply all settings stages every available choice and still only asks");
            Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            Click("RefreshWindowsSettingsButton"); Layout(new Size(920, 820));
            Click("ActionsButton"); Wait(150); Click("ActionUndoButton"); Wait(150);
            Require(((TextBlock)window.FindName("WindowsSettingsStatus")).Text.Contains("Nothing to undo") && overlay.Visibility == Visibility.Collapsed, "Undo all changes says so when Hush has changed nothing");
            Click("RefreshWindowsSettingsButton");
            // A device-wide choice without administrator rights: the popup says so, nothing is written, and the restart is only a launcher call (faked here).
            var launched = new List<string>(); bool exited = false, grant = true;
            var oldLauncher = MainWindow.ElevatedLauncher; var oldExit = MainWindow.ExitForRestart;
            MainWindow.ElevatedLauncher = arguments => { launched.Add(arguments); return grant; };
            MainWindow.ExitForRestart = () => exited = true;
            try
            {
                Layout(new Size(800, 740)); Toggle("widgets", true); before = store.Writes;
                Click("ApplyWindowsSettingsButton"); Wait(340);
                var confirm = (Button)window.FindName("ConfirmWindowsSettingsButton");
                Require(overlay.Visibility == Visibility.Visible && confirm.Content.ToString() == "Restart as administrator"
                    && ((TextBlock)window.FindName("WindowsConfirmNote")).Text.Contains("administrator rights"), "A device-wide choice explains that administrator rights are needed before anything happens");
                Capture(root, "windows-admin-dark");
                grant = false; Click("ConfirmWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
                Require(launched.SequenceEqual(new[] { "--windows-review +widgets" }) && !exited && store.Writes == before
                    && ((TextBlock)window.FindName("WindowsSettingsStatus")).Text.Contains("declined"), "A declined permission prompt changes nothing and keeps Hush open");
                Click("ApplyWindowsSettingsButton"); grant = true; launched.Clear();
                Click("ConfirmWindowsSettingsButton"); Until(() => exited);
                Require(launched.SequenceEqual(new[] { "--windows-review +widgets" }) && store.Writes == before, "Confirming restarts Hush as administrator with the staged choice and writes nothing itself");
                Click("RefreshWindowsSettingsButton");
                // The administrator copy stages the same choice and asks again.
                window.ResumeWindowsReview(["--windows-review", "+widgets,-nothing,+tailored"]); Wait(340);
                Require(overlay.Visibility == Visibility.Visible && ((TextBlock)window.FindName("WindowsConfirmSummary")).Text.Contains("Widgets")
                    && !((TextBlock)window.FindName("WindowsConfirmSummary")).Text.Contains("nothing"), "The restarted copy re-opens the review with only catalog choices staged");
                Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
                Click("RefreshWindowsSettingsButton");
            }
            finally { MainWindow.ElevatedLauncher = oldLauncher; MainWindow.ExitForRestart = oldExit; }
            store.Configuration = store.Configuration with { Administrator = true };
            Click("RefreshWindowsSettingsButton");
            Layout(new Size(800, 740)); Toggle("widgets", true); Click("ApplyWindowsSettingsButton");
            Require(((Button)window.FindName("ConfirmWindowsSettingsButton")).Content.ToString() == "Apply changes"
                && ((TextBlock)window.FindName("WindowsConfirmNote")).Text.Contains("Restart your PC"), "Device-wide changes tell the person to restart the PC");
            ConfirmAndClose("Restart your PC");
            Require(store.Read(Definition("widgets")).Is(0), "An administrator copy applies the device-wide fixture value after confirmation");
            Click("UndoWindowsSettingsButton"); ConfirmAndClose("Restart your PC");
            store.Configuration = store.Configuration with { Administrator = false };
            store.Configuration = store.Configuration with { Managed = true }; Click("RefreshWindowsSettingsButton");
            Require(window.AdjustmentChoices.All(choice => choice.CanEdit) && ((TextBlock)window.FindName("WindowsSettingsSummary")).Text.Contains("managed") && ((Button)window.FindName("ThemeButton")).IsEnabled,
                "A managed PC shows a warning instead of locking Windows choices");
            store.Configuration = store.Configuration with { Managed = false };
            Click("SettingsBackButton"); Wait(340);
            Require(((FrameworkElement)window.FindName("ScanPage")).Visibility == Visibility.Visible, "Settings returns to the originating page");
        }
        finally { window.Close(); }
    }

    private sealed class FixtureStore(string registryRoot) : IWindowsPolicyStore
    {
        internal WindowsEnvironment Configuration = new(26200, "Professional", true, false, "synthetic-test-account");
        public WindowsEnvironment Environment => Configuration;
        internal int Writes;
        internal Action? BeforeWrite, AfterWrite;
        internal string PathFor(WindowsAdjustment setting, bool machine = false) => registryRoot + (setting.Machine || machine ? @"\Machine\" : @"\User\") + setting.Id;
        public PolicyValue Read(WindowsAdjustment setting, bool machineOverride = false)
        { using var key = Registry.CurrentUser.OpenSubKey(PathFor(setting, machineOverride)); return RegistryPolicyStore.ReadValue(key, "value"); }
        public void Write(WindowsAdjustment setting, PolicyValue value)
        {
            BeforeWrite?.Invoke();
            using var key = Registry.CurrentUser.CreateSubKey(PathFor(setting));
            key.SetValue("neighbor", "keep");
            RegistryPolicyStore.WriteValue(key, "value", value); Writes++; AfterWrite?.Invoke();
        }
        internal void Seed(string id, PolicyValue value, bool machine = false)
        {
            using var key = Registry.CurrentUser.CreateSubKey(PathFor(Definition(id), machine));
            if (value.Exists && !value.IsDword) key.SetValue("value", "unexpected-string", RegistryValueKind.String);
            else RegistryPolicyStore.WriteValue(key, "value", value);
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    { for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) { var child = VisualTreeHelper.GetChild(parent, index); if (child is T match) yield return match; foreach (var item in Descendants<T>(child)) yield return item; } }
    private static void Capture(FrameworkElement root, string name)
    {
        if (System.Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is not string directory) return;
        Directory.CreateDirectory(directory); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
    private static void Reject(Action action)
    { bool rejected = false; try { action(); } catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { rejected = true; } Require(rejected, "Unsafe Windows operation was not rejected"); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Wait(int milliseconds) { var clock = Stopwatch.StartNew(); Until(() => clock.ElapsedMilliseconds >= milliseconds); }
    private static void Until(Func<bool> complete)
    {
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || clock.ElapsedMilliseconds > 6000) frame.Continue = false; }; timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Require(complete(), "Windows settings fixture did not settle within six seconds");
    }
}
