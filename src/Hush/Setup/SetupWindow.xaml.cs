using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace Hush;

internal enum SetupMode { Install, Uninstall }

// The installer and uninstaller. It is the same app as the portable Hush.exe, started as Hush-Setup.exe (or with --setup / --uninstall).
// It states exactly what will happen before it does anything, needs no administrator rights and only touches Hush's own files.
public partial class SetupWindow : Window
{
    private readonly SetupMode mode;
    private readonly SetupLocations locations;
    private readonly string? sourceExe, runningExe;
    private readonly Action<string> openInstalled;
    private readonly Action runPortable;
    private bool finished, busy;

    internal SetupWindow(SetupMode mode, SetupLocations locations, string? sourceExe = null, string? runningExe = null,
        Action<string>? openInstalled = null, Action? runPortable = null)
    {
        this.mode = mode; this.locations = locations; this.sourceExe = sourceExe; this.runningExe = runningExe;
        this.openInstalled = openInstalled ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(path)! })?.Dispose());
        this.runPortable = runPortable ?? (() => { var window = new MainWindow(); Application.Current.MainWindow = window; window.Show(); });
        InitializeComponent();
        TermsNote.Text = Terms.Notice + " Read the full terms under About > Terms of use after installing, or in TERMS.md.";
        ShowIntro();
    }

    private void ShowIntro()
    {
        Status.Visibility = Visibility.Collapsed;
        OptionPanel.Visibility = Visibility.Visible;
        SecondaryButton.Visibility = Visibility.Visible;
        if (mode == SetupMode.Install)
        {
            bool update = HushSetup.IsInstalled(locations);
            Heading.Text = update ? "Update Hush" : "Install Hush";
            Details.Text = update
                ? $"Hush is already installed on this PC. This replaces it with version {UpdateService.DisplayVersion}. Your settings and history are kept."
                : $"Hush will be copied to {locations.InstallDirectory}, added to your Start menu and listed under Settings > Apps so you can remove it later. It doesn't need administrator rights, and your files and Windows settings are not touched.";
            OptionBox.Content = "Add a shortcut to my desktop";
            OptionBox.IsChecked = true;
            OptionHint.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = update ? "Update" : "Install";
            SecondaryButton.Content = "Run without installing";
        }
        else
        {
            Heading.Text = "Remove Hush?";
            Details.Text = "This removes Hush's program files, its Start menu and desktop shortcuts and its entry under Settings > Apps. Files Hush cleaned earlier can't be restored by this, and nothing else on your PC is touched.";
            OptionBox.Content = "Also delete my Hush settings, history and logs";
            OptionBox.IsChecked = false;
            OptionHint.Text = "This also removes the Undo history for Windows privacy changes.";
            OptionHint.Visibility = Visibility.Visible;
            PrimaryButton.Content = "Remove Hush";
            PrimaryButton.Style = (Style)FindResource("DangerButton");
            SecondaryButton.Content = "Keep Hush";
        }
        PrimaryButton.Focus();
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (finished)
        {
            if (mode == SetupMode.Install) { try { openInstalled(locations.InstalledExe); } catch { Status.Text = "Hush couldn't be opened. Find it in your Start menu."; Status.Visibility = Visibility.Visible; return; } }
            Close();
            return;
        }
        busy = true;
        PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = OptionBox.IsEnabled = false;
        Status.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        Status.Text = mode == SetupMode.Install ? "Installing…" : "Removing…";
        Status.Visibility = Visibility.Visible;
        bool option = OptionBox.IsChecked == true;
        SetupResult result;
        if (mode == SetupMode.Install)
            result = await Task.Run(() => HushSetup.Install(sourceExe ?? Environment.ProcessPath ?? "", option, locations));
        else
        {
            result = await Task.Run(() => HushSetup.Uninstall(locations, option, runningExe, HushSetup.IsAdminCopy(runningExe, locations) ? AdminMode.Scheduler : null, new RunKeyStartup()));
            if (result is { Success: true, Deferred: true })
            {
                try { HushSetup.StartDeferredRemoval(locations, runningExe); }
                catch { result = new(true, "Hush was removed. A few leftover files remain in " + locations.InstallDirectory + " and can be deleted by hand."); }
            }
        }
        busy = false;
        PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = OptionBox.IsEnabled = true;
        if (!result.Success)
        {
            Status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "CleanTextBrush");
            Status.Text = result.Message;
            return;
        }
        finished = true;
        OptionPanel.Visibility = Visibility.Collapsed;
        Status.Visibility = Visibility.Collapsed;
        if (mode == SetupMode.Install)
        {
            Heading.Text = "Hush is installed";
            Details.Text = "You'll find Hush in your Start menu" + (option ? " and on your desktop." : ".");
            PrimaryButton.Content = "Open Hush";
            SecondaryButton.Content = "Close";
        }
        else
        {
            Heading.Text = "Hush was removed";
            Details.Text = result.Message.Contains("leftover") ? result.Message : "Thank you for trying Hush.";
            // The red-orange Remove button is retired; the only action left is a quiet Close.
            PrimaryButton.Visibility = Visibility.Collapsed;
            SecondaryButton.Content = "Close";
            SecondaryButton.HorizontalAlignment = HorizontalAlignment.Right;
            System.Windows.Controls.Grid.SetColumn(SecondaryButton, 2);
        }
        (mode == SetupMode.Install ? PrimaryButton : SecondaryButton).Focus();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (mode == SetupMode.Install && !finished) { try { runPortable(); } catch { } }
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) { if (!busy) Close(); }
    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) DragMove(); }
}
