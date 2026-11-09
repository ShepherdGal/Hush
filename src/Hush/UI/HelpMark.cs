using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Hush;

// The small "?" after a setting's name. Hovering it shows the explanation in a popup in the app's own look; clicking (or Enter/Space) pins the popup
// open until the next click anywhere else; Escape closes it. The popup never steals the mouse while it is only a hover, so the "?" keeps its hover state.
public sealed class HelpMark : Button
{
    public static readonly DependencyProperty HelpTextProperty = DependencyProperty.Register(nameof(HelpText), typeof(string), typeof(HelpMark), new PropertyMetadata(""));
    public string HelpText { get => (string)GetValue(HelpTextProperty); set => SetValue(HelpTextProperty, value); }

    // The popup, its bubble, its shadow and its text are built the first time the explanation is shown (0.9 audit): a Settings page has well over a hundred of
    // these marks, and building a popup with a shadow effect for each of them when the page opens was a large part of its first-open cost.
    private Popup? popup;
    private TextBlock? text;
    private bool pinned;
    private long closedAt;

    public HelpMark()
    {
        MouseEnter += (_, _) => { if (popup?.IsOpen != true) Open(pin: false); };
        MouseLeave += (_, _) => { if (!pinned) Close(); };
        LostKeyboardFocus += (_, _) => { if (pinned) Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Unloaded += (_, _) => Close();
    }

    private Popup EnsurePopup()
    {
        if (popup != null) return popup;
        text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 21, FontWeight = FontWeights.Normal };
        var created = new Popup { AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, Placement = PlacementMode.Bottom, HorizontalOffset = -14, VerticalOffset = 4, PlacementTarget = this };
        var bubble = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(15, 12, 15, 12), MaxWidth = 400, Margin = new Thickness(0, 0, 14, 14), BorderThickness = new Thickness(1), Child = text,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.35, Color = Colors.Black } };
        bubble.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        bubble.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        text.SetResourceReference(TextBlock.FontSizeProperty, "SupportFontSize");
        created.Child = bubble;
        created.Closed += (_, _) => { pinned = false; closedAt = Environment.TickCount64; };
        return popup = created;
    }

    internal bool IsPopupOpen => popup?.IsOpen == true;
    internal bool IsPinned => pinned;
    internal FrameworkElement? Bubble => popup?.Child as FrameworkElement;

    private void Open(bool pin)
    {
        if (string.IsNullOrWhiteSpace(HelpText)) return;
        var shown = EnsurePopup();
        text!.Text = HelpText;
        text.FontFamily = FontFamily;
        pinned = pin;
        shown.StaysOpen = !pin; // a pinned popup closes on any click outside it; a hover popup must not capture the mouse
        shown.IsOpen = true;
    }

    private void Close()
    {
        pinned = false;
        if (popup != null) popup.IsOpen = false;
    }

    // A click pins the popup (or unpins it). When the click that arrives is the one that just closed a pinned popup, it must not reopen it.
    protected override void OnClick()
    {
        base.OnClick();
        TogglePinned();
    }

    internal void TogglePinned()
    {
        bool isOpen = popup?.IsOpen == true;
        if (pinned || (!isOpen && Environment.TickCount64 - closedAt < 350)) { Close(); return; }
        if (isOpen) { pinned = true; popup!.StaysOpen = false; return; }
        Open(pin: true);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ButtonAutomationPeer(this);
}
