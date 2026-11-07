using System.Windows;
using System.Windows.Controls;
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
    public AccordionBody() { ClipToBounds = true; }
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
        return arrangeSize;
    }
}
