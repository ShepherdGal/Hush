using System.Windows;

namespace Hush;

public partial class App : Application
{
    // Smooth wheel scrolling for every ScrollViewer in the app (one class handler; see SmoothScroll).
    static App() => SmoothScroll.Install();

    // One Hush at a time: held for the life of the process, released only to hand over to an elevated copy of ourselves.
    private static SingleInstance? singleInstance;
    internal static void ReleaseSingleInstance() { singleInstance?.Dispose(); singleInstance = null; }
    internal static bool ReacquireSingleInstance() { singleInstance ??= SingleInstance.TryAcquire(SingleInstance.NameFor()); return singleInstance != null; }
    private static string QuoteArguments(IEnumerable<string> args) => string.Join(" ", args.Select(a => a.Contains(' ') || a.Length == 0 ? "\"" + a + "\"" : a));

    // Starts a copy of Hush through Windows (task or permission prompt) after letting go of the one-at-a-time lock, so the new copy can take it.
    internal static bool HandOver(Func<bool> start)
    {
        ReleaseSingleInstance();
        bool started = false;
        try { started = start(); } catch { }
        if (!started) ReacquireSingleInstance();
        return started;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await UpdateInstaller.ApplyAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length is 2 or 3 && e.Args[0] == "--finish-uninstall")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await HushSetup.FinishUninstallAsync(e.Args[1], e.Args.Length == 3 && e.Args[2] is "user" or "admin" ? e.Args[2] : "all"));
            return;
        }
        base.OnStartup(e);
        // The test runner hosts App for its resources; it must not open a real window
        // that reads the user's saved preferences.
        if (System.Reflection.Assembly.GetEntryAssembly() != typeof(App).Assembly) return;
        CrashLog.InstallGlobalHandlers();
        // One executable, three ways to start: portable (default), setup (Hush-Setup.exe or --setup) and uninstall (--uninstall from an installed copy).
        var locations = SetupLocations.Default;
        string? self = Environment.ProcessPath;
        bool installedCopy = HushSetup.IsInstalledCopy(self, locations);
        bool setupName = self != null && System.IO.Path.GetFileName(self).Equals("Hush-Setup.exe", StringComparison.OrdinalIgnoreCase);
        // The elevated helpers of administrator mode: no window, they only do the privileged half and exit with a result code.
        if (e.Args is [AdminModeService.SetupArgument] or [AdminModeService.RemoveArgument])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(AdminHelper(e.Args[0], self, locations));
            return;
        }
        if (e.Args is ["--uninstall"] && installedCopy)
        {
            // Removing the Program Files copy and its task needs administrator rights.
            if (HushSetup.IsAdminCopy(self, locations) && !AdminMode.Elevation.IsElevated && self != null)
            {
                if (AdminMode.Elevation.Launch(self, "--uninstall", false, out _)) { Shutdown(); return; }
            }
            var uninstall = new SetupWindow(SetupMode.Uninstall, locations, runningExe: self);
            MainWindow = uninstall; uninstall.Show();
            return;
        }
        if ((e.Args is ["--setup"] || setupName) && !installedCopy && self != null)
        {
            var setup = new SetupWindow(SetupMode.Install, locations, sourceExe: self, runningExe: self);
            MainWindow = setup; setup.Show();
            return;
        }
        // ----- One Hush at a time, and administrator mode -----
        if (!ReacquireSingleInstance()) { SingleInstance.AskRunningToShow(); Shutdown(); return; }
        bool? runAsAdmin = Preferences.Load(System.IO.Path.Combine(CleanupEngine.DataDirectory, "settings.json")).RunAsAdmin;
        bool elevated = AdminMode.Elevation.IsElevated;
        // The task is only looked up when the answer can matter (a normal start of Hush with administrator mode on): asking the Task Scheduler launches schtasks.exe.
        bool taskExists = runAsAdmin == true && !elevated && AdminMode.Scheduler.Exists();
        var action = AdminMode.DecideStart(runAsAdmin, elevated, HushSetup.IsAdminCopy(self, locations), taskExists);
        // An update that starts without administrator rights while administrator mode is on only hands over to an elevated copy and exits. The updater must not
        // read that as "the new version did not start" and roll back, so the receipt is written now (0.9 audit).
        if (ReceiptBeforeHandOver(action, e.Args) is { } updateReceipt) { try { UpdateInstaller.ConfirmStartup(updateReceipt); } catch { } }
        switch (action)
        {
            case StartAction.RunTask when HandOver(AdminMode.Scheduler.Run): Shutdown(); return;
            case StartAction.RunTask or StartAction.Relaunch when self != null && HandOver(() => AdminMode.Elevation.Launch(self, QuoteArguments(e.Args), false, out _)):
                Shutdown(); return;
        }
        if (installedCopy) HushSetup.RefreshRegistration(locations);
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("window", args.Exception);
            MessageBox.Show("Hush couldn't finish this operation. No further cleanup will run.\n\n" + args.Exception.Message,
                "Hush", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        var window = new MainWindow();
        if (e.Args.Length == 2 && e.Args[0] == "--update-complete")
            window.Loaded += (_, _) => { try { UpdateInstaller.ConfirmStartup(e.Args[1]); } catch { } };
        // Hush restarts itself as administrator for device-wide Windows choices; it then reopens the same review (the person confirms again).
        if (e.Args.Length >= 1 && e.Args[0] is "--windows-review" or "--windows-undo")
            window.Loaded += (_, _) => window.ResumeWindowsReview(e.Args);
        if (e.Args is ["--services"] or ["--services-undo"])
            window.Loaded += (_, _) => window.ResumeServices(e.Args[0] == "--services-undo");
        if (e.Args is ["--windows-update-off"] or ["--windows-update-on"])
            window.Loaded += (_, _) => window.ResumeWindowsUpdate(e.Args[0] == "--windows-update-off");
        MainWindow = window;
        SingleInstance.ListenForActivation(window);
        window.Show();
    }

    // The update receipt path when this start is about to hand over to an elevated copy (and so never reaches its own window); null otherwise.
    internal static string? ReceiptBeforeHandOver(StartAction action, string[] args) =>
        action != StartAction.Continue && args is ["--update-complete", var receipt] ? receipt : null;

    // Elevated half of turning administrator mode on or off. Exit code 0 means it worked.
    private static int AdminHelper(string mode, string? self, SetupLocations locations)
    {
        try
        {
            if (!AdminMode.Elevation.IsElevated || self == null) return 2;
            var result = mode == AdminModeService.SetupArgument
                ? AdminModeService.ElevatedSetup(self, locations, AdminMode.Scheduler)
                : AdminModeService.Disable(locations, AdminMode.Scheduler);
            return result.Success ? 0 : 1;
        }
        catch { return 1; }
    }
}
