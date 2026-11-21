using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hush;

// The fill of a row's progress bar while something downloads and installs without telling how far it is (owner, November 20, 2026: "easing update
// animation for any app that is downloading and updating"). The shared loading-bar rule: one bar that only fills, never a travelling one; where there is no
// real progress it eases toward nearly full over the expected time and completes only when the work is really done.
// States: "idle" (no bar), "queued" (an empty track: waiting its turn), "working" (eases from empty to 92 % over the expected time, slower and slower),
// "done" (eases the rest of the way, then the bar fades), "failed" (the bar fades where it is). Scale and opacity only, on the render thread; nothing repeats.
public static class EasedProgress
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(string), typeof(EasedProgress),
        new PropertyMetadata("idle", (element, args) => Apply((FrameworkElement)element, (string)args.NewValue)));
    public static string GetState(DependencyObject element) => (string)element.GetValue(StateProperty);
    public static void SetState(DependencyObject element, string value) => element.SetValue(StateProperty, value);

    // How long the expected work takes, in seconds (the fill reaches 92 % by then and keeps creeping only if the work takes longer).
    public static readonly DependencyProperty ExpectedSecondsProperty = DependencyProperty.RegisterAttached("ExpectedSeconds", typeof(double), typeof(EasedProgress),
        new PropertyMetadata(40.0));
    public static double GetExpectedSeconds(DependencyObject element) => (double)element.GetValue(ExpectedSecondsProperty);
    public static void SetExpectedSeconds(DependencyObject element, double value) => element.SetValue(ExpectedSecondsProperty, value);

    internal const double WorkingCeiling = 0.92, DoneMilliseconds = 450, FadeMilliseconds = 300;

    // The element is the track (a Border); its child is the fill with a ScaleTransform (origin at the left).
    private static void Apply(FrameworkElement track, string state)
    {
        if ((track as System.Windows.Controls.Border)?.Child is not FrameworkElement fill) return;
        if (fill.RenderTransform is not ScaleTransform scale || scale.IsFrozen) { scale = new ScaleTransform(0, 1); fill.RenderTransform = scale; fill.RenderTransformOrigin = new Point(0, 0.5); }
        double now = scale.ScaleX;
        switch (state)
        {
            case "queued":
                Stop(track, scale, 0); track.Visibility = Visibility.Visible; track.Opacity = 1;
                break;
            case "working":
                track.Visibility = Visibility.Visible; track.BeginAnimation(UIElement.OpacityProperty, null); track.Opacity = 1;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(now, WorkingCeiling, Motion.Span(GetExpectedSeconds(track) * 1000))
                    { EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 3 } });
                break;
            case "done":
                track.Visibility = Visibility.Visible;
                var finish = new DoubleAnimation(now, 1, Motion.Span(DoneMilliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                finish.Completed += (_, _) => FadeOut(track);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, finish);
                break;
            case "failed":
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.ScaleX = now;
                FadeOut(track);
                break;
            default:
                Stop(track, scale, 0); track.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private static void Stop(FrameworkElement track, ScaleTransform scale, double value)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.ScaleX = value;
        track.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private static void FadeOut(FrameworkElement track)
    {
        var fade = new DoubleAnimation(0, Motion.Span(FadeMilliseconds));
        fade.Completed += (_, _) => { if (GetState(track) is "done" or "failed") track.Visibility = Visibility.Collapsed; };
        track.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // Test seam: the fill's current width share (0 to 1).
    internal static double Shown(FrameworkElement track) =>
        (track as System.Windows.Controls.Border)?.Child is FrameworkElement { RenderTransform: ScaleTransform scale } ? scale.ScaleX : 0;
}
