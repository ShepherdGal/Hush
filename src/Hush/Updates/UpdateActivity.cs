using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hush;

// The one loading line used everywhere in Hush (update check and download, Boost): a thin track with ONE bar that fills smoothly from empty to full.
// Never a travelling or sweeping bar. Where real progress exists (Progress = 0..1: download bytes, files cleaned) the bar follows it, easing between reports.
// Where it does not (Progress = NaN: the update check, freeing memory) the caller asks for FillOver(duration): the bar fills over the stage's minimum
// time, slowing as it nears the end, and only CompleteAsync() takes it to full once the work is really done, so it never sits at "full" early.
// Everything is a short WPF animation on a transform (render thread), not a timer repaint, and nothing loops, so no frame-rate cap is needed.
public sealed class UpdateActivity : Decorator
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(UpdateActivity), new PropertyMetadata(false, Changed));
    // NaN = no real progress (see FillOver); 0..1 = real progress.
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(UpdateActivity), new PropertyMetadata(double.NaN, Changed));
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    // How full the bar is drawn right now (0..1); the value a test or a screen reader can read.
    public double Fill => grow.ScaleX;
    private readonly Border track = new() { CornerRadius = new CornerRadius(2.5), Opacity = 0.12 };
    private readonly Border fill = new() { CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.9, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly ScaleTransform grow = new(0, 1);

    public UpdateActivity()
    {
        ClipToBounds = true;
        track.SetResourceReference(Border.BackgroundProperty, "TealTextBrush");
        fill.SetResourceReference(Border.BackgroundProperty, "TealTextBrush");
        fill.RenderTransform = grow; fill.RenderTransformOrigin = new Point(0, 0.5);
        var layers = new Grid();
        layers.Children.Add(track); layers.Children.Add(fill);
        Child = layers;
        SizeChanged += (_, _) => Restart();
        Unloaded += (_, _) => Stop();
        Loaded += (_, _) => Restart();
        System.Windows.Automation.AutomationProperties.SetName(this, "Update request in progress");
    }

    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        var activity = (UpdateActivity)value;
        // A real value while the bar is already showing only retargets it, so it glides instead of restarting from empty.
        if (args.Property == ProgressProperty && activity.IsRunning && activity.IsLoaded && !double.IsNaN((double)args.NewValue)) activity.Retarget(Math.Clamp((double)args.NewValue, 0, 1), 240);
        else if (args.Property == ProgressProperty && activity.IsRunning && double.IsNaN((double)args.NewValue)) return; // back to "no real progress": keep what is drawn
        else activity.Restart();
    }

    private static DoubleAnimation Animation(double from, double to, double milliseconds, bool slowEnd = false)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)))
        { EasingFunction = slowEnd ? new QuadraticEase { EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
        // (the frame rate is left to WPF: a 30 fps cap on these short fills starved other animations in windows that are not shown)
        return animation;
    }

    private void Retarget(double to, double milliseconds) { if (ActualWidth > 0) grow.BeginAnimation(ScaleTransform.ScaleXProperty, Animation(grow.ScaleX, to, milliseconds)); }

    // No real progress: fill toward `to` (never full) over `duration`, easing out so the last stretch is slow.
    // Remembered, so a bar that is laid out a moment later (a popup still opening) still plays it from the start.
    private (TimeSpan Duration, double To)? timed;
    public void FillOver(TimeSpan duration, double to = 0.92)
    {
        timed = (duration, Math.Clamp(to, 0, 1));
        if (IsRunning && IsLoaded && ActualWidth > 0) PlayTimed();
    }
    private void PlayTimed()
    {
        if (timed is not { } plan) return;
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, Animation(grow.ScaleX, plan.To, plan.Duration.TotalMilliseconds, slowEnd: true));
    }

    // The work is really done: take the bar to full and let the person see it arrive.
    public Task CompleteAsync()
    {
        timed = null;
        if (!IsRunning || !IsLoaded || ActualWidth <= 0) return Task.CompletedTask;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = Animation(grow.ScaleX, 1, 260);
        animation.Completed += (_, _) => finished.TrySetResult();
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        // Animations only tick while something is drawn; a minimized window must not leave the caller waiting, so a short watchdog finishes the move.
        _ = Task.Delay(700).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (finished.Task.IsCompleted) return;
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            grow.ScaleX = 1;
            finished.TrySetResult();
        }), TaskScheduler.Default);
        return finished.Task;
    }

    private void Stop() => grow.BeginAnimation(ScaleTransform.ScaleXProperty, null);

    private void Restart()
    {
        Stop();
        bool active = IsRunning && IsLoaded && ActualWidth > 0;
        track.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        if (!IsRunning) timed = null;
        if (!active) { fill.Visibility = Visibility.Collapsed; return; }
        fill.Visibility = Visibility.Visible;
        fill.Width = ActualWidth;
        grow.ScaleX = 0;
        if (!double.IsNaN(Progress)) Retarget(Math.Clamp(Progress, 0, 1), 240);
        else PlayTimed();
    }
}
