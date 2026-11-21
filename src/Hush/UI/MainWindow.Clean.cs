using System.Windows;

namespace Hush;

// The Clean page (Phase D, November 19, 2026): the parts only this page has. Everything else on it is the Review page's existing logic.
public partial class MainWindow
{
    // A category holding items that need administrator mode (today: the all-users registry rows, which are listed but locked) shows a lock and this button on its
    // card (owner's choice: only items that are already locked). Hush asks first, then restarts with Windows' own permission prompt; nothing is cleaned or changed
    // before, and the next scan can select those items too.
    private async void RestartAsAdministrator_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // the button sits in the card's header: the click must not also open or close the card
        if (busy) return;
        bool agreed = await AskConsent("Restart as administrator?",
            "Some items in this card can only be cleaned in administrator mode. Hush restarts with Windows' own permission prompt.\n\n"
            + "Your selection is not kept: scan again after the restart, and these items can be selected too.",
            "Restart as administrator", "Cancel");
        if (!agreed) return;
        if (await Task.Run(() => ElevatedLauncher(""))) { ExitForRestart(); return; }
        SelectionSummary.Text = "Windows' permission prompt was declined or could not open, so nothing changed.";
    }
}
