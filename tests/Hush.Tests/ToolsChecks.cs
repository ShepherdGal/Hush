using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Hush;

// Tools (owner, November 19, 2026; Pro only): programs and their leftovers, backups and restore, Store apps, browser extensions, shredder and free-space wipe.
// Fixtures only: temp folders and a fixture registry key under HKCU\Software\Hush.TestFixtures. No real program is uninstalled, no real extension removed,
// no real drive filled.
internal static class ToolsChecks
{
    internal sealed class FakePrograms : IProgramSystem
    {
        public readonly List<InstalledProgram> Installed = [];
        public int Runs;
        public bool HandsOver;   // the uninstaller "returns at once" and the program is still listed until Continue
        public IReadOnlyList<InstalledProgram> List() => Installed.ToList();
        public Task<UninstallRun> RunUninstallerAsync(InstalledProgram program, CancellationToken token)
        {
            Runs++;
            if (!HandsOver) Installed.RemoveAll(p => p.Id == program.Id);
            return Task.FromResult(new UninstallRun(true, null));
        }
        public bool StillInstalled(InstalledProgram program) => Installed.Any(p => p.Id == program.Id);
    }

    internal sealed class FakeStoreApps : IStoreAppSystem
    {
        public readonly List<StoreApp> Apps = [new("Contoso.Notes_1.0.0.0_x64__abc", "Contoso Notes", "Contoso", "1.0.0.0", @"C:\Program Files\WindowsApps\Contoso.Notes")];
        public readonly List<string> Calls = [];
        public IReadOnlyList<StoreApp> List() => Apps.ToList();
        public Task<string?> UninstallAsync(StoreApp app) { Calls.Add("uninstall " + app.Name); Apps.Remove(app); return Task.FromResult<string?>(null); }
        public Task<string?> ResetAsync(StoreApp app) { Calls.Add("reset " + app.Name); return Task.FromResult<string?>(null); }
        public string? End(StoreApp app) { Calls.Add("end " + app.Name); return null; }
    }

    // A small PC made of temp folders and a fixture key.
    internal sealed class Pc : IDisposable
    {
        public readonly string KeyPath = @"Software\Hush.TestFixtures\" + Guid.NewGuid().ToString("N");
        public readonly string Root, ProgramFiles, AppData, Local, StartMenu, Documents;
        public string Hk => @"HKCU\" + KeyPath;
        public LeftoverPlaces Places => new([ProgramFiles, AppData, Local], [StartMenu], [Hk + @"\Software"], [Hk + @"\Run"], [ProgramFiles, AppData, Local, StartMenu, Documents]);
        public Pc(string fixture, string name)
        {
            Root = Path.Combine(fixture, "tools-" + name);
            ProgramFiles = Path.Combine(Root, "Program Files"); AppData = Path.Combine(Root, "Roaming"); Local = Path.Combine(Root, "Local"); StartMenu = Path.Combine(Root, "Start Menu"); Documents = Path.Combine(Root, "Documents");
            foreach (var folder in new[] { ProgramFiles, AppData, Local, StartMenu, Documents }) Directory.CreateDirectory(folder);
        }
        public void Set(string sub, string name, object value) { using var key = Registry.CurrentUser.CreateSubKey(KeyPath + "\\" + sub); key.SetValue(name, value); }
        public bool KeyExists(string sub) { using var key = Registry.CurrentUser.OpenSubKey(KeyPath + "\\" + sub); return key != null; }
        public object? Get(string sub, string name) { using var key = Registry.CurrentUser.OpenSubKey(KeyPath + "\\" + sub); return key?.GetValue(name); }
        public static void Write(string path, string text = "data") { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        public void Dispose() { try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false); } catch { } }

        // "Foo App" by "Foo Software, Inc." installed in Program Files, with settings, a cache, a shortcut, a start-up entry, registry keys; "Other App" stays installed.
        public (InstalledProgram Foo, InstalledProgram Other) Populate()
        {
            Write(Path.Combine(ProgramFiles, "Foo App", "foo.exe"), "program");
            Write(Path.Combine(AppData, "Foo App", "settings.ini"), "theme=dark");
            Write(Path.Combine(Local, "Foo Software", "Foo App", "cache.bin"), new string('x', 4096));
            Write(Path.Combine(Local, "FooAppUpdater", "log.txt"));                // only a partial match: Deep
            Write(Path.Combine(ProgramFiles, "Other App", "other.exe"));          // another installed program: never
            Write(Path.Combine(StartMenu, "Foo App.lnk"), "link");
            Write(Path.Combine(Documents, "Foo App notes.txt"), "mine");           // personal: never
            Set(@"Software\Foo Software\Foo App", "Window", "1");
            Set(@"Software\FooAppHelper", "x", "1");                             // only a partial match: Deep
            Set(@"Software\Other Vendor\Other App", "x", "1");
            Set("Run", "FooTray", "\"" + Path.Combine(ProgramFiles, "Foo App", "tray.exe") + "\"");
            Set("Run", "OtherTray", "\"" + Path.Combine(ProgramFiles, "Other App", "other.exe") + "\"");
            Set(@"Uninstall\FooApp_is1", "DisplayName", "Foo App 2.1 (x64)");
            var foo = new InstalledProgram(Hk + @"\Uninstall\FooApp_is1", "FooApp_is1", "Foo App 2.1 (x64)", "Foo Software, Inc.", "2.1", 0, null, Path.Combine(ProgramFiles, "Foo App"), "\"" + Path.Combine(ProgramFiles, "Foo App", "unins000.exe") + "\"", false, false);
            var other = new InstalledProgram(Hk + @"\Uninstall\Other", "Other", "Other App", "Other Vendor", "1.0", 0, null, Path.Combine(ProgramFiles, "Other App"), "other.exe /uninstall", false, false);
            return (foo, other);
        }
    }

    internal static void RunEngine(string fixture, Action<bool, string> check)
    {
        // Uninstall command lines are read the way Windows runs them.
        check(UninstallCommand.Split("\"C:\\Program Files\\Foo\\unins000.exe\" /SILENT") is { Exe: @"C:\Program Files\Foo\unins000.exe", Arguments: "/SILENT" }, "A quoted uninstall command splits into program and arguments");
        check(UninstallCommand.Split(@"C:\Program Files\Foo Bar\uninstall.exe --remove") is { Exe: @"C:\Program Files\Foo Bar\uninstall.exe", Arguments: "--remove" }, "An unquoted command with spaces ends at .exe");
        check(UninstallCommand.ForUninstall("MsiExec.exe", "/I{12345678-1234-1234-1234-123456789012}").Arguments == "/X{12345678-1234-1234-1234-123456789012}", "A Windows Installer entry is run as a removal (/X), like Settings > Apps");

        using var pc = new Pc(fixture, "engine");
        var (foo, other) = pc.Populate();

        // The programs list: Windows components, updates and Hush itself are never offered; a missing uninstaller is recognized.
        pc.Set(@"Uninstall\Visible", "DisplayName", "Visible App"); pc.Set(@"Uninstall\Visible", "UninstallString", Path.Combine(pc.Root, "gone", "unins.exe"));
        pc.Set(@"Uninstall\Visible", "Publisher", "Vis"); pc.Set(@"Uninstall\Visible", "EstimatedSize", 2048); pc.Set(@"Uninstall\Visible", "InstallDate", "20261101");
        pc.Set(@"Uninstall\Component", "DisplayName", "Windows Part"); pc.Set(@"Uninstall\Component", "SystemComponent", 1);
        pc.Set(@"Uninstall\Patch", "DisplayName", "Update for X"); pc.Set(@"Uninstall\Patch", "ParentKeyName", "X");
        pc.Set(@"Uninstall\Hush", "DisplayName", "Hush");
        pc.Set(@"Uninstall\{12345678-1234-1234-1234-123456789012}", "DisplayName", "Msi Thing"); pc.Set(@"Uninstall\{12345678-1234-1234-1234-123456789012}", "UninstallString", "MsiExec.exe /I{12345678-1234-1234-1234-123456789012}");
        var listed = new RegistryProgramSystem([pc.Hk + @"\Uninstall"]).List();
        var names = listed.Select(p => p.Name).ToHashSet();
        check(names.SetEquals(["Foo App 2.1 (x64)", "Visible App", "Msi Thing"]), $"Installed programs: components, updates and Hush itself are left out ({string.Join(", ", names)})");
        var visible = listed.Single(p => p.Name == "Visible App");
        check(visible.UninstallerMissing && visible.Bytes == 2048 * 1024 && visible.Installed == new DateTime(2026, 11, 1), "A program whose uninstaller file is gone is marked, with its size and date");
        check(!listed.Single(p => p.Name == "Msi Thing").UninstallerMissing, "A Windows Installer entry is never 'missing' (msiexec is Windows' own)");

        // Leftovers: Safe, Moderate and Deep find more and more; nothing another program uses, no personal file, no protected folder.
        var finder = new LeftoverFinder(pc.Places);
        var safe = finder.Find(foo, LeftoverDepth.Safe, [other], uninstallEntryLeft: false);
        var safePaths = safe.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        check(safePaths.SetEquals([Path.Combine(pc.ProgramFiles, "Foo App"), Path.Combine(pc.AppData, "Foo App"), Path.Combine(pc.Local, "Foo Software", "Foo App"), Path.Combine(pc.StartMenu, "Foo App.lnk")]) && safe.All(item => item.Safe),
            $"Safe finds its install folder, folders named like it, its publisher's folder for it and its shortcut ({string.Join(" | ", safePaths)})");
        var moderate = finder.Find(foo, LeftoverDepth.Moderate, [other], uninstallEntryLeft: false);
        var moderatePaths = moderate.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        check(moderatePaths.Contains(pc.Hk + @"\Software\Foo Software\Foo App") && moderatePaths.Contains(pc.Hk + @"\Software\Foo Software") && moderatePaths.Contains(pc.Hk + @"\Run|FooTray")
            && !moderatePaths.Contains(pc.Hk + @"\Run|OtherTray") && moderate.Where(item => !safePaths.Contains(item.Path)).All(item => !item.Safe),
            "Moderate adds its registry settings, its publisher's key (nothing else of that publisher is installed) and its broken start-up entry, none of them ticked");
        var deep = finder.Find(foo, LeftoverDepth.Deep, [other], uninstallEntryLeft: true);
        var deepPaths = deep.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        check(deepPaths.Contains(pc.Hk + @"\Software\FooAppHelper") && deepPaths.Contains(Path.Combine(pc.Local, "FooAppUpdater")) && deepPaths.Contains(foo.Id), "Deep adds partial name matches; a leftover Uninstall entry is offered");
        check(!deepPaths.Any(path => path.Contains("Other App", StringComparison.OrdinalIgnoreCase) || path.Contains("Other Vendor", StringComparison.OrdinalIgnoreCase) || path.Contains("Documents", StringComparison.OrdinalIgnoreCase))
            && !deepPaths.Contains(pc.ProgramFiles) && !deepPaths.Contains(pc.AppData) && !deepPaths.Contains(pc.Hk + @"\Software"),
            "Never offered: another installed program's folder or keys, personal files, the place roots themselves");
        // The publisher's folder/key is never offered while another program of that publisher is installed.
        var sibling = other with { Publisher = "Foo Software, Inc.", Id = "x" };
        check(!finder.Find(foo, LeftoverDepth.Moderate, [sibling], false).Any(item => item.Path.EndsWith(@"\Foo Software", StringComparison.OrdinalIgnoreCase)), "A publisher's folder or key stays while another of its programs is installed");

        // Remove with a backup, then restore it exactly.
        string backups = Path.Combine(pc.Root, "Uninstall backups"), log = Path.Combine(pc.Root, "Logs", "uninstall.jsonl");
        var remover = new LeftoverRemover(backups, log, pc.Places, elevated: false);
        var chosen = moderate.Where(item => !item.Path.EndsWith(@"\Foo Software", StringComparison.OrdinalIgnoreCase) || item.Kind != LeftoverKind.RegistryKey).ToList();
        var report = remover.Remove("Foo App", chosen);
        check(report.Failed == 0 && report.Removed == chosen.Count && !Directory.Exists(Path.Combine(pc.ProgramFiles, "Foo App")) && !Directory.Exists(Path.Combine(pc.AppData, "Foo App"))
            && !pc.KeyExists(@"Software\Foo Software\Foo App") && pc.Get("Run", "FooTray") == null && pc.Get("Run", "OtherTray") != null && File.Exists(Path.Combine(pc.Documents, "Foo App notes.txt")),
            $"Chosen leftovers are removed, nothing else ({report.Text} {string.Join("; ", report.Problems)})");
        check(report.BackupFolder is { } folder && File.Exists(Path.Combine(folder, "files.zip")) && File.Exists(Path.Combine(folder, "manifest.json")) && File.ReadAllLines(log).Length == chosen.Count, "Everything removed was backed up first and each item is logged");
        var kept = remover.Backups();
        check(kept.Count == 1 && kept[0].Program == "Foo App" && !kept[0].Restored, "The backup is listed for restoring");
        var restored = remover.Restore(kept[0].Folder);
        check(restored.Failed == 0 && File.ReadAllText(Path.Combine(pc.AppData, "Foo App", "settings.ini")) == "theme=dark" && File.Exists(Path.Combine(pc.ProgramFiles, "Foo App", "foo.exe"))
            && pc.Get(@"Software\Foo Software\Foo App", "Window") as string == "1" && (pc.Get("Run", "FooTray") as string ?? "").Contains("tray.exe"),
            $"Restore puts back files, registry settings and start-up entries ({string.Join("; ", restored.Problems)})");
        check(remover.Backups()[0].Restored, "A restored backup says so");
        bool refused = false;
        try { remover.Restore(pc.Root); } catch (IOException) { refused = true; }
        check(refused, "Restore refuses a folder that is not one of Hush's backups");
        remover.DeleteBackup(kept[0].Folder);
        check(remover.Backups().Count == 0, "A backup can be deleted");

        // Shredder: overwrite and delete; refuse drive roots and protected folders; wipe free space leaves no file.
        string secret = Path.Combine(pc.Root, "shred", "secret.txt");
        Pc.Write(secret, new string('s', 300_000));
        Pc.Write(Path.Combine(pc.Root, "shred", "sub", "b.txt"), "b");
        var shred = Shredder.Shred([Path.Combine(pc.Root, "shred"), Path.GetPathRoot(pc.Root)!, pc.Documents], [pc.Documents], null, CancellationToken.None);
        check(shred.Files == 2 && shred.Failed == 0 && !Directory.Exists(Path.Combine(pc.Root, "shred")) && shred.Problems.Count == 2 && Directory.Exists(pc.Documents),
            $"Shred overwrites and deletes the chosen folder and refuses a whole drive and a protected folder ({string.Join("; ", shred.Problems)})");
        string wipe = Path.Combine(pc.Root, "wipe");
        long written = Shredder.WipeFreeSpace(wipe, null, CancellationToken.None, limit: 3 << 20);
        check(written == 3 << 20 && Directory.GetFiles(wipe).Length == 0, "Wipe free space writes, then leaves no file behind");

        // Browser extensions: a Chromium profile (store, component, policy) and a Firefox profile (user, built-in).
        string chrome = Path.Combine(pc.Root, "Chrome", "User Data"), profile = Path.Combine(chrome, "Default");
        string idA = new('a', 32), idB = new('b', 32), idC = new('c', 32);
        Pc.Write(Path.Combine(profile, "Preferences"), "{\"extensions\":{\"settings\":{\"" + idB + "\":{\"location\":5},\"" + idC + "\":{\"location\":6}}}}");
        Pc.Write(Path.Combine(profile, "Extensions", idA, "1.2_0", "manifest.json"), "{\"name\":\"__MSG_appName__\",\"version\":\"1.2\",\"default_locale\":\"en\"}");
        Pc.Write(Path.Combine(profile, "Extensions", idA, "1.2_0", "_locales", "en", "messages.json"), "{\"appName\":{\"message\":\"Tab Tidy\"}}");
        Pc.Write(Path.Combine(profile, "Extensions", idB, "1.0", "manifest.json"), "{\"name\":\"Built In\",\"version\":\"1.0\"}");
        Pc.Write(Path.Combine(profile, "Extensions", idC, "2.0", "manifest.json"), "{\"name\":\"Company Tool\",\"version\":\"2.0\"}");
        string fox = Path.Combine(pc.Root, "Firefox", "Profiles"), foxProfile = Path.Combine(fox, "abc.default-release"), xpi = Path.Combine(foxProfile, "extensions", "block@example.xpi");
        Pc.Write(xpi, "zip");
        Pc.Write(Path.Combine(foxProfile, "extensions.json"), "{\"addons\":[{\"id\":\"block@example\",\"type\":\"extension\",\"location\":\"app-profile\",\"version\":\"3.0\",\"path\":" + System.Text.Json.JsonSerializer.Serialize(xpi) + ",\"defaultLocale\":{\"name\":\"Blocker\"}},"
            + "{\"id\":\"builtin@mozilla\",\"type\":\"extension\",\"location\":\"app-system-defaults\",\"version\":\"1\",\"path\":\"x\",\"defaultLocale\":{\"name\":\"Built In Fox\"}}]}");
        var extensions = new BrowserExtensionFinder([new("Chrome", chrome, true, "chrome", null), new("Firefox", fox, false, "firefox", null)]).List();
        check(extensions.Select(e => e.Name).ToHashSet().SetEquals(["Tab Tidy", "Company Tool", "Blocker"]) && extensions.Single(e => e.Name == "Company Tool").ByPolicy && extensions.Single(e => e.Name == "Blocker").Profile == "default-release",
            $"Extensions per browser and profile: names from their locale, the browser's own components left out, policy ones marked ({string.Join(", ", extensions.Select(e => e.Name))})");
        var blocker = extensions.Single(e => e.Name == "Blocker");
        string extensionBackups = Path.Combine(pc.Root, "Extension backups");
        check(BrowserExtensionFinder.Remove(blocker, extensionBackups, _ => true) is { } open && open.Contains("open") && File.Exists(xpi), "A Firefox extension is never removed while the browser is open");
        check(BrowserExtensionFinder.Remove(blocker, extensionBackups, _ => false) == null && !File.Exists(xpi) && Directory.GetFiles(extensionBackups, "*.xpi", SearchOption.AllDirectories).Length == 1, "With the browser closed it is removed and a copy is kept");
        check(BrowserExtensionFinder.Remove(extensions.Single(e => e.Name == "Tab Tidy"), extensionBackups, _ => false) != null, "Hush never deletes a Chromium extension's files (the browser's own page does it)");
    }

    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        using var pc = new Pc(fixture, "ui");
        var (foo, other) = pc.Populate();
        var programs = new FakePrograms(); programs.Installed.AddRange([foo, other, new("x2", "x2", "Zed Editor", "Zed", "0.9", 50 << 20, DateTime.Today, null, null, false, true)]);
        var store = new FakeStoreApps();
        var oldPrograms = MainWindow.ProgramSystemFactory; var oldPlaces = MainWindow.LeftoverPlacesFactory; var oldStore = MainWindow.StoreAppSystemFactory;
        MainWindow.ProgramSystemFactory = () => programs; MainWindow.LeftoverPlacesFactory = () => pc.Places; MainWindow.StoreAppSystemFactory = () => store;
        var window = new MainWindow(engine, preferencesPath: Path.Combine(pc.Root, "data", "preferences.json")) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            T Find<T>(string name) where T : class => (T)window.FindName(name);
            void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check(Find<Button>("ToolsButton").IsVisible, "Pro mode shows the Tools icon in the header");
            Click("ToolsButton");
            UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            // One screen (owner, November 21, 2026): the rail (Overview first, then the ten tools) is always on the left; Tools opens on the Overview, which names
            // at most three things worth a look and reads nothing; the window keeps its one 920 size.
            var card = Find<Expander>("ToolsProgramsSection");
            var tiles = Find<ItemsControl>("ToolTiles");
            check(window.ToolsHomeShown && tiles.IsVisible && tiles.Items.Count == 11 && window.ToolRail[0].Key == "Overview" && window.ToolRail[0].Open
                && Find<FrameworkElement>("ToolsOverview").IsVisible && window.ToolSuggestions.Count is > 0 and <= 3 && !card.IsVisible
                && window.ProgramRows.Count == 0 && window.StoreAppRows.Count == 0 && window.MaxWidth <= 921,
                $"Tools opens on the Overview with the rail at the left ({tiles.Items.Count} entries, {window.ToolSuggestions.Count} suggestions) and reads nothing");
            var programsSuggestion = UiLayoutChecks.Descendants<Button>(Find<ItemsControl>("ToolsSuggestions")).Single(button => (button.Tag as Suggestion)?.Key == "Programs");
            programsSuggestion.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiLayoutChecks.PumpUntil(() => window.ProgramRows.Count == 3);
            check(Find<Grid>("ToolsPage").IsVisible && card.IsVisible && window.OpenTool == "Programs" && !Find<FrameworkElement>("ToolsOverview").IsVisible && window.MaxWidth <= 921
                && window.ToolRail.Single(entry => entry.Open).Key == "Programs" && !Find<Expander>("ToolsStoreSection").IsVisible && window.StoreAppRows.Count == 0,
                "A suggestion opens its tool in the card beside the rail (only it is read), Programs is marked, and the window stays 920 wide");
            check(Find<TextBlock>("ProgramsSummary").Text.StartsWith("3 programs installed") && window.ProgramRows.Single(r => r.Name == "Zed Editor").ActionLabel == "Remove leftovers",
                "Opening Programs lists them; one whose uninstaller is gone offers Remove leftovers");
            // Owner's pick: largest first, with a switch to name or date installed.
            var list = Find<ItemsControl>("ProgramsList");
            var bySize = list.Items.Cast<ProgramRow>().Select(row => row.Program.Bytes).ToList();
            check(window.ProgramsSort == "size" && bySize.SequenceEqual(bySize.OrderByDescending(bytes => bytes)) && list.Items.Cast<ProgramRow>().First().Share == 1,
                "Programs are listed largest first, the largest with a full size bar");
            Click("SortByNameButton");
            var names = list.Items.Cast<ProgramRow>().Select(row => row.Name).ToList();
            check(window.ProgramsSort == "name" && names.SequenceEqual(names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)), "The sort switch orders them by name");
            Click("SortBySizeButton");
            check(window.ProgramRows.All(row => !row.HasIcon && row.TileBrush is System.Windows.Media.SolidColorBrush) && ReferenceEquals(ProgramIcons.TileFor("Zed Editor"), ProgramIcons.TileFor("Zed Editor")),
                "A program without a readable icon gets a letter tile in a color that always follows its name");
            // A tile shows its tool in the card; the choice is remembered for the next visit.
            var storeEntry = UiLayoutChecks.Descendants<Button>(tiles).Single(button => (button.Tag as TweaksTab)?.Key == "Store");
            storeEntry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiLayoutChecks.PumpUntil(() => window.StoreAppRows.Count == 1);
            check(Find<Expander>("ToolsStoreSection").IsVisible && !card.IsVisible && window.ToolRail.Single(entry => entry.Open).Key == "Store"
                && Preferences.Load(Path.Combine(pc.Root, "data", "preferences.json")).LastTool == "Store",
                "A rail entry swaps its tool into the card, the rail marks it, and the choice is remembered");
            // Owner, November 21, 2026: the rail's highlight lands exactly on the entry even when the click arrives during the button's press shrink.
            var railPanel = UiLayoutChecks.Descendants<SegmentPanel>(tiles).Single();
            var registryButton = UiLayoutChecks.Descendants<Button>(tiles).Single(button => (button.Tag as TweaksTab)?.Key == "Registry");
            registryButton.RenderTransformOrigin = new Point(0.5, 0.5);
            registryButton.RenderTransform = new System.Windows.Media.ScaleTransform(0.9, 0.9);
            window.ShowTool("Registry");
            UiLayoutChecks.PumpUntil(() => !railPanel.IsSpringing); UiLayoutChecks.Wait(100);
            var registrySlot = new Rect(((FrameworkElement)registryButton.Parent ?? (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(registryButton)).TranslatePoint(new Point(0, 0), railPanel), new Size(registryButton.ActualWidth, registryButton.ActualHeight));
            check(railPanel.Shown is Rect landed && Math.Abs(landed.Y - registrySlot.Y) < 0.5 && Math.Abs(landed.X - registrySlot.X) < 0.5 && Math.Abs(landed.Width - registrySlot.Width) < 0.5,
                $"The rail's highlight lands exactly on the entry even while its button is pressed ({railPanel.Shown} vs {registrySlot})");
            registryButton.RenderTransform = System.Windows.Media.Transform.Identity;
            // Browser extensions (owner, November 20, 2026: the Software Updates layout): one icon tab per browser with its count; a tab shows only its browser.
            string uiChrome = Path.Combine(pc.Root, "UiChrome", "User Data"), uiFox = Path.Combine(pc.Root, "UiFirefox", "Profiles"), uiXpi = Path.Combine(uiFox, "p.default", "extensions", "saver@example.xpi");
            Pc.Write(Path.Combine(uiChrome, "Default", "Preferences"), "{}");
            Pc.Write(Path.Combine(uiChrome, "Default", "Extensions", new string('d', 32), "1.0_0", "manifest.json"), "{\"name\":\"Page Saver\",\"version\":\"1.0\"}");
            Pc.Write(Path.Combine(uiChrome, "Default", "Extensions", new string('e', 32), "1.0_0", "manifest.json"), "{\"name\":\"Dark Pages\",\"version\":\"1.0\"}");
            Pc.Write(uiXpi, "zip");
            Pc.Write(Path.Combine(uiFox, "p.default", "extensions.json"), "{\"addons\":[{\"id\":\"saver@example\",\"type\":\"extension\",\"location\":\"app-profile\",\"version\":\"1\",\"path\":" + System.Text.Json.JsonSerializer.Serialize(uiXpi) + ",\"defaultLocale\":{\"name\":\"Fox Saver\"}}]}");
            var oldRoots = MainWindow.BrowserRootsFactory;
            MainWindow.BrowserRootsFactory = () => [new("Chrome", uiChrome, true, "chrome", null), new("Firefox", uiFox, false, "firefox", null)];
            try
            {
                window.ShowTool("Extensions");
                UiLayoutChecks.PumpUntil(() => window.ExtensionRows.Count == 3);
                var extensionView = (System.ComponentModel.ICollectionView)Find<ItemsControl>("ExtensionsList").ItemsSource;
                string firstBrowser = window.ExtensionTabs.FirstOrDefault()?.Key ?? "";
                check(window.ExtensionTabs.Select(tab => tab.Key).ToHashSet().SetEquals(["Chrome", "Firefox"]) && window.OpenExtensionTab == firstBrowser
                    && window.ExtensionTabs.Sum(tab => int.Parse(tab.Count)) == 3 && window.ExtensionTabs.All(tab => tab.IconGeometry != null)
                    && extensionView.Cast<ExtensionRow>().All(row => row.Extension.Browser == firstBrowser) && Find<FrameworkElement>("ExtensionTabsBar").Visibility == Visibility.Visible,
                    $"Browser extensions have an icon tab per browser with its count, and the open tab lists only that browser ({string.Join(", ", window.ExtensionTabs.Select(tab => tab.Key + " " + tab.Count))})");
                window.ShowExtensionTab(firstBrowser == "Chrome" ? "Firefox" : "Chrome");
                check(extensionView.Cast<ExtensionRow>().All(row => row.Extension.Browser == window.OpenExtensionTab) && extensionView.Cast<object>().Any(),
                    "Another tab shows that browser's extensions");
            }
            finally { MainWindow.BrowserRootsFactory = oldRoots; }
            var overviewEntry = UiLayoutChecks.Descendants<Button>(tiles).Single(button => (button.Tag as TweaksTab)?.Key == "Overview");
            overviewEntry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check(window.ToolsHomeShown && Find<FrameworkElement>("ToolsOverview").IsVisible && !Find<Expander>("ToolsExtensionsSection").IsVisible && Find<TextBlock>("ToolsTitle").Text == "Tools"
                && window.ToolRail.Single(entry => entry.Key == "Extensions").Hint == "3 extensions",
                "The rail's Overview entry returns to the Overview, and the rail's hints say what is known (3 extensions)");
            Click("ToolsButton");
            check(window.ToolsHomeShown, "The Tools tab while on the Overview stays there");
            window.ShowTool("Programs"); Click("ToolsButton");
            check(window.ToolsHomeShown, "The Tools tab while a tool is open returns to the Overview");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string homeShots)
            {
                UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(homeShots, "tools-home-dark.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(homeShots, "tools-home-light.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500);
            }
            window.ShowTool("Programs");
            UiLayoutChecks.Layout(root, new Size(920, 820));
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots)
            {
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "tools-page-dark.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "tools-page-light.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500);
            }

            // Uninstall Foo App: Prepare → its own uninstaller → leftovers (Moderate) → grouped review → remove with backup.
            window.OpenUninstall([foo]);
            UiLayoutChecks.Wait(400);
            check(window.UninstallPopupOpen && Find<TextBlock>("UninstallTitle").Text == "Uninstall Foo App 2.1 (x64)?" && Find<Button>("DepthModerateButton").Tag as string == "On"
                && Find<Button>("UninstallActionButton").Content as string == "Uninstall" && Find<Button>("UninstallCancelButton").Content as string == "Cancel", "The popup opens on Prepare, Moderate chosen, action left and Cancel right");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string prepareShots) UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(prepareShots, "uninstall-prepare-dark.png"));
            _ = window.PressUninstallAction();
            UiLayoutChecks.PumpUntil(() => window.LeftoverGroupRows.Count > 0);
            UiLayoutChecks.Wait(200);
            var groups = window.LeftoverGroupRows;
            check(programs.Runs == 1 && groups.Select(g => g.Name).SequenceEqual(["Files and folders", "Registry keys", "Startup entries"]) && groups.All(g => !g.IsOpen),
                $"After its uninstaller, leftovers are grouped by kind, closed ({string.Join(", ", groups.Select(g => g.Name))})");
            check(groups[0].Rows.All(r => r.Checked == r.Item.Safe) && groups[0].Checked == null && groups[1].Checked == false && groups[2].Checked == false,
                "Only clear matches start ticked: its own folders and shortcut yes, its publisher's folder, registry and start-up entries no (so Files is mixed)");
            groups[1].Checked = true;
            check(groups[1].Rows.All(r => r.Checked) && groups[1].Checked == true, "A group's own tick ticks its whole group");
            groups[1].Rows[0].Checked = false;
            check(groups[1].Checked == null, "Clearing one row makes the group's tick mixed");
            groups[0].IsOpen = true; UiLayoutChecks.Wait(400);
            check(Find<Button>("UninstallActionButton").Content as string == "Remove selected" && Find<Button>("UninstallCancelButton").Content as string == "Keep them", "Review: Remove selected (left), Keep them (right)");
            UiLayoutChecks.Layout(root, new Size(920, 820));
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string reviewShots)
            {
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(reviewShots, "uninstall-review-dark.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(reviewShots, "uninstall-review-light.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500);
            }
            _ = window.PressUninstallAction();
            UiLayoutChecks.PumpUntil(() => Find<Button>("UninstallActionButton").Content as string == "Done");
            check(!Directory.Exists(Path.Combine(pc.ProgramFiles, "Foo App")) && Directory.Exists(Path.Combine(pc.ProgramFiles, "Other App")) && window.UninstallStatusText.StartsWith("Removed"),
                $"Remove takes only what was ticked and reports it ({window.UninstallStatusText})");
            check(Find<StackPanel>("BackupsPanel").Visibility == Visibility.Visible && Directory.Exists(Path.Combine(pc.Root, "data", "Uninstall backups")), "The backup shows up under the Programs card");
            _ = window.PressUninstallAction();
            UiLayoutChecks.PumpUntil(() => !window.UninstallPopupOpen);

            // An uninstaller that hands over to another window: Hush waits for Continue, and never looks for leftovers of a program that is still installed.
            programs.HandsOver = true;
            window.OpenUninstall([other]);
            UiLayoutChecks.Wait(300);
            _ = window.PressUninstallAction();
            UiLayoutChecks.PumpUntil(() => Find<Button>("UninstallActionButton").Content as string == "Continue");
            _ = window.PressUninstallAction();
            UiLayoutChecks.PumpUntil(() => Find<Button>("UninstallActionButton").Content as string == "Done");
            check(window.UninstallStatusText.Contains("still installed") && Directory.Exists(Path.Combine(pc.ProgramFiles, "Other App")), "Still installed after Continue: nothing is searched or offered");
            _ = window.PressUninstallAction();
            UiLayoutChecks.PumpUntil(() => !window.UninstallPopupOpen);

            // Simple mode hides Tools and leaves the page.
            window.SetProMode(false);
            UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(300);
            check(!Find<Button>("ToolsButton").IsVisible && !Find<Grid>("ToolsPage").IsVisible, "Simple mode hides the Tools icon and leaves the Tools page");
            window.SetProMode(true);
        }
        finally
        {
            window.Close();
            MainWindow.ProgramSystemFactory = oldPrograms; MainWindow.LeftoverPlacesFactory = oldPlaces; MainWindow.StoreAppSystemFactory = oldStore;
        }
    }
}
