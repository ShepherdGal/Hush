using System.IO;
using System.Windows;

namespace Hush;

// The one-time "Let Hush run as administrator?" question and the Settings switch that changes the answer.
public partial class MainWindow
{
    // Tests replace the exit so a real shutdown never happens; the locations are the per-user install and Program Files ones.
    internal static SetupLocations AdminLocations = SetupLocations.Default;
    private bool adminLoading;

    // Asked once (first run, right after the Terms; existing users once on their first 0.9 start). The answer is remembered either way.
    internal async Task AskAdministratorChoice()
    {
        if (prefs.RunAsAdmin != null) return;
        bool agreed = await AskConsent(AdminMode.QuestionTitle, AdminMode.QuestionBody, "Run as administrator", "Not now");
        prefs.RunAsAdmin = agreed;
        SavePreferences();
        RefreshAdminSwitch();
        if (agreed) await ApplyAdministratorChoice(true);
    }

    private void RefreshAdminSwitch()
    {
        adminLoading = true;
        try
        {
            AdminModeBox.IsChecked = prefs.RunAsAdmin == true;
            bool elevated = AdminMode.Elevation.IsElevated;
            AdminSummary.Text = prefs.RunAsAdmin == true ? (elevated ? "On · Hush is running as administrator" : "On · applies the next time Hush starts") : "Off";
        }
        finally { adminLoading = false; }
    }

    private async void AdminMode_Click(object sender, RoutedEventArgs e)
    {
        if (adminLoading) return;
        bool wanted = AdminModeBox.IsChecked == true;
        prefs.RunAsAdmin = wanted;
        SavePreferences();
        RefreshAdminSwitch();
        await ApplyAdministratorChoice(wanted);
    }

    // Turns the saved choice into the real thing. Installed copy: Program Files + one task (a single permission prompt now, none later).
    // Portable copy: Windows' own prompt on each start. Any failure leaves Hush as it was and says so.
    internal async Task ApplyAdministratorChoice(bool on)
    {
        string? self = Environment.ProcessPath;
        if (self == null || Application.Current?.MainWindow != this) return; // test windows never touch Windows
        var locations = AdminLocations;
        bool elevated = AdminMode.Elevation.IsElevated;
        bool installed = HushSetup.IsInstalledCopy(self, locations), adminCopy = HushSetup.IsAdminCopy(self, locations);
        string failure = "";
        if (on)
        {
            if (installed && !adminCopy)
            {
                var result = elevated
                    ? await Task.Run(() => AdminModeService.ElevatedSetup(self, locations, AdminMode.Scheduler))
                    : await Task.Run(() => AdminMode.Elevation.Launch(self, AdminModeService.SetupArgument, true, out int code) && code == 0
                        ? new SetupResult(true, "") : new SetupResult(false, "Windows did not give permission, so nothing was changed."));
                if (result.Success)
                {
                    AdminModeService.RepointInstalled(locations);
                    try { HushSetup.StartDeferredRemoval(locations, self, "user"); } catch { }
                    if (App.HandOver(AdminMode.Scheduler.Run)) { ExitForRestart(); return; }
                    failure = "Hush is set up to run as administrator, but couldn't start the new copy. Open it from the Start menu.";
                }
                else failure = result.Message;
            }
            else if (adminCopy && !elevated)
            {
                if (App.HandOver(AdminMode.Scheduler.Run)) { ExitForRestart(); return; }
                failure = "Windows did not start Hush as administrator.";
            }
            else if (!elevated)
            {
                if (App.HandOver(() => AdminMode.Elevation.Launch(self, "", false, out _))) { ExitForRestart(); return; }
                failure = "Windows did not give permission, so Hush stays as it is.";
            }
        }
        else if (adminCopy || AdminMode.Scheduler.Exists())
        {
            var result = elevated
                ? await Task.Run(() => AdminModeService.Disable(locations, AdminMode.Scheduler))
                : await Task.Run(() => AdminMode.Elevation.Launch(self, AdminModeService.RemoveArgument, true, out int code) && code == 0
                    ? new SetupResult(true, "") : new SetupResult(false, "Windows did not give permission, so administrator mode is still set up."));
            if (result.Success)
            {
                // The admin folder goes away after this copy closes; a normal (not elevated) copy opens through Explorer.
                if (adminCopy)
                {
                    try { HushSetup.StartDeferredRemoval(locations, self, "admin"); } catch { }
                    if (File.Exists(locations.InstalledExe))
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{locations.InstalledExe}\"") { UseShellExecute = false })?.Dispose(); } catch { }
                        ExitForRestart(); return;
                    }
                }
            }
            else failure = result.Message;
        }
        if (failure.Length > 0)
        {
            // Nothing changed, so the switch goes back to what is really true.
            prefs.RunAsAdmin = !on;
            SavePreferences();
            RefreshAdminSwitch();
            await AskConsent("Administrator mode", failure, "OK", "Close");
        }
    }
}
