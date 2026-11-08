using System.Windows;
using System.Windows.Media.Animation;

namespace Hush;

// Rolls a number smoothly from one value to another with a WPF animation (render-thread timing, no timer), so a value can count down
// while its bar eases down beside it. The owner supplies what to do with each intermediate value (usually: format it into a TextBlock).
internal sealed class NumberTween : Animatable
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumberTween),
        new PropertyMetadata(0.0, (owner, args) => ((NumberTween)owner).ValueChanged?.Invoke((double)args.NewValue)));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Action<double>? ValueChanged { get; set; }
    protected override Freezable CreateInstanceCore() => new NumberTween();

    // Completes when the roll has finished (or immediately for a zero duration). The last value is always exactly `to`.
    public Task RunAsync(double from, double to, int milliseconds)
    {
        if (milliseconds <= 0 || Math.Abs(from - to) < double.Epsilon) { BeginAnimation(ValueProperty, null); Value = to; return Task.CompletedTask; }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => { Value = to; done.TrySetResult(); };
        BeginAnimation(ValueProperty, animation);
        return done.Task;
    }
}
