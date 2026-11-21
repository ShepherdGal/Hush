using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hush;

// Smooth scrolling (0.9): the wheel glides instead of jumping, further notches retarget, an inner list hands over at its end, anything else that moves the
// offset (scrollbar, ScrollToTop) cancels the glide, and item-based lists keep WPF's own behavior.
internal static class SmoothScrollChecks
{
    private static bool Wheel(UIElement target, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = target };
        target.RaiseEvent(args);
        return args.Handled;
    }

    internal static void Run(Action<bool, string> check)
    {
        SmoothScroll.Install();
        var outerContent = new TextBlock { Height = 900, Text = "outer" };
        var innerContent = new TextBlock { Height = 400, Text = "inner" };
        var inner = new ScrollViewer { Height = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = innerContent };
        var outer = new ScrollViewer { Height = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { new TextBlock { Height = 40, Text = "top" }, inner, outerContent } } };
        var list = new ListBox { Height = 120 };
        for (int i = 0; i < 100; i++) list.Items.Add("row " + i);
        var window = new Window { Width = 400, Height = 700, ShowActivated = false, ShowInTaskbar = false, Content = new StackPanel { Children = { outer, list } } };
        try
        {
            window.Show(); UiLayoutChecks.Layout((FrameworkElement)window.Content, new Size(400, 700));
            int lines = SystemParameters.WheelScrollLines;
            double notch = lines < 0 ? outer.ViewportHeight * 0.9 : lines * 16;

            // One notch down over the outer page: handled, not a jump, then it settles exactly one notch down.
            bool handled = Wheel(outerContent, -120);
            check(handled && outer.VerticalOffset < notch * 0.6 && SmoothScroll.IsGliding(outer), "A wheel notch starts a glide instead of jumping");
            UiLayoutChecks.PumpUntil(() => !SmoothScroll.IsGliding(outer));
            check(Math.Abs(outer.VerticalOffset - notch) < 1.0, $"The glide settles exactly one notch down ({outer.VerticalOffset:0.#} of {notch:0.#})");

            // Quick further notches retarget the same glide instead of restarting from the beginning.
            double before = outer.VerticalOffset;
            Wheel(outerContent, -120); UiLayoutChecks.Wait(60); Wheel(outerContent, -120);
            UiLayoutChecks.PumpUntil(() => !SmoothScroll.IsGliding(outer));
            check(Math.Abs(outer.VerticalOffset - (before + 2 * notch)) < 1.0, "Two quick notches add up to two notches");

            // Up again, and the ends are respected.
            Wheel(outerContent, 120 * 100); UiLayoutChecks.PumpUntil(() => !SmoothScroll.IsGliding(outer));
            check(outer.VerticalOffset == 0 && !Wheel(outerContent, 120), "Scrolling past the top stops at the top, and a wheel that cannot move anything is left to Windows");

            // An inner list scrolls first; when it is at its end the page takes the wheel.
            outer.ScrollToVerticalOffset(0); inner.ScrollToTop();
            UiLayoutChecks.Layout((FrameworkElement)window.Content, new Size(400, 700));
            Wheel(innerContent, -120); UiLayoutChecks.PumpUntil(() => !SmoothScroll.IsGliding(inner));
            check(Math.Abs(inner.VerticalOffset - Math.Min(notch, inner.ScrollableHeight)) < 1.0 && outer.VerticalOffset == 0, "The innermost scroll area under the pointer takes the wheel first");
            inner.ScrollToBottom(); UiLayoutChecks.Wait(50);
            Wheel(innerContent, -120); UiLayoutChecks.PumpUntil(() => !SmoothScroll.IsGliding(outer));
            check(outer.VerticalOffset > 0 && inner.VerticalOffset == inner.ScrollableHeight, "At the end of the inner area the page scrolls instead");

            // Anything else that moves the offset ends the glide at once.
            outer.ScrollToTop(); Wheel(outerContent, -120 * 5); UiLayoutChecks.Wait(80);
            outer.ScrollToTop(); UiLayoutChecks.Wait(400);
            check(outer.VerticalOffset == 0 && !SmoothScroll.IsGliding(outer), "Code (or the scrollbar) taking over cancels the glide: it does not pull the page back");

            // An item-based list keeps WPF's own wheel behavior (its offsets are rows, not DIP).
            check(!Wheel(list, -120), "A list that scrolls by item is left alone");
            // Nothing is scrolled while a glide has nowhere to go.
            check(!SmoothScroll.IsGliding(list.Template.FindName("PART_ScrollViewer", list) as ScrollViewer ?? outer), "No glide runs when nothing moves");
        }
        finally { window.Close(); }
    }
}
