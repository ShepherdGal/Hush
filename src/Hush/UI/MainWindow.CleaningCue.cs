using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Hush;

// The window's share of the Smash cue (ScanActivity): at every smash the percentage swells a little and the gauge ring's glow flares for a moment (the final smash
// a bit more). Both are one short animation that releases itself (FillBehavior.Stop); nothing runs between smashes and no timer is involved.
public partial class MainWindow
{
    private ScaleTransform? gaugeValuePulse;

    private void CleaningCue_Smashed(object? sender, EventArgs e)
    {
        bool final = FileScanActivity.IsFinalSmash;
        if (gaugeValuePulse == null)
        {
            gaugeValuePulse = new ScaleTransform(1, 1);
            GaugeValue.RenderTransformOrigin = new Point(0.5, 0.5);
            GaugeValue.RenderTransform = gaugeValuePulse;
        }
        var out1 = new CubicEase { EasingMode = EasingMode.EaseOut };
        DoubleAnimationUsingKeyFrames Pulse(double rest, double peak, int peakAt, int endAt)
        {
            var frames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(peakAt)), out1));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(endAt)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            return frames;
        }
        double swell = final ? 1.09 : 1.05;
        gaugeValuePulse.BeginAnimation(ScaleTransform.ScaleXProperty, Pulse(1, swell, 110, 600));
        gaugeValuePulse.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(1, swell, 110, 600));
        // The ring's glow (already on the arc) flares and settles back to its resting strength. The final smash leaves it alone: the finished look (CompleteGauge) takes over
        // right after it and keeps the whole ring glowing, which a pulse that returns to the resting value would undo.
        if (final) return;
        GaugeFinishGlow.BeginAnimation(DropShadowEffect.OpacityProperty, Pulse(0.22, 0.6, 130, 900));
        GaugeFinishGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, Pulse(12, 20, 130, 900));
    }
}
