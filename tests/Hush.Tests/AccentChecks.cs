using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hush;

// Settings > Appearance > Dark theme color (owner request, November 11, 2026): the swatch list, its safety rules and the live recoloring of the real window.
internal static class AccentChecks
{
    private static double Luminance(Color color)
    {
        double Channel(byte value) { double v = value / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
    private static double Contrast(Color a, Color b) { double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    internal static void Run(string fixture, Action<bool, string> check)
    {
        var all = AccentPalette.All;
        check(all.Count >= 6 && all.Select(s => s.Id).Distinct().Count() == all.Count && all[0].Id == AccentPalette.DefaultId && all[0].Color == "#3FC3EE" && all[1].Color == "#38CFC8",
            "The Dark theme colors start with the icon's blue (the default) and keep the old teal as the second choice");
        check(all.All(s => !ColorRuleChecks.IsRedFamily(s.Color.TrimStart('#')) && !ColorRuleChecks.IsRedFamily(s.Hover.TrimStart('#'))),
            "No Dark theme color (or its hover shade) is red, pink or coral: the Clean red stays the only red");
        // Not too close to the three risk colors: green (Safe), amber (Review), red (Advanced), measured by hue.
        double Hue(string hex) { var c = (Color)ColorConverter.ConvertFromString(hex); var d = System.Drawing.Color.FromArgb(c.R, c.G, c.B); return d.GetHue(); }
        check(all.All(s => { double h = Hue(s.Color); return !(h >= 25 && h <= 160) && !(h >= 335 || h <= 22); }),
            "No Dark theme color sits in the green, amber or red hue range of the risk colors");
        Color panel = (Color)ColorConverter.ConvertFromString("#1B212B"), window = (Color)ColorConverter.ConvertFromString("#11151B");
        check(all.All(s => Contrast(s.ColorValue, panel) >= 4.5 && Contrast(s.ColorValue, window) >= 4.5 && Contrast((Color)ColorConverter.ConvertFromString(s.Hover), panel) >= 4.5),
            "Every Dark theme color and hover shade is readable (contrast at least 4.5:1) on the dark panel and window");

        // Light theme: every color has a slightly darker Light shade that is readable on the light surfaces (white-ish panels would swallow the pale Dark shades).
        double Ratio(string a, string b) => AccentPalette.Contrast(a, b);
        check(all.All(s => Ratio(s.LightColor, AccentPalette.LightWindow) >= 3.6 && Ratio(s.LightColor, AccentPalette.LightRaised) >= 3.3 && Ratio(s.LightColor, "#F9FAFC") >= 3.6),
            "Every Light shade is readable (contrast at least 3.6:1) on the light window, panel and raised surfaces: none is too pale for the Light theme");
        check(all.All(s => Ratio(s.LightHover, AccentPalette.LightWindow) >= 4.3) && all.All(s => !ColorRuleChecks.IsRedFamily(s.LightColor.TrimStart('#')) && !ColorRuleChecks.IsRedFamily(s.LightHover.TrimStart('#'))),
            "Light hover shades are a little darker still, and no Light shade is red");
        check(all.All(s => Luminance(s.LightColorValue) < Luminance(s.ColorValue)), "A Light shade is always the darker version of its Dark color");
        check(all.Skip(1).All(s => { double h = Hue(s.Color), l = Hue(s.LightColor); double d = Math.Abs(h - l); return Math.Min(d, 360 - d) <= 12; }),
            "A derived Light shade keeps the hue of its Dark color: it is the same color made slightly darker, not another color");
        check(all[0].LightColor == "#4C7BC8" && SafetyPalette.Accent(true) == "#4C7BC8", "The default blue keeps the soft Light blue the Light theme always had");
        check(AccentPalette.Find("nope").Id == AccentPalette.DefaultId && AccentPalette.Find(null).Id == AccentPalette.DefaultId && AccentPalette.Normalize("violet") == "violet",
            "An unknown or missing color id reads as the default");

        string file = Path.Combine(fixture, "accent-preferences.json");
        new Preferences { Accent = "orchid" }.Save(file);
        bool saved = Preferences.Load(file).Accent == "orchid";
        File.WriteAllText(file, "{\"Accent\":\"not-a-color\"}");
        bool bad = Preferences.Load(file).Accent == AccentPalette.DefaultId;
        check(saved && bad && new Preferences().Accent == AccentPalette.DefaultId, "The chosen Dark theme color is remembered, and a damaged value falls back to the default");
    }

    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        string preferences = Path.Combine(fixture, "accent-ui-preferences.json");
        new Preferences { TermsAccepted = Terms.Version, RunAsAdmin = false }.Save(preferences);
        var window = new MainWindow(engine, preferencesPath: preferences, serviceSystem: ServiceChecks.Pc()) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            Color Resource(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;
            Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
            var panel = (Panel)window.FindName("AccentPanel");
            var settings = (Button)window.FindName("SettingsButton");
            settings.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            var card = (Expander)window.FindName("SettingsSizeSection"); card.IsExpanded = true; UiLayoutChecks.Wait(400);
            var buttons = panel.Children.OfType<RadioButton>().ToList();
            check(buttons.Count == AccentPalette.All.Count && buttons[0].IsChecked == true && buttons.Count(b => b.IsChecked == true) == 1 && buttons.All(b => b.IsDescendantOf(card)),
                "The Appearance card shows one swatch per color, with the default (the icon's blue) chosen");
            check(Resource("TealTextBrush") == Hex("#3FC3EE") && ((Color)Application.Current.Resources["PrimaryAccentColor"]) == Hex("#3FC3EE") && Resource("AccentHoverBrush") == Hex("#8EDDF7"),
                "A fresh Hush in the Dark theme uses the icon's blue as its main color and its own hover shade");

            // Choose teal with a real click: every accent brush follows and the choice is saved.
            var teal = buttons.First(b => (string)b.Tag == "hush-teal");
            teal.IsChecked = true; teal.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); UiLayoutChecks.Wait(430);
            check(Resource("TealTextBrush") == Hex("#38CFC8") && Resource("SafetyGreenBrush") == Hex("#38CFC8") && Resource("TealBrush") == Hex("#38CFC8") && Resource("AccentHoverBrush") == Hex("#7FE3DD")
                && Preferences.Load(preferences).Accent == "hush-teal" && buttons.Count(b => b.IsChecked == true) == 1,
                "Choosing a swatch recolors the accent brushes (and the hover shade) and saves the choice");

            // The Light theme uses the chosen color's darker Light shade (never the pale Dark one), swatches show the shade in use, and going back restores the Dark one.
            var teal2 = AccentPalette.Find("hush-teal");
            window.ToggleTheme(); UiLayoutChecks.Wait(430);
            check(Resource("TealTextBrush") == Hex(teal2.LightColor) && Resource("AccentHoverBrush") == Hex(teal2.LightHover) && teal2.LightColor != teal2.Color
                && ((SolidColorBrush)teal.Background).Color == Hex(teal2.LightColor) && !((TextBlock)window.FindName("AccentSummary")).Text.Contains("Light theme keeps"),
                "In the Light theme the chosen color shows as its slightly darker Light shade (accent, hover and the swatch itself) and the card no longer says Dark only");
            window.ChooseAccent("violet"); UiLayoutChecks.Wait(430);
            check(Resource("TealTextBrush") == Hex(AccentPalette.Find("violet").LightColor) && Preferences.Load(preferences).Accent == "violet", "Choosing a color while in the Light theme works the same way");
            window.ChooseAccent("hush-teal"); UiLayoutChecks.Wait(100);
            window.ToggleTheme(); UiLayoutChecks.Wait(430);
            check(Resource("TealTextBrush") == Hex("#38CFC8") && ((SolidColorBrush)teal.Background).Color == Hex("#38CFC8"), "Back in Dark, the chosen color and the swatches return to their Dark shades");

            // A second window starts from the saved choice.
            window.ChooseAccent("violet"); UiLayoutChecks.Wait(430);
            check(Preferences.Load(preferences).Accent == "violet" && Resource("TealTextBrush") == Hex("#A78BFA"), "Every swatch works the same way (violet) in Dark");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string shots)
            {
                foreach (var (id, light) in new[] { ("hush-blue", false), ("violet", false), ("hush-blue", true), ("orchid", true), ("indigo", true) })
                {
                    if (window.IsLightTheme != light) window.ToggleTheme();
                    window.ChooseAccent(id); panel.BringIntoView(); UiLayoutChecks.Wait(700);
                    UiLayoutChecks.Layout((FrameworkElement)window.Content, new Size(920, 820));
                    ThemeMotionChecks.Capture((FrameworkElement)window.Content, $"appearance-{id}-{(light ? "light" : "dark")}");
                }
            }
        }
        finally { window.Close(); }
    }
}
