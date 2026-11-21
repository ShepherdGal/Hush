using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hush;

// Device repair, Bluetooth (owner, November 20, 2026): a fake PC only. Nothing real is ever restarted, removed, unpaired or deleted in a test.
internal static class DeviceRepairChecks
{
    internal sealed class FakeDevices : IDeviceSystem
    {
        internal bool Admin = true;
        internal bool? Radio = true;
        internal List<BtDevice> Devices = [];
        internal HashSet<string> Pairings = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> RefuseRemove = new(StringComparer.OrdinalIgnoreCase), RefuseUnpair = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Calls = [];
        // What a restart or a reinstall does to the adapter (fixes its problem, by default).
        internal Action<FakeDevices>? OnRestart = pc => pc.FixAdapters(), OnScan = pc => pc.FixAdapters();
        internal void FixAdapters() { for (int i = 0; i < Devices.Count; i++) if (Devices[i].IsAdapter) Devices[i] = Devices[i] with { Problem = 0, Present = true }; }
        public bool IsAdministrator => Admin;
        public Task<IReadOnlyList<BtDevice>> BluetoothDevicesAsync(CancellationToken token) { Calls.Add("list"); return Task.FromResult<IReadOnlyList<BtDevice>>(Devices.ToList()); }
        public Task<bool?> RadioOnAsync() => Task.FromResult(Radio);
        public Task<RepairResult> RestartDeviceAsync(string id, CancellationToken token) { Calls.Add("restart " + id); OnRestart?.Invoke(this); return Task.FromResult(new RepairResult(true, "restarted")); }
        public Task<RepairResult> RemoveDeviceAsync(string id, CancellationToken token)
        {
            Calls.Add("remove " + id);
            if (RefuseRemove.Contains(id)) return Task.FromResult(new RepairResult(false, "Failed to remove the device"));
            var device = Devices.FirstOrDefault(d => d.InstanceId == id);
            if (device != null && !device.IsAdapter) { Devices.Remove(device); if (device.Address is string a) Pairings.Remove(a); }
            if (device != null && device.IsAdapter) { int i = Devices.IndexOf(device); Devices[i] = device with { Present = false }; }
            return Task.FromResult(new RepairResult(true, "removed"));
        }
        public Task<RepairResult> ScanForDevicesAsync(CancellationToken token) { Calls.Add("scan"); OnScan?.Invoke(this); return Task.FromResult(new RepairResult(true, "scanned")); }
        public RepairResult Unpair(string address) { Calls.Add("unpair " + address); if (RefuseUnpair.Contains(address)) return new(false, "refused"); return new(true, "unpaired"); }
        public bool PairingExists(string address) => Pairings.Contains(address);
        public RepairResult ExportPairing(string address, string file) { Calls.Add("export " + address); File.WriteAllText(file, "Windows Registry Editor Version 5.00"); return new(true, "backed up"); }
        public RepairResult DeletePairing(string address)
        {
            Calls.Add("delete " + address); Pairings.Remove(address);
            Devices.RemoveAll(device => device.Address == address);
            return new(true, "removed");
        }
    }

    internal static BtDevice Adapter(int problem = 0) => new(@"USB\VID_8087&PID_0026\5&1A2B3C4D&0&10", "Intel(R) Wireless Bluetooth(R)", problem == 0 ? "OK" : "Error", problem, true, "22.150.0.6", "3 Mar 2023", "Intel");
    internal static BtDevice Old(string name, string address) => new($@"BTHENUM\{{0000110B-0000-1000-8000-00805F9B34FB}}_LOCALMFG&0002\7&2B1C3D4E&0&{address}_C00000000", name, "Unknown", 0, false, "", "", "");
    private static ServiceChecks.FakeServices Services(int bthStart = 3, bool running = true) => new()
    {
        All = [new("bthserv", "Bluetooth Support Service", 0x20, bthStart, false, @"C:\Windows\system32\svchost.exe -k LocalService", running && bthStart != 4, running ? 40 : 0),
               new("BthAvctpSvc", "AVCTP service", 0x20, 3, false, @"C:\Windows\system32\svchost.exe -k LocalService", false, 0)]
    };

    internal static void RunEngine(string fixture, Action<bool, string> check)
    {
        string folder = Path.Combine(fixture, "device-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string backups = Path.Combine(folder, "backups"), log = Path.Combine(folder, "Logs", "device-repair.jsonl");

        // Plain words and the address a paired device carries.
        check(DeviceRepair.ProblemText(43).Contains("stopped it") && DeviceRepair.ProblemText(28) == "no driver is installed" && DeviceRepair.ProblemText(999) == "it reports a problem",
            "Windows' device problem codes are said in plain words");
        check(Old("Headphones", "A1B2C3D4E5F6").Address == "a1b2c3d4e5f6" && new BtDevice(@"BTHLE\DEV_0011223344AA\8&1&0", "Mouse", "Unknown", 0, false, "", "", "").Address == "0011223344aa"
            && Adapter().IsAdapter && !Old("x", "A1B2C3D4E5F6").IsAdapter && Old("x", "A1B2C3D4E5F6").IsPairedDevice && Adapter().Address == null,
            "Adapters and paired devices are told apart, and a paired device's Bluetooth address is read from its id");
        var parsed = RealDeviceSystem.Parse("[{\"Id\":\"USB\\\\VID_8087\\\\1\",\"Name\":\"Intel BT\",\"Status\":\"Error\",\"Present\":true,\"Problem\":43,\"Version\":\"1.2\",\"Date\":\"2023-03-03\",\"Provider\":\"Intel\"},{\"Id\":\"BTHENUM\\\\x_A1B2C3D4E5F6_C\",\"Name\":\"\",\"Present\":false,\"Problem\":0}]");
        check(parsed.Count == 2 && parsed[0] is { Problem: 43, Present: true, Name: "Intel BT", DriverDate: "3 Mar 2023" } && parsed[1] is { Present: false, Name: "Unnamed Bluetooth device" }
            && RealDeviceSystem.Parse("{\"Id\":\"PCI\\\\X\",\"Present\":true}").Count == 1 && RealDeviceSystem.Parse("not json").Count == 0,
            "Windows' device list (one object or many) is read into devices; anything unreadable is ignored");

        // The wider driver search sorts Bluetooth by every signal (owner: a Bluetooth driver landed in the wrong tab).
        check(DriverCategories.Of("USB", "Intel - USB - 23.40.0.2", @"USB\VID_8087&PID_0026", "Bluetooth") == "Bluetooth"
            && DriverCategories.Of("Extension", "Contoso extension 1.0", @"BTHENUM\{0000110B}") == "Bluetooth"
            && DriverCategories.Of("Bluetooth", "Realtek Bluetooth adapter graphics-free 1.2") == "Bluetooth"
            && DriverCategories.Of("Display", "Intel Graphics with Bluetooth audio 31.0") == "Graphics"
            && DriverCategories.Of("System", "Intel Display Adapter 31.0", "PCI\\VEN_8086", "Display") == "Graphics",
            "Driver updates go to the Bluetooth tab by the update's class, the device's own class, a Bluetooth hardware id or the word; only a display driver beats it");

        // A broken PC: adapter error 43, the Bluetooth Support Service turned off by Hush, Bluetooth switched off, two old devices.
        var services = Services();
        var tuning = new ServiceTuning(services, Path.Combine(folder, "Windows settings"));
        tuning.Apply(["bthserv"]);
        var pc = new FakeDevices { Devices = [Adapter(43), Old("Old Headphones", "A1B2C3D4E5F6"), Old("Stuck Speaker", "0A0B0C0D0E0F")], Radio = false, Pairings = ["a1b2c3d4e5f6", "0a0b0c0d0e0f"] };
        var repair = new DeviceRepair(pc, services, tuning, backups, log);
        var diagnosis = repair.DiagnoseAsync(CancellationToken.None).GetAwaiter().GetResult();
        check(diagnosis.BrokenAdapters.Count == 1 && diagnosis.OldDevices.Count == 2 && diagnosis.OffServices.Single().OffByHush && diagnosis.RadioOn == false && !diagnosis.Healthy
            && diagnosis.Problems.Any(p => p.Contains("error 43")) && diagnosis.Problems.Any(p => p.Contains("turned off by Hush")) && diagnosis.Problems.Contains("Bluetooth is switched off"),
            "The check finds the adapter's error, a Bluetooth service Hush turned off, Bluetooth switched off and the old devices: " + string.Join(" | ", diagnosis.Problems));
        check(pc.Calls.All(call => call == "list") && services.Read("bthserv")!.Start == 4, "The check changes nothing");

        var quick = repair.QuickRepairAsync(diagnosis, CancellationToken.None).GetAwaiter().GetResult();
        var after = repair.DiagnoseAsync(CancellationToken.None).GetAwaiter().GetResult();
        check(quick.Ok && services.Read("bthserv")!.Start != 4 && !tuning.ChangedNames.Contains("bthserv") && pc.Calls.Contains("restart " + Adapter().InstanceId) && after.BrokenAdapters.Count == 0,
            "Quick repair puts back the service Hush turned off (its history too) and restarts the adapter, which fixes it: " + quick.Message);

        var other = Services(bthStart: 4, running: false);
        var otherRepair = new DeviceRepair(new FakeDevices { Devices = [Adapter()] }, other, new ServiceTuning(other, Path.Combine(folder, "other")), backups, log);
        var off = otherRepair.DiagnoseAsync(CancellationToken.None).GetAwaiter().GetResult();
        var turnedOn = otherRepair.QuickRepairAsync(off, CancellationToken.None).GetAwaiter().GetResult();
        check(off.OffServices.Single() is { OffByHush: false, Disabled: true } && other.Read("bthserv")!.Start == 3 && turnedOn.Ok,
            "A Bluetooth service turned off by something else is set back to Windows' default (start when needed)");

        var reinstall = repair.ReinstallDriverAsync(new BtDiagnosis([Adapter(43)], [], true, []), CancellationToken.None).GetAwaiter().GetResult();
        check(reinstall.Ok && pc.Calls.Contains("remove " + Adapter().InstanceId) && pc.Calls.Last() == "scan", "Reinstall removes the adapter, then Windows looks for devices again");

        // Old devices: one goes the normal way, one is stuck; force clean backs up its pairing first, then removes it.
        pc.RefuseRemove.Add(Old("Stuck Speaker", "0A0B0C0D0E0F").InstanceId); pc.RefuseUnpair.Add("0a0b0c0d0e0f");
        var cleaned = repair.RemoveOldDevicesAsync([Old("Old Headphones", "A1B2C3D4E5F6"), Old("Stuck Speaker", "0A0B0C0D0E0F")], CancellationToken.None).GetAwaiter().GetResult();
        check(!cleaned.Ok && cleaned.Message.StartsWith("Removed 1 old device") && cleaned.Message.Contains("Stuck Speaker") && pc.Devices.Count(d => d.IsPairedDevice) == 1,
            "Removing old devices removes what Windows lets go and names what it refused: " + cleaned.Message);
        int callsBefore = pc.Calls.Count;
        var forced = repair.ForceCleanAsync([Old("Stuck Speaker", "0A0B0C0D0E0F")], CancellationToken.None).GetAwaiter().GetResult();
        var forceCalls = pc.Calls.Skip(callsBefore).ToList();
        check(forced.Ok && !pc.PairingExists("0a0b0c0d0e0f") && forceCalls.IndexOf("export 0a0b0c0d0e0f") >= 0 && forceCalls.IndexOf("export 0a0b0c0d0e0f") < forceCalls.IndexOf("delete 0a0b0c0d0e0f")
            && Directory.GetFiles(backups, "bluetooth-0a0b0c0d0e0f-*.reg").Length == 1 && services.Calls.Contains("stop bthserv") == services.Calls.Contains("start bthserv"),
            "Force clean tries unpairing and removing first, and only then deletes the stored pairing, backed up beforehand: " + forced.Message);
        check(File.ReadAllLines(log).Length >= 4, "Every repair step is written to the device-repair log");
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        string folder = Path.Combine(fixture, "device-repair-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var pc = new FakeDevices { Devices = [Adapter(43), Old("Old Headphones", "A1B2C3D4E5F6")], Pairings = ["a1b2c3d4e5f6"] };
        var services = Services();
        var oldFactory = MainWindow.DeviceSystemFactory; MainWindow.DeviceSystemFactory = () => pc;
        var window = new MainWindow(engine, preferencesPath: Path.Combine(folder, "preferences.json"), serviceSystem: services) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            T Find<T>(string name) where T : class => (T)window.FindName(name);
            void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Click("ToolsButton");
            UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            check(window.ToolRail.Any(tool => tool.Key == "Repair") && pc.Calls.Count == 0,
                "Device repair is a tool in the Tools rail, and nothing is checked before it is opened");
            // Owner, November 21, 2026 ("Split"): Software Updates no longer looks at Bluetooth; only Device repair does.
            window.ShowTool("Updates");
            UiLayoutChecks.Wait(1500);
            check(pc.Calls.Count == 0 && window.FindName("UpdatesRepairLink") == null, "Software Updates leaves Bluetooth's health to Device repair (nothing is checked there)");
            window.ShowTool("Repair");
            UiLayoutChecks.PumpUntil(() => window.OpenTool == "Repair" && window.RepairSteps[0].State == "done");
            var steps = window.RepairSteps;
            check(steps[1].State == "current" && steps[2].State == "pending" && steps[3].State == "pending" && Find<TextBlock>("RepairSummary").Text.StartsWith("1 problem found")
                && Find<Button>("RepairContinueButton").IsVisible && Find<Button>("RepairContinueButton").Content as string == "Continue",
                "Opening Device repair checks Bluetooth, says what is wrong, and offers the gentlest fix first: " + Find<TextBlock>("RepairSummary").Text);
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots)
            {
                Find<FrameworkElement>("ToolFocusCard").BeginAnimation(UIElement.OpacityProperty, null);   // a test window does not run the fade-in
                UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "device-repair-dark.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout(root, new Size(920, 820));
                UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, "device-repair-light.png"));
                window.ToggleTheme(); UiLayoutChecks.Wait(500);
            }
            // Continue: asks first, repairs, checks again; the adapter is fixed, so reinstall is no longer needed and old devices come next.
            Click("RepairContinueButton");
            UiLayoutChecks.PumpUntil(() => Find<FrameworkElement>("ConsentOverlay").Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
            check(Find<TextBlock>("ConsentTitle").Text == "Run a quick repair?" && !pc.Calls.Any(call => call.StartsWith("restart")), "A fix asks first and nothing runs before the answer");
            Click("ConsentConfirmButton");
            UiLayoutChecks.PumpUntil(() => steps[1].State is "done" or "failed" && steps[3].State == "current");
            check(steps[1].State == "done" && steps[2].State == "notneeded" && steps[3].State == "current" && Find<FrameworkElement>("RepairDevicesPanel").IsVisible
                && !Find<Button>("RepairContinueButton").IsEnabled && window.RepairDevices.All(row => !row.Checked),
                "After the quick repair Bluetooth is checked again: the driver step is not needed, old devices come next and none is ticked for you");
            window.RepairDevices[0].Checked = true;
            check(Find<Button>("RepairContinueButton").IsEnabled && Find<Button>("RepairContinueButton").Content as string == "Remove 1", "Ticking an old device enables Remove 1");
            Click("RepairSkipButton");
            check(steps[3].State == "skipped" && steps[4].State == "current" && Find<Button>("RepairContinueButton").Content as string == "Force clean 1"
                && Find<Button>("RepairContinueButton").Style == (Style)Application.Current.FindResource("DangerButton"),
                "Skip moves on; the force clean is the red button");
            pc.Admin = false;
            bool launched = false; var oldLauncher = MainWindow.ElevatedLauncher; MainWindow.ElevatedLauncher = _ => { launched = true; return false; };
            try
            {
                Click("RepairContinueButton");
                UiLayoutChecks.PumpUntil(() => Find<FrameworkElement>("ConsentOverlay").Visibility == Visibility.Visible); UiLayoutChecks.Wait(340);
                check(Find<TextBlock>("ConsentTitle").Text == "Restart as administrator?", "Without administrator mode a fix offers the restart first");
                Click("ConsentCancelButton"); UiLayoutChecks.Wait(300);
                check(!launched && pc.Calls.All(call => !call.StartsWith("unpair") && !call.StartsWith("delete")), "Declining changes nothing");
            }
            finally { MainWindow.ElevatedLauncher = oldLauncher; }
        }
        finally
        {
            window.Close();
            MainWindow.DeviceSystemFactory = oldFactory;
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
