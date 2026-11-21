using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Hush;

internal enum GaugeHoverStyle { Off, Ready, Complete }

// The hover on the big gauge (0.9, user request): a calm, finite response, with a different personality before and after a scan.
//   Ready (before a scan, "Click to scan"): the ring wakes up. A soft comet of light makes one lap of the track, one ring of light breathes outward
//   from the gauge, and the track takes a gentle accent tint while the pointer stays.
//   Complete (after a scan, "Click to scan again"): the ring settles. The inside fills with a faint glow, two rings of light contract onto the gauge one
//   after the other, and a short glint glides once around the finished ring.
// Everything is drawn once, up front (hairlines inside layered translucent strokes, which reads as a soft glow without any blur effect) and animated on
// WPF's render thread by transform and opacity only: no timer, no effect, no mask or clip, nothing that repeats forever, and every clock is released when
// it ends. Leaving the gauge fades the held parts out; a click (the gauge becomes busy) removes everything at once. Accent brushes only.
internal sealed class GaugeHoverCue : Grid
{
    private const double Size = 320, Center = 160, TrackRadius = 127.5, TrackThickness = 7;
    private const int Rest = 700; // milliseconds before the same sweep may play again (rapid in-and-out never restarts it mid-air)

    // Held while the pointer stays.
    private readonly Ellipse trackTint = Ring(29, TrackThickness, "TealBrush");
    private readonly Ellipse innerGlow = new() { Margin = new Thickness(35), IsHitTestVisible = false, Opacity = 0 };
    // One-shot parts.
    private readonly Grid comet = Layer();
    private readonly Path[] cometLayers;
    private readonly double[] cometPeaks = [0.10, 0.16, 0.40, 0.95];
    private readonly RingPair pingOut = new(), contractFirst = new(), contractSecond = new();
    private readonly Grid glint = Layer();
    private readonly Path[] glintLayers;
    private readonly double[] glintPeaks = [0.12, 0.95];
    private readonly RotateTransform cometTurn = new(), glintTurn = new();

    private GaugeHoverStyle style = GaugeHoverStyle.Off;
    private long lastSweep, sweepEnds;

    internal GaugeHoverStyle Current => style;
    // True while a one-shot sweep is on screen (used by tests).
    internal bool IsPlaying => lastSweep != 0 && Environment.TickCount64 - lastSweep < 1800;
    internal double TrackTint => trackTint.Opacity;
    internal double InnerGlow => innerGlow.Opacity;

    public GaugeHoverCue()
    {
        IsHitTestVisible = false;
        Width = Height = Size;
        Visibility = Visibility.Collapsed; // collapsed while idle: a hidden cue costs the gauge's other animations nothing (measured: seven idle layers doubled the cleaning cue's frame time with software rendering)
        innerGlow.SetResourceReference(Shape.FillProperty, "TealBrush");
        // The comet: a bloom, a long faint tail, a middle and a bright head, all ending at the same point (twelve o'clock) so the head leads.
        cometLayers = [Arc(80, 16, "TealBrush"), Arc(130, TrackThickness, "TealBrush"), Arc(70, TrackThickness, "TealBrush"), Arc(26, TrackThickness, "TealBrush")];
        foreach (var layer in cometLayers) comet.Children.Add(layer);
        comet.RenderTransform = cometTurn;
        // The glint: a short, lighter segment with a faint bloom, for the finished ring.
        glintLayers = [Arc(52, 15, "AccentHoverBrush"), Arc(20, TrackThickness, "AccentHoverBrush")];
        foreach (var layer in glintLayers) glint.Children.Add(layer);
        glint.RenderTransform = glintTurn;
        foreach (UIElement part in new UIElement[] { innerGlow, trackTint, pingOut, contractFirst, contractSecond, comet, glint }) Children.Add(part);
    }

    // ---------- public surface ----------
    // The pointer is over the gauge and the gauge can be used: play the response for this style (again only when the style changed or the sweep has rested).
    internal void Enter(GaugeHoverStyle wanted)
    {
        if (wanted == GaugeHoverStyle.Off) { Leave(); return; }
        bool changed = wanted != style;
        Visibility = Visibility.Visible;
        if (changed) ReleaseHeld(quick: true);
        style = wanted;
        bool ready = wanted == GaugeHoverStyle.Ready;
        Fade(trackTint, ready ? 0.34 : 0.14, 320);
        Fade(innerGlow, ready ? 0 : 0.075, 420);
        long now = Environment.TickCount64;
        if (!changed && now - lastSweep < Rest) return;
        lastSweep = now;
        sweepEnds = now + (ready ? 1400 : 1900);
        if (ready) PlayReady(); else PlayComplete();
    }

    // The pointer left: the held parts fade away; a sweep that is already under way finishes (it is short) so nothing ever snaps.
    internal void Leave()
    {
        if (style == GaugeHoverStyle.Off) return;
        style = GaugeHoverStyle.Off;
        Fade(trackTint, 0, 320, Settled);
        Fade(innerGlow, 0, 320);
    }

    // Called when a fade-out or a sweep ends: once nothing is held or playing any more the cue collapses again.
    private void Settled()
    {
        if (style == GaugeHoverStyle.Off && Environment.TickCount64 + 60 >= sweepEnds && trackTint.Opacity < 0.005 && innerGlow.Opacity < 0.005) Visibility = Visibility.Collapsed;
    }

    // The gauge is busy (or the page is hidden): remove everything now. Leaves no clock behind.
    internal void Stop()
    {
        style = GaugeHoverStyle.Off;
        foreach (UIElement part in Children) part.BeginAnimation(OpacityProperty, null);
        foreach (var layer in cometLayers.Concat(glintLayers)) layer.BeginAnimation(OpacityProperty, null);
        ClearTransforms();
        trackTint.Opacity = innerGlow.Opacity = 0;
        foreach (var layer in cometLayers.Concat(glintLayers)) layer.Opacity = 0;
        foreach (var pair in new[] { pingOut, contractFirst, contractSecond }) pair.Stop();
        lastSweep = sweepEnds = 0;
        Visibility = Visibility.Collapsed;
    }

    // ---------- the two responses ----------
    private void PlayReady()
    {
        // Comet: fade in fast, hold, dissolve while it slows to a stop after one lap (decelerating, so it settles instead of ending abruptly).
        const int lap = 1300;
        Turn(cometTurn, 0, 360, lap, new CubicEase { EasingMode = EasingMode.EaseOut });
        for (int i = 0; i < cometLayers.Length; i++)
        {
            var pulse = Pulse(cometPeaks[i], lap, riseAt: 0.09, fallFrom: 0.58);
            if (i == 0) pulse.Completed += (_, _) => Settled();
            cometLayers[i].BeginAnimation(OpacityProperty, pulse);
        }
        // One ring of light breathes out from the track and fades.
        pingOut.Play(from: 1.0, to: 1.085, delay: 0, duration: 1000, peak: 1, riseAt: 0.09);
    }

    private void PlayComplete()
    {
        // Two rings contract onto the finished gauge, one after the other, then a glint glides once around it.
        contractFirst.Play(from: 1.11, to: 1.0, delay: 0, duration: 1000, peak: 0.85, riseAt: 0.3);
        contractSecond.Play(from: 1.11, to: 1.0, delay: 230, duration: 1000, peak: 0.55, riseAt: 0.3);
        const int lap = 1800;
        Turn(glintTurn, 0, 360, lap, new SineEase { EasingMode = EasingMode.EaseInOut });
        for (int i = 0; i < glintLayers.Length; i++)
        {
            var pulse = Pulse(glintPeaks[i], lap, riseAt: 0.12, fallFrom: 0.7);
            if (i == 0) pulse.Completed += (_, _) => Settled();
            glintLayers[i].BeginAnimation(OpacityProperty, pulse);
        }
    }

    // ---------- helpers ----------
    private void ReleaseHeld(bool quick)
    {
        Fade(trackTint, 0, quick ? 160 : 320);
        Fade(innerGlow, 0, quick ? 160 : 320);
    }

    private void ClearTransforms()
    {
        cometTurn.BeginAnimation(RotateTransform.AngleProperty, null); cometTurn.Angle = 0;
        glintTurn.BeginAnimation(RotateTransform.AngleProperty, null); glintTurn.Angle = 0;
    }

    private static void Turn(RotateTransform turn, double from, double to, int milliseconds, IEasingFunction ease) =>
        turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });

    // Up to its peak quickly, level for a while, then down to nothing. The clock is released when it ends (the resting opacity is 0).
    private static DoubleAnimationUsingKeyFrames Pulse(double peak, int milliseconds, double riseAt, double fallFrom)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(milliseconds), FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(riseAt), new SineEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(fallFrom)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new SineEase { EasingMode = EasingMode.EaseInOut }));
        return animation;
    }

    private static void Fade(UIElement element, double to, double milliseconds, Action? done = null)
    {
        var tween = Motion.Tween(element.Opacity, to, milliseconds);
        if (done != null) tween.Completed += (_, _) => done();
        element.BeginAnimation(OpacityProperty, tween);
    }

    private static Grid Layer() => new() { Width = Size, Height = Size, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };

    private static Ellipse Ring(double margin, double thickness, string brush)
    {
        var ring = new Ellipse { Margin = new Thickness(margin), StrokeThickness = thickness, Fill = Brushes.Transparent, IsHitTestVisible = false, Opacity = 0 };
        ring.SetResourceReference(Shape.StrokeProperty, brush);
        return ring;
    }

    // An arc of the track that ends at twelve o'clock and reaches back "degrees" counter-clockwise, so a turn of the group makes its end lead clockwise.
    private static Path Arc(double degrees, double thickness, string brush)
    {
        static Point At(double angle) { double radians = angle * Math.PI / 180; return new(Center + TrackRadius * Math.Sin(radians), Center - TrackRadius * Math.Cos(radians)); }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(At(-degrees), false, false);
            context.ArcTo(At(0), new Size(TrackRadius, TrackRadius), 0, degrees > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        var path = new Path { Data = geometry, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false, Opacity = 0, Width = Size, Height = Size, Stretch = Stretch.None };
        path.SetResourceReference(Shape.StrokeProperty, brush);
        return path;
    }

    // A ring of light: a hairline wrapped in a faint wide stroke (the glow), scaled and faded together.
    private sealed class RingPair : Grid
    {
        private readonly Ellipse hairline = Ring(29, 1.6, "TealBrush"), glow = Ring(29, 9, "TealBrush");
        private readonly ScaleTransform scale = new(1, 1);

        internal RingPair()
        {
            IsHitTestVisible = false;
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = scale;
            Children.Add(glow); Children.Add(hairline);
        }

        internal void Play(double from, double to, int delay, int duration, double peak, double riseAt)
        {
            var begin = TimeSpan.FromMilliseconds(delay);
            var grow = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(duration)) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            var line = Pulse(peak, duration, riseAt, riseAt + 0.001); line.BeginTime = begin;
            var halo = Pulse(peak * 0.16, duration, riseAt, riseAt + 0.001); halo.BeginTime = begin;
            hairline.BeginAnimation(OpacityProperty, line);
            glow.BeginAnimation(OpacityProperty, halo);
        }

        internal void Stop()
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = 1;
            hairline.BeginAnimation(OpacityProperty, null); glow.BeginAnimation(OpacityProperty, null);
            hairline.Opacity = glow.Opacity = 0;
        }
    }
}
