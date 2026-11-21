using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Hush;

// Smooth scrolling for the whole app (0.9, user request). WPF jumps a fixed distance for each wheel notch; here every vertical ScrollViewer glides to where the
// wheel or touchpad points, with an eased animation that is retargeted (not restarted) by each further notch, so a quick spin keeps accelerating and a single
// notch settles softly. One class handler covers every ScrollViewer in the app, present and future, so no screen needs its own code.
// Rules: the innermost ScrollViewer under the pointer that can still move in that direction takes the wheel (a list inside a page scrolls first and hands over at
// its end); dragging the scrollbar, the keyboard and code such as ScrollToTop cancel a glide at once; item-based (logical) scrolling is left alone; Ctrl/Shift/Alt
// wheel and horizontal scrolling are untouched; the glide is driven frame by frame only while it runs (nothing at all while the page is still).
public static class SmoothScroll
{
    private static bool installed;
    // Time constant of the glide: the page covers about 63% of the way to where the wheel points in this long, and is within a pixel after roughly five times
    // as much (short moves feel crisp, long ones take a little longer).
    private const double SettleMilliseconds = 75;

    public static void Install()
    {
        if (installed) return;
        installed = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel), handledEventsToo: false);
    }

    private static readonly DependencyProperty GlideProperty = DependencyProperty.RegisterAttached("Glide", typeof(Glide), typeof(SmoothScroll));

    // Test seam: whether this ScrollViewer is gliding right now.
    internal static bool IsGliding(ScrollViewer viewer) => viewer.GetValue(GlideProperty) is Glide { Active: true };

    private static Glide For(ScrollViewer viewer)
    {
        if (viewer.GetValue(GlideProperty) is Glide existing) return existing;
        var glide = new Glide(viewer);
        viewer.SetValue(GlideProperty, glide);
        return glide;
    }

    // Item-based scrolling (a virtualizing list set to scroll by item) measures offsets in rows, not DIP, so it keeps WPF's own behavior.
    private static bool PixelScrolling(ScrollViewer viewer)
    {
        if (!viewer.CanContentScroll) return true;
        // The panel that does the scrolling is the first virtualizing panel inside the viewer's content presenter (a ListBox's rows).
        var presenter = viewer.Template?.FindName("PART_ScrollContentPresenter", viewer) as DependencyObject;
        var panel = presenter == null ? null : FirstVirtualizingPanel(presenter, 0);
        if (panel == null) return true;
        // ScrollUnit is set on the list (the ItemsControl that owns the panel), not on the panel itself: reading it from the panel always said "by item", so a list that
        // scrolls by pixel (the Review file lists) was never given the wheel and the page behind it scrolled instead.
        return VirtualizingPanel.GetScrollUnit(ItemsControl.GetItemsOwner(panel) ?? (DependencyObject)panel) != ScrollUnit.Item;
    }

    // Used by the scrollbar's up and down chevrons (ScrollSteps): where the viewer is heading (the glide's target while one runs), and a way to move it softly.
    internal static double TargetOffset(ScrollViewer viewer) => viewer.GetValue(GlideProperty) is Glide { Active: true } glide ? glide.Target : viewer.VerticalOffset;
    internal static void MoveBy(ScrollViewer viewer, double distance) => For(viewer).Nudge(distance);

    // A jump to a place (owner, November 20, 2026: the Settings index glide "feels weird", remade): one fixed-length glide, slow-fast-slow, for any distance,
    // instead of the wheel's smoothing (which lurched on the first frame and then crawled for most of a second). Its end is re-clamped every frame, so a page
    // that grows or shrinks meanwhile cannot strand it; a layout change never stops it; the wheel, the scrollbar or the keyboard take over at once.
    internal static double GlideTo(ScrollViewer viewer, double offset, double milliseconds) => For(viewer).Jump(offset, milliseconds);
    // True while such a jump runs (not the wheel's glide).
    internal static bool IsJumping(ScrollViewer viewer) => viewer.GetValue(GlideProperty) is Glide { Active: true, Timed: true };

    // The jump's curve: cubic ease-in-out (no overshoot), shared with the Settings index highlight so page and highlight move as one.
    internal static double JumpEase(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    internal static VirtualizingPanel? FirstVirtualizingPanel(DependencyObject parent, int depth)
    {
        if (depth > 6) return null;
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is VirtualizingPanel panel) return panel;
            if (child is ScrollViewer) continue; // a nested viewer has its own panel
            if (FirstVirtualizingPanel(child, depth + 1) is { } found) return found;
        }
        return null;
    }

    private static bool CanMove(ScrollViewer viewer, int delta)
    {
        if (viewer.ScrollableHeight <= 0 || viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled || !PixelScrolling(viewer)) return false;
        double at = viewer.GetValue(GlideProperty) is Glide { Active: true } glide ? glide.Target : viewer.VerticalOffset;
        return delta > 0 ? at > 0.5 : at < viewer.ScrollableHeight - 0.5;
    }

    // The first ScrollViewer from the pointer upward that can still move in this direction.
    private static ScrollViewer? Owner(DependencyObject? source, int delta)
    {
        for (var element = source; element != null; element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is ScrollViewer viewer && CanMove(viewer, delta)) return viewer;
        return null;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None || sender is not ScrollViewer viewer) return;
        if (!ReferenceEquals(Owner(e.OriginalSource as DependencyObject, e.Delta), viewer)) return; // an inner list (or none) takes it
        e.Handled = true;
        // One notch (120) moves the distance Windows is set to: three lines by default, or a page when the setting says so.
        int lines = SystemParameters.WheelScrollLines;
        double notch = lines < 0 ? viewer.ViewportHeight * 0.9 : lines * 16;
        For(viewer).Nudge(-e.Delta / 120.0 * notch);
    }

    // The glide of one ScrollViewer. Every rendered frame moves the offset a fixed share of the remaining distance to the target (exponential smoothing,
    // scaled by the real time since the last frame), so a further notch just moves the target: the speed never restarts, a quick spin keeps accelerating
    // smoothly and a slow frame (a weak PC) makes the next step longer instead of the glide longer. It runs only while a glide is in progress. A change of
    // the offset that did not come from here (the scrollbar, ScrollToTop, the keyboard) ends the glide.
    private sealed class Glide
    {
        private readonly ScrollViewer viewer;
        private double lastSet = double.NaN, position;
        private TimeSpan lastFrame = TimeSpan.MinValue;
        // A timed jump: where it started, how long it takes and when its first frame was.
        private double jumpFrom, jumpMilliseconds;
        private TimeSpan jumpStart = TimeSpan.MinValue;
        internal bool Active { get; private set; }
        internal bool Timed { get; private set; }
        internal double Target { get; private set; }

        internal Glide(ScrollViewer viewer)
        {
            this.viewer = viewer;
            viewer.ScrollChanged += (_, e) =>
            {
                if (!Active || e.VerticalChange == 0) return;
                // A jump ignores offset changes that come from the page's own size changing (a list loading, a line of text growing): only a real move by
                // someone else (the scrollbar, the keyboard, code) stops it.
                if (Timed && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)) return;
                if (Math.Abs(viewer.VerticalOffset - lastSet) > 1.5) Stop(); // someone else moved it
            };
            viewer.Unloaded += (_, _) => Stop();
        }

        internal void Nudge(double distance)
        {
            double from = viewer.VerticalOffset;
            if (Timed) { Timed = false; Target = position = lastSet = from; }   // the wheel takes over a jump from where the page is now
            double start = Active ? Target : from;
            Target = Math.Clamp(start + distance, 0, viewer.ScrollableHeight);
            if (Math.Abs(Target - from) < 0.5) { Stop(); return; }
            if (Active) return; // already gliding: only the target moved
            Active = true;
            position = lastSet = from;
            lastFrame = TimeSpan.MinValue;
            CompositionTarget.Rendering += OnFrame;
        }

        internal double Jump(double offset, double milliseconds)
        {
            double from = viewer.VerticalOffset;
            Target = Math.Clamp(offset, 0, viewer.ScrollableHeight);
            if (Math.Abs(Target - from) < 0.5 || milliseconds <= 0 || !Motion.Animates) { Stop(); viewer.ScrollToVerticalOffset(Target); return Target; }
            jumpFrom = position = lastSet = from;
            jumpMilliseconds = milliseconds;
            jumpStart = TimeSpan.MinValue;
            Timed = true;
            if (Active) return Target;   // a running glide or jump turns into this one from where it is
            Active = true;
            lastFrame = TimeSpan.MinValue;
            CompositionTarget.Rendering += OnFrame;
            return Target;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            if (!Active) { CompositionTarget.Rendering -= OnFrame; return; }
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == lastFrame) return; // WPF can raise the event twice for one frame
            double elapsed = lastFrame == TimeSpan.MinValue ? 16.7 : Math.Min(50, (now - lastFrame).TotalMilliseconds);
            lastFrame = now;
            if (Timed)
            {
                // The first drawn frame is the start (so a slow first frame never skips the beginning); the end follows the page if its size changed.
                if (jumpStart == TimeSpan.MinValue) jumpStart = now - TimeSpan.FromMilliseconds(16.7);
                double t = Math.Min(1, (now - jumpStart).TotalMilliseconds / jumpMilliseconds);
                double end = Math.Clamp(Target, 0, viewer.ScrollableHeight);
                position = jumpFrom + (end - jumpFrom) * JumpEase(t);
                if (t >= 1) { position = end; Target = end; Finish(); }
            }
            else
            {
                position += (Target - position) * (1 - Math.Exp(-elapsed / SettleMilliseconds));
                if (Math.Abs(Target - position) < 0.4) { position = Target; Finish(); }
            }
            lastSet = position;
            viewer.ScrollToVerticalOffset(position);
        }

        private void Finish() { Active = false; Timed = false; CompositionTarget.Rendering -= OnFrame; }

        internal void Stop()
        {
            if (!Active) return;
            Finish();
        }
    }
}
