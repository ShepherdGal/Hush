using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hush;

// The calm "something is happening" cue while a scan runs: soft rings of light drift outward from the gauge like sonar and fade away.
// Each ring is drawn once (a hairline wrapped in layered translucent strokes, which reads as a soft glow without any blur effect) and WPF
// animates its scale and opacity on the render thread at the display's own refresh rate (never a frame-rate cap), so nothing repaints from a timer. The rings are staggered so one
// is always travelling. It fades in when a scan starts, fades out when it ends, and is switched off completely while hidden or not scanning.
// Only the accent brush is used.
public sealed class ScanRipples : Grid
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(ScanRipples),
        new PropertyMetadata(false, (sender, _) => ((ScanRipples)sender).Update()));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(ScanRipples),
        new PropertyMetadata(Brushes.Teal, (sender, args) => { foreach (var ring in ((ScanRipples)sender).rings) ring.Stroke = (Brush)args.NewValue; }));
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    // One ring's whole journey; the three rings are spread evenly across it. Slow enough to stay calm.
    internal static readonly TimeSpan Cycle = TimeSpan.FromSeconds(4.5);
    private const int RingCount = 3;
    // How far a ring grows (1.0 = the edge of the gauge's track): about 55 DIP past the track at the end, where it has faded out.
    private const double Reach = 1.42;
    private readonly Ring[] rings = new Ring[RingCount];
    private bool rippling;

    public ScanRipples()
    {
        IsHitTestVisible = false;
        Opacity = 0;
        for (int i = 0; i < RingCount; i++) { rings[i] = new Ring(); Children.Add(rings[i]); }
        IsVisibleChanged += (_, _) => Update();
        System.Windows.Automation.AutomationProperties.SetName(this, "Scanning");
    }

    // True while the ripple animations are actually playing (used by tests).
    internal bool IsRippling => rippling;
    // The first ring's current size factor, so tests can see it move.
    internal double Phase => rings[0].Scale.ScaleX;

    private void Update()
    {
        // Deliberately does not follow Windows' "animation effects" switch: many PCs run with it off for speed (this one does), and like every
        // other motion in Hush this cue is the person's own choice of look. It is calm, runs at the display's refresh rate, and runs only while scanning.
        if (IsRunning && IsVisible)
        {
            if (!rippling)
            {
                rippling = true;
                for (int i = 0; i < RingCount; i++)
                {
                    // A negative start time begins the ring part-way through its journey, so rings are already out there the moment a scan starts.
                    var begin = TimeSpan.FromTicks(-Cycle.Ticks * i / RingCount);
                    var grow = new DoubleAnimation(1.0, Reach, Cycle) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin, EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } };
                    rings[i].Scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                    rings[i].Scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                    // Rise gently, then dissolve over most of the journey.
                    var glow = new DoubleAnimationUsingKeyFrames { Duration = Cycle, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
                    glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                    glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.12), new SineEase { EasingMode = EasingMode.EaseOut }));
                    glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new SineEase { EasingMode = EasingMode.EaseOut }));
                    rings[i].BeginAnimation(OpacityProperty, glow);
                }
            }
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
        }
        else if (IsRunning)
        {
            Stop(); // scanning but the page is hidden: nothing to draw, so nothing runs
        }
        else if (rippling || Opacity > 0)
        {
            // The scan ended: let the whole cue fade out softly, then stop the rings.
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(480)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (_, _) => { if (!IsRunning) Stop(); };
            BeginAnimation(OpacityProperty, fade);
        }
    }

    private void Stop()
    {
        if (!rippling) return;
        rippling = false;
        foreach (var ring in rings)
        {
            ring.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ring.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ring.BeginAnimation(OpacityProperty, null);
            ring.Scale.ScaleX = ring.Scale.ScaleY = 1;
            ring.Opacity = 0;
        }
    }

    // One ripple: the glowing ring at its starting size. Scale and opacity are animated from outside.
    private sealed class Ring : FrameworkElement
    {
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Ring),
            new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));
        public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
        internal ScaleTransform Scale { get; } = new(1, 1);
        // The starting radius: just outside the gauge's track (which reaches about 131 of the 160 half-width).
        private const double Radius = 134;

        public Ring()
        {
            IsHitTestVisible = false;
            Opacity = 0;
            RenderTransform = Scale;
            RenderTransformOrigin = new Point(0.5, 0.5);
        }

        protected override void OnRender(DrawingContext drawing)
        {
            var centre = new Point(ActualWidth / 2, ActualHeight / 2);
            var brush = Stroke;
            // The soft glow: wide, very faint strokes stacked from the outside in.
            (double Width, double Alpha)[] halo = [(34, 0.020), (26, 0.030), (19, 0.045), (13, 0.065), (8, 0.095), (4.5, 0.15)];
            foreach (var (width, alpha) in halo)
            {
                drawing.PushOpacity(alpha);
                drawing.DrawEllipse(null, new Pen(brush, width), centre, Radius, Radius);
                drawing.Pop();
            }
            // The line itself: thin and gentle, never sharp.
            drawing.PushOpacity(0.55);
            drawing.DrawEllipse(null, new Pen(brush, 1.6), centre, Radius, Radius);
            drawing.Pop();
        }
    }
}
