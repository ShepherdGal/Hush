using System.Windows.Media;

namespace Hush;

// The choices for the main (accent) color (Settings > Appearance, owner request November 11, 2026). The first one is the blue of the app icon and the default;
// the older teal stays as the second. A short fixed list instead of a free color picker, so every choice stays readable and none can be mistaken for the three
// risk colors: no red, pink or coral (the Clean red is the only red), no amber (Review) and no green (Safe).
// Each swatch has a Dark shade (its own color) and a Light shade: the same color darkened by a small amount, just enough to read on the light surfaces (pale
// cyan or silver on a white-ish panel would vanish), with its own hover shade. The default blue keeps the soft blue the Light theme always had.
public sealed record AccentSwatch(string Id, string Name, string Color, string Hover, string LightColor, string LightHover)
{
    public Color ColorValue => (Color)ColorConverter.ConvertFromString(Color);
    public Color LightColorValue => (Color)ColorConverter.ConvertFromString(LightColor);
    public string For(bool light) => light ? LightColor : Color;
    public string HoverFor(bool light) => light ? LightHover : Hover;
}

public static class AccentPalette
{
    public const string DefaultId = "hush-blue";
    // The surfaces the Light shade must be readable on: the window, and the raised surface switches and chips sit on (ThemePalette, Light).
    internal const string LightWindow = "#EFF1F5", LightRaised = "#E4E8F0";
    // The contrast a Light shade reaches on the Light window (the soft blue it replaces measures about 3.9:1 there).
    internal const double LightContrast = 4.0;

    public static readonly IReadOnlyList<AccentSwatch> All =
    [
        Make("hush-blue", "Hush blue (the icon)", "#3FC3EE", "#8EDDF7", "#4C7BC8", "#3A66AE"),
        Make("hush-teal", "Teal", "#38CFC8", "#7FE3DD"),
        Make("indigo", "Indigo", "#7F9CFF", "#A9BDFF"),
        Make("violet", "Violet", "#A78BFA", "#C4B0FD"),
        Make("orchid", "Orchid", "#D58CF0", "#E4B4F7"),
        Make("silver", "Silver", "#AEB9C9", "#D0D8E4"),
    ];

    // Anything unknown (an old or edited settings file) reads as the default.
    public static AccentSwatch Find(string? id) => All.FirstOrDefault(swatch => swatch.Id == id) ?? All[0];
    public static string Normalize(string? id) => Find(id).Id;

    private static AccentSwatch Make(string id, string name, string dark, string darkHover, string? light = null, string? lightHover = null)
    {
        light ??= DarkenForLight(dark);
        lightHover ??= Shade(light, -0.07);
        return new AccentSwatch(id, name, dark, darkHover, light, lightHover);
    }

    // The same hue, darkened in small steps (a little less saturated, since a darkened vivid color looks loud) until it reaches LightContrast on the Light window.
    internal static string DarkenForLight(string dark)
    {
        var (h, s, l) = ToHsl(dark);
        for (int step = 0; step < 60; step++)
        {
            string candidate = FromHsl(h, s * (0.8 + 0.004 * step), l - 0.01 * step);
            if (Contrast(candidate, LightWindow) >= LightContrast) return candidate;
        }
        return FromHsl(h, s, 0.25);
    }

    private static string Shade(string hex, double lightnessChange) { var (h, s, l) = ToHsl(hex); return FromHsl(h, s, Math.Clamp(l + lightnessChange, 0, 1)); }

    internal static double Contrast(string a, string b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        static double Channel(byte value) { double v = value / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static (double H, double S, double L) ToHsl(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    private static string FromHsl(double h, double s, double l)
    {
        l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2, r, g, b;
        (r, g, b) = (int)(h / 60) switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        static int B(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return $"#{B(r + m):X2}{B(g + m):X2}{B(b + m):X2}";
    }
}
