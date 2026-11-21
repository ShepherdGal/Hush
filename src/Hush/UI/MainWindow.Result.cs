using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Hush;

// One line of a result: what was done, and whether it worked.
internal sealed record ResultLine(bool Ok, string Text);

// The result popup (owner, November 20, 2026, mockup A): after changes are applied the person sees at a glance that they worked: a big green check whose two
// rings open once (scale and opacity, finite), or an amber "!" when a part did not work; the checked list; advice in its own box; one centered OK.
public partial class MainWindow
{
    private TaskCompletionSource? resultShown;
    private IInputElement? resultReturnFocus;
    private bool resultSurfaceWasEnabled = true;

    internal bool ResultPopupOpen => ResultOverlay.Visibility == Visibility.Visible;
    internal IReadOnlyList<ResultLine> ResultLinesShown => ResultLines.ItemsSource as IReadOnlyList<ResultLine> ?? [];

    internal Task ShowResultAsync(string title, string subtitle, IReadOnlyList<ResultLine> lines, string advice)
    {
        resultShown?.TrySetResult();
        resultShown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool success = lines.All(line => line.Ok);
        ResultTitle.Text = title; ResultSubtitle.Text = subtitle;
        ResultSubtitle.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultLines.ItemsSource = lines.ToList();
        ResultAdvice.Text = advice.Trim();
        ResultAdviceBox.Visibility = advice.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Green check for success, the review amber with "!" when a part did not work.
        string tone = success ? "GoodBrush" : "MeterWarnBrush";
        foreach (var shape in new Shape[] { ResultRingOuter, ResultRingInner }) shape.SetResourceReference(Shape.StrokeProperty, tone);
        ResultBadgeFill.SetResourceReference(Shape.FillProperty, tone);
        ResultCheck.Visibility = success ? Visibility.Visible : Visibility.Collapsed;
        ResultMark.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
        resultReturnFocus = Keyboard.FocusedElement;
        resultSurfaceWasEnabled = AppSurface.IsEnabled;
        AppSurface.IsEnabled = false;
        ResultOverlay.Visibility = Visibility.Visible;
        Motion.OpenPopup(ResultOverlay);
        PlayResultRings();
        ResultOkButton.Focus();
        return resultShown.Task;
    }

    // The two rings open once from the badge: scale 0.7 → 1 with the jump's ease, opacity in; nothing repeats.
    private void PlayResultRings()
    {
        foreach (var (ring, delay) in new[] { (ResultRingInner, 80), (ResultRingOuter, 200) })
        {
            if (ring.RenderTransform is not ScaleTransform scale || scale.IsFrozen) { scale = new ScaleTransform(); ring.RenderTransform = scale; }
            var grow = new DoubleAnimation(0.7, 1, Motion.Span(700)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            double rest = ring == ResultRingInner ? 0.28 : 0.14;
            ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0, rest, Motion.Span(700)) { BeginTime = TimeSpan.FromMilliseconds(delay) });
        }
    }

    private async void ResultOk_Click(object sender, RoutedEventArgs e)
    {
        var pending = resultShown; resultShown = null;
        ResultOverlay.IsEnabled = false;
        await Motion.FadeOut(ResultOverlay);
        ResultOverlay.Visibility = Visibility.Collapsed;
        ResultOverlay.IsEnabled = true;
        AppSurface.IsEnabled = resultSurfaceWasEnabled;
        if (resultReturnFocus != null) Keyboard.Focus(resultReturnFocus);
        pending?.TrySetResult();
    }
}
