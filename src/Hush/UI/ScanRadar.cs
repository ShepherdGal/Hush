using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Hush;

// The cue while a scan runs (owner's pick, November 19, 2026, from the Gauge Motion previews: "M2", Radar + Orbit + Gather, softer):
//  * Radar: a soft beam turns inside the ring (3.4 s a turn); small points light up where it passes and fade, like files being found.
//  * Orbit: two beads of light circle the ring at different speeds (3.0 s and 4.6 s), each with a short fading tail.
//  * Gather: specks drift in from outside and settle onto the ring; the ring answers each touch with a short springy pulse of light.
// Under the digits a soft plate (discs in the window's own color behind the gauge) fades whatever passes, so the number always stays clear.
//
// How it stays cheap (the design rules, 60 fps native): every shape is built ONCE when the control is made; glow is layered translucent discs (no blur, no effect,
// no mask, no clip). Motion is only rotation, translation and opacity on the render thread at the display's own refresh rate; the endless animations exist only
// while a scan runs and the gauge is visible, and are released when it stops. Colors are theme resources (the accent, the plate color), so both themes and
// every main color follow by themselves.
public sealed class ScanRadar : Grid
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(ScanRadar),
        new PropertyMetadata(false, (sender, _) => ((ScanRadar)sender).Update()));
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    // Sizes are in the gauge's own 320 DIP space (Home shows it at 0.66). The previews were drawn at Home's size, so their distances are multiplied by K.
    private const double Size = 320, Middle = Size / 2, Radius = 128.5, K = Radius / 85.5;
    private const double Top = -Math.PI / 2;
    internal static readonly TimeSpan BeamTurn = TimeSpan.FromSeconds(3.4);
    private static readonly TimeSpan BlipFade = BeamTurn, GatherTrip = TimeSpan.FromSeconds(2.6);
    // The softer mix (M2): the beam and its points at 75 %, the beads at 85 %, the specks at 80 % and only 15 of them.
    private const double BeamStrength = 0.75, BeadStrength = 0.85, SpeckStrength = 0.8;
    private const int BeamSlices = 16, BlipCount = 16, TailDots = 7, SpeckCount = 15;
    private const double BeamSpan = 1.25, SpeckTravel = 48 * K, SpeckTurn = 20;

    private readonly RotateTransform beamTurn = new(0);
    private readonly List<(IAnimatable Target, DependencyProperty Property, AnimationTimeline Animation)> tracks = [];
    private bool sweeping;

    public ScanRadar()
    {
        IsHitTestVisible = false;
        Width = Size; Height = Size;
        Opacity = 0;
        var random = new Random(7);

        // Radar: the beam is a fan of thin pie slices, brightest at the leading edge, with a faint line on that edge; the whole fan turns.
        var beam = new Grid { Width = Size, Height = Size, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = beamTurn, Opacity = BeamStrength };
        for (int slice = 0; slice < BeamSlices; slice++)
        {
            double from = Top - (slice + 1) * BeamSpan / BeamSlices, to = Top - slice * BeamSpan / BeamSlices + 0.003;
            beam.Children.Add(Accent(new Path { Data = Pie(from, to, Radius - 3), Opacity = 0.16 * Math.Pow(1 - (double)slice / BeamSlices, 1.6) }, fill: true));
        }
        beam.Children.Add(Accent(new Path { Data = new LineGeometry(At(Top, 16 * K), At(Top, Radius - 3)), StrokeThickness = 1.5, Opacity = 0.42 }, fill: false));
        Children.Add(beam);
        tracks.Add((beamTurn, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, BeamTurn) { RepeatBehavior = RepeatBehavior.Forever }));

        // The points the beam finds: each lights at once when the beam reaches its angle and fades over the rest of the turn.
        var blips = new Canvas { Width = Size, Height = Size };
        for (int index = 0; index < BlipCount; index++)
        {
            double angle = random.NextDouble() * 360, distance = (26 + random.NextDouble() * 52) * K;
            var blip = GlowDot(1.5 * K, At(Top + angle * Math.PI / 180, distance));
            blip.Opacity = 0;
            blips.Children.Add(blip);
            var fade = new DoubleAnimationUsingKeyFrames { Duration = BlipFade, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(BeamTurn.TotalSeconds * angle / 360) };
            foreach (double seconds in new[] { 0, 0.3, 0.7, 1.2, 2.0, BlipFade.TotalSeconds })
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9 * BeamStrength * Math.Exp(-seconds / 1.1), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds))));
            tracks.Add((blip, OpacityProperty, fade));
        }
        Children.Add(blips);

        // Orbit: two beads, each a head with a fading tail drawn once on the ring; the whole bead turns around the middle.
        foreach (var (seconds, start) in new[] { (3.0, 0.0), (4.6, 180.0) })
        {
            var turn = new RotateTransform(start);
            var bead = new Canvas { Width = Size, Height = Size, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = turn, Opacity = BeadStrength };
            for (int dot = TailDots; dot >= 0; dot--)
            {
                var place = At(Top - dot * 0.07, Radius);
                double size = 2.1 * K * (1 - dot * 0.08);
                bead.Children.Add(dot == 0 ? GlowDot(size, place) : Disc(size, place, 0.75 * Math.Pow(1 - dot / 8.0, 1.6)));
            }
            Children.Add(bead);
            tracks.Add((turn, RotateTransform.AngleProperty, new DoubleAnimation(start, start + 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever }));
        }

        // Gather: each speck travels inward along its own lane (which turns a little as it comes) and lands on the ring, where the ring answers its touch.
        // The touch (owner's pick, November 20, 2026, "B, ring pulse, but a bit brighter"): the bit of ring where it lands lights up, swells with a spring
        // (about 1.7 times, a small bounce back) and fades within 0.6 s. It is drawn at the landing place in its own non-turning layer, so it stays where the
        // speck touched while the lane starts its next trip; a faint wider arc under it is its glow (no blur). Scale and opacity only.
        for (int index = 0; index < SpeckCount; index++)
        {
            double angle = random.NextDouble() * 360, offset = random.NextDouble();
            var begin = TimeSpan.FromSeconds(-offset * GatherTrip.TotalSeconds);
            var laneTurn = new RotateTransform(angle);
            var lane = new Canvas { Width = Size, Height = Size, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = laneTurn };
            var travel = new TranslateTransform(0, -(Radius + SpeckTravel));
            var speck = GlowDot(1.3 * K, new Point(Middle, Middle));
            speck.RenderTransform = travel; speck.Opacity = 0;
            lane.Children.Add(speck);
            Children.Add(lane);
            tracks.Add((laneTurn, RotateTransform.AngleProperty, new DoubleAnimation(angle, angle + SpeckTurn, GatherTrip) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin }));
            tracks.Add((travel, TranslateTransform.YProperty, new DoubleAnimation(-(Radius + SpeckTravel), -Radius, GatherTrip)
                { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } }));
            tracks.Add((speck, OpacityProperty, Keys(begin, (0, 0), (0.25, 0.85 * SpeckStrength), (1, 0.85 * SpeckStrength))));

            var swell = new ScaleTransform(1, 1, Middle, Middle - Radius);
            var touch = new Canvas { Width = Size, Height = Size, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(angle + SpeckTurn) };
            var pulse = new Canvas { Width = Size, Height = Size, RenderTransform = swell };
            pulse.Children.Add(Accent(new Path { Data = Arc(Top - 0.075, Top + 0.075, Radius), StrokeThickness = 6 * K, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0.22 }, fill: false));
            pulse.Children.Add(Accent(new Path { Data = Arc(Top - 0.05, Top + 0.05, Radius), StrokeThickness = 2.4 * K, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }, fill: false));
            touch.Children.Add(pulse);
            Children.Add(touch);
            // A trip ends where the next begins: the touch plays at the start of each trip (the speck that just landed), with a faint glow as the next one nears.
            tracks.Add((touch, OpacityProperty, Keys(begin, (0, TouchStrength), (TouchSeconds * 0.15 / TripSeconds, TouchStrength), (TouchSeconds / TripSeconds, 0), (0.9, 0), (1, 0.35 * TouchStrength))));
            tracks.Add((swell, ScaleTransform.ScaleXProperty, TouchSpring(begin)));
            tracks.Add((swell, ScaleTransform.ScaleYProperty, TouchSpring(begin)));
        }

        Children.Add(Plate(30, 54));   // sized for the smaller percentage (owner, November 20, 2026: 56 instead of 90)
        IsVisibleChanged += (_, _) => Update();
        System.Windows.Automation.AutomationProperties.SetName(this, "Scanning");
    }

    // True while the animations are actually playing; the beam's current angle (tests use both to see the cue start, move and stop).
    internal bool IsSweeping => sweeping;
    internal double Phase => beamTurn.Angle;

    private void Update()
    {
        // Like every motion in Hush, this cue is the person's chosen look and does not follow Windows' "animation effects" switch; it runs only while scanning.
        if (IsRunning && IsVisible)
        {
            if (!sweeping)
            {
                sweeping = true;
                foreach (var (target, property, animation) in tracks) target.BeginAnimation(property, animation);
            }
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
        }
        else if (IsRunning)
        {
            Stop(); // scanning but the page is hidden: nothing to draw, so nothing runs
        }
        else if (sweeping || Opacity > 0)
        {
            // The scan ended: the whole cue fades out softly, then everything stops.
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(480)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (_, _) => { if (!IsRunning) Stop(); };
            BeginAnimation(OpacityProperty, fade);
        }
    }

    private void Stop()
    {
        if (!sweeping) return;
        sweeping = false;
        foreach (var (target, property, _) in tracks) target.BeginAnimation(property, null);   // back to the built values
    }

    // ---- shapes, all made once ----

    private static Point At(double angle, double distance) => new(Middle + Math.Cos(angle) * distance, Middle + Math.Sin(angle) * distance);

    private static Geometry Pie(double from, double to, double radius)
    {
        var figure = new PathFigure { StartPoint = new Point(Middle, Middle), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(At(from, radius), false));
        figure.Segments.Add(new ArcSegment(At(to, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false));
        var geometry = new PathGeometry([figure]); geometry.Freeze();
        return geometry;
    }

    private static Geometry Arc(double from, double to, double radius)
    {
        var figure = new PathFigure { StartPoint = At(from, radius) };
        figure.Segments.Add(new ArcSegment(At(to, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]); geometry.Freeze();
        return geometry;
    }

    private static Shape Accent(Shape shape, bool fill)
    {
        shape.SetResourceReference(fill ? Shape.FillProperty : Shape.StrokeProperty, "TealTextBrush");
        return shape;
    }

    // A disc of the accent, centred on a point of the 320 space (the parent is a Canvas).
    private static Ellipse Disc(double radius, Point centre, double opacity)
    {
        var disc = (Ellipse)Accent(new Ellipse { Width = radius * 2, Height = radius * 2, Opacity = opacity }, fill: true);
        Canvas.SetLeft(disc, centre.X - radius); Canvas.SetTop(disc, centre.Y - radius);
        return disc;
    }

    // A point of light: the dot plus two wider, fainter discs around it (the glow, no blur), moved and faded as one.
    private static Grid GlowDot(double radius, Point centre)
    {
        double halo = radius * 4.2;
        var dot = new Grid { Width = halo * 2, Height = halo * 2, RenderTransformOrigin = new Point(0.5, 0.5) };
        foreach (var (scale, opacity) in new[] { (4.2, 0.07), (2.4, 0.16), (1.0, 1.0) })
            dot.Children.Add(Accent(new Ellipse { Width = radius * 2 * scale, Height = radius * 2 * scale, Opacity = opacity, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, fill: true));
        Canvas.SetLeft(dot, centre.X - halo); Canvas.SetTop(dot, centre.Y - halo);
        return dot;
    }

    // The touch's swell: a spring released by the landing, 1 → about 1.7 → a small dip to about 0.86 → back to 1, all within TouchSeconds; then it rests at 1.
    internal const double TouchStrength = 1.0, TouchSeconds = 0.6, TouchPeak = 1.5;
    private static double TripSeconds => GatherTrip.TotalSeconds;
    internal static double TouchScale(double seconds) =>
        seconds >= TouchSeconds ? 1 : 1 + TouchPeak * Math.Sin(Math.PI * seconds / 0.18) * Math.Exp(-6.5 * seconds) * (1 - seconds / TouchSeconds);
    private static DoubleAnimationUsingKeyFrames TouchSpring(TimeSpan begin)
    {
        var frames = new DoubleAnimationUsingKeyFrames { Duration = GatherTrip, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
        for (int step = 0; step <= 24; step++)
        {
            double seconds = TouchSeconds * step / 24;
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(TouchScale(seconds), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds))));
        }
        return frames;
    }

    // Opacity keys at fractions of a gather trip, repeating with the speck.
    private static DoubleAnimationUsingKeyFrames Keys(TimeSpan begin, params (double At, double Value)[] keys)
    {
        var frames = new DoubleAnimationUsingKeyFrames { Duration = GatherTrip, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
        foreach (var (at, value) in keys) frames.KeyFrames.Add(new LinearDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at * GatherTrip.TotalSeconds))));
        return frames;
    }

    // The soft plate under the digits: discs in the color behind the gauge, nearly solid inside "solid" and fading to nothing at "edge" (shared with the cleaning cue).
    internal static Grid Plate(double solid, double edge)
    {
        var plate = new Grid { Width = edge * 2, Height = edge * 2, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        const int bands = 8; const double strength = 0.9;
        double previous = 0;
        void Add(double radius, double opacity)
        {
            var disc = new Ellipse { Width = radius * 2, Height = radius * 2, Opacity = Math.Clamp(opacity, 0, 1) };
            disc.SetResourceReference(Shape.FillProperty, "GaugePlateBrush");
            plate.Children.Add(disc);
        }
        for (int band = 0; band < bands; band++)
        {
            double radius = edge - (edge - solid) * band / bands, middle = radius - (edge - solid) / bands / 2;
            double wanted = strength * (edge - middle) / (edge - solid);
            Add(radius, 1 - (1 - wanted) / (1 - previous)); previous = wanted;
        }
        Add(solid, 1 - (1 - strength) / (1 - previous));
        return plate;
    }
}
