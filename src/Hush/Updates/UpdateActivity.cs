using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hush;

// A thin track used while Hush talks to the update server.
// Indeterminate (checking, verifying): a calm arc travels along a quiet track, like an orbiting arc laid out in a line: a crisp rounded
// arc in the accent with a fainter arc trailing behind it, easing in and out over 2.4 s per pass and leaving and re-entering at the track ends.
// Determinate (downloading): the track fills with the real download fraction, easing between reports.
// Everything is a WPF animation on a transform, so it runs smoothly on the render thread instead of repainting from a timer.
public sealed class UpdateActivity : Decorator
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(UpdateActivity), new PropertyMetadata(false, Changed));
    // NaN = indeterminate; 0..1 = real progress.
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(UpdateActivity), new PropertyMetadata(double.NaN, Changed));
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    private readonly Border track = new() { CornerRadius = new CornerRadius(2), Opacity = 0.12 };
    // The moving pair of arcs: a faint trailing arc, a small gap, then the bright leading arc (same proportions as the circular orbit: 17%, 4%, 30.5%).
    private readonly Grid glide = new() { HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
    private readonly Border tailArc = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.35 };
    private readonly Border headArc = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.95 };
    private readonly Border fill = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.9, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TranslateTransform shift = new();
    private readonly ScaleTransform grow = new(0, 1);
    public UpdateActivity()
    {
        ClipToBounds = true;
        track.SetResourceReference(Border.BackgroundProperty, "TealTextBrush");
        fill.SetResourceReference(Border.BackgroundProperty, "TealTextBrush");
        tailArc.SetResourceReference(Border.BackgroundProperty, "TealTextBrush");
        headArc.SetResourceReference(Border.BackgroundProperty, "TealTextBrush");
        glide.Children.Add(tailArc); glide.Children.Add(headArc);
        glide.RenderTransform = shift;
        fill.RenderTransform = grow; fill.RenderTransformOrigin = new Point(0, 0.5);
        var layers = new Grid();
        layers.Children.Add(track); layers.Children.Add(glide); layers.Children.Add(fill);
        Child = layers;
        SizeChanged += (_, _) => Restart();
        Unloaded += (_, _) => Stop();
        Loaded += (_, _) => Restart();
        System.Windows.Automation.AutomationProperties.SetName(this, "Update request in progress");
    }
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        var activity = (UpdateActivity)value;
        // Progress ticks while already filling only retarget the fill, so the bar glides instead of restarting.
        if (args.Property == ProgressProperty && activity.IsRunning && activity.IsLoaded && !double.IsNaN((double)args.OldValue) && !double.IsNaN((double)args.NewValue))
            activity.Retarget();
        else activity.Restart();
    }
    private void Retarget() =>
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(grow.ScaleX, Math.Clamp(Progress, 0, 1), TimeSpan.FromMilliseconds(240))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd });
    private void Stop()
    {
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, null);
    }
    private void Restart()
    {
        Stop();
        bool active = IsRunning && IsLoaded && ActualWidth > 0;
        track.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        if (!active) { glide.Visibility = fill.Visibility = Visibility.Collapsed; return; }
        double progress = Progress;
        if (double.IsNaN(progress))
        {
            fill.Visibility = Visibility.Collapsed;
            glide.Visibility = Visibility.Visible;
            // Solid, crisp rounded arcs (no glow) in the current theme's accent, sized from the track width.
            double headWidth = Math.Max(24, ActualWidth * 0.305), gap = Math.Max(4, ActualWidth * 0.04), tailWidth = Math.Max(14, ActualWidth * 0.17);
            tailArc.Width = tailWidth; headArc.Width = headWidth;
            double total = tailWidth + gap + headWidth;
            glide.Width = total;
            // The pair enters from the left edge and leaves past the right edge, easing in and out like the orbit's turn.
            var sweep = new DoubleAnimation(-total, ActualWidth, TimeSpan.FromMilliseconds(2400))
            { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            shift.BeginAnimation(TranslateTransform.XProperty, sweep);
        }
        else
        {
            glide.Visibility = Visibility.Collapsed;
            fill.Visibility = Visibility.Visible;
            fill.Width = ActualWidth;
            grow.ScaleX = 0;
            Retarget();
        }
    }
}
