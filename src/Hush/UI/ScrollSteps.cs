using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Hush;

// The up and down chevrons at the ends of the scroll bar of a category's file list (owner, November 11, 2026): every click moves the list by exactly one item, so the
// next (or previous) file lines up at the top; holding the button repeats. One class handler serves every chevron (a RepeatButton marked with the attached Direction),
// so no screen needs its own code. The move glides like the wheel does (SmoothScroll.MoveBy) and is measured from where the list is heading, so quick repeated clicks
// advance one item each instead of re-measuring a half-finished glide.
public static class ScrollSteps
{
    public static readonly DependencyProperty DirectionProperty = DependencyProperty.RegisterAttached("Direction", typeof(int), typeof(ScrollSteps), new PropertyMetadata(0));
    public static int GetDirection(DependencyObject value) => (int)value.GetValue(DirectionProperty);
    public static void SetDirection(DependencyObject value, int direction) => value.SetValue(DirectionProperty, direction);

    private static bool installed;

    public static void Install()
    {
        if (installed) return;
        installed = true;
        EventManager.RegisterClassHandler(typeof(RepeatButton), ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RepeatButton button || GetDirection(button) == 0) return;
        for (DependencyObject? element = button; element != null; element = VisualTreeHelper.GetParent(element))
            if (element is ScrollViewer viewer) { Step(viewer, GetDirection(button)); e.Handled = true; return; }
    }

    // Moves the viewer by one item in the direction (+1 down, -1 up) and returns how far it asked it to move (test seam).
    internal static double Step(ScrollViewer viewer, int direction)
    {
        double target = SmoothScroll.TargetOffset(viewer), shift = target - viewer.VerticalOffset;
        var panel = viewer.Content is DependencyObject content ? SmoothScroll.FirstVirtualizingPanel(content, 0) : null;
        double distance;
        // Where each realized item will sit (relative to the top of the viewport) once the viewer has reached its target.
        var tops = new List<double>();
        if (panel != null)
            foreach (UIElement child in panel.Children)
                if (child is FrameworkElement item && item.IsVisible) tops.Add(item.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y - shift);
        if (direction > 0)
        {
            double next = tops.Where(top => top > 1.0).DefaultIfEmpty(double.NaN).Min();
            distance = double.IsNaN(next) ? viewer.ScrollableHeight - target : next;   // no item below the top: go to the end
        }
        else
        {
            double previous = tops.Where(top => top < -1.0).DefaultIfEmpty(double.NaN).Max();
            distance = double.IsNaN(previous) ? -target : previous;                      // no item above the top: go to the start
        }
        if (Math.Abs(distance) >= 0.5) SmoothScroll.MoveBy(viewer, distance);
        return distance;
    }
}
