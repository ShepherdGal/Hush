using System.IO;
using System.Xml.Linq;
using Hush;
using Microsoft.Win32;

// Administrator mode and the one-Hush-at-a-time lock. Fakes stand in for the task scheduler and for elevation; folders are fixtures and the registry
// is HKCU\Software\Hush.TestFixtures\<GUID>. No real task is registered, nothing is relaunched and the real app is never moved.
internal static class AdminChecks
{
    private sealed class FakeScheduler : IAdminTaskScheduler
    {
        internal string? Registered; internal int Removed, Runs;
        public bool Exists() => Registered != null;
        public void Register(string exe) => Registered = exe;
        public void Remove() { Registered = null; Removed++; }
        public bool Run() { Runs++; return Registered != null; }
    }

    private sealed class FakeElevation(bool elevated, bool signedIn) : IElevation
    {
        public bool IsElevated => elevated;
        public bool IsSignedInUser => signedIn;
        public bool Launch(string exe, string arguments, bool wait, out int exitCode) { exitCode = 0; return false; }
    }

    internal static void Run(string fixture, Action<bool, string> check)
    {
        // ---- What administrator mode (and the 0.9 cleaning rules) change in the target list ----
        string profile = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var normalTargets = CleanupEngine.DefaultTargets();
        var oldElevation = AdminMode.Elevation;
        try
        {
            AdminMode.Elevation = new FakeElevation(true, false);
            var foreign = CleanupEngine.DefaultTargets();
            check(normalTargets.Any(t => Path.GetFullPath(t.Root).StartsWith(profile, StringComparison.OrdinalIgnoreCase)) && !foreign.Any(t => Path.GetFullPath(t.Root).StartsWith(profile, StringComparison.OrdinalIgnoreCase))
                && foreign.Any(t => t.DeletableOnly), "Elevated as a different account: the administrator's own profile folders are left out and system locations stay");
        }
        finally { AdminMode.Elevation = oldElevation; }
        check(normalTargets.Where(t => t.Category == "Prefetch").All(t => t.MinimumAge == TimeSpan.Zero && t.DeletableOnly && ReviewCategory.SafetyOf("Prefetch") == SafetyLevel.Review),
            "Prefetch files of any age are listed, still opt-in (Review) and deletable-only");
        check(normalTargets.Where(t => t.Category == "EventTrace").All(t => t.MinimumAge == TimeSpan.FromDays(1) && t.Pattern == "*.etl" && t.DeletableOnly), "Event trace logs: one day old, .etl files only, never the event log");
        check(normalTargets.Where(t => t.Category == "Update" && t.Group == "Windows Update downloads").All(t => t.Processes is { } p && p.Contains("TiWorker")),
            "Windows Update downloads are skipped while Windows is installing an update");
        check(normalTargets.Where(t => t.Category == "NuGetPackages").All(t => t.Root.EndsWith(@".nuget\packages") && ReviewCategory.SafetyOf("NuGetPackages") == SafetyLevel.Review)
            && !normalTargets.Any(t => t.Category == "DevCache" && t.Root.EndsWith(@".nuget\packages")), "NuGet packages are their own Review row, separate from the NuGet caches");

        // ---- Who starts how ----
        check(AdminMode.DecideStart(null, false, false, false) == StartAction.Continue, "Administrator mode: nothing is forced before the question was answered");
        check(AdminMode.DecideStart(false, false, true, true) == StartAction.Continue, "Administrator mode: a 'Not now' answer never elevates");
        check(AdminMode.DecideStart(true, true, true, true) == StartAction.Continue, "Administrator mode: an already elevated Hush just continues");
        check(AdminMode.DecideStart(true, false, true, true) == StartAction.RunTask, "Administrator mode: the Program Files copy starts through its task (no prompt)");
        check(AdminMode.DecideStart(true, false, true, false) == StartAction.Relaunch && AdminMode.DecideStart(true, false, false, true) == StartAction.Relaunch,
            "Administrator mode: a portable copy, or a copy without its task, asks Windows' own prompt");

        // ---- The task may only point into an administrator-only folder ----
        string pf = Path.Combine(fixture, "ProgramFiles");
        string[] roots = [pf];
        check(AdminMode.IsAdminOnlyFolder(Path.Combine(pf, "Hush", "Hush.exe"), roots), "An exe under the administrator root counts as administrator-only");
        check(!AdminMode.IsAdminOnlyFolder(Path.Combine(fixture, "Users", "me", "AppData", "Local", "Programs", "Hush", "Hush.exe"), roots)
            && !AdminMode.IsAdminOnlyFolder(Path.Combine(pf + "-evil", "Hush.exe"), roots), "A user folder or a look-alike prefix is never administrator-only");
        check(!AdminMode.IsAdminOnlyFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Hush", "Hush.exe")),
            "The real %LOCALAPPDATA% install folder is not administrator-only");
        bool refused = false;
        try { new SchtasksScheduler().Register(Path.Combine(fixture, "portable", "Hush.exe")); } catch (IOException) { refused = true; }
        check(refused, "The real scheduler refuses a portable or user-writable exe before it asks Windows for anything");
        var xml = XDocument.Parse(SchtasksScheduler.TaskXml(@"C:\Program Files\Hush\Hush.exe", "S-1-5-21-1-2-3-1001"));
        string flat = xml.ToString();
        check(flat.Contains("HighestAvailable") && flat.Contains("InteractiveToken") && flat.Contains(@"C:\Program Files\Hush\Hush.exe") && flat.Contains("S-1-5-21-1-2-3-1001")
            && !flat.Contains("<Triggers"), "The task is on-demand, runs with the highest privileges, for one user, with no trigger");

        // ---- Turning it on / off for an installed copy ----
        string guid = Guid.NewGuid().ToString("N");
        string registryRoot = @"Software\Hush.TestFixtures\" + guid;
        string root = Path.Combine(fixture, "admin");
        string source = Path.Combine(AppContext.BaseDirectory, "Hush.exe");
        var loc = new SetupLocations(Path.Combine(root, "user", "Programs", "Hush"), Path.Combine(root, "StartMenu"), Path.Combine(root, "Desktop"),
            registryRoot + @"\main", Path.Combine(root, "data"), Path.Combine(pf, "Hush"));
        try
        {
            check(HushSetup.Install(source, true, loc).Success, "Fixture: per-user install exists");
            var scheduler = new FakeScheduler();
            var bad = loc with { AdminInstallDirectory = Path.Combine(root, "not-admin", "Hush") };
            var rejected = AdminModeService.ElevatedSetup(loc.InstalledExe, bad, scheduler, roots);
            check(!rejected.Success && scheduler.Registered == null && !Directory.Exists(bad.AdminInstallDirectory), "A non-administrator-only target is refused and nothing is copied or registered");
            var setup = AdminModeService.ElevatedSetup(loc.InstalledExe, loc, scheduler, roots);
            check(setup.Success && File.Exists(loc.AdminExe) && scheduler.Registered == loc.AdminExe, "Elevated setup copies Hush into the administrator folder and registers the task for that exact exe");
            check(!Directory.EnumerateFiles(loc.AdminInstallDirectory).Any(file => Path.GetFileName(file).StartsWith(".hush-install-")), "Setup leaves no temporary files behind");
            AdminModeService.RepointInstalled(loc);
            check(HushSetup.SamePath(HushSetup.ShortcutTarget(loc.StartMenuShortcut)!, loc.AdminExe) && HushSetup.SamePath(HushSetup.ShortcutTarget(loc.DesktopShortcut)!, loc.AdminExe),
                "Start menu and desktop shortcuts now start the administrator copy");
            using (var key = Registry.CurrentUser.OpenSubKey(loc.RegistryKey))
                check((string?)key?.GetValue("UninstallString") == $"\"{loc.AdminExe}\" --uninstall" && (string?)key.GetValue("InstallLocation") == loc.AdminInstallDirectory,
                    "Settings > Apps points at the administrator copy");
            check(HushSetup.IsAdminCopy(loc.AdminExe, loc) && HushSetup.IsInstalledCopy(loc.AdminExe, loc) && !HushSetup.IsAdminCopy(loc.InstalledExe, loc), "The administrator copy is recognized by its own path");

            File.Delete(loc.InstalledExe);
            var off = AdminModeService.Disable(loc, scheduler);
            check(off.Success && scheduler.Registered == null && scheduler.Removed == 1 && File.Exists(loc.InstalledExe), "Turning it off removes the task and puts the per-user copy back");
            check(HushSetup.SamePath(HushSetup.ShortcutTarget(loc.StartMenuShortcut)!, loc.InstalledExe), "Shortcuts are back on the normal copy");
            using (var key = Registry.CurrentUser.OpenSubKey(loc.RegistryKey))
                check((string?)key?.GetValue("InstallLocation") == loc.InstallDirectory, "Settings > Apps is back on the per-user folder");

            // Uninstall removes the task and both folders.
            AdminModeService.ElevatedSetup(loc.InstalledExe, loc, scheduler, roots);
            var removed = HushSetup.Uninstall(loc, false, null, scheduler);
            check(removed.Success && scheduler.Registered == null && !Directory.Exists(loc.AdminInstallDirectory) && !File.Exists(loc.InstalledExe), "Uninstall removes the administrator task and the administrator folder");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(registryRoot, throwOnMissingSubKey: false); }

        // ---- One Hush at a time ----
        string name = @"Local\Hush-Test-" + guid;
        var first = SingleInstance.TryAcquire(name);
        check(first != null, "The first start gets the one-at-a-time lock");
        check(SingleInstance.TryAcquire(name) == null && SingleInstance.TryAcquire(name) == null, "Every later start is turned away while it is held");
        first!.Dispose();
        var again = SingleInstance.TryAcquire(name);
        check(again != null, "Closing the first Hush frees the lock (the hand-over to an elevated copy relies on this)");
        again!.Dispose();
        check(SingleInstance.NameFor().StartsWith(@"Local\Hush-") && SingleInstance.NameFor("HushSetup") != SingleInstance.NameFor(), "The lock is per user session and setup has its own name");

        // ---- The saved answer ----
        string file = Path.Combine(fixture, "admin-prefs.json");
        var prefs = new Preferences();
        check(prefs.RunAsAdmin == null, "Administrator mode is unanswered until the person decides");
        prefs.RunAsAdmin = true; prefs.Save(file);
        check(Preferences.Load(file).RunAsAdmin == true, "The 'Run as administrator' answer is remembered");
        prefs.RunAsAdmin = false; prefs.Save(file);
        check(Preferences.Load(file).RunAsAdmin == false, "A 'Not now' answer is remembered, so the question is asked only once");
    }
}
