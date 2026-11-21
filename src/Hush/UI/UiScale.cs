using System.Windows;

namespace Hush;

// The interface size choice (Settings > Interface size). The whole window content is scaled as one piece (a layout transform), so text, spacing
// and buttons keep their proportions and the window itself is simply made that much bigger or smaller. All sizes here are device-independent units.
internal static class UiScale
{
    internal const string Smart = "smart";
    internal static readonly string[] Choices = [Smart, "75", "100", "120"];

    // The windows one size at 100% (owner, November 20, 2026: never resized by hand; the interface size scales it).
    internal const double BaseWidth = 920, BaseHeight = 820;
    // Never fill the whole screen: leave room for the taskbar, window edges and the shadow.
    private const double Margin = 0.96;

    internal static string Normalize(string? choice) => Choices.Contains(choice) ? choice! : Smart;

    // Smart keeps the established 100% look whenever the window fits, shrinks it on small screens (laptops, high Windows scaling),
    // and grows it to 120% only on very large screens where 100% would look small.
    internal static double SmartScale(Rect workArea)
    {
        double fit = Math.Min(workArea.Width * Margin / BaseWidth, workArea.Height * Margin / BaseHeight);
        if (fit < 1) return Math.Max(0.6, Math.Floor(fit * 20) / 20);
        return workArea.Width >= 1700 && workArea.Height >= 1380 ? 1.2 : 1.0;
    }

    // The biggest scale at which the window still fits on this screen. A chosen size above it is lowered to it.
    internal static double LargestThatFits(Rect workArea) =>
        Math.Max(0.5, Math.Floor(Math.Min(workArea.Width * Margin / BaseWidth, workArea.Height * Margin / BaseHeight) * 20) / 20);

    internal static double Requested(string choice, Rect workArea) => Normalize(choice) switch
    {
        "75" => 0.75,
        "100" => 1.0,
        "120" => 1.2,
        _ => SmartScale(workArea)
    };

    internal static double Resolve(string choice, Rect workArea) => Math.Min(Requested(choice, workArea), LargestThatFits(workArea));

    // One size for every page (owner, November 21, 2026: "I want the app to always be same size"): Tools and Tweaks fit their rail into it, like Settings.
    internal static Size WindowSize(double scale, Rect workArea) =>
        new(Math.Min(BaseWidth * scale, workArea.Width), Math.Min(BaseHeight * scale, workArea.Height));

    internal static string Label(string choice, double scale) =>
        Normalize(choice) == Smart ? $"Smart · {scale * 100:0}%" : $"{scale * 100:0}%";
}
