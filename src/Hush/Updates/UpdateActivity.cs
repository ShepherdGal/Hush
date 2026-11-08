using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hush;

// A thin track used while Hush talks to the update server.
// Indeterminate (checking, verifying): ONE calm bar travels along a quiet track (no second bar follows it): a crisp rounded bar in the
// accent, easing in and out over 2.4 s per pass, leaving at the right end and entering again at the left.
// Determinate (downloading): the track fills with the real download fraction, easing between reports.
// Everything is a WPF animation on a transform, so it runs smoothly on the render thread instead of repainting from a timer.
public sealed class UpdateActivity : Decorator
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(UpdateActivity), new PropertyMetadata(false, Changed));
    // NaN = indeterminate; 0..1 = real progress.
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(UpdateActivity), new PropertyMetadata(double.NaN, Changed));
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    private readonly Border track = new() { CornerRadius = new CornerRadius(2.5), Opacity = 0.12 };
    // The one moving bar (about 30% of the track).
    private readonly Border glide = new() { CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.95, IsHitTestVisible = false };
    private readonly Border fill = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.9, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TranslateTransform shift = new();
    private readonly ScaleTransform grow = new(0, 1);
    public UpdateActivity()
    {
        ClipToBounds = true;
        track.SetResourceReference(Border.BackgroundProperty, "TealTextBrush");
        fill.SetResourceReference(Border.BackgroundProperty, "TealTextBrush");
        glide.SetResourceReference(Border.BackgroundProperty, "TealTextBrush");
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
            // One solid, crisp rounded bar (no glow, nothing trailing) in the current theme's accent, sized from the track width.
            double total = Math.Max(28, ActualWidth * 0.30);
            glide.Width = total;
            // The bar enters from the left edge and leaves past the right edge, easing in and out.
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
