using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hush;

// Animate live resources only. Page navigation owns visibility; themes never capture a page.
internal static class ThemePalette
{
    internal const double Milliseconds = 320;
    private sealed record Transition(Color Target, Stopwatch Clock);
    private static readonly ConditionalWeakTable<SolidColorBrush, Transition> transitions = new();
    internal static Color Destination(SolidColorBrush brush) => transitions.TryGetValue(brush, out var state) ? state.Target : brush.Color;
    internal static double Remaining(SolidColorBrush brush) => transitions.TryGetValue(brush, out var state)
        ? Math.Max(0, Milliseconds - state.Clock.Elapsed.TotalMilliseconds) : 0;

    internal static void Set(ResourceDictionary resources, string key, string color, bool animate)
    {
        Color target = (Color)ColorConverter.ConvertFromString(color);
        Color from = resources[key] is SolidColorBrush previous ? previous.Color : target;
        // A newly attached WPF clock can wait until the next render tick. Its base
        // color must therefore already be the displayed start, never the destination.
        var brush = new SolidColorBrush(animate ? from : target);
        if (animate && from != target)
        {
            transitions.Add(brush, new(target, Stopwatch.StartNew()));
            var animation = new ColorAnimation(from, target, TimeSpan.FromMilliseconds(Milliseconds))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            animation.Completed += (_, _) =>
            {
                // This completion only owns its brush; rapid reversals create independent clocks.
                brush.Color = target;
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                transitions.Remove(brush);
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }
        resources[key] = brush;
    }
}
