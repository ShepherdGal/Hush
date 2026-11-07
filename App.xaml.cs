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
        base.OnStartup(e);
        // The test runner hosts App for its resources; it must not open a real window
        // that reads the user's saved preferences.
        if (System.Reflection.Assembly.GetEntryAssembly() != typeof(App).Assembly) return;
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
