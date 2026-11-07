using System.Windows;

namespace Hush;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await UpdateInstaller.ApplyAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--finish-uninstall")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await HushSetup.FinishUninstallAsync(e.Args[1]));
            return;
        }
        base.OnStartup(e);
        // The test runner hosts App for its resources; it must not open a real window
        // that reads the user's saved preferences.
        if (System.Reflection.Assembly.GetEntryAssembly() != typeof(App).Assembly) return;
        // One executable, three ways to start: portable (default), setup (Hush-Setup.exe or --setup) and uninstall (--uninstall from an installed copy).
        var locations = SetupLocations.Default;
        string? self = Environment.ProcessPath;
        bool installedCopy = HushSetup.IsInstalledCopy(self, locations);
        bool setupName = self != null && System.IO.Path.GetFileName(self).Equals("Hush-Setup.exe", StringComparison.OrdinalIgnoreCase);
        if (e.Args is ["--uninstall"] && installedCopy)
        {
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
        if (installedCopy) HushSetup.RefreshRegistration(locations);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Hush couldn't finish this operation. No further cleanup will run.\n\n" + args.Exception.Message,
                "Hush", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        var window = new MainWindow();
        if (e.Args.Length == 2 && e.Args[0] == "--update-complete")
            window.Loaded += (_, _) => { try { UpdateInstaller.ConfirmStartup(e.Args[1]); } catch { } };
        MainWindow = window;
        window.Show();
    }
}
