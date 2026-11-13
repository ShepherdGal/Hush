using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hush;

// The Boost chip's border that draws itself (owner, November 13, 2026, after seeing the demo): while a Boost runs, a brighter line starts at the top centre of the chip and
// travels clockwise along its own outline, easing smoothly, until it closes where it began. It follows the same run as the shared loading bar in Settings (UpdateActivity.Mirror):
// real progress where there is one, a slow fill over the stage's time where there is not, and CompleteAsync() takes it to closed only when the work is really done. Once closed it
// stays a moment, then fades back to the chip's ordinary border (never cut off half drawn, never a loop).
//
// It sits over the whole button (same size, not hit-testable), draws nothing at rest and costs nothing then; while it runs it redraws one 28 DIP high outline, nothing else.
// Geometry follows the button's border: 1 DIP thick, corner radius 13.
public sealed class BorderSweep : FrameworkElement, ISweepFollower
{
    private const double BorderThickness = 1, CornerRadius = 13, LineThickness = 1.75;

    public static readonly DependencyProperty DrawnProperty = DependencyProperty.Register(nameof(Drawn), typeof(double), typeof(BorderSweep),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(BorderSweep),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // How much of the outline is drawn right now (0..1), also what a test reads.
    public double Drawn { get => (double)GetValue(DrawnProperty); private set => SetValue(DrawnProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private bool running;
    private double progress = double.NaN;
    private (TimeSpan Duration, double To)? timed;
    private int generation;

    public BorderSweep()
    {
        IsHitTestVisible = false;
        SetResourceReference(StrokeProperty, "TealTextBrush");
        Unloaded += (_, _) => Reset();
    }

    public bool IsRunning
    {
        get => running;
        set
        {
            if (running == value) return;
            running = value;
            if (value) { generation++; BeginAnimation(OpacityProperty, null); Opacity = 1; Drawn = 0; BeginAnimation(DrawnProperty, null); Apply(); }
            else End();
        }
    }

    public double Progress
    {
        get => progress;
        set { progress = value; if (running && !double.IsNaN(value)) Glide(Math.Clamp(value, 0, 1), 240, false); }
    }

    public void FillOver(TimeSpan duration, double to = 0.92)
    {
        timed = (duration, Math.Clamp(to, 0, 1));
        if (running && double.IsNaN(progress)) Glide(timed.Value.To, duration.TotalMilliseconds, true);
    }

    // The work is really done: close the outline and let the person see it arrive.
    public Task CompleteAsync()
    {
        timed = null;
        if (!running || !IsLoaded || ActualWidth <= 0) return Task.CompletedTask;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = Animation(Drawn, 1, 360, false);
        close.Completed += (_, _) => finished.TrySetResult();
        BeginAnimation(DrawnProperty, close);
        // Animations only tick while something is drawn; a minimized window must not leave the caller waiting.
        _ = Task.Delay(800).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (finished.Task.IsCompleted) return;
            BeginAnimation(DrawnProperty, null); Drawn = 1; finished.TrySetResult();
        }), TaskScheduler.Default);
        return finished.Task;
    }

    private void Apply()
    {
        if (!double.IsNaN(progress)) Glide(Math.Clamp(progress, 0, 1), 240, false);
        else if (timed is { } plan) Glide(plan.To, plan.Duration.TotalMilliseconds, true);
    }

    private void Glide(double to, double milliseconds, bool slowEnd)
    {
        if (!IsLoaded || ActualWidth <= 0) return;
        BeginAnimation(DrawnProperty, Animation(Drawn, to, milliseconds, slowEnd));
    }

    private static DoubleAnimation Animation(double from, double to, double milliseconds, bool slowEnd) =>
        new(from, to, TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)))
        {
            EasingFunction = slowEnd ? new QuadraticEase { EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };

    // The run is over. A closed outline stays a moment and fades back to the ordinary border; one that was stopped part way fades quickly.
    private void End()
    {
        timed = null;
        int id = ++generation;
        bool closed = Drawn >= 0.99;
        if (Drawn <= 0.001) { Reset(); return; }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(closed ? 600 : 250))
        {
            BeginTime = TimeSpan.FromMilliseconds(closed ? 900 : 0),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        fade.Completed += (_, _) => { if (id == generation) Reset(); };
        BeginAnimation(OpacityProperty, fade);
    }

    private void Reset()
    {
        BeginAnimation(DrawnProperty, null); BeginAnimation(OpacityProperty, null);
        Drawn = 0; Opacity = 1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double drawn = Drawn, w = ActualWidth, h = ActualHeight;
        if (drawn <= 0.001 || Stroke is not { } brush || w < 8 || h < 8) return;
        double half = BorderThickness / 2, x0 = half, y0 = half, x1 = w - half, y1 = h - half, r = Math.Min(CornerRadius - half, (h - BorderThickness) / 2), cx = w / 2;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            // From the top centre, clockwise, back to the top centre.
            context.BeginFigure(new Point(cx, y0), false, false);
            context.LineTo(new Point(x1 - r, y0), true, false);
            context.ArcTo(new Point(x1, y0 + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(x1, y1 - r), true, false);
            context.ArcTo(new Point(x1 - r, y1), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(x0 + r, y1), true, false);
            context.ArcTo(new Point(x0, y1 - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(x0, y0 + r), true, false);
            context.ArcTo(new Point(x0 + r, y0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(cx, y0), true, false);
        }
        geometry.Freeze();
        double length = 2 * ((x1 - x0) - 2 * r) + 2 * ((y1 - y0) - 2 * r) + 2 * Math.PI * r;
        double dash = length / LineThickness;   // dash units are multiples of the pen thickness
        var pen = new Pen(brush, LineThickness)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, DashCap = PenLineCap.Round,
            DashStyle = new DashStyle([dash, dash], dash * (1 - Math.Min(1, drawn)))
        };
        dc.DrawGeometry(null, pen, geometry);
    }
}
