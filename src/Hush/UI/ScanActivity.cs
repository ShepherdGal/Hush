using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Hush;

// The "files are being cleaned" cue inside the gauge (redesigned for smoothness, 0.9, user direction: "the app should feel 60 fps native always").
// A radar sweep: a soft wedge of the accent colour with a bright leading line turns once around the disc, over a few faint static rings. The whole sweep is ONE
// element turned by ONE linear animation on WPF's render thread, at the screen's own refresh rate (no frame-rate cap, ever): no timer, no repainting from code,
// no opacity mask, no clip on moving content, no blur or shadow effect, nothing that has to be re-rasterized per frame. Everything that is not the sweep is
// static. The animation runs only while the cue is running and visible. The percentage and its label are drawn on top, unchanged.
public sealed class ScanActivity : Grid
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(ScanActivity), new PropertyMetadata(false, (sender, _) => ((ScanActivity)sender).Update()));
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    // The gauge's inner disc is 250 DIP across (the track ring sits outside it).
    private const double Size = 250, Centre = Size / 2, Reach = Centre - 7, WedgeDegrees = 84;
    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(2.6);
    private readonly RotateTransform sweepTurn = new();
    private readonly Grid sweep;
    private readonly Path wedge, leadingLine;
    private readonly Ellipse tip;
    private bool playing;

    public ScanActivity()
    {
        IsHitTestVisible = false;
        Width = Size; Height = Size;

        // Faint static rings: they give the sweep something to pass over.
        foreach (double fraction in new[] { 0.34, 0.68 })
        {
            var ring = new Ellipse { Width = Size * fraction, Height = Size * fraction, StrokeThickness = 1, Opacity = 0.13 };
            ring.SetResourceReference(Shape.StrokeProperty, "TealTextBrush");
            Children.Add(ring);
        }
        var dashed = new Ellipse { Width = Size - 14, Height = Size - 14, StrokeThickness = 1.3, StrokeDashArray = [0.9, 4.2], StrokeDashCap = PenLineCap.Round, Opacity = 0.34 };
        dashed.SetResourceReference(Shape.StrokeProperty, "TealTextBrush");
        Children.Add(dashed);

        // The sweep. Its geometry is built once; the brushes are (re)built from the current accent colour whenever it starts, so a theme change between two cleans shows.
        double trailing = -WedgeDegrees * Math.PI / 180;
        var wedgeGeometry = new StreamGeometry();
        using (var context = wedgeGeometry.Open())
        {
            context.BeginFigure(new Point(Centre, Centre), true, true);
            context.LineTo(new Point(Centre + Reach * Math.Sin(trailing), Centre - Reach * Math.Cos(trailing)), false, false);
            context.ArcTo(new Point(Centre, Centre - Reach), new Size(Reach, Reach), 0, false, SweepDirection.Clockwise, false, false);
        }
        wedgeGeometry.Freeze();
        wedge = new Path { Data = wedgeGeometry, Width = Size, Height = Size };
        leadingLine = new Path { Data = new LineGeometry(new Point(Centre, Centre), new Point(Centre, Centre - Reach)), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = Size, Height = Size };
        tip = new Ellipse { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Centre - Reach - 3, 0, 0) };
        sweep = new Grid { Width = Size, Height = Size, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = sweepTurn, Opacity = 0 };
        sweep.Children.Add(wedge); sweep.Children.Add(leadingLine); sweep.Children.Add(tip);
        Children.Add(sweep);
        PaintSweep();

        IsVisibleChanged += (_, _) => Update();
        Unloaded += (_, _) => Stop();
        Loaded += (_, _) => Update();
        System.Windows.Automation.AutomationProperties.SetName(this, "Cleaning files");
    }

    private void PaintSweep()
    {
        Color accent = Application.Current?.Resources["PrimaryAccentColor"] is Color color ? color : Color.FromRgb(0x38, 0xCF, 0xC8);
        Color Alpha(byte alpha) => Color.FromArgb(alpha, accent.R, accent.G, accent.B);
        double trailing = -WedgeDegrees * Math.PI / 180;
        // The fade runs perpendicular to the trailing edge, so every point of that straight edge is fully transparent (no visible seam) and the wedge brightens
        // toward the leading edge; it is also clearest at the rim and fades toward the centre.
        var inward = new Vector(Math.Cos(trailing), Math.Sin(trailing));
        var fade = new LinearGradientBrush(new GradientStopCollection { new(Alpha(0), 0), new(Alpha(30), 0.62), new(Alpha(96), 1) },
            new Point(Centre, Centre), new Point(Centre, Centre) + inward * (Reach * Math.Sin(-trailing))) { MappingMode = BrushMappingMode.Absolute };
        fade.Freeze();
        wedge.Fill = fade;
        var line = new LinearGradientBrush(new GradientStopCollection { new(Alpha(0), 0), new(Alpha(120), 0.55), new(Alpha(255), 1) },
            new Point(Centre, Centre), new Point(Centre, Centre - Reach)) { MappingMode = BrushMappingMode.Absolute };
        line.Freeze();
        leadingLine.Stroke = line;
        var tipBrush = new SolidColorBrush(Alpha(235)); tipBrush.Freeze();
        tip.Fill = tipBrush;
    }

    // True while the animation is actually playing (used by tests).
    internal bool IsPlaying => playing;
    // How far the sweep has turned (degrees), so tests can see it move.
    internal double SweepAngle => sweepTurn.Angle;

    private void Update()
    {
        if (IsRunning && IsVisible && IsLoaded) Play(); else Stop();
    }

    private void Play()
    {
        if (playing) return;
        playing = true;
        PaintSweep();
        // One linear turn, forever, at the display's refresh rate (no frame-rate cap on purpose: a 30 fps cap is what made the old cue look like it stuttered).
        sweepTurn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, Turn) { RepeatBehavior = RepeatBehavior.Forever });
        sweep.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Stop()
    {
        if (!playing) return;
        playing = false;
        sweepTurn.BeginAnimation(RotateTransform.AngleProperty, null); sweepTurn.Angle = 0;
        sweep.BeginAnimation(OpacityProperty, null); sweep.Opacity = 0;
    }
}
