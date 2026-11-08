using System.Windows;
using System.Windows.Controls;

namespace Hush;

// Settings > Temporary files: how old a file in the Temp folders must be before it is listed (day = default, hour, or almost everything not in use).
public partial class MainWindow
{
    private bool tempAgeLoading;

    private void InitializeTempAge()
    {
        prefs.TempAge = Preferences.NormalizeTempAge(prefs.TempAge);
        CleanupEngine.TempAgeChoice = prefs.TempAge;
        RefreshTempAgeChoices();
    }

    private void RefreshTempAgeChoices()
    {
        if (TempAgeSummary == null) return;
        tempAgeLoading = true;
        try { foreach (var chip in new[] { TempAgeDay, TempAgeHour, TempAgeAny }) chip.IsChecked = (string)chip.Tag == prefs.TempAge; }
        finally { tempAgeLoading = false; }
        TempAgeSummary.Text = prefs.TempAge switch { "hour" => "Older than 1 hour", "any" => "Almost everything not in use", _ => "Older than 1 day" };
        TempAgeNote.Text = prefs.TempAge switch
        {
            "hour" => "Files from the last hour are left alone. Scan again to use this choice.",
            "any" => "Only files from the last 10 minutes are left alone, and anything a program has open is skipped. Scan again to use this choice.",
            _ => "The same limit Windows' own Storage Sense uses. Scan again after changing it."
        };
    }

    private void TempAge_Click(object sender, RoutedEventArgs e)
    {
        if (tempAgeLoading || sender is not RadioButton { Tag: string choice }) return;
        prefs.TempAge = Preferences.NormalizeTempAge(choice);
        SavePreferences();
        CleanupEngine.TempAgeChoice = prefs.TempAge;
        engine.ReloadTargets();
        RefreshTempAgeChoices();
    }
}
