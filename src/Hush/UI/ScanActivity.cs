using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Hush;

// The "files are being cleaned" cue inside the gauge: SMASH (owner's pick, November 12, 2026, from four concepts, then two refinements and three ways to keep the
// percentage readable; the owner chose "B, soft plate" and no spark on the ring tip).
//
// The picture (owner, November 13, 2026: the smash happens ONLY at 100%, never while the clean is still running): while files are being cleaned, dust drifts in from the
// rim and spirals into a charged core in the middle, on a calm 3.78 s loop that never smashes. When the clean really finishes, FinishAsync() squashes the core tight,
// fades the dust and plays ONE smash: a white flash, a fast shockwave ring with thinner ones behind it, and sparks flying outward. Then the cue is done (the gauge itself
// shows the finished state). The percentage sits on top of a soft plate (a few translucent discs in the panel color, built once) that fades whatever passes under the
// digits, so the number never clashes with the animation.
//
// How it stays cheap (60 fps native on an i3, docs/RULES.md; no frame-rate cap, ever): everything is built ONCE (lazily, when the cue first plays) from shapes whose
// glow is layered translucent discs. All motion is a transform (scale, rotate, translate) or an opacity change on the render thread at the display's own refresh rate.
// No timer, no repainting from code, no opacity mask, no clip, no blur or shadow, nothing that repeats forever: a cycle is two short storyboards played one after the
// other (before the impact, after it), each started by the previous one's Completed event, so the cue can also be sped up and ended on purpose. The animations run
// only while the cue is running and visible and are released when it stops. Brushes are theme resources (the accent, the muted text color, the panel color), so both
// themes and every main color follow by themselves; the flash and the sparks are white.
public sealed class ScanActivity : Grid
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(ScanActivity), new PropertyMetadata(false, (sender, _) => ((ScanActivity)sender).Update()));
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    // Raised on the UI thread at every smash (IsFinalSmash tells the last one). The window uses it to pulse the percentage and the ring's glow with the blast.
    public event EventHandler? Smashed;
    public bool IsFinalSmash { get; private set; }

    // The gauge's inner disc is 250 DIP across (the track ring sits outside it).
    private const double Size = 250, Reach = 104, DustEnd = 16;
    // One cycle (seconds) and where the impact falls in it; the final smash is slower and the dust is fast-forwarded before it.
    // While the clean runs the dust drifts in on a calm loop (CycleSeconds) around a charged core; nothing smashes. At 100% (FinishAsync) the core squashes (ChargeSeconds),
    // the dust fades, and ONE smash (FinalSeconds) lands.
    private const double CycleSeconds = 3.78, WarmSeconds = 0.9, ChargeSeconds = 0.7, FinalSeconds = 1.5;
    // The core's resting (charged) radius while the clean runs, and its radius when squashed just before the smash.
    private const double HeldRadius = 12, SquashedRadius = 22 * 0.45;
    // The core is one 32 DIP disc scaled; its radius at rest is 7 (times 0.7), the glow is a 120 DIP stack scaled.
    private const double CoreDiameter = 32, GlowDiameter = 120, FlashDiameter = 120;
    private const int DustCount = 56, SparkCount = 18, Steps = 12;

    private const string OpacityPath = "(UIElement.Opacity)";
    private const string ScaleXPath = "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", ScaleYPath = "(UIElement.RenderTransform).(ScaleTransform.ScaleY)";
    private const string AnglePath = "(UIElement.RenderTransform).(RotateTransform.Angle)", MovePath = "(UIElement.RenderTransform).(TranslateTransform.X)";

    private static readonly Brush White = Freeze(new SolidColorBrush(Colors.White));
    private static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }

    private sealed class Speck
    {
        public required Grid Lane;
        public required RotateTransform Turn;
        public required Ellipse Dot;
        public required TranslateTransform Move;
        public required double Angle, Start, Length, Width;
    }

    private sealed class Spark
    {
        public required Rectangle Bar;
        public required double Angle, Length;
    }

    private readonly List<Speck> dust = [];
    private readonly List<Spark> sparks = [];
    private readonly Grid glow, core, plate, flash, ring1, ring2, ring3;
    private readonly ScaleTransform glowScale = new(GlowRest, GlowRest), coreScale = new(CoreRest, CoreRest), flashScale = new(1, 1), ring1Scale = new(1, 1), ring2Scale = new(1, 1), ring3Scale = new(1, 1);
    private readonly Ellipse coreWhite;
    private Storyboard? warm, cycle, charge, final;
    private bool warmActive, cycleActive, chargeActive, finalActive, playing, finishing;
    private enum Stage { Idle, Cycle, Charge, Final, Done }
    private Stage stage = Stage.Idle;
    private TaskCompletionSource? finished;

    // Where the cue rests when it is off (so tests can see the motion start and stop): the core at its smallest.
    internal const double CoreRest = 7 * 0.7 / (CoreDiameter / 2), GlowRest = (7 * 2.4 + 0) / (GlowDiameter / 2);

    public ScanActivity()
    {
        IsHitTestVisible = false;
        Width = Size; Height = Size;

        // Dust: a fixed spread (a seeded generator, so the picture is the same every time and tests can rely on it). Each speck sits in a lane that is turned to its
        // direction; the lane turns (the spiral) while the dot moves along it (the distance).
        var random = new Random(5);
        for (int index = 0; index < DustCount; index++)
        {
            double angle = random.NextDouble() * 360, size = 1.8 + random.NextDouble() * 2.4, start = 0.02 + 0.58 * random.NextDouble(), length = 0.26 + 0.14 * random.NextDouble();
            var turn = new RotateTransform(angle);
            var move = new TranslateTransform(Reach, 0);
            var dot = new Ellipse { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, RenderTransform = move };
            dot.SetResourceReference(Shape.FillProperty, "MutedBrush");
            var lane = new Grid { Width = Size, Height = Size, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = turn, IsHitTestVisible = false };
            lane.Children.Add(dot);
            Children.Add(lane);
            dust.Add(new Speck { Lane = lane, Turn = turn, Dot = dot, Move = move, Angle = angle, Start = start, Length = length, Width = size });
        }

        // The charging glow around the core: seven translucent accent discs stacked (no blur), scaled as one.
        glow = Discs("TealTextBrush", GlowDiameter / 2, 7, 0.13, glowScale);
        glow.Opacity = 0.48;
        Children.Add(glow);

        // The core: an accent disc with a white one over it that fades in as it charges.
        core = new Grid { Width = CoreDiameter, Height = CoreDiameter, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = coreScale };
        var coreDisc = new Ellipse { Opacity = 0.95 }; coreDisc.SetResourceReference(Shape.FillProperty, "TealTextBrush");
        coreWhite = new Ellipse { Fill = White, Opacity = 0.2 };
        core.Children.Add(coreDisc); core.Children.Add(coreWhite);
        Children.Add(core);

        // The blast, all invisible until it is played: the flash (white discs stacked), the shockwave (an accent ring with a thin white edge) and a thinner ring behind
        // it, a third ring for the final smash, and sparks (short white bars, each in a lane turned to its own direction).
        flash = Discs(null, FlashDiameter / 2, 7, 0.26, flashScale); flash.Opacity = 0;
        Children.Add(flash);
        ring1 = Ring(Reach, 4, 1.6, ring1Scale); ring2 = Ring(90.5, 2.2, 1, ring2Scale); ring3 = Ring(87, 2, 1, ring3Scale);
        Children.Add(ring1); Children.Add(ring2); Children.Add(ring3);
        var sparkRandom = new Random(11);
        for (int index = 0; index < SparkCount; index++)
        {
            double angle = index * 360.0 / SparkCount + sparkRandom.NextDouble() * 17, length = 0.6 + 0.4 * sparkRandom.NextDouble();
            var bar = new Rectangle { Width = 14 * length, Height = 2, RadiusX = 1, RadiusY = 1, Fill = White, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform(12, 0) };
            var lane = new Grid { Width = Size, Height = Size, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(angle), IsHitTestVisible = false };
            lane.Children.Add(bar);
            Children.Add(lane);
            sparks.Add(new Spark { Bar = bar, Angle = angle, Length = length });
        }

        // The soft plate under the percentage (on top of everything above): discs in the panel color, from almost opaque in the middle to nothing at the edge.
        plate = new Grid { Width = 132, Height = 132 };
        const double solidRadius = 41, edgeRadius = 66; const int bands = 8; const double solid = 0.9;
        double previous = 0;
        for (int band = 0; band < bands; band++)
        {
            double radius = edgeRadius - (edgeRadius - solidRadius) * band / bands, middle = radius - (edgeRadius - solidRadius) / bands / 2;
            double wanted = solid * (edgeRadius - middle) / (edgeRadius - solidRadius);
            AddPlate(radius, 1 - (1 - wanted) / (1 - previous)); previous = wanted;
        }
        AddPlate(solidRadius, 1 - (1 - solid) / (1 - previous));
        Children.Add(plate);

        IsVisibleChanged += (_, _) => Update();
        Unloaded += (_, _) => Stop();
        Loaded += (_, _) => Update();
        System.Windows.Automation.AutomationProperties.SetName(this, "Cleaning files");
    }

    private void AddPlate(double radius, double opacity)
    {
        var disc = new Ellipse { Width = radius * 2, Height = radius * 2, Opacity = Math.Clamp(opacity, 0, 1), IsHitTestVisible = false };
        disc.SetResourceReference(Shape.FillProperty, "PanelBrush");
        plate.Children.Add(disc);
    }

    // Translucent discs stacked from the middle out (the glow is the sum, no blur). A null resource means white.
    private static Grid Discs(string? resource, double radius, int count, double opacity, ScaleTransform scale)
    {
        var stack = new Grid { Width = radius * 2, Height = radius * 2, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale, IsHitTestVisible = false };
        for (int index = 1; index <= count; index++)
        {
            double size = radius * 2 * index / count;
            var disc = new Ellipse { Width = size, Height = size, Opacity = opacity };
            if (resource == null) disc.Fill = White; else disc.SetResourceReference(Shape.FillProperty, resource);
            stack.Children.Add(disc);
        }
        return stack;
    }

    // A shockwave: an accent ring with a thin white edge, scaled as one. Its outer radius is "radius" when the scale is 1.
    private static Grid Ring(double radius, double thickness, double edge, ScaleTransform scale)
    {
        var wave = new Grid { Width = radius * 2, Height = radius * 2, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale, Opacity = 0, IsHitTestVisible = false };
        var body = new Ellipse { StrokeThickness = thickness }; body.SetResourceReference(Shape.StrokeProperty, "TealTextBrush");
        wave.Children.Add(body);
        wave.Children.Add(new Ellipse { StrokeThickness = edge, Stroke = White, Margin = new Thickness(thickness * 0.2), Opacity = 0.8 });
        scale.ScaleX = scale.ScaleY = 8 / radius;
        return wave;
    }

    // True while the animation is actually playing (used by tests).
    internal bool IsPlaying => playing;
    // How far the core has grown (its scale), where it rests when the cue is off (so tests can see the motion start and stop).
    internal double Motion => coreScale.ScaleX;
    internal const double RestMotion = CoreRest;
    internal bool IsFinishing => finishing;

    private void Update()
    {
        if (IsRunning && IsVisible && IsLoaded) Play(); else Stop();
    }

    // Ends the cue on purpose: the core squashes, the dust fades, ONE smash lands, then the task completes (the cue is then done until it stops).
    // Returns at once when nothing is playing.
    internal Task FinishAsync()
    {
        if (!playing) return Task.CompletedTask;
        if (finished != null) return finished.Task;
        finishing = true;
        finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartCharge(); // the core squashes and the dust fades; the Completed event then starts the one smash
        return finished.Task;
    }

    // ---- building the animations (once, the first time the cue plays) -------------------------------------------------------------------------------------

    private static DoubleAnimationUsingKeyFrames Track(Storyboard board, DependencyObject target, string path, double seconds)
    {
        var track = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(seconds) };
        Storyboard.SetTarget(track, target); Storyboard.SetTargetProperty(track, new PropertyPath(path));
        board.Children.Add(track);
        return track;
    }

    private static void Key(DoubleAnimationUsingKeyFrames track, double seconds, double value, IEasingFunction? ease = null)
    {
        var time = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
        track.KeyFrames.Add(ease == null ? new LinearDoubleKeyFrame(value, time) : new EasingDoubleKeyFrame(value, time, ease));
    }

    private static void KeyBoth(Storyboard board, DependencyObject target, double seconds, Action<DoubleAnimationUsingKeyFrames> keys)
    {
        keys(Track(board, target, ScaleXPath, seconds)); keys(Track(board, target, ScaleYPath, seconds));
    }

    private static double DustRadius(double u) => DustEnd + (Reach - DustEnd) * Math.Pow(1 - u, 1.45);
    private static double DustAngle(double start, double u) => start + 160.43 * Math.Pow(u, 1.5);
    private static double DustAlpha(double u) => Math.Min(1, u * 7) * Math.Min(1, (1 - u) * 10);
    private static double CoreScaleFor(double radius) => radius * 0.7 / (CoreDiameter / 2);
    private static double GlowScaleFor(double radius) => radius / (GlowDiameter / 2);
    private static double Eased(double k) => 1 - Math.Pow(1 - Math.Min(1, k), 3);

    private void EnsureBuilt()
    {
        if (cycle != null) return;
        warm = BuildWarm(); cycle = BuildCycle(); charge = BuildCharge(); final = BuildBlast(FinalSeconds, true);
        cycle.Completed += (_, _) => { if (playing && stage == Stage.Cycle) StartCycle(); };
        charge.Completed += (_, _) => { if (playing && stage == Stage.Charge) StartBlast(); };
        final.Completed += (_, _) => { if (playing && stage == Stage.Final) { stage = Stage.Done; finished?.TrySetResult(); } };
    }

    // The core grows from its rest size to the charged size once, when the cue starts, and then holds (the storyboard keeps its last value).
    private Storyboard BuildWarm()
    {
        var board = new Storyboard { Duration = TimeSpan.FromSeconds(WarmSeconds) };
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        KeyBoth(board, core, WarmSeconds, keys => { Key(keys, 0, CoreScaleFor(7)); Key(keys, WarmSeconds, CoreScaleFor(HeldRadius), ease); });
        KeyBoth(board, glow, WarmSeconds, keys => { Key(keys, 0, GlowScaleFor(7 * 2.4)); Key(keys, WarmSeconds, GlowScaleFor(HeldRadius * 2.4 + 4), ease); });
        var glowOpacity = Track(board, glow, OpacityPath, WarmSeconds); Key(glowOpacity, 0, 0.48); Key(glowOpacity, WarmSeconds, 0.7, ease);
        var white = Track(board, coreWhite, OpacityPath, WarmSeconds); Key(white, 0, 0.2); Key(white, WarmSeconds, 0.35, ease);
        return board;
    }

    // The calm loop (3.78 s): dust spirals in and is absorbed by the core. Every speck finishes inside the cycle, so the loop restarts cleanly and nothing smashes.
    private Storyboard BuildCycle()
    {
        var board = new Storyboard { Duration = TimeSpan.FromSeconds(CycleSeconds) };
        foreach (var speck in dust)
        {
            double begins = speck.Start * CycleSeconds, ends = Math.Min(CycleSeconds, (speck.Start + speck.Length) * CycleSeconds);
            var turn = Track(board, speck.Lane, AnglePath, CycleSeconds); var move = Track(board, speck.Dot, MovePath, CycleSeconds); var fade = Track(board, speck.Dot, OpacityPath, CycleSeconds);
            Key(turn, 0, DustAngle(speck.Angle, 0)); Key(move, 0, Reach); Key(fade, 0, 0);
            for (int step = 0; step <= Steps; step++)
            {
                double u = (double)step / Steps, time = begins + (ends - begins) * step / Steps;
                if (step == 0) time += 0.0005; // keeps the first point apart from the hold at the start
                Key(turn, time, DustAngle(speck.Angle, u)); Key(move, time, DustRadius(u));
            }
            // Fades in over the first seventh of the way, holds, and goes out over the last tenth (absorbed).
            Key(fade, begins, 0);
            foreach (double u in new[] { 1.0 / 7, 0.9, 1.0 }) Key(fade, begins + (ends - begins) * u, DustAlpha(u));
            if (ends < CycleSeconds - 0.001) { Key(turn, CycleSeconds, DustAngle(speck.Angle, 1)); Key(move, CycleSeconds, DustRadius(1)); Key(fade, CycleSeconds, 0); }
        }
        return board;
    }

    // At 100%: the core squashes tight (its glow follows) and whatever dust is still on its way fades out; the smash follows at once.
    private Storyboard BuildCharge()
    {
        var board = new Storyboard { Duration = TimeSpan.FromSeconds(ChargeSeconds) };
        var squash = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        double hold = ChargeSeconds * 0.2;
        KeyBoth(board, core, ChargeSeconds, keys => { Key(keys, 0, CoreScaleFor(HeldRadius)); Key(keys, hold, CoreScaleFor(HeldRadius)); Key(keys, ChargeSeconds, CoreScaleFor(SquashedRadius), squash); });
        KeyBoth(board, glow, ChargeSeconds, keys => { Key(keys, 0, GlowScaleFor(HeldRadius * 2.4 + 4)); Key(keys, hold, GlowScaleFor(HeldRadius * 2.4 + 4)); Key(keys, ChargeSeconds, GlowScaleFor(SquashedRadius * 2.4 + 10), squash); });
        var glowOpacity = Track(board, glow, OpacityPath, ChargeSeconds); Key(glowOpacity, 0, 0.7); Key(glowOpacity, hold, 0.7); Key(glowOpacity, ChargeSeconds, 1, squash);
        var white = Track(board, coreWhite, OpacityPath, ChargeSeconds); Key(white, 0, 0.35); Key(white, hold, 0.35); Key(white, ChargeSeconds, 0.8, squash);
        foreach (var speck in dust)
        {
            var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromSeconds(ChargeSeconds * 0.7) };
            Storyboard.SetTarget(fade, speck.Dot); Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityPath));
            board.Children.Add(fade);
        }
        return board;
    }

    // The smash (1.5 s): flash, shockwave rings, sparks and the core settling back.
    private Storyboard BuildBlast(double seconds, bool big)
    {
        var board = new Storyboard { Duration = TimeSpan.FromSeconds(seconds) };
        double reach = big ? Reach * 1.115 : Reach, span = big ? 1.0 : 1.1; // the ring reaches its full size at (1 / span) of the blast
        double toFull = seconds / span;

        // The core springs back from the squash and settles; the glow drops with it.
        double[] marks = [0, 0.04, 0.1, 0.2, 0.35, 0.5, 0.75, 1];
        KeyBoth(board, core, seconds, keys => { foreach (double k in marks) Key(keys, k * seconds, CoreScaleFor(7 + 11 * Math.Exp(-6 * k))); });
        KeyBoth(board, glow, seconds, keys => { foreach (double k in marks) Key(keys, k * seconds, GlowScaleFor((7 + 11 * Math.Exp(-6 * k)) * 2.4)); });
        var glowOpacity = Track(board, glow, OpacityPath, seconds); Key(glowOpacity, 0, 1); Key(glowOpacity, 0.08 * seconds, 0.48); Key(glowOpacity, seconds, 0.48);
        var white = Track(board, coreWhite, OpacityPath, seconds); Key(white, 0, 0.8); Key(white, 0.08 * seconds, 0.2); Key(white, seconds, 0.2);

        // The flash: a white bloom that swells a little and is gone within a fraction of the blast.
        double flashPeak = big ? 1.0 : 0.9;
        KeyBoth(board, flash, seconds, keys => { Key(keys, 0, (big ? 1.25 : 1) * 40.0 / 60); Key(keys, seconds, (big ? 1.25 : 1) * 70.0 / 60); });
        var flashOpacity = Track(board, flash, OpacityPath, seconds);
        foreach (double k in new[] { 0, 0.04, 0.08, 0.15, 0.25, 0.4, 1 }) Key(flashOpacity, k * seconds, k >= 1 ? 0 : flashPeak * Math.Exp(-12 * k));

        // The shockwave rings spread (fast at first, then slowing) and fade; the first wipes the dust it passes.
        AnimateWave(board, ring1, ring1Scale, 8 / Reach, reach / Reach, 0, toFull, seconds, 0.95);
        AnimateWave(board, ring2, ring2Scale, 8 / 90.5, big ? 1.115 : 1, 0.1 * seconds, 0.9 * seconds, seconds, 0.5);
        if (big) AnimateWave(board, ring3, ring3Scale, 8 / 87.0, 1, 0.22 * seconds, 0.78 * seconds, seconds, 0.5);

        // Sparks fly outward in every direction and fade.
        foreach (var spark in sparks)
        {
            var travel = Track(board, spark.Bar, MovePath, seconds); var fade = Track(board, spark.Bar, OpacityPath, seconds);
            Key(travel, 0, 12); Key(travel, seconds / 1.15, 12 + 76.8 * spark.Length * (big ? 1.115 : 1), new CubicEase { EasingMode = EasingMode.EaseOut }); Key(travel, seconds, 12 + 76.8 * spark.Length * (big ? 1.115 : 1));
            Key(fade, 0, 1); Key(fade, seconds / 1.2, 0); Key(fade, seconds, 0);
        }

        return board;
    }

    private static void AnimateWave(Storyboard board, Grid ring, ScaleTransform scale, double from, double to, double delay, double growSeconds, double seconds, double peak)
    {
        KeyBoth(board, ring, seconds, keys =>
        {
            Key(keys, 0, from); Key(keys, delay + 0.0005, from);
            Key(keys, delay + growSeconds, to, new CubicEase { EasingMode = EasingMode.EaseOut }); Key(keys, seconds, to);
        });
        var fade = Track(board, ring, OpacityPath, seconds);
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(delay + 0.0005))));
        Key(fade, seconds, 0, new PowerEase { Power = 1.4, EasingMode = EasingMode.EaseOut });
    }

    // ---- playing ------------------------------------------------------------------------------------------------------------------------------------

    private void Play()
    {
        if (playing) return;
        playing = true; finishing = false; finished = null;
        EnsureBuilt();
        warm!.Begin(this, true); warmActive = true;   // the core charges up once and holds
        StartCycle();
    }

    private void StartCycle()
    {
        stage = Stage.Cycle;
        cycle!.Begin(this, true); cycleActive = true;
    }

    // At 100%: the core squashes and the dust fades (these boards replace what the loop was animating); the smash starts when this board completes.
    private void StartCharge()
    {
        stage = Stage.Charge;
        charge!.Begin(this, true); chargeActive = true;
    }

    private void StartBlast()
    {
        IsFinalSmash = true;
        stage = Stage.Final; final!.Begin(this, true); finalActive = true;
        Smashed?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveBoard(Storyboard? board, ref bool active)
    {
        if (board == null || !active) return;
        active = false;
        try { board.Remove(this); } catch (InvalidOperationException) { /* never started or already removed: nothing to release */ }
    }

    private void Stop()
    {
        if (!playing) return;
        playing = false; finishing = false; stage = Stage.Idle; IsFinalSmash = false;
        RemoveBoard(warm, ref warmActive); RemoveBoard(cycle, ref cycleActive); RemoveBoard(charge, ref chargeActive); RemoveBoard(final, ref finalActive);
        ReleaseAnimations();
        finished?.TrySetResult(); finished = null;
    }

    // Boards that took over from each other (a later one replaces the earlier one's animation on a property) can leave a snapshot behind when removed, so every
    // animated property is released directly as well: the cue then rests exactly at its built values.
    private void ReleaseAnimations()
    {
        static void Free(Animatable target, params DependencyProperty[] properties) { foreach (var property in properties) target.BeginAnimation(property, null); }
        foreach (var speck in dust) { Free(speck.Turn, RotateTransform.AngleProperty); Free(speck.Move, TranslateTransform.XProperty); speck.Dot.BeginAnimation(OpacityProperty, null); }
        foreach (var scale in new[] { glowScale, coreScale, flashScale, ring1Scale, ring2Scale, ring3Scale }) Free(scale, ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty);
        foreach (var element in new UIElement[] { glow, coreWhite, flash, ring1, ring2, ring3 }) element.BeginAnimation(OpacityProperty, null);
        foreach (var spark in sparks) { spark.Bar.BeginAnimation(OpacityProperty, null); ((TranslateTransform)spark.Bar.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null); }
    }
}
