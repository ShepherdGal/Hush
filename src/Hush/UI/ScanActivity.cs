using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hush;

public sealed class ScanActivity : FrameworkElement
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(ScanActivity), new PropertyMetadata(false, Changed));
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    // Redraw interval of the looping animation; low-resource mode slows it down.
    public static TimeSpan FrameInterval { get; set; } = TimeSpan.FromMilliseconds(33);
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer timer;
    public ScanActivity()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) => InvalidateVisual();
        Unloaded += (_, _) => timer.Stop();
        Loaded += (_, _) => { if (IsRunning) timer.Start(); };
        System.Windows.Automation.AutomationProperties.SetName(this, "Scanning files");
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var activity = (ScanActivity)sender;
        if ((bool)args.NewValue) { activity.clock.Restart(); activity.timer.Interval = FrameInterval; activity.timer.Start(); }
        else { activity.timer.Stop(); activity.clock.Stop(); }
        activity.InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        if (!IsRunning) return;
        var accent = (Brush?)TryFindResource("TealTextBrush") ?? Brushes.Teal;
        double phase = clock.Elapsed.TotalSeconds / 2.8 * Math.PI * 2;
        for (int index = 0; index < 3; index++)
        {
            double x = ActualWidth / 2 - 39 + index * 29;
            drawing.PushOpacity(0.24 + 0.22 * (1 + Math.Sin(phase - index * 0.7)));
            drawing.DrawRoundedRectangle(null, new Pen(accent, 1.2), new Rect(x, 4, 20, 25), 4, 4);
            drawing.DrawLine(new Pen(accent, 1), new Point(x + 5, 11), new Point(x + 15, 11));
            drawing.DrawLine(new Pen(accent, 1), new Point(x + 5, 16), new Point(x + 13, 16));
            drawing.Pop();
        }
        drawing.PushOpacity(0.42);
        double y = 6 + 20 * (0.5 - 0.5 * Math.Cos(phase));
        drawing.DrawLine(new Pen(accent, 1.3), new Point(ActualWidth / 2 - 45, y), new Point(ActualWidth / 2 + 43, y));
        drawing.Pop();
    }
}
