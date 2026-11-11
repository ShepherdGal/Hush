using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Hush;

// The "files are being cleaned" cue inside the gauge: a sonar ripple (owner's pick "A, sonar ripple" from the two previews of November 11, 2026). A glowing ring
// spreads from the centre to the edge of the disc; every dust speck breaks up and fades at the moment the ring reaches it, then settles back for the next pass.
// A small core in the middle pulses. The percentage and its label are drawn on top, unchanged. Brushes are theme resources (the accent for the ring and core, the
// muted text color for the dust), so every accent choice and both themes follow by themselves.
// How it stays cheap (60 fps native on an i3, docs/RULES.md; no frame-rate cap, ever): every shape is built ONCE. The ring is two strokes (the crisp edge over a wider, fainter one) in one container moved by
// ONE scale and ONE opacity animation. Each speck sits in a container turned once to its own direction (static), so only its distance (a translate) and its opacity
// are animated. All animations are as long as one pass of the ripple and start together, so a speck always fades exactly when the ring touches it. Everything is
// transform/opacity on WPF's render thread at the display's own refresh rate. No timer, no repainting from code, no opacity mask, no clip, no blur or shadow. The
// animations run only while the cue is running and visible and are released when it stops.
public sealed class ScanActivity : Grid
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(ScanActivity), new PropertyMetadata(false, (sender, _) => ((ScanActivity)sender).Update()));
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    // The gauge's inner disc is 250 DIP across (the track ring sits outside it).
    private const double Size = 250, RingSize = Size - 12, Reach = RingSize / 2, StartScale = 0.02;
    private const int DustCount = 28;
    // One pass of the ripple. The ring reaches the edge at ReachFraction of the pass and rests (invisible) for the remainder while the dust settles back.
    private const double PassSeconds = 3.6, ReachFraction = 0.8, DustFade = 0.07;

    private sealed class Dust
    {
        public required Ellipse Dot;
        public required TranslateTransform Move;
        public required double Distance;
    }

    private readonly List<Dust> dust = [];
    private readonly Grid ring;
    private readonly ScaleTransform ringScale = new(StartScale, StartScale);
    private readonly Ellipse core;
    private readonly ScaleTransform coreScale = new(1, 1);
    private bool playing;

    public ScanActivity()
    {
        IsHitTestVisible = false;
        Width = Size; Height = Size;

        // Dust: a fixed spread (a seeded generator, so the picture is the same every time and tests can rely on it), kept off the very centre where the percentage sits.
        var random = new Random(23);
        for (int index = 0; index < DustCount; index++)
        {
            double angle = random.NextDouble() * 360, distance = Reach * (0.3 + 0.66 * Math.Sqrt(random.NextDouble())), size = 2 + random.NextDouble() * 3;
            var move = new TranslateTransform(distance, 0);
            var dot = new Ellipse { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55, RenderTransform = move };
            dot.SetResourceReference(Shape.FillProperty, "MutedBrush");
            var lane = new Grid { Width = Size, Height = Size, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(angle), IsHitTestVisible = false };
            lane.Children.Add(dot);
            Children.Add(lane);
            dust.Add(new Dust { Dot = dot, Move = move, Distance = distance });
        }

        // The ripple: a crisp ring over a wider, fainter one (the glow, drawn as layered strokes, no blur), both scaled together from the centre.
        ring = new Grid { Width = RingSize, Height = RingSize, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = ringScale, Opacity = 0 };
        var inner = new Ellipse { StrokeThickness = 4, Opacity = 0.4 };
        var edge = new Ellipse { StrokeThickness = 1.8 };
        foreach (var shape in new[] { inner, edge }) { shape.SetResourceReference(Shape.StrokeProperty, "TealTextBrush"); ring.Children.Add(shape); }
        Children.Add(ring);

        core = new Ellipse { Width = 14, Height = 14, Opacity = 0.55, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = coreScale };
        core.SetResourceReference(Shape.FillProperty, "TealTextBrush");
        Children.Add(core);

        IsVisibleChanged += (_, _) => Update();
        Unloaded += (_, _) => Stop();
        Loaded += (_, _) => Update();
        System.Windows.Automation.AutomationProperties.SetName(this, "Cleaning files");
    }

    // True while the animation is actually playing (used by tests).
    internal bool IsPlaying => playing;
    // How far the ripple has spread (its scale), where it rests when the cue is off (so tests can see the motion start and stop).
    internal double Motion => ringScale.ScaleX;
    internal const double RestMotion = StartScale;

    private void Update()
    {
        if (IsRunning && IsVisible && IsLoaded) Play(); else Stop();
    }

    private static DoubleAnimationUsingKeyFrames Pass(params (double Fraction, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(PassSeconds), RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (fraction, value) in frames) animation.KeyFrames.Add(new LinearDoubleKeyFrame(value, KeyTime.FromPercent(fraction)));
        animation.Freeze();
        return animation;
    }

    private void Play()
    {
        if (playing) return;
        playing = true;
        // Every animation repeats forever at the display's refresh rate (no frame-rate cap on purpose: a 30 fps cap is what made an old cue look like it stuttered).
        var spread = Pass((0, StartScale), (ReachFraction, 1), (1, 1));
        ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, spread);
        ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, spread);
        ring.BeginAnimation(OpacityProperty, Pass((0, 0.95), (ReachFraction * 0.6, 0.6), (ReachFraction, 0), (1, 0)));

        // Dust: at the moment the ring reaches its distance it drifts a little outward and fades; it is back (faded in) before the next pass.
        foreach (var speck in dust)
        {
            double touched = ReachFraction * speck.Distance / Reach, gone = Math.Min(touched + DustFade, 0.9);
            speck.Dot.BeginAnimation(OpacityProperty, Pass((0, 0.55), (touched, 0.55), (gone, 0), (0.93, 0), (1, 0.55)));
            var drift = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(PassSeconds), RepeatBehavior = RepeatBehavior.Forever };
            drift.KeyFrames.Add(new LinearDoubleKeyFrame(speck.Distance, KeyTime.FromPercent(0)));
            drift.KeyFrames.Add(new LinearDoubleKeyFrame(speck.Distance, KeyTime.FromPercent(touched)));
            drift.KeyFrames.Add(new LinearDoubleKeyFrame(speck.Distance + 10, KeyTime.FromPercent(gone)));
            drift.KeyFrames.Add(new DiscreteDoubleKeyFrame(speck.Distance, KeyTime.FromPercent(0.95)));
            drift.KeyFrames.Add(new LinearDoubleKeyFrame(speck.Distance, KeyTime.FromPercent(1)));
            drift.Freeze();
            speck.Move.BeginAnimation(TranslateTransform.XProperty, drift);
        }

        // The core in the middle breathes faster than the ripple.
        var breathe = TimeSpan.FromSeconds(0.6);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var swell = new DoubleAnimation(1, 1.6, breathe) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        coreScale.BeginAnimation(ScaleTransform.ScaleXProperty, swell);
        coreScale.BeginAnimation(ScaleTransform.ScaleYProperty, swell);
        core.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0.9, breathe) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
    }

    private void Stop()
    {
        if (!playing) return;
        playing = false;
        ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ringScale.ScaleX = ringScale.ScaleY = StartScale;
        ring.BeginAnimation(OpacityProperty, null); ring.Opacity = 0;
        foreach (var speck in dust)
        {
            speck.Dot.BeginAnimation(OpacityProperty, null); speck.Dot.Opacity = 0.55;
            speck.Move.BeginAnimation(TranslateTransform.XProperty, null); speck.Move.X = speck.Distance;
        }
        coreScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); coreScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); coreScale.ScaleX = coreScale.ScaleY = 1;
        core.BeginAnimation(OpacityProperty, null); core.Opacity = 0.55;
    }
}
