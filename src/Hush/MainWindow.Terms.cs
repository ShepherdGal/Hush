using System.Windows;

namespace Hush;

// First-run terms ("use at your own risk"), the About notice and the full text on demand.
public partial class MainWindow
{
    // True once the person has accepted the current terms (and on every later start). A decline returns false and saves nothing:
    // the real app then closes. The popup is the shared consent dialog, so it blurs the app and cannot be skipped by clicking around it.
    internal async Task<bool> EnsureTermsAccepted()
    {
        if (prefs.TermsAccepted == Terms.Version) return true;
        bool agreed = await AskConsent(Terms.Title, Terms.Body, "I understand", "Quit");
        if (!agreed) return false;
        prefs.TermsAccepted = Terms.Version;
        SavePreferences();
        return true;
    }

    // Only the real app asks at start-up (tests build windows without making them the application's main window).
    private async void ShowTermsOnStart()
    {
        if (!await EnsureTermsAccepted()) { Close(); return; }
        await AskAdministratorChoice();
    }

    private async void Terms_Click(object sender, RoutedEventArgs e) => await AskConsent("Terms of use", Terms.Body, "OK", "Close");
}
