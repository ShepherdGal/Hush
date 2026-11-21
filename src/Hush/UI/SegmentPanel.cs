using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hush;

// The spring every selection moves with (owner, November 20, 2026: "spring physics when selecting", lively): a damped spring released at the old place and
// pulled to the new one. Damping 0.7 gives about a 4.6 % overshoot and a barely visible bounce back (owner, November 20, 2026: the first spring, 0.6, was "a bit
// too strong"). A pure damped spring never quite arrives: at the end of Motion.SpringMilliseconds it is still a fraction of a percent short, and the drawing
// then snapped the last pixels ("not aligned when landing"). So the small leftover is removed along the way with a smootherstep weight (zero speed at both
// ends): the curve ends exactly on 1 and rests there without a jump.
public sealed class SpringEase : EasingFunctionBase
{
    private const double Damping = 0.7, Stiffness = 9.2;   // Stiffness = the spring's angular frequency times the duration (15.3 rad/s x 0.6 s)
    private static readonly double Leftover = Raw(1);       // how far short (or past) the pure spring is at the end
    public SpringEase() => EasingMode = EasingMode.EaseIn;  // EaseInCore is the whole curve
    private static double Raw(double t)
    {
        double damped = Stiffness * Math.Sqrt(1 - Damping * Damping);
        return 1 - Math.Exp(-Damping * Stiffness * t) * (Math.Cos(damped * t) + Damping * Stiffness / damped * Math.Sin(damped * t));
    }
    protected override double EaseInCore(double t)
    {
        if (t >= 1) return 1;
        if (t <= 0) return 0;
        double weight = t * t * t * (t * (t * 6 - 15) + 10);   // smootherstep: 0 at the start, 1 at the end, no speed at either
        return Raw(t) + (1 - Leftover) * weight;
    }
    protected override Freezable CreateInstanceCore() => new SpringEase();
}

// The jump's curve as a WPF easing (cubic ease-in-out, the same numbers as SmoothScroll.JumpEase).
public sealed class JumpEase : EasingFunctionBase
{
    public JumpEase() => EasingMode = EasingMode.EaseIn;   // EaseInCore is the whole curve
    protected override double EaseInCore(double t) => SmoothScroll.JumpEase(t);
    protected override Freezable CreateInstanceCore() => new JumpEase();
}

// One selection that slides between the choices of a bar (owner, November 20, 2026: every bar in the app springs when you pick): the Interface size and Boost
// chips, the uninstall depth, the Programs sort, the Tweaks and Software updates tabs, the Settings index. The panel draws ONE pill (or, with Underline, one
// 2 DIP line) behind its children, at the child marked SegmentPanel.IsSelected (a style trigger sets it on a checked chip or a Tag="On" segment; an item
// container style binds it for lists). A new pick moves the drawing with the spring on the render thread (a RectAnimation clock in the panel's own drawing):
// no layout, no timer, no effect, nothing per frame on the UI thread. A resize or the first pick snaps it in place.
public sealed class SegmentPanel : StackPanel
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached("IsSelected", typeof(bool), typeof(SegmentPanel),
        new PropertyMetadata(false, SelectedChanged));
    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);

    public static readonly DependencyProperty ThumbBrushProperty = DependencyProperty.Register(nameof(ThumbBrush), typeof(Brush), typeof(SegmentPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThumbEdgeProperty = DependencyProperty.Register(nameof(ThumbEdge), typeof(Brush), typeof(SegmentPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThumbRadiusProperty = DependencyProperty.Register(nameof(ThumbRadius), typeof(double), typeof(SegmentPanel),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnderlineProperty = DependencyProperty.Register(nameof(Underline), typeof(bool), typeof(SegmentPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush? ThumbBrush { get => (Brush?)GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }
    public Brush? ThumbEdge { get => (Brush?)GetValue(ThumbEdgeProperty); set => SetValue(ThumbEdgeProperty, value); }
    public double ThumbRadius { get => (double)GetValue(ThumbRadiusProperty); set => SetValue(ThumbRadiusProperty, value); }
    public bool Underline { get => (bool)GetValue(UnderlineProperty); set => SetValue(UnderlineProperty, value); }
    // Jump: the selection moves on the page jump's curve and time (Motion.Jump, 350 ms, no overshoot) instead of the spring: the Settings index, whose
    // highlight travels together with the page glide (owner, November 20, 2026).
    public static readonly DependencyProperty JumpProperty = DependencyProperty.Register(nameof(Jump), typeof(bool), typeof(SegmentPanel), new PropertyMetadata(false));
    public bool Jump { get => (bool)GetValue(JumpProperty); set => SetValue(JumpProperty, value); }
    private IEasingFunction Ease => Jump ? Motion.Jump : Motion.Spring;

    private Rect? target;               // where the selection rests
    private Rect from, to;              // the running spring, if any
    private AnimationClock? clock;

    // Test and preview seam: where the selection is drawn right now (the spring's current place while it moves).
    internal Rect? Shown => clock is { CurrentProgress: double progress } && clock.CurrentState == ClockState.Active
        ? Lerp(from, to, Ease.Ease(progress)) : target;
    internal bool IsSpringing => clock?.CurrentState == ClockState.Active;

    private static void SelectedChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (!(bool)args.NewValue) return;   // the newly picked one moves the selection; the old one only lets go
        for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is SegmentPanel panel) { panel.Retarget(animate: true); return; }
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        Retarget(animate: false);           // a layout change (size, a count that grew) puts the selection where it belongs at once
        return size;
    }

    private void Retarget(bool animate)
    {
        Rect? next = SelectedBounds();
        if (next is not Rect place) { if (target != null) { target = null; clock = null; InvalidateVisual(); } return; }
        if (target == place) return;   // already there, or the spring is on its way there
        if (animate && target is Rect && IsLoaded && IsVisible && Motion.Animates)
        {
            from = Shown ?? place;
            to = place;
            var spring = new RectAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(Jump ? Motion.JumpMilliseconds : Motion.SpringMilliseconds))) { EasingFunction = Ease };
            var started = spring.CreateClock();
            started.Completed += (_, _) => { if (ReferenceEquals(clock, started)) { clock = null; InvalidateVisual(); } };
            clock = started;
        }
        else clock = null;
        target = place;
        InvalidateVisual();
    }

    private Rect? SelectedBounds()
    {
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Visible || !GetIsSelected(child)) continue;
            // A list item: measure what the item shows, not its container (its margin is spacing, not part of the choice).
            var shown = child is ContentPresenter presenter && VisualTreeHelper.GetChildrenCount(presenter) > 0 && VisualTreeHelper.GetChild(presenter, 0) is FrameworkElement inner
                ? inner : (FrameworkElement)child;
            if (shown.ActualWidth <= 0 || shown.ActualHeight <= 0) return null;
            // Where layout put it, without render transforms: a click arrives while the button's 97 % press is still shrinking it, and measuring that pressed
            // shape made the selection land a few pixels off its entry (owner, November 21, 2026: the vertical rails' landing).
            var corner = new Point();
            for (Visual? step = shown; step != null && !ReferenceEquals(step, this); step = VisualTreeHelper.GetParent(step) as Visual)
            {
                var offset = VisualTreeHelper.GetOffset(step);
                corner.Offset(offset.X, offset.Y);
            }
            var box = new Rect(corner, new Size(shown.ActualWidth, shown.ActualHeight));
            return Underline ? new Rect(box.X, box.Bottom - 1, box.Width, 2) : box;
        }
        return null;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (target is not Rect place || ThumbBrush is not Brush fill) return;
        double radius = Underline ? 1 : ThumbRadius;
        var edge = ThumbEdge is Brush line && !Underline ? new Pen(line, 1) : null;
        if (clock != null) drawing.DrawRoundedRectangle(fill, edge, to, clock, radius, null, radius, null);
        else drawing.DrawRoundedRectangle(fill, edge, place, radius, radius);
    }

    private static Rect Lerp(Rect a, Rect b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, Math.Max(0, a.Width + (b.Width - a.Width) * t), Math.Max(0, a.Height + (b.Height - a.Height) * t));
}
