using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hush;

// Settings > Appearance > Main color: a row of round swatches (AccentPalette). Choosing one saves it and recolors the app through the same theme code that
// the Dark/Light switch uses (ApplyTheme eases every brush over 320 ms), so nothing else needs to know about it.
public sealed partial class MainWindow
{
    private bool accentLoading;

    private void InitializeAccentChoices()
    {
        prefs.Accent = AccentPalette.Normalize(prefs.Accent);
        foreach (var swatch in AccentPalette.All)
        {
            var button = new RadioButton
            {
                GroupName = "Accent", Tag = swatch.Id, Style = (Style)FindResource("AccentSwatch"),
                ToolTip = swatch.Name
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{swatch.Name} main color");
            button.Click += AccentSwatch_Click;
            AccentPanel.Children.Add(button);
        }
        RefreshAccentChoices();
    }

    private void RefreshAccentChoices()
    {
        if (AccentPanel == null) return;
        accentLoading = true;
        try
        {
            foreach (RadioButton button in AccentPanel.Children)
            {
                // Each swatch shows the shade the current theme really uses, so what is clicked is what is seen.
                button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(AccentPalette.Find((string)button.Tag).For(light)));
                button.IsChecked = (string)button.Tag == prefs.Accent;
            }
        }
        finally { accentLoading = false; }
        AccentSummary.Text = AccentPalette.Find(prefs.Accent).Name;
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (accentLoading || sender is not RadioButton { Tag: string id }) return;
        ChooseAccent(id);
    }

    internal void ChooseAccent(string id)
    {
        prefs.Accent = AccentPalette.Normalize(id);
        SavePreferences();
        ApplyTheme(animate: true);
        RefreshAccentChoices();
    }
}
