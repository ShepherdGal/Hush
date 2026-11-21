using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Hush;

// The "files are being cleaned" cue inside the gauge (owner's pick, November 19, 2026, from the Gauge Motion previews: "X2", Dissolve + splash and wave).
//
// While files are being cleaned, fine dust inside the gauge lifts and fades, as if it is being cleared away: each speck rises a little, sways and fades in and out
// on its own slightly different rhythm, so the field never repeats in step. Five levels, as with the cue before it (owner, November 14, 2026): a further stream of
// dust joins at 20, 40, 60 and 80% of the clean (ShowProgress, fed by the gauge), fading in so nothing pops up mid-flight.
//
// When the clean really finishes, FinishAsync() plays ONE finish (owner: "Smash, but not the lines that go out of it, just a splash effect with light"): the dust
// fades, a soft splash of light swells from the middle and fades, and the Smash's shockwave rings roll outward (circles only, no spark lines). The window swells the
// percentage at the same moment (Smashed). The digits sit on a soft plate in the color behind the gauge, so nothing ever clashes with the number.
//
// How it stays cheap (60 fps native on an i3, the design rules): everything is built once when the control is made; glow is layered translucent shapes or one fixed
// radial-gradient disc (no blur, no effect, no mask, no clip); motion is only translation, scale and opacity on the render thread at the display's own refresh rate.
// The loop's animations exist only while the cue runs and is visible, and are released when it stops. Colors are theme resources (the accent), so both themes and
// every main color follow; the splash's gradient is made from the accent at the moment the finish plays.
public sealed class ScanActivity : Grid
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(ScanActivity), new PropertyMetadata(false, (sender, _) => ((ScanActivity)sender).Update()));
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    // Raised on the UI thread when the finish lands (IsFinalSmash is then true). The window swells the percentage with it.
    public event EventHandler? Smashed;
    public bool IsFinalSmash { get; private set; }

    // The gauge's inner disc is 250 DIP across in the gauge's own 320 space (Home shows it at 0.66); preview distances are multiplied by K.
    private const double Size = 250, Middle = Size / 2, Radius = 128.5, K = Radius / 85.5;
    // Where dust may start (inside the ring, minus a speck's rise and halo) and how far a speck lifts.
    private const double DustField = (85.5 - 14) * K, Lift = 26 * K, Sway = 3 * K;
    // A speck's rhythm (seconds): each one between these, so the field never repeats in step.
    private const double ShortestTrip = 2.9, LongestTrip = 3.6;
    // Specks per level: level 1 from the start, a further stream at 20, 40, 60 and 80%. Always threes: the three specks of a group share one rhythm a third of a trip
    // apart, and a speck is lit for two thirds of its trip, so exactly two of every three are lit at any moment: the field never thins out, yet groups move out of step.
    internal static readonly int[] LevelSpecks = [21, 6, 6, 9, 9];
    private const double DustStrength = 0.7, LevelFadeSeconds = 0.6;
    // The finish: the dust fades (DustOutSeconds), the light swells and fades (SplashSeconds), three rings roll out (RingSeconds each, staggered); FinishSeconds in all.
    private const double DustOutSeconds = 0.35, SplashSeconds = 0.9, RingSeconds = 0.7, RingStagger = 0.09, FinishSeconds = 1.5;
    // The splash disc and the rings are built at a fixed size and scaled: the light from 34 to 108 (preview units), a ring from 12 to 140.
    private const double SplashFrom = 34 * K, SplashTo = 108 * K, RingBuilt = 150, RingFrom = 12 * K, RingTo = (12 + 85.5 * 1.5) * K;

    private sealed class Speck
    {
        public required Grid Dot;
        public required TranslateTransform Move;
        public required double X, Y, Trip, Offset;
    }

    private sealed class Stream
    {
        public required Grid Host;
        public readonly List<Speck> Specks = [];
        public bool Running;
    }

    private readonly Stream[] streams = new Stream[LevelSpecks.Length];
    private readonly List<Speck> dust = [];
    private readonly Canvas finish;
    private readonly Ellipse splash;
    private readonly ScaleTransform splashScale = new(1, 1);
    private readonly (Grid Ring, ScaleTransform Scale)[] rings = new (Grid, ScaleTransform)[3];
    private int requestedLevel;
    private bool playing, finishing;
    private TaskCompletionSource? finished;

    public ScanActivity()
    {
        IsHitTestVisible = false;
        Width = Size; Height = Size;

        // The dust: a fixed spread (a seeded generator per stream, so the picture is the same every time and tests can rely on it). Positions cover the disc
        // evenly (square root of the distance), rhythms and starting points are spread so about the same number of specks is lit at every moment.
        for (int level = 0; level < LevelSpecks.Length; level++)
        {
            var stream = streams[level] = new Stream { Host = new Grid { Width = Size, Height = Size, IsHitTestVisible = false, Visibility = level == 0 ? Visibility.Visible : Visibility.Collapsed } };
            var random = new Random(5 + level);
            int count = LevelSpecks[level];
            double groupTrip = 0, groupStart = 0;
            for (int index = 0; index < count; index++)
            {
                double angle = random.NextDouble() * Math.PI * 2, distance = Math.Sqrt(random.NextDouble()) * DustField, size = (0.9 + random.NextDouble() * 0.8) * K;
                double x = Math.Cos(angle) * distance, y = Math.Sin(angle) * distance + Lift / 2; // centred on the middle of its rise
                double halo = size * 3;
                var move = new TranslateTransform(x, y);
                var dot = new Grid { Width = halo * 2, Height = halo * 2, Opacity = 0, RenderTransform = move, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                foreach (var (scale, opacity) in new[] { (3.0, 0.12), (1.0, 1.0) })
                {
                    var disc = new Ellipse { Width = size * 2 * scale, Height = size * 2 * scale, Opacity = opacity, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    disc.SetResourceReference(Shape.FillProperty, "TealTextBrush");
                    dot.Children.Add(disc);
                }
                stream.Host.Children.Add(dot);
                if (index % 3 == 0) { groupTrip = ShortestTrip + random.NextDouble() * (LongestTrip - ShortestTrip); groupStart = random.NextDouble(); }
                var speck = new Speck { Dot = dot, Move = move, X = x, Y = y, Trip = groupTrip, Offset = (groupStart + index % 3 / 3.0) % 1 };
                stream.Specks.Add(speck); dust.Add(speck);
            }
            Children.Add(stream.Host);
        }

        // The soft plate under the digits, sized for the smaller percentage (owner, November 20, 2026: 56 instead of 90).
        Children.Add(ScanRadar.Plate(28, 40));

        // The finish, on a Canvas so the light and the rings may grow past the gauge without being cut off by layout: the rings (accent, with a thin white edge,
        // glow from wider fainter strokes) and the splash of light over the plate. All invisible until the finish plays.
        finish = new Canvas { Width = Size, Height = Size, IsHitTestVisible = false };
        for (int index = 0; index < rings.Length; index++)
        {
            double width = index == 0 ? 1.4 * K : 0.8 * K;
            var scale = new ScaleTransform(RingFrom / RingBuilt, RingFrom / RingBuilt);
            var ring = new Grid { Width = RingBuilt * 2, Height = RingBuilt * 2, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale };
            foreach (var (thickness, opacity) in new[] { (width * 9, 0.05), (width * 4.5, 0.11), (width, 1.0) })
            {
                var stroke = new Ellipse { StrokeThickness = thickness, Opacity = opacity, Margin = new Thickness(-thickness / 2) };
                stroke.SetResourceReference(Shape.StrokeProperty, "TealTextBrush");
                ring.Children.Add(stroke);
            }
            ring.Children.Add(new Ellipse { StrokeThickness = width * 0.5, Stroke = Brushes.White, Opacity = 0.6, Margin = new Thickness(-width * 0.25) });
            Canvas.SetLeft(ring, Middle - RingBuilt); Canvas.SetTop(ring, Middle - RingBuilt);
            finish.Children.Add(ring);
            rings[index] = (ring, scale);
        }
        splash = new Ellipse { Width = SplashFrom * 2, Height = SplashFrom * 2, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = splashScale };
        Canvas.SetLeft(splash, Middle - SplashFrom); Canvas.SetTop(splash, Middle - SplashFrom);
        finish.Children.Add(splash);
        Children.Add(finish);

        IsVisibleChanged += (_, _) => Update();
        Unloaded += (_, _) => Stop();
        Loaded += (_, _) => Update();
        System.Windows.Automation.AutomationProperties.SetName(this, "Cleaning files");
    }

    // True while the animation is actually playing (used by tests).
    internal bool IsPlaying => playing;
    // How far the first speck has risen from where it rests (0 when the cue is off), so tests can see the motion start and stop.
    internal double Motion => dust[0].Move.Y - dust[0].Y;
    internal const double RestMotion = 0;
    internal bool IsFinishing => finishing;
    // How many dust streams are playing now (1 to 5; 0 when the cue is off), for tests and the perf run.
    internal int ActiveLevels => streams.Count(stream => stream.Running);

    // The clean's progress (0-100), fed by the gauge: a further stream joins at 20, 40, 60 and 80%. Levels only go up while a clean runs; a stop resets them.
    internal void ShowProgress(double percent)
    {
        if (!playing || finishing) return; // the gauge also moves during a scan: only a running clean raises the level
        int level = Math.Clamp((int)(percent / 20), 0, LevelSpecks.Length - 1);
        if (level <= requestedLevel) return;
        requestedLevel = level;
        StartLevels();
    }

    // How evenly the first stream fills the gauge: the fewest and the most specks clearly lit at any moment over ten seconds (tests: the field never empties).
    internal (int Fewest, int Most) LoopDensity()
    {
        int fewest = int.MaxValue, most = 0;
        for (int sample = 0; sample < 400; sample++)
        {
            double t = sample * 10.0 / 400;
            int lit = streams[0].Specks.Count(speck => Glow(((t / speck.Trip + speck.Offset) % 1 + 1) % 1) >= 0.5);
            fewest = Math.Min(fewest, lit); most = Math.Max(most, lit);
        }
        return (fewest, most);
    }

    private static double Glow(double u) => Math.Sin(Math.PI * u);

    private void Update()
    {
        if (IsRunning && IsVisible && IsLoaded) Play(); else Stop();
    }

    // Ends the cue on purpose: the dust fades and ONE finish plays (light, rings), then the task completes. Returns at once when nothing is playing.
    internal Task FinishAsync()
    {
        if (!playing) return Task.CompletedTask;
        if (finished != null) return finished.Task;
        finishing = true;
        finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlayFinish();
        return finished.Task;
    }

    // ---- playing ------------------------------------------------------------------------------------------------------------------------------------

    private void Play()
    {
        if (playing) return;
        playing = true; finishing = false; finished = null;
        StartLevels();
    }

    // Starts every stream up to the level the clean has reached (the first always). A stream that joins later fades in as a whole, so nothing pops up.
    private void StartLevels()
    {
        for (int level = 0; level <= requestedLevel; level++)
        {
            var stream = streams[level];
            if (stream.Running) continue;
            stream.Running = true;
            stream.Host.Visibility = Visibility.Visible;
            if (level > 0) stream.Host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(LevelFadeSeconds)));
            foreach (var speck in stream.Specks) Drift(speck);
        }
    }

    // One speck, forever while the cue runs: it rises by Lift with a slight sway and fades in and out over its own trip.
    private static void Drift(Speck speck)
    {
        var trip = TimeSpan.FromSeconds(speck.Trip);
        var begin = TimeSpan.FromSeconds(-speck.Offset * speck.Trip);
        var rise = new DoubleAnimation(speck.Y, speck.Y - Lift, trip) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
        var sway = new DoubleAnimationUsingKeyFrames { Duration = trip, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
        var fade = new DoubleAnimationUsingKeyFrames { Duration = trip, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
        double phase = speck.X; // any fixed number: specks sway out of step
        for (int step = 0; step <= 8; step++)
        {
            double u = step / 8.0;
            var at = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(u * speck.Trip));
            sway.KeyFrames.Add(new SplineDoubleKeyFrame(speck.X + Sway * Math.Sin(u * Math.PI * 2 + phase), at));
            fade.KeyFrames.Add(new SplineDoubleKeyFrame(DustStrength * Glow(u), at));
        }
        speck.Move.BeginAnimation(TranslateTransform.YProperty, rise);
        speck.Move.BeginAnimation(TranslateTransform.XProperty, sway);
        speck.Dot.BeginAnimation(OpacityProperty, fade);
    }

    // The finish: the dust fades, the light swells and fades, the three rings roll out one after another. The task completes when the last ring has faded.
    private void PlayFinish()
    {
        IsFinalSmash = true;
        foreach (var stream in streams.Where(stream => stream.Running))
            stream.Host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(DustOutSeconds)));

        // The light: white in the middle, the accent around it, nothing at the edge; it appears at once, swells and fades (time in fractions of SplashSeconds).
        var accent = TryFindResource("PrimaryAccentColor") is Color color ? color : Colors.White;
        var light = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(205, 255, 255, 255), 0), new GradientStop(Color.FromArgb(153, accent.R, accent.G, accent.B), 0.25), new GradientStop(Color.FromArgb(56, accent.R, accent.G, accent.B), 0.55), new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1) } };
        light.Freeze();
        splash.Fill = light;
        var swell = new DoubleAnimation(1, SplashTo / SplashFrom, TimeSpan.FromSeconds(SplashSeconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        splashScale.BeginAnimation(ScaleTransform.ScaleXProperty, swell); splashScale.BeginAnimation(ScaleTransform.ScaleYProperty, swell);
        var shine = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(SplashSeconds) };
        foreach (double u in new[] { 0, 0.07, 0.2, 0.4, 0.6, 0.8, 1 })
            shine.KeyFrames.Add(new LinearDoubleKeyFrame(Math.Min(1, u / 0.07) * Math.Pow(1 - u, 1.4), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(u * SplashSeconds))));
        splash.BeginAnimation(OpacityProperty, shine);

        // The rings (the Smash's shockwave, circles only): fast at first, then slowing, fading as they go; the first is the brightest.
        for (int index = 0; index < rings.Length; index++)
        {
            var (ring, scale) = rings[index];
            var delay = TimeSpan.FromSeconds(index * RingStagger);
            var grow = new DoubleAnimation(RingFrom / RingBuilt, RingTo / RingBuilt, TimeSpan.FromSeconds(RingSeconds)) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow); scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            double peak = index == 0 ? 0.75 : 0.4;
            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = delay, Duration = TimeSpan.FromSeconds(FinishSeconds - index * RingStagger) };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(RingSeconds))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(FinishSeconds - index * RingStagger))));
            if (index == rings.Length - 1) fade.Completed += (_, _) => { if (playing && finishing) finished?.TrySetResult(); };
            ring.BeginAnimation(OpacityProperty, fade);
        }
        Smashed?.Invoke(this, EventArgs.Empty);
    }

    private void Stop()
    {
        if (!playing) return;
        playing = false; finishing = false; IsFinalSmash = false;
        for (int level = 0; level < streams.Length; level++)
        {
            var stream = streams[level];
            stream.Running = false;
            stream.Host.BeginAnimation(OpacityProperty, null);
            if (level > 0) stream.Host.Visibility = Visibility.Collapsed; // a further stream costs nothing until it is reached again
        }
        requestedLevel = 0;
        // Every animated property goes back to its built value: the cue rests exactly where it was made.
        foreach (var speck in dust)
        {
            speck.Move.BeginAnimation(TranslateTransform.XProperty, null); speck.Move.BeginAnimation(TranslateTransform.YProperty, null);
            speck.Dot.BeginAnimation(OpacityProperty, null);
        }
        splashScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); splashScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        splash.BeginAnimation(OpacityProperty, null);
        foreach (var (ring, scale) in rings)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ring.BeginAnimation(OpacityProperty, null);
        }
        finished?.TrySetResult(); finished = null;
    }
}
