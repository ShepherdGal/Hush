using System.Windows;
using System.Windows.Media;

namespace Hush;

// One suggestion on an Overview (Tools and Tweaks): an icon, what is worth a look, one plain line, and one button that opens it.
internal sealed record Suggestion(string Key, Geometry Icon, string Title, string Line, string Action, Action Run)
{
    public string SpokenName => Action + ": " + Title;
}

// The left rail of Tools and Tweaks (owner, November 21, 2026: "it should not have 2 screens"; owner's picks: the rail always on the left with an Overview
// entry first, the Overview names a few things worth a look ("smart suggestions"), and the window keeps ONE size, 920, like Settings). Both rails use the
// shared RailPanel + RailTemplate and the Settings index's look; a suggestion only names what is already known, nothing is read to fill it.
public partial class MainWindow
{
    // The Overview's icon: a small house, on the rail's 20 px grid.
    internal static readonly Geometry OverviewIcon = Frozen("M3.5,9 L10,3.5 L16.5,9 V16.5 H3.5 Z M8,16.5 V12 H12 V16.5");

    private static Geometry Frozen(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    // An icon drawn on the 24 px grid (UpdateIcons) moved onto the rail's 20 px grid; the pen is not scaled, so strokes stay as thin as the Settings index's.
    internal static Geometry RailGlyph(Geometry? onTwentyFour)
    {
        if (onTwentyFour == null) return Geometry.Empty;
        var geometry = onTwentyFour.Clone();
        geometry.Transform = new ScaleTransform(20 / 24.0, 20 / 24.0);
        geometry.Freeze();
        return geometry;
    }

    // One click handler for both rails: the entry's own list says which page it belongs to.
    private void Rail_Select(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TweaksTab entry) return;
        if (toolRail.Contains(entry)) { ShowTool(entry.Key); Motion.Reveal(ToolFocusCard); }
        else { ShowTweaksTab(entry.Key); Motion.Reveal(TweaksCard); }
    }

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Suggestion suggestion) suggestion.Run();
    }
}
