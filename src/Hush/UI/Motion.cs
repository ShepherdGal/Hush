using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Hush;

public static class Motion
{
    // Frost + Prism motion family (owner-approved November 19, 2026; the design rules "New look"; applied everywhere in the clean-up step, November 20, 2026).
    // The XAML templates use the same values (0:0:0.15 hover, 0:0:0.16 switch and check, 0:0:0.28 chevrons); ThemeMotionChecks keeps them in step.
    internal const double PillMoveMilliseconds = 280;      // a selected pill, thumb or chevron moving to its new place (ease-in-out)
    internal const double HoverMilliseconds = 150;         // hover color and glow
    internal const double SwitchMilliseconds = 160;        // switch knob, staged-row feedback
    internal const double PageOutMilliseconds = 120, PageInMilliseconds = 200;
    internal const double PopupInMilliseconds = 200, PopupOutMilliseconds = 150;
    internal const double TooltipMilliseconds = 120, TooltipDelayMilliseconds = 500;
    internal const double PressMilliseconds = 100, PressScale = 0.97;
    // Every selection that moves (the tab pill, the mode and theme thumbs, each bar's SegmentPanel) springs (owner, November 20, 2026: "lively"): about 10 %
    // past the new place, one small bounce back, settled in 600 ms. Replaces the 280 ms ease-in-out for things that move.
    internal const double SpringMilliseconds = 600;
    internal static readonly SpringEase Spring = Freeze(new SpringEase());
    private static SpringEase Freeze(SpringEase ease) { ease.Freeze(); return ease; }
    // A jump to a place on a long page and its index highlight (owner, November 20, 2026: the Settings glide remade): one fixed length for any distance,
    // slow-fast-slow, no overshoot, so page and highlight move as one gesture.
    internal const double JumpMilliseconds = 350;
    internal static readonly JumpEase Jump = FreezeJump(new JumpEase());
    private static JumpEase FreezeJump(JumpEase ease) { ease.Freeze(); return ease; }

    // Windows' "Show animations" (Settings > Accessibility > Visual effects): off means every transition snaps to its end. This is the person's own choice,
    // not a frame-rate cap; results and progress still show. Tests can hold it on or off.
    internal static bool? AnimationsForTests;
    internal static bool Animates => AnimationsForTests ?? SystemParameters.ClientAreaAnimation;
    internal static TimeSpan Span(double milliseconds) => TimeSpan.FromMilliseconds(Animates ? milliseconds : 0);

    // Tooltips (once per process): wait 500 ms, and once one has shown, the next ones show at once while the pointer moves on (2 s window). The fade is in App.xaml.
    private static bool tooltipTimingSet;
    internal static void ApplyTooltipTiming()
    {
        if (tooltipTimingSet) return;
        tooltipTimingSet = true;
        try
        {
            ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata((int)TooltipDelayMilliseconds));
            ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(2000));
        }
        catch (ArgumentException) { }   // already set for this type: keep what is there
    }

    // Moves something that is already on screen (a pill, a thumb) to a new place: speeds up, then slows down. Transform only; instant when asked.
    internal static void Glide(TranslateTransform shift, double x, bool animate)
    {
        if (!animate || !Animates)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = x;
            return;
        }
        // The spring (owner, November 20, 2026), from where the pill is drawn right now, so a second pick mid-way never jumps.
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(shift.X, x, Span(SpringMilliseconds)) { EasingFunction = Spring });
    }
    public static readonly DependencyProperty HoverForegroundProperty = DependencyProperty.RegisterAttached(
        "HoverForeground", typeof(Brush), typeof(Motion), new PropertyMetadata(null, AppearanceChanged));
    public static Brush? GetHoverForeground(DependencyObject value) => (Brush?)value.GetValue(HoverForegroundProperty);
    public static void SetHoverForeground(DependencyObject value, Brush? brush) => value.SetValue(HoverForegroundProperty, brush);
    // One retargetable color transition for category outlines, labels and checkbox text.
    public static readonly DependencyProperty InkProperty = DependencyProperty.RegisterAttached(
        "Ink", typeof(Brush), typeof(Motion), new PropertyMetadata(null, InkChanged));
    private static readonly DependencyProperty InkBrushProperty = DependencyProperty.RegisterAttached(
        "InkBrush", typeof(SolidColorBrush), typeof(Motion));
    private static readonly DependencyProperty ColorClockProperty = DependencyProperty.RegisterAttached(
        "ColorClock", typeof(ColorAnimation), typeof(Motion));
    private static void AnimateColor(SolidColorBrush brush, Color target, double milliseconds)
    {
        Color displayed = brush.Color;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.Color = displayed;
        var animation = new ColorAnimation(displayed, target, Span(milliseconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        brush.SetValue(ColorClockProperty, animation);
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(brush.GetValue(ColorClockProperty), animation)) return;
            brush.Color = target;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.ClearValue(ColorClockProperty);
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }
    public static Brush GetInk(DependencyObject value) => (Brush)value.GetValue(InkProperty);
    public static void SetInk(DependencyObject value, Brush brush) => value.SetValue(InkProperty, brush);
    private static void InkChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not SolidColorBrush target) return;
        if (ReferenceEquals(args.OldValue, args.NewValue)) return; // Live palette subframes already have a destination tween.
        DependencyProperty? property = value switch
        {
            TextBlock => TextBlock.ForegroundProperty,
            Border => Border.BorderBrushProperty,
            Control => Control.ForegroundProperty,
            _ => null
        };
        if (property == null) return;
        // Template triggers may clear Ink briefly while returning to their base value.
        // Retain the displayed brush so deselection still retargets smoothly.
        var previous = value.GetValue(InkBrushProperty) as SolidColorBrush;
        Color destination = ThemePalette.Destination(target);
        var brush = new SolidColorBrush(previous?.Color ?? destination);
        value.SetValue(InkBrushProperty, brush);
        value.SetValue(property, brush);
        if (previous != null)
            AnimateColor(brush, destination, Math.Max(HoverMilliseconds, ThemePalette.Remaining(target)));
    }
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Motion), new PropertyMetadata(false, EnabledChanged));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.RegisterAttached(
        "Accent", typeof(Color), typeof(Motion), new PropertyMetadata(Color.FromRgb(56, 207, 200), AppearanceChanged));
    public static readonly DependencyProperty GlowProperty = DependencyProperty.RegisterAttached(
        "Glow", typeof(double), typeof(Motion), new PropertyMetadata(0.08, AppearanceChanged));
    public static readonly DependencyProperty OuterGlowProperty = DependencyProperty.RegisterAttached(
        "OuterGlow", typeof(bool), typeof(Motion), new PropertyMetadata(false, AppearanceChanged));
    // Only the mouse highlights this button (keyboard focus alone does not): the header wordmark must look the same whether the window is active or
    // not, and Windows puts keyboard focus on it as soon as the window is activated.
    public static readonly DependencyProperty MouseOnlyProperty = DependencyProperty.RegisterAttached(
        "MouseOnly", typeof(bool), typeof(Motion), new PropertyMetadata(false, AppearanceChanged));
    // How much stronger the glow gets on hover (the main Scan button asks for a clear but gentle lift; the default is the faint one).
    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(double), typeof(Motion), new PropertyMetadata(-1.0, AppearanceChanged));
    public static bool GetMouseOnly(DependencyObject value) => (bool)value.GetValue(MouseOnlyProperty);
    public static void SetMouseOnly(DependencyObject value, bool mouseOnly) => value.SetValue(MouseOnlyProperty, mouseOnly);
    public static double GetHoverLift(DependencyObject value) => (double)value.GetValue(HoverLiftProperty);
    public static void SetHoverLift(DependencyObject value, double lift) => value.SetValue(HoverLiftProperty, lift);
    // A click presses the button to 97 % for 100 ms (press only, never hover). Off for the gauge, which never moves (the design rules).
    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Motion), new PropertyMetadata(true));
    public static bool GetPress(DependencyObject value) => (bool)value.GetValue(PressProperty);
    public static void SetPress(DependencyObject value, bool press) => value.SetValue(PressProperty, press);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(ButtonMotion), typeof(Motion));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
    public static Color GetAccent(DependencyObject value) => (Color)value.GetValue(AccentProperty);
    public static void SetAccent(DependencyObject value, Color color) => value.SetValue(AccentProperty, color);
    public static double GetGlow(DependencyObject value) => (double)value.GetValue(GlowProperty);
    public static void SetGlow(DependencyObject value, double strength) => value.SetValue(GlowProperty, strength);
    public static bool GetOuterGlow(DependencyObject value) => (bool)value.GetValue(OuterGlowProperty);
    public static void SetOuterGlow(DependencyObject value, bool enabled) => value.SetValue(OuterGlowProperty, enabled);

    private static void EnabledChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not Button button) return;
        if ((bool)args.NewValue)
        {
            button.Loaded += Loaded;
            button.Unloaded += Unloaded;
            if (button.IsLoaded) Ensure(button);
        }
        else
        {
            if (button.GetValue(StateProperty) is ButtonMotion state) state.Detach();
            button.ClearValue(StateProperty);
            button.Loaded -= Loaded;
            button.Unloaded -= Unloaded;
        }
    }
    private static void Loaded(object sender, RoutedEventArgs e) => Ensure((Button)sender);
    private static void Unloaded(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.GetValue(StateProperty) is ButtonMotion state) state.Detach();
        button.ClearValue(StateProperty);
    }
    internal static void Ensure(Button button)
    {
        if (button.GetValue(StateProperty) is ButtonMotion) return;
        button.ApplyTemplate();
        button.SetValue(StateProperty, new ButtonMotion(button));
    }
    // Deterministic render previews share the input handlers' state path without desktop input.
    internal static void PreviewInteraction(Button button, bool hover, bool pressed)
    {
        Ensure(button);
        ((ButtonMotion)button.GetValue(StateProperty)).SetInteraction(hover, pressed);
    }
    private static void AppearanceChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value.GetValue(StateProperty) is ButtonMotion state) state.Update();
    }

    internal static DoubleAnimation Tween(double from, double to, double milliseconds = HoverMilliseconds) =>
        new(from, to, Span(milliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

    // Pages and popups fade in and out by opacity only. (0.9: a cached-picture fade was tried to save CPU; it drew the page at a lower resolution while it
    // faded and snapped it sharp at the end, so every page change flashed blurry text, and it measured no gain. The real CPU causes were elsewhere.)
    // Opacity only: a RenderTransform slide of 6 DIP put the text on fractional pixels for the whole fade, which looks blurry until it lands.
    // New content inside a page (a tool, a Tweaks tab, a Clean group) comes in like a page (200 ms).
    internal static void Reveal(FrameworkElement element) => element.BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, PageInMilliseconds));

    // A popup and its scrim: in 200 ms, out 150 ms (paired).
    internal static void OpenPopup(FrameworkElement overlay) => overlay.BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, PopupInMilliseconds));

    internal static Task FadeOut(FrameworkElement element)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = Tween(element.Opacity, 0, PopupOutMilliseconds);
        animation.Completed += (_, _) => completion.TrySetResult();
        element.BeginAnimation(UIElement.OpacityProperty, animation);
        return completion.Task;
    }

    private static readonly DependencyProperty TransitionVersionProperty = DependencyProperty.RegisterAttached(
        "TransitionVersion", typeof(int), typeof(Motion), new PropertyMetadata(0));
    private static readonly DependencyProperty EnteringProperty = DependencyProperty.RegisterAttached(
        "Entering", typeof(bool), typeof(Motion), new PropertyMetadata(false));
    internal static bool IsEntering(FrameworkElement surface) => (bool)surface.GetValue(EnteringProperty);

    // A page change: the page on screen fades out (120 ms), the change is made, the new page fades in (200 ms). Instant (no fades, the change made at once) when
    // asked (a tab change by keyboard) or when Windows' animations are off. Every caller's change sets the visibility of every page, so when a second change
    // arrives during a fade-out only the newest one is made.
    internal static bool InstantPageChange;
    // Test seam: most checks were written for a change that is made at once; the test runner skips only the fade-out (the new page still fades in), and
    // MotionTimingChecks turns it back on to check the whole sequence.
    internal static bool SkipPageFadeOutForTests;
    internal static void EnterPage(FrameworkElement surface, Action change, bool instant = false)
    {
        int version = (int)surface.GetValue(TransitionVersionProperty) + 1;
        surface.SetValue(TransitionVersionProperty, version);
        bool Current() => (int)surface.GetValue(TransitionVersionProperty) == version;
        if (instant || InstantPageChange || !Animates || !surface.IsVisible)
        {
            surface.BeginAnimation(UIElement.OpacityProperty, null);
            surface.Opacity = 1;
            surface.SetValue(EnteringProperty, false);
            change();
            return;
        }
        surface.SetValue(EnteringProperty, true);
        void ChangeAndFadeIn()
        {
            change(); // the outgoing page is removed before the new one fades in; its pixels are never kept
            var fadeIn = Tween(0, 1, PageInMilliseconds);
            fadeIn.Completed += (_, _) =>
            {
                if (!Current()) return;
                surface.BeginAnimation(UIElement.OpacityProperty, null);
                surface.Opacity = 1;
                surface.SetValue(EnteringProperty, false);
            };
            surface.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }
        if (SkipPageFadeOutForTests) { ChangeAndFadeIn(); return; }
        var fadeOut = Tween(surface.Opacity, 0, PageOutMilliseconds);
        fadeOut.Completed += (_, _) => { if (Current()) ChangeAndFadeIn(); };
        surface.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    // One drop-shadow light and the element it lights. The effect is attached while its opacity is above zero (or still fading out) and removed
    // as soon as it has faded to nothing, so a resting button without a glow carries no effect at all.
    private sealed class Light(DropShadowEffect effect)
    {
        internal UIElement? Host { get; set; }
        private int version;
        internal void Show(Color color, double opacity, double? blur)
        {
            if (Host == null) return;
            int mine = ++version;
            bool visible = opacity > 0.0005;
            if (!visible && !ReferenceEquals(Host.Effect, effect)) return; // already off: nothing to animate
            if (visible && !ReferenceEquals(Host.Effect, effect)) Host.Effect = effect;
            effect.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(effect.Color, color, Span(HoverMilliseconds)));
            if (blur is double radius) effect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, Tween(effect.BlurRadius, radius));
            var fade = Tween(effect.Opacity, opacity);
            if (!visible) fade.Completed += (_, _) => { if (mine == version && ReferenceEquals(Host.Effect, effect)) Host.Effect = null; };
            effect.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
        }
    }

    private sealed class ButtonMotion
    {
        private readonly Button button;
        // Effects stay on separate outline layers; the surface and label remain sharp. A light is attached to its layer only while it is visible:
        // WPF re-renders every attached effect that a redraw touches (even at opacity 0), and 104 idle ones made each redraw of Settings cost 5x the CPU.
        private readonly Light glow = new(new DropShadowEffect { ShadowDepth = 2, Direction = 270, BlurRadius = 10, Opacity = 0 });
        private readonly Light edge = new(new DropShadowEffect { ShadowDepth = 0, BlurRadius = 4, Opacity = 0 });
        private readonly Light halo = new(new DropShadowEffect { ShadowDepth = 0, BlurRadius = 12, Opacity = 0 });
        private readonly Grid? outerGlow;
        private readonly ContentPresenter? content;
        private readonly DependencyPropertyDescriptor background, foreground;
        private SolidColorBrush? fill;
        private SolidColorBrush? ink;
        private Color? fillTarget, inkTarget;
        private FrameworkElement? inkContent;
        private bool pressed, hovered, drawnPressed, scrolls;
        // A button inside a scrolling area keeps no glow at rest (owner, November 20, 2026: the Settings glide stuttered on a weak PC): a resting glow is a
        // blur effect that moves with every scrolled frame, and software rendering re-blurs it each time. It still lights up on hover, like every button.
        private static bool InScrolledArea(DependencyObject element)
        {
            for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer) return true;
            return false;
        }
        private ScaleTransform? pressScale;
        internal ButtonMotion(Button button)
        {
            this.button = button;
            glow.Host = button.Template.FindName("GlowUnderlay", button) as UIElement ?? button;
            edge.Host = button.Template.FindName("GlowEdge", button) as UIElement;
            halo.Host = button.Template.FindName("GlowHalo", button) as UIElement;
            outerGlow = button.Template.FindName("OuterGlow", button) as Grid;
            content = button.Template.FindName("ButtonContent", button) as ContentPresenter;
            hovered = button.IsMouseOver;
            scrolls = InScrolledArea(button);
            // A page that was not laid out yet when this button loaded may not have its scroll area in place; look again when the button shows (cheap: only
            // a change of the answer updates the look).
            button.IsVisibleChanged += VisibleChanged;
            button.SizeChanged += GlowSizeChanged;
            ClipOuterGlow();
            background = DependencyPropertyDescriptor.FromProperty(Control.BackgroundProperty, typeof(Button));
            background.AddValueChanged(button, BackgroundChanged);
            foreground = DependencyPropertyDescriptor.FromProperty(Control.ForegroundProperty, typeof(Button));
            foreground.AddValueChanged(button, ForegroundChanged);
            button.MouseEnter += Enter;
            button.MouseLeave += Leave;
            button.IsKeyboardFocusWithinChanged += FocusChanged;
            button.PreviewMouseLeftButtonDown += Down;
            button.PreviewMouseLeftButtonUp += Up;
            button.LostMouseCapture += Up;
            button.IsEnabledChanged += Disabled;
            Update();
        }
        private void VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!button.IsVisible) return;
            bool now = InScrolledArea(button);
            if (now != scrolls) { scrolls = now; Update(); }
        }
        private void GlowSizeChanged(object sender, SizeChangedEventArgs e) => ClipOuterGlow();
        private void ClipOuterGlow()
        {
            if (outerGlow == null) return;
            // Clip after the child effects: only the light outside the original outline survives.
            // The filled silhouettes produce a smooth falloff without painting the button interior.
            outerGlow.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(-30, -30, button.ActualWidth + 60, button.ActualHeight + 60)),
                new RectangleGeometry(new Rect(0, 0, button.ActualWidth, button.ActualHeight), 13, 13));
        }
        private void BackgroundChanged(object? sender, EventArgs e)
        {
            UpdateSurface(paletteChange: true);
        }
        private bool Highlighted => button.IsEnabled && (hovered || (!GetMouseOnly(button) && button.IsKeyboardFocusWithin));
        // The strong Clean hover follows the mouse only: Hush focuses the Clean button after a scan, and focus alone must not look like a hover.
        private bool MouseHighlighted => button.IsEnabled && hovered;
        private void ForegroundChanged(object? sender, EventArgs e) { UpdateInk(paletteChange: true); UpdateSurface(paletteChange: true); }
        private void UpdateSurface(bool paletteChange = false)
        {
            if (button.Template.FindName("Surface", button) is not Border surface || button.Background is not SolidColorBrush source) return;
            if (button.Template.FindName("GlowUnderlay", button) is Border underlay)
                underlay.Opacity = source.Color.A == 0 || GetGlow(button) <= 0 ? 0 : 0.12;
            Color target = ThemePalette.Destination(source);
            // Outline-glow buttons (Clean, Clean selected) have a nearly transparent fill, so on hover the fill itself fills in with their own
            // color: a strong, obvious response (still the one Hush red, only more opaque) that deepens when pressed.
            if (GetOuterGlow(button) && source.Color.A < 255)
            {
                if (MouseHighlighted) target = Color.FromArgb((byte)(pressed ? 0x74 : 0x52), target.R, target.G, target.B);
            }
            // Refine the existing surface itself; transparent navigation never gains a wash.
            else if (Highlighted && source.Color.A == 255 && button.Foreground is SolidColorBrush accent)
            {
                double amount = pressed ? 0.02 : 0.035;
                byte Blend(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
                Color accentTarget = ThemePalette.Destination(accent);
                target = Color.FromArgb(source.Color.A, Blend(target.R, accentTarget.R), Blend(target.G, accentTarget.G), Blend(target.B, accentTarget.B));
            }
            if (fill == null) { fill = new SolidColorBrush(source.Color); surface.Background = fill; }
            if (fillTarget != target)
            {
                fillTarget = target;
                double remaining = Math.Max(ThemePalette.Remaining(source), button.Foreground is SolidColorBrush foreground ? ThemePalette.Remaining(foreground) : 0);
                AnimateColor(fill, target, paletteChange && remaining > 0 ? remaining : HoverMilliseconds);
            }
        }
        private void UpdateInk(bool paletteChange = false)
        {
            if (content == null || button.Foreground is not SolidColorBrush source) return;
            var targetBrush = Highlighted && GetHoverForeground(button) is SolidColorBrush highlight ? highlight : source;
            Color target = ThemePalette.Destination(targetBrush);
            if (ink == null)
            {
                ink = new SolidColorBrush(targetBrush.Color);
                content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, ink);
            }
            if (inkTarget != target)
            {
                inkTarget = target;
                double remaining = ThemePalette.Remaining(targetBrush);
                AnimateColor(ink, target, paletteChange && remaining > 0 ? remaining : HoverMilliseconds);
            }
            // UIElement content inherits through the Button's logical tree, bypassing its presenter.
            // Give unstyled icon/label groups the same brush, preserving explicitly styled content.
            if (!ReferenceEquals(inkContent, button.Content))
            {
                ClearContentInk();
                if (button.Content is FrameworkElement element
                    && element.ReadLocalValue(System.Windows.Documents.TextElement.ForegroundProperty) == DependencyProperty.UnsetValue)
                {
                    inkContent = element;
                    element.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, ink);
                }
            }
        }
        private void ClearContentInk()
        {
            if (inkContent != null && ReferenceEquals(inkContent.GetValue(System.Windows.Documents.TextElement.ForegroundProperty), ink))
                inkContent.ClearValue(System.Windows.Documents.TextElement.ForegroundProperty);
            inkContent = null;
        }
        internal void SetInteraction(bool hover, bool press) { hovered = hover; pressed = press; Update(); }
        private void Enter(object sender, System.Windows.Input.MouseEventArgs e) => SetInteraction(true, false);
        private void Leave(object sender, System.Windows.Input.MouseEventArgs e) => SetInteraction(false, false);
        private void FocusChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();
        private void Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => SetInteraction(hovered, true);
        private void Up(object sender, RoutedEventArgs e) => SetInteraction(hovered, false);
        // Only the button's own enabled state changes its look. When a whole page is disabled from above (a popup blocks the app behind its scrim), WPF coerces
        // every button's IsEnabled; animating all of them (fade to 45 %, glow and colour) at once made opening and closing a popup stutter, and nobody sees it
        // behind the scrim. The look is brought up to date the next time anything about the button changes.
        private bool heldByParent;
        private void Disabled(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!button.IsEnabled && button.ReadLocalValue(UIElement.IsEnabledProperty) is not false && VisualTreeHelper.GetParent(button) is UIElement parent && !parent.IsEnabled)
            { heldByParent = true; return; }   // disabled from above: keep the look
            if (button.IsEnabled && heldByParent) { heldByParent = false; return; }   // enabled again from above: the look never changed
            heldByParent = false;
            Update();
        }
        internal void Update()
        {
            bool hover = Highlighted;
            // Outline-glow buttons (Clean, Clean selected) get a clearly visible hover lift in their own color; quiet buttons keep the faint one.
            double baseLift = GetHoverLift(button) >= 0 ? GetHoverLift(button) : 0.035;
            double lift = GetOuterGlow(button) ? (pressed ? 0.22 : MouseHighlighted ? 0.42 : 0.04) : (pressed ? baseLift * 0.55 : baseLift);
            double strength = button.IsEnabled && GetGlow(button) > 0 ? (hover ? GetGlow(button) + lift : scrolls ? 0 : GetGlow(button)) : 0;
            UpdateSurface();
            UpdateInk();
            glow.Show(GetAccent(button), GetOuterGlow(button) ? 0 : strength, hover ? 12 : 10);
            edge.Show(GetAccent(button), GetOuterGlow(button) ? strength * 1.7 : 0, null);
            halo.Show(GetAccent(button), GetOuterGlow(button) ? strength : 0, MouseHighlighted && GetOuterGlow(button) ? 26 : 12);
            button.BeginAnimation(UIElement.OpacityProperty, Tween(button.Opacity, button.IsEnabled ? 1 : 0.45));
            UpdatePress();
        }
        // The press: one ScaleTransform made on the first press (a button that already moves itself by a transform keeps it and is not pressed).
        private void UpdatePress()
        {
            bool down = pressed && button.IsEnabled && GetPress(button);
            if (down == drawnPressed) return;
            if (pressScale == null)
            {
                if (!down || !ReferenceEquals(button.RenderTransform, Transform.Identity)) return;
                pressScale = new ScaleTransform(1, 1);
                button.RenderTransformOrigin = new Point(0.5, 0.5);
                button.RenderTransform = pressScale;
            }
            drawnPressed = down;
            double to = down ? PressScale : 1;
            pressScale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(pressScale.ScaleX, to, PressMilliseconds));
            pressScale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(pressScale.ScaleY, to, PressMilliseconds));
        }
        internal void Detach()
        {
            ClearContentInk();
            background.RemoveValueChanged(button, BackgroundChanged);
            foreground.RemoveValueChanged(button, ForegroundChanged);
            button.SizeChanged -= GlowSizeChanged;
            button.IsVisibleChanged -= VisibleChanged;
            button.MouseEnter -= Enter;
            button.MouseLeave -= Leave;
            button.IsKeyboardFocusWithinChanged -= FocusChanged;
            button.PreviewMouseLeftButtonDown -= Down;
            button.PreviewMouseLeftButtonUp -= Up;
            button.LostMouseCapture -= Up;
            button.IsEnabledChanged -= Disabled;
        }
    }
}
