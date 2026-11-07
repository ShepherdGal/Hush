using System.Windows;
using System.Windows.Media;

namespace Hush;

internal static class ProgressRing
{
    internal static Geometry Create(double percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent <= 0) return Geometry.Empty;
        if (percent >= 100) return new EllipseGeometry(new Point(79, 79), 63, 63);
        double angle = percent / 100 * Math.PI * 2;
        var end = new Point(79 + 63 * Math.Sin(angle), 79 - 63 * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(79, 16), false, false);
            context.ArcTo(end, new Size(63, 63), 0, percent > 50, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
