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

    // Gradient brushes of the Frost look (window, glow, header band; Phase D). A new brush is made for every theme change and each of its stops eases from the
    // color the old brush showed to the new one, on WPF's render thread, exactly like the solid brushes above (320 ms, the shared ease-out). The brushes are
    // static fills of non-moving layers, never effects.
    internal static void SetLinear(ResourceDictionary resources, string key, Point start, Point end, string[] colors, bool animate)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        Fill(resources, key, brush, colors, animate);
    }

    internal static void SetRadial(ResourceDictionary resources, string key, Point center, double radius, string[] colors, bool animate)
    {
        var brush = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = radius, RadiusY = radius };
        Fill(resources, key, brush, colors, animate);
    }

    private static void Fill(ResourceDictionary resources, string key, GradientBrush brush, string[] colors, bool animate)
    {
        var previous = resources[key] as GradientBrush;
        for (int index = 0; index < colors.Length; index++)
        {
            Color target = (Color)ColorConverter.ConvertFromString(colors[index]);
            Color from = previous != null && index < previous.GradientStops.Count ? previous.GradientStops[index].Color : target;
            var stop = new GradientStop(animate ? from : target, colors.Length == 1 ? 0 : index / (double)(colors.Length - 1));
            brush.GradientStops.Add(stop);
            if (animate && from != target)
            {
                var animation = new ColorAnimation(from, target, TimeSpan.FromMilliseconds(Milliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                animation.Completed += (_, _) => { stop.Color = target; stop.BeginAnimation(GradientStop.ColorProperty, null); };
                stop.BeginAnimation(GradientStop.ColorProperty, animation);
            }
        }
        resources[key] = brush;
    }
}
