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
        // The catalog is the only source of registry destinations: ids are unique, every key is a Microsoft policy key, and the scope is deliberate.
        var catalog = WindowsSettingsService.Catalog;
        Require(catalog.Select(item => item.Id).Distinct().Count() == catalog.Count && catalog.All(item => item.Key.Contains(@"Policies\Microsoft", StringComparison.OrdinalIgnoreCase) && item.ValueName.Length > 0),
            "Every Windows setting has a unique id and sits under a Microsoft policy key");
        string[] perUser = ["diagnostics", "tailored", "suggestions", "location", "office-telemetry", "office-personal", "office-ceip", "search-web", "typing"];
        string[] perDevice = ["widgets", "telemetry", "feedback", "advertising-id", "windows-ceip", "inventory", "activity", "delivery", "maps", "error-reporting", "background-apps"];
        Require(perUser.All(id => !Definition(id).Machine) && perDevice.All(id => Definition(id).Machine) && perUser.Length + perDevice.Length == catalog.Count,
            "Office and per-user choices write only the current user; device-wide choices are marked administrator-only");
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
        Require(!first.Settings.Single(row => row.Definition.Id == "suggestions").CanEdit, "Enterprise-only suggestions are unavailable on Pro");
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
        Require(strict.Enabled && !strict.CanEdit, "Externally configured stricter diagnostics are never weakened");
        store.Seed("diagnostics", PolicyValue.Missing);
        store.Seed("tailored", new(true, IsDword: false));
        Require(!service.Read().Settings.Single(row => row.Definition.Id == "tailored").CanEdit, "Unknown registry types are read-only");
        store.Seed("tailored", PolicyValue.Missing);
        store.Seed("diagnostics", new(true, 3), machine: true);
        Require(!service.Read().Settings.Single(row => row.Definition.Id == "diagnostics").CanEdit, "Existing machine policies block per-user overrides");
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
        Require(store.Writes == writes && !service.Read().Settings.Single(row => row.Definition.Id == "widgets").CanEdit, "Administrator access is rechecked after confirmation");
        store.Configuration = store.Configuration with { Administrator = true, Managed = true };
        Require(service.Read().Settings.All(row => !row.CanEdit), "Managed devices cannot apply policy changes");
        store.Configuration = store.Configuration with { Managed = false, Edition = "Core" };
        Require(service.Read().Settings.All(row => !row.CanEdit), "Home is read-only rather than claiming unsupported policies work");
        store.Configuration = store.Configuration with { Edition = "Professional", Build = 28000 };
        Require(service.Read().Settings.All(row => !row.CanEdit), "Unverified future builds are read-only");
        store.Configuration = store.Configuration with { Edition = "Enterprise", Build = 26200 };
        Require(service.Read().Settings.Single(row => row.Definition.Id == "suggestions").CanEdit, "Documented Enterprise-only customization is edition-gated");
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
                && ((TextBlock)window.FindName("PrivacySummary")).Text == "0 of 16 turned on"
                && ((TextBlock)window.FindName("ImpactSummary")).Text.StartsWith("0 of 4 turned on"),
                "Settings opens as three collapsed summary cards that state each section's current state");
            Capture(root, "settings-summary-dark");
            foreach (var section in sections) section.IsExpanded = true;
            Wait(380); Layout(new Size(920, 820));
            int before = store.Writes;
            Toggle("diagnostics", true); Toggle("tailored", true);
            Require(store.Writes == before && apply.IsEnabled && apply.Content.ToString() == "Review 2 changes", "Actual checkbox bindings stage choices without writing");
            Require(((TextBlock)window.FindName("PrivacySummary")).Text == "2 of 16 turned on · 2 waiting for review", "A section summary shows staged choices before anything is applied");
            Click("ApplyWindowsSettingsButton"); Wait(350);
            Require(overlay.Visibility == Visibility.Visible && !((UIElement)window.FindName("AppSurface")).IsEnabled
                && ((BlurEffect)((UIElement)window.FindName("AppSurface")).Effect).Radius == 7 && store.Writes == before
                && KeyboardNavigation.GetTabNavigation(overlay) == KeyboardNavigationMode.Cycle,
                "Windows confirmation blurs/blocks the app, traps focus and does not apply changes");
            Capture(root, "windows-confirm-dark");
            Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            Require(store.Writes == before, "Cancel preserves all Windows values");
            Click("ApplyWindowsSettingsButton"); Click("ConfirmWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            Require(store.Read(Definition("diagnostics")).Is(1) && store.Read(Definition("tailored")).Is(1)
                && ((Button)window.FindName("UndoWindowsSettingsButton")).IsEnabled, "Explicit confirmation applies and verifies only the fixture-backed choices");
            Toggle("tailored", false); before = store.Writes;
            Click("ApplyWindowsSettingsButton");
            Require(store.Writes == before, "Unchecking an applied choice also waits for confirmation");
            Click("ConfirmWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            Require(!store.Read(Definition("tailored")).Exists && store.Read(Definition("diagnostics")).Is(1), "A confirmed uncheck restores only that choice's original value");
            Click("UndoWindowsSettingsButton"); Wait(340); Capture(root, "windows-undo-dark");
            before = store.Writes; Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
            Require(store.Writes == before && store.Read(Definition("diagnostics")).Is(1), "Undo also requires confirmation and supports cancellation");
            Click("UndoWindowsSettingsButton"); Click("ConfirmWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
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
                Require(window.AdjustmentChoices.Single(choice => choice.Id == "location").CanEdit
                    && !window.AdjustmentChoices.Single(choice => choice.Id == "widgets").CanEdit, "Location and device-wide Widgets are separated, with administrator gating");
                Toggle("location", true); Click("ApplyWindowsSettingsButton"); Wait(340); Layout(new Size(800, 740));
                var card = (FrameworkElement)window.FindName("WindowsSettingsCard");
                Require(card.ActualHeight <= 650 && new Rect(root.RenderSize).Contains(card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize))), "Windows confirmation fits minimum layout in both themes");
                Capture(root, $"windows-location-confirm-{(light ? "light" : "dark")}");
                Click("CancelWindowsSettingsButton"); Until(() => overlay.Visibility == Visibility.Collapsed);
                Click("RefreshWindowsSettingsButton");
            }
            store.Configuration = store.Configuration with { Managed = true }; Click("RefreshWindowsSettingsButton");
            Require(window.AdjustmentChoices.All(choice => !choice.CanEdit) && ((Button)window.FindName("ThemeButton")).IsEnabled,
                "Managed Windows policies are read-only while app appearance remains usable");
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
