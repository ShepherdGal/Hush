using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hush;

// Reveal content by measured height, never by scaling its text or controls.
public sealed class AccordionBody : Decorator
{
    public static readonly DependencyProperty ExpandedProperty = DependencyProperty.Register(nameof(Expanded), typeof(bool), typeof(AccordionBody),
        new PropertyMetadata(false, ExpandedChanged));
    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(double), typeof(AccordionBody),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool Expanded { get => (bool)GetValue(ExpandedProperty); set => SetValue(ExpandedProperty, value); }
    private double Reveal => (double)GetValue(RevealProperty);
    // The body is cut to its revealed height, but NOT to its width: the choice chips and switches in the cards pull their rounded backgrounds a few DIP
    // into the card's padding (so their text lines up with the rest), and a plain ClipToBounds cut the selected chip's left edge off.
    private const double SideBleed = 24;
    private Size clippedFor;
    public AccordionBody() { }
    private static void ExpandedChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        var body = (AccordionBody)value;
        body.BeginAnimation(RevealProperty, Motion.Tween(body.Reveal, (bool)args.NewValue ? 1 : 0, 320));
        body.BeginAnimation(OpacityProperty, Motion.Tween(body.Opacity, (bool)args.NewValue ? 1 : 0, 280));
    }
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child == null || (!Expanded && Reveal <= 0)) return new Size();
        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * Math.Clamp(Reveal, 0, 1));
    }
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Child.DesiredSize.Height));
        if (arrangeSize != clippedFor)
        {
            clippedFor = arrangeSize;
            var clip = new RectangleGeometry(new Rect(-SideBleed, 0, arrangeSize.Width + 2 * SideBleed, arrangeSize.Height));
            clip.Freeze();
            Clip = clip;
        }
        return arrangeSize;
    }
}
