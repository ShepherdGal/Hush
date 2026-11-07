using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Hush;

// Locks the colour rules: the Clean button's red is the ONLY red in Hush, and the dark accent is the icon's ice blue.
// Source files are scanned as text, so nobody can add a second red (or a "slightly different" one) without this suite failing.
internal static class ColorRuleChecks
{
    // The only red-family values that may appear in the source: Clean red in Dark and in Light (alpha prefixes are allowed for fills/glows).
    private static readonly string[] AllowedReds = ["FF795C", "B54428"];
    private static readonly string[] RedNames = ["Red", "DarkRed", "Crimson", "Firebrick", "OrangeRed", "Tomato", "IndianRed", "Maroon", "Salmon", "Coral", "DarkSalmon", "LightCoral", "Brown"];

    internal static bool IsRedFamily(string rgb)
    {
        int r = Convert.ToInt32(rgb[..2], 16), g = Convert.ToInt32(rgb[2..4], 16), b = Convert.ToInt32(rgb[4..], 16);
        double max = Math.Max(r, Math.Max(g, b)) / 255.0, min = Math.Min(r, Math.Min(g, b)) / 255.0, delta = max - min;
        if (max < 0.35 || delta <= 0 || delta / max < 0.4) return false;
        double hue = max == r / 255.0 ? 60 * (((g - b) / 255.0 / delta) % 6) : max == g / 255.0 ? 60 * ((b - r) / 255.0 / delta + 2) : 60 * ((r - g) / 255.0 / delta + 4);
        if (hue < 0) hue += 360;
        return hue <= 22 || hue >= 340;
    }

    internal static void Run(Action<bool, string> check)
    {
        string testsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string source = Path.GetFullPath(Path.Combine(testsDirectory, "..", "..", "src", "Hush"));
        var files = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs") || path.EndsWith(".xaml") || path.EndsWith(".ps1"))
            .Where(path => !path.Contains(@"\obj\") && !path.Contains(@"\bin\")).ToList();
        check(files.Count > 20, "The colour rules scan the real source files");
        var hex = new Regex(@"#(?:[0-9A-Fa-f]{2})?([0-9A-Fa-f]{6})\b");
        var named = new Regex(@"(?:Brushes|Colors)\.(" + string.Join("|", RedNames) + @")\b|=""(" + string.Join("|", RedNames) + @")""");
        var offenders = new List<string>();
        int allowedUses = 0;
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (Match match in hex.Matches(text))
            {
                string rgb = match.Groups[1].Value.ToUpperInvariant();
                if (!IsRedFamily(rgb)) continue;
                if (AllowedReds.Contains(rgb)) allowedUses++;
                else offenders.Add($"{Path.GetFileName(file)} #{rgb}");
            }
            foreach (Match match in named.Matches(text)) offenders.Add($"{Path.GetFileName(file)} {match.Value}");
        }
        check(offenders.Count == 0, "No red other than the Clean button's red exists anywhere in the source" + (offenders.Count == 0 ? "" : ": " + string.Join(", ", offenders.Distinct())));
        check(allowedUses >= 4, $"The Clean red is actually used ({allowedUses} references), so the scan is not passing by finding nothing");
        check(IsRedFamily("FF795C") && IsRedFamily("B54428") && IsRedFamily("FF5C6C") && IsRedFamily("B3263A") && IsRedFamily("DC143C") && !IsRedFamily("FFA24D") && !IsRedFamily("B35F0C")
            && !IsRedFamily("7FE0F6") && !IsRedFamily("4C7BC8"), "The red detector flags reds (including the removed ones) and not the Review orange or the accents");

        foreach (bool light in new[] { false, true })
        {
            string clean = SafetyPalette.Danger(light);
            check(AllowedReds.Contains(clean.TrimStart('#').ToUpperInvariant()) && SafetyPalette.Text(SafetyLevel.Advanced, light) == clean,
                $"Advanced items use exactly the Clean button red in {(light ? "Light" : "Dark")}");
            check(!IsRedFamily(SafetyPalette.Text(SafetyLevel.Review, light).TrimStart('#')) && !IsRedFamily(SafetyPalette.Accent(light).TrimStart('#')),
                $"Review (orange) and the accent are never red in {(light ? "Light" : "Dark")}");
        }
        check(SafetyPalette.Color(SafetyLevel.Advanced) == SafetyPalette.Danger(false) && SafetyPalette.Accent(false) == "#7FE0F6" && SafetyPalette.Accent(true) == "#4C7BC8",
            "The dark accent is the icon's ice blue (#7FE0F6) and the light accent is unchanged");
        string app = File.ReadAllText(Path.Combine(source, "App.xaml"));
        check(app.Contains("<Color x:Key=\"PrimaryAccentColor\">#7FE0F6</Color>") && !app.Contains("3CCFD9"), "App.xaml starts in the ice-blue accent and no longer contains the old aqua");
        check(!files.Any(path => File.ReadAllText(path).Contains("3CCFD9", StringComparison.OrdinalIgnoreCase)), "The old aqua accent appears nowhere in the source");
    }
}
