using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Hush;

// Phase B (owner, November 19, 2026): more registry issues (this account + all users, locked without administrator mode), full registry backups, registry
// search and delete, and the Windows tweaks card. Everything runs against fixture keys under HKCU\Software\Hush.TestFixtures\<GUID>; the "all users" part of
// the registry is a second fixture key. Nothing real is read for deleting or changed.
internal static class RegistryToolsChecks
{
    internal static void RunEngine(string fixture, Action<bool, string> check)
    {
        string id = Guid.NewGuid().ToString("N");
        string userPrefix = $@"Software\Hush.TestFixtures\{id}\User\", machinePrefix = $@"Software\Hush.TestFixtures\{id}\Machine\";
        void Set(string prefix, string path, string name, object value, RegistryValueKind kind = RegistryValueKind.Unknown)
        {
            using var key = Registry.CurrentUser.CreateSubKey(prefix + path);
            if (kind == RegistryValueKind.Unknown) key.SetValue(name, value); else key.SetValue(name, value, kind);
        }
        void MakeKey(string prefix, string path) { using var _ = Registry.CurrentUser.CreateSubKey(prefix + path); }
        object? Get(string prefix, string path, string name) { using var key = Registry.CurrentUser.OpenSubKey(prefix + path); return key?.GetValue(name); }
        bool Exists(string prefix, string path) { using var key = Registry.CurrentUser.OpenSubKey(prefix + path); return key != null; }
        string gone = @"C:\Hush Test Gone " + id[..8];
        string existing = Environment.ProcessPath!;
        const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall", Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        try
        {
            // ----- More registry issues -----
            Set(userPrefix, Uninstall + @"\GoneApp", "DisplayName", "Gone App"); Set(userPrefix, Uninstall + @"\GoneApp", "UninstallString", $"\"{gone}\\unins000.exe\"");
            Set(userPrefix, Uninstall + @"\MsiApp", "DisplayName", "Msi App"); Set(userPrefix, Uninstall + @"\MsiApp", "UninstallString", $"\"{gone}\\x.exe\""); Set(userPrefix, Uninstall + @"\MsiApp", "WindowsInstaller", 1);
            Set(userPrefix, Uninstall + @"\LiveApp", "DisplayName", "Live App"); Set(userPrefix, Uninstall + @"\LiveApp", "UninstallString", $"\"{existing}\" /uninstall");
            Set(userPrefix, Uninstall + @"\HalfApp", "UninstallString", $"\"{gone}\\u.exe\""); Set(userPrefix, Uninstall + @"\HalfApp", "InstallLocation", Path.GetDirectoryName(existing)!); // its folder still exists: left alone
            Set(machinePrefix, Uninstall + @"\GoneMachineApp", "DisplayName", "Gone For All"); Set(machinePrefix, Uninstall + @"\GoneMachineApp", "UninstallString", $"\"{gone}\\uninstall.exe\"");
            Set(machinePrefix, @"Software\Microsoft\Windows\CurrentVersion\SharedDLLs", gone + @"\shared.dll", 1);
            Set(machinePrefix, @"Software\Microsoft\Windows\CurrentVersion\SharedDLLs", existing, 1);
            Set(machinePrefix, @"Software\Microsoft\Windows\CurrentVersion\Installer\Folders", gone + @"\Data\", "");
            Set(userPrefix, @"Software\Classes\Directory\shell\GoneVerb\command", "", $"\"{gone}\\tool.exe\" \"%1\"");
            Set(userPrefix, @"Software\Classes\Directory\shell\DelegateVerb\command", "DelegateExecute", "{00000000-0000-0000-0000-000000000000}");
            Set(userPrefix, @"Software\Classes\*\shell\LiveVerb\command", "", $"\"{existing}\" \"%1\"");
            Set(userPrefix, Run, "GoneTray", $"\"{gone}\\tray.exe\" --min"); Set(userPrefix, Run, "Hush", $"\"{gone}\\Hush.exe\" --startup"); Set(userPrefix, Run, "LiveTray", $"\"{existing}\"");
            MakeKey(userPrefix, @"Software\HushEmptyThing"); Set(userPrefix, @"Software\HushFullThing", "x", 1); MakeKey(userPrefix, @"Software\Microsoft Empty");

            var traces = new RegistryTraces(Registry.CurrentUser, userPrefix, Registry.CurrentUser, machinePrefix, elevated: false);
            var found = new List<CleanupItem>();
            traces.Scan(found, CancellationToken.None);
            CleanupItem? By(string category, string tail) => found.FirstOrDefault(item => item.CategoryId == category && item.Path.EndsWith(tail, StringComparison.OrdinalIgnoreCase));
            check(By("RegUninstall", @"\GoneApp") != null && By("RegUninstall", @"\MsiApp") == null && By("RegUninstall", @"\LiveApp") == null && By("RegUninstall", @"\HalfApp") == null,
                "A broken uninstall entry is found; Windows Installer entries, live programs and entries whose folder exists are left alone");
            var machineEntry = By("RegUninstall", @"\GoneMachineApp");
            check(machineEntry != null && machineEntry.Path.StartsWith(@"HKLM\") && machineEntry.Locked && machineEntry.Detail!.Contains("administrator mode"), "An all-users finding is listed as HKLM, locked, and says it needs administrator mode");
            machineEntry!.Selected = true;
            check(!machineEntry.Selected, "A locked finding cannot be ticked");
            check(By("RegSharedDlls", "shared.dll") != null && !found.Any(item => item.CategoryId == "RegSharedDlls" && item.Path.EndsWith(Path.GetFileName(existing))), "A shared-DLL entry for a missing file is found; one for an existing file is not");
            check(By("RegInstallerFolders", @"\Data\") != null, "An installer folder that no longer exists is found");
            check(By("RegContextMenu", @"\GoneVerb") != null && By("RegContextMenu", @"\DelegateVerb") == null && By("RegContextMenu", @"\LiveVerb") == null, "A right-click command of a removed program is found; delegate and live ones are not");
            check(By("RegStartupMissing", "GoneTray") != null && By("RegStartupMissing", @"\Hush") == null && By("RegStartupMissing", "LiveTray") == null, "A start-up entry whose program is gone is found; Hush's own and live ones are not");
            check(By("RegEmptyKeys", "HushEmptyThing") != null && By("RegEmptyKeys", "HushFullThing") == null && By("RegEmptyKeys", "Microsoft Empty") == null, "Empty program keys are found, never Microsoft's");

            string backups = Path.Combine(fixture, "regtools", "Registry backups");
            bool refused = false;
            try { traces.Delete(machineEntry, backups, fixture); } catch (IOException) { refused = true; }
            check(refused && Exists(machinePrefix, Uninstall + @"\GoneMachineApp"), "Without administrator mode an all-users entry is never deleted");
            var admin = new RegistryTraces(Registry.CurrentUser, userPrefix, Registry.CurrentUser, machinePrefix, elevated: true);
            var adminFound = new List<CleanupItem>(); admin.Scan(adminFound, CancellationToken.None);
            var newCategories = new[] { "RegUninstall", "RegSharedDlls", "RegInstallerFolders", "RegContextMenu", "RegStartupMissing", "RegEmptyKeys" };
            var toDelete = adminFound.Where(item => newCategories.Contains(item.CategoryId)).ToList();
            check(toDelete.All(item => !item.Locked), "In administrator mode nothing is locked");
            foreach (var item in toDelete) admin.Delete(item, backups, fixture);
            check(!Exists(userPrefix, Uninstall + @"\GoneApp") && !Exists(machinePrefix, Uninstall + @"\GoneMachineApp") && Get(machinePrefix, @"Software\Microsoft\Windows\CurrentVersion\SharedDLLs", gone + @"\shared.dll") == null
                && Get(machinePrefix, @"Software\Microsoft\Windows\CurrentVersion\SharedDLLs", existing) != null && !Exists(userPrefix, @"Software\Classes\Directory\shell\GoneVerb") && Exists(userPrefix, @"Software\Classes\*\shell\LiveVerb")
                && Get(userPrefix, Run, "GoneTray") == null && Get(userPrefix, Run, "LiveTray") != null && Get(userPrefix, Run, "Hush") != null && !Exists(userPrefix, @"Software\HushEmptyThing") && Exists(userPrefix, @"Software\HushFullThing"),
                "Cleaning removes exactly the findings and keeps their neighbours");
            check(Directory.GetFiles(backups, "*.reg").Length >= toDelete.Count && File.ReadAllText(Directory.GetFiles(backups, "*uninstall-m*")[0]).Contains("[HKEY_LOCAL_MACHINE\\"), "Each removal wrote a .reg backup first, an all-users one under HKEY_LOCAL_MACHINE");

            // ----- Full registry backup -----
            Set(userPrefix, @"Software\BackupMe", "Color", "blue");
            var service = new RegistryBackupService(Path.Combine(fixture, "regtools", "Full"), [new("Fixture", @"HKCU\" + userPrefix + @"Software\BackupMe")]);
            var made = service.Create("test");
            check(File.Exists(made.File) && service.List().Count == 1 && service.List()[0].Label == "test", "A full backup is one zip, listed with its label");
            Set(userPrefix, @"Software\BackupMe", "Color", "red");
            check(service.Restore(made.File) == null && Get(userPrefix, @"Software\BackupMe", "Color") as string == "blue", "Restoring puts the saved values back");
            check(service.Restore(Path.Combine(fixture, "elsewhere registry.zip")) != null, "Restore refuses a file that is not one of Hush's backups");
            service.Delete(made.File);
            check(service.List().Count == 0, "A full backup can be deleted");

            // ----- Registry search -----
            string needle = "HushNeedle" + id[..6];
            Set(userPrefix, @"Software\SearchMe", "Path", @"C:\" + needle + @"\a.exe");
            Set(userPrefix, @"Software\SearchMe\" + needle + "Key", "x", 1);
            Set(userPrefix, @"Software\Policies\" + needle, "y", 1);
            var finds = RegistrySearch.Search(needle, [@"HKCU\" + userPrefix.TrimEnd('\\')], keys: true, values: true, data: true, CancellationToken.None);
            check(finds.Count == 3 && finds.Count(f => f.Match == RegistryMatch.ValueData) == 1 && finds.Count(f => f.Match == RegistryMatch.KeyName) == 2, $"Search finds key names and text values ({finds.Count})");
            check(finds.Single(f => f.Key.Contains(@"\Policies\")).Protected && RegistrySearch.IsProtected(@"HKCU\Software") && !RegistrySearch.IsProtected(@"HKCU\Software\SearchMe"), "Policy keys and top-level keys are shown but protected");
            var (deleted, problems) = RegistrySearch.Delete(finds, Path.Combine(fixture, "regtools", "Search backups"), elevated: false);
            check(deleted == 2 && problems.Count == 1 && Get(userPrefix, @"Software\SearchMe", "Path") == null && !Exists(userPrefix, @"Software\SearchMe\" + needle + "Key") && Exists(userPrefix, @"Software\Policies\" + needle)
                && Directory.GetFiles(Path.Combine(fixture, "regtools", "Search backups"), "*.reg").Length == 2, "Deleting exports each find first and never touches a protected one");

            // ----- Windows tweaks -----
            var tweaks = new WindowsTweaks(Registry.CurrentUser, userPrefix, Registry.CurrentUser, machinePrefix, Path.Combine(fixture, "regtools", "Windows settings"), Path.Combine(fixture, "regtools", "Logs", "tweaks.jsonl"), elevated: false);
            const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            Set(userPrefix, Adv, "HideFileExt", 1);
            var states = tweaks.Scan();
            check(states.Count == WindowsTweaks.Catalog.Length && states.All(state => !state.On) && WindowsTweaks.Catalog.All(t => WindowsTweaks.TopicOrder.Contains(t.Topic)), "Every tweak is listed under one of the three topics, all off at first");
            check(!WindowsTweaks.Catalog.Any(t => t.Values.Any(v => v.Key.Contains("Policies") || v.Name is "EnableLUA" or "SmartScreenEnabled" or "AllowNewsAndInterests" or "DisableSearchBoxSuggestions" or "BackgroundModeEnabled")),
                "No security-weakening tweak, and nothing the Privacy card already offers");
            var applied = tweaks.Change(["show-extensions", "drive-letters-first", "take-ownership", "no-shortcut-arrow"], on: true);
            check(applied.Done == 3 && applied.Problems.Single().Contains("administrator") && Get(userPrefix, Adv, "HideFileExt") is 0 && Get(userPrefix, @"Software\Classes\*\shell\HushTakeOwnership\command", "") is string command && command.Contains("takeown")
                && File.Exists(Path.Combine(fixture, "regtools", "Windows settings", "tweaks-changed.json")), "Tweaks are applied, read back and their originals saved; an all-users tweak needs administrator mode");
            check(tweaks.Scan().Single(s => s.Tweak.Id == "show-extensions") is { On: true, ChangedByHush: true }, "An applied tweak reads as on, set by Hush");
            var back = tweaks.Change(["show-extensions", "drive-letters-first", "take-ownership"], on: false);
            check(back.Done == 3 && Get(userPrefix, Adv, "HideFileExt") is 1 && Get(userPrefix, @"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowDriveLettersFirst") == null && !Exists(userPrefix, @"Software\Classes\*\shell\HushTakeOwnership"),
                "Switching back restores the exact originals and removes the keys Hush created");
            check(tweaks.Change(["not-a-tweak"], on: true).Done == 0, "An unknown id changes nothing");
        }
        finally { try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Hush.TestFixtures\{id}", false); } catch { } }
    }

    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        string id = Guid.NewGuid().ToString("N");
        string userPrefix = $@"Software\Hush.TestFixtures\{id}\User\", machinePrefix = $@"Software\Hush.TestFixtures\{id}\Machine\";
        var oldTweaks = MainWindow.WindowsTweaksFactory; var oldRoots = MainWindow.RegistrySearchRoots;
        MainWindow.WindowsTweaksFactory = (history, log) => new WindowsTweaks(Registry.CurrentUser, userPrefix, Registry.CurrentUser, machinePrefix, history, log, elevated: false);
        using (var key = Registry.CurrentUser.CreateSubKey(userPrefix + @"Software\FindMe")) key.SetValue("Name", "HushUiNeedle");
        MainWindow.RegistrySearchRoots = [@"HKCU\" + userPrefix.TrimEnd('\\')];
        string data = Path.Combine(fixture, "regtools-ui");
        var window = new MainWindow(engine, preferencesPath: Path.Combine(data, "preferences.json")) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            T Find<T>(string name) where T : class => (T)window.FindName(name);
            void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string Text(string name) => Find<TextBlock>(name).Text;

            // Settings > Windows tweaks: staged into the one Review changes button, applied, reported.
            Click("SettingsButton");
            UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            var card = Find<Expander>("SettingsTweaksSection");
            // Phase D (owner, November 20, 2026): Windows tweaks is a tab of the Tweaks page; Settings no longer shows it, and nothing is read until Tweaks opens.
            check(!card.IsVisible && !card.IsExpanded && window.TweakChoices.Count == 0, "Windows tweaks is not on Settings any more, and nothing is read until Tweaks is opened");
            window.OpenTweaksPage("Tweaks");
            UiLayoutChecks.PumpUntil(() => card.IsVisible && window.OpenTweaksTab == "Tweaks");
            UiLayoutChecks.PumpUntil(() => window.TweakChoices.Count > 0);
            var view = (System.Windows.Data.ListCollectionView)Find<ItemsControl>("TweaksList").ItemsSource;
            check(view.Groups.Cast<System.Windows.Data.CollectionViewGroup>().Select(g => g.Name.ToString()).SequenceEqual(WindowsTweaks.TopicOrder), "Tweaks sit under the three chosen topics, in order");
            var extensions = window.TweakChoices.Single(choice => choice.Id == "show-extensions");
            extensions.On = true;
            check(extensions.Status.StartsWith("Will be applied after you review") && Find<Button>("ApplyWindowsSettingsButton").Content?.ToString() == "Review 1 change", "Switching a tweak on stages it in the one Review button");
            UiLayoutChecks.Layout(root, new Size(920, 820));
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots)
            {
                foreach (var toggle in UiLayoutChecks.Descendants<System.Windows.Controls.Primitives.ToggleButton>(card).Where(toggle => toggle.Uid == "topic").Take(2)) toggle.IsChecked = true;
                UiLayoutChecks.Wait(400); UiLayoutChecks.Layout(root, new Size(920, 820));
                var scroll = Find<ScrollViewer>("TweaksScroll");
                scroll.ScrollToVerticalOffset(Math.Max(0, card.TransformToAncestor((System.Windows.Media.Visual)scroll.Content).Transform(new Point()).Y - 10)); UiLayoutChecks.Wait(150);
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "tweaks-card-dark.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "tweaks-card-light.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500);
            }
            Click("ApplyWindowsSettingsButton"); UiLayoutChecks.PumpUntil(() => Find<FrameworkElement>("ConsentOverlay").Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(Text("ConsentTitle") == "Apply 1 change?" && Text("ConsentText").Contains("Windows tweaks to apply (1)"), "Review lists the tweak to apply");
            Click("ConsentConfirmButton");
            UiLayoutChecks.PumpUntil(() => Find<FrameworkElement>("ResultOverlay").Visibility == Visibility.Visible && Text("ResultTitle") == "Changes applied");
            check(Text("ResultAdvice").Contains("File Explorer"), "The report says some tweaks show after File Explorer restarts");
            Click("ResultOkButton");
            UiLayoutChecks.PumpUntil(() => window.TweakChoices.Single(choice => choice.Id == "show-extensions").State.On);
            using (var adv = Registry.CurrentUser.OpenSubKey(userPrefix + @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                check(adv?.GetValue("HideFileExt") is 0, "Confirming applies the tweak");

            // Tools > Registry: backups and search.
            Click("ToolsButton");
            UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            var registry = Find<Expander>("ToolsRegistrySection");
            registry.IsExpanded = true; UiLayoutChecks.Wait(300);
            check(Find<CheckBox>("RegistryAutoBackupSwitch").IsChecked == true, "A full backup before each registry clean is on by default");
            Click("RegistryBackupNowButton");
            UiLayoutChecks.PumpUntil(() => Text("RegistryBackupStatus").StartsWith("Backed up") || Text("RegistryBackupStatus").Contains("could not"));
            check(Text("RegistryBackupStatus").StartsWith("Backed up") && ((System.Collections.IList)Find<ItemsControl>("RegistryBackupsList").ItemsSource).Count == 1, $"Back up now makes one listed backup ({Text("RegistryBackupStatus")})");
            Find<TextBox>("RegistrySearchBox").Text = "HushUiNeedle";
            _ = window.SearchRegistryAsync();
            UiLayoutChecks.PumpUntil(() => window.RegistryFindRows.Count > 0);
            check(window.RegistryFindRows.Count == 1 && Find<Button>("RegistryDeleteButton").IsVisible && !Find<Button>("RegistryDeleteButton").IsEnabled, "Search lists the match; Delete waits for a tick");
            window.RegistryFindRows[0].Checked = true;
            check(Find<Button>("RegistryDeleteButton").IsEnabled, "Ticking a match enables Delete");
            UiLayoutChecks.Layout(root, new Size(920, 820));
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string toolShots)
            {
                var scroll = Find<ScrollViewer>("ToolsScroll");
                scroll.ScrollToVerticalOffset(Math.Max(0, registry.TransformToAncestor((System.Windows.Media.Visual)scroll.Content).Transform(new Point()).Y - 10)); UiLayoutChecks.Wait(150);
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(toolShots, "registry-card-dark.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(toolShots, "registry-card-light.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500);
            }
        }
        finally
        {
            window.Close();
            MainWindow.WindowsTweaksFactory = oldTweaks; MainWindow.RegistrySearchRoots = oldRoots;
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Hush.TestFixtures\{id}", false); } catch { }
        }
    }
}
