using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hush;

// Locks the colour rules: the Clean button's red is the ONLY red in Hush, and the dark accent defaults to the icon's blue (the person can choose another in Settings).
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
        check(SafetyPalette.Color(SafetyLevel.Advanced) == SafetyPalette.Danger(false) && SafetyPalette.Accent(false) == "#3FC3EE" && SafetyPalette.Accent(true) == "#4C7BC8",
            "The default dark accent is the icon's blue #3FC3EE (Dark can be changed in Settings > Appearance) and the light accent is the soft blue #4C7BC8");
        string app = File.ReadAllText(Path.Combine(source, "App.xaml"));
        check(app.Contains("<Color x:Key=\"PrimaryAccentColor\">#3FC3EE</Color>"), "App.xaml starts in the icon's blue accent");
        check(!files.Any(path => { string text = File.ReadAllText(path); return new[] { "7FE0F6", "B5EEFB", "3CCFD9", "80E0E6" }.Any(old => text.Contains(old, StringComparison.OrdinalIgnoreCase)); }),
            "The rejected ice-blue and the earlier aqua (#3CCFD9) appear nowhere in the source");

        // The icon is the owner's supplied artwork: its corners are truly transparent and the ICO holds every size.
        string assets = Path.Combine(source, "Assets");
        var decoder = new PngBitmapDecoder(new Uri(Path.Combine(assets, "Hush-icon-source.png")), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        int width = frame.PixelWidth, stride = width * 4;
        byte[] pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        // The icon is the owner's artwork exactly as supplied (no recolor): its lit rim is the bright cyan-blue of the original, and the dark fill is navy.
        int centreI = 256 * stride + 40 * 4;   // inside the square, left of the brush
        check(pixels[10 * stride + 200 * 4 + 3] >= 250 && pixels[10 * stride + 200 * 4 + 2] < pixels[10 * stride + 200 * 4] && pixels[centreI + 3] == 255 && pixels[centreI] > pixels[centreI + 2],
            "The icon keeps the supplied artwork's own colors (blue-cyan rim, navy fill), not the app's teal");
        check(pixels[3] == 0 && pixels[stride - 1] == 0 && pixels[(frame.PixelHeight - 1) * stride + 3] == 0 && width == 512, "The icon's corners are real transparency, not a black or glowing surround");
        byte[] ico = File.ReadAllBytes(Path.Combine(assets, "Hush.ico"));
        int frames = BitConverter.ToUInt16(ico, 4);
        check(ico[0] == 0 && ico[2] == 1 && frames == 7 && ico.Length > 20000 && !File.Exists(Path.Combine(assets, "Icon-layout.json")),
            "Hush.ico holds the seven sizes (16 to 256) of the new icon and the old icon's layout file is gone");
        check(File.ReadAllText(Path.Combine(source, "Hush.csproj")).Contains(@"<ApplicationIcon>Assets\Hush.ico</ApplicationIcon>")
            && File.ReadAllText(Path.Combine(source, "MainWindow.xaml")).Contains("Hush;component/Assets/Hush.ico")
            && File.ReadAllText(Path.Combine(source, "Setup", "SetupWindow.xaml")).Contains("Hush;component/Assets/Hush.ico"),
            "The executable and both windows use the new icon");
    }
}
