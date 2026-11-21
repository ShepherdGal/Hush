using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Hush;

// The shared waiting look (owner, November 20, 2026: "nothing in the app should be blank for waiting"; mockup B, "ticking steps + bar"): a short title, the
// named steps of what is happening, each ticking off with a green check (and its own result line) as it finishes, the step in progress turning a small accent
// arc, and one bar under them that eases toward full and completes only when all is done (EasedProgress). Built once in code; the arc's rotation is the only
// endless animation and runs only while a step is in progress and the control is visible (PerformanceRuleChecks allows it here and in the two gauge cues).
public sealed class WaitSteps : StackPanel
{
    private sealed class Step
    {
        public required Grid Row; public required TextBlock Title, Detail; public required Grid Marker; public required Path Arc; public required TextBlock Check;
        public string State = "pending";
    }

    private readonly TextBlock heading = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel list = new();
    private readonly Border track = new() { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 12, 0, 0) };
    private readonly List<Step> steps = [];
    private readonly RotateTransform spin = new(0, 8, 8);
    private bool spinning;

    public WaitSteps()
    {
        Visibility = Visibility.Collapsed;
        heading.SetResourceReference(TextBlock.FontSizeProperty, "BodyFontSize");
        heading.SetResourceReference(TextBlock.ForegroundProperty, "HeadingBrush");
        track.SetResourceReference(Border.BackgroundProperty, "GlassEdgeBrush");
        var fill = new Border { CornerRadius = new CornerRadius(2), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new ScaleTransform(0, 1) };
        fill.SetResourceReference(Border.BackgroundProperty, "TealTextBrush");
        track.Child = fill;
        Children.Add(heading); Children.Add(list); Children.Add(track);
        IsVisibleChanged += (_, _) => UpdateSpin();
        System.Windows.Automation.AutomationProperties.SetName(this, "Working");
    }

    // Test seam: each step's state ("pending", "now", "done") and its result line.
    internal IReadOnlyList<(string Title, string State, string Detail)> Shown => steps.Select(step => (step.Title.Text, step.State, step.Detail.Text)).ToList();
    internal bool IsSpinning => spinning;

    // Starts a new wait: the heading, the step names, and how long the whole thing is expected to take (the bar's pace, not a promise).
    public void Begin(string title, double expectedSeconds, params string[] names)
    {
        heading.Text = title;
        list.Children.Clear(); steps.Clear();
        foreach (string name in names) AddStep(name);
        EasedProgress.SetExpectedSeconds(track, expectedSeconds);
        EasedProgress.SetState(track, "idle");
        EasedProgress.SetState(track, "working");
        track.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        if (steps.Count > 0) Now(0);
    }

    // The step in progress (the ones before it count as done when they were not ticked yet).
    public void Now(int index, string detail = "")
    {
        for (int i = 0; i < steps.Count; i++)
        {
            if (i < index && steps[i].State != "done") Set(steps[i], "done", steps[i].Detail.Text);
            else if (i == index) Set(steps[i], "now", detail);
        }
        UpdateSpin();
    }

    // One step finished, with what it found ("4 updates found").
    public void Done(int index, string detail = "") { if (index < steps.Count) Set(steps[index], "done", detail); UpdateSpin(); }

    // All done: every step ticked, the bar completes and fades, and the whole thing leaves after a short moment so the last tick can be seen.
    public async Task FinishAsync()
    {
        foreach (var step in steps.Where(step => step.State != "done")) Set(step, "done", step.Detail.Text);
        UpdateSpin();
        EasedProgress.SetState(track, "done");
        await Task.Delay(Motion.Animates ? 650 : 0);
        Visibility = Visibility.Collapsed;
        EasedProgress.SetState(track, "idle");
    }

    // Stopped or failed: it leaves at once.
    public void Cancel() { Visibility = Visibility.Collapsed; EasedProgress.SetState(track, "idle"); UpdateSpin(); }

    private void AddStep(string name)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var marker = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        var disc = new Ellipse(); disc.SetResourceReference(Shape.FillProperty, "RaisedBrush");
        var arc = new Path { Data = Geometry.Parse("M 8,1.5 A 6.5,6.5 0 1 1 1.5,8"), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 16, Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = spin, Visibility = Visibility.Collapsed };
        arc.SetResourceReference(Shape.StrokeProperty, "TealTextBrush");
        var check = new TextBlock { Text = "✓", FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        check.SetResourceReference(TextBlock.ForegroundProperty, "GoodBrush");
        marker.Children.Add(disc); marker.Children.Add(arc); marker.Children.Add(check);
        var text = new StackPanel();
        var title = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.FontSizeProperty, "SupportFontSize");
        title.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var detail = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        text.Children.Add(title); text.Children.Add(detail);
        Grid.SetColumn(text, 1);
        row.Children.Add(marker); row.Children.Add(text);
        list.Children.Add(row);
        steps.Add(new Step { Row = row, Title = title, Detail = detail, Marker = marker, Arc = arc, Check = check });
    }

    private static void Set(Step step, string state, string detail)
    {
        step.State = state;
        step.Arc.Visibility = state == "now" ? Visibility.Visible : Visibility.Collapsed;
        step.Check.Visibility = state == "done" ? Visibility.Visible : Visibility.Collapsed;
        step.Title.SetResourceReference(TextBlock.ForegroundProperty, state == "pending" ? "MutedBrush" : "HeadingBrush");
        step.Detail.Text = detail;
        step.Detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // The arc turns only while a step is in progress and the control is on screen.
    private void UpdateSpin()
    {
        bool want = IsVisible && Visibility == Visibility.Visible && steps.Any(step => step.State == "now") && Motion.Animates;
        if (want == spinning) return;
        spinning = want;
        spin.BeginAnimation(RotateTransform.AngleProperty, want ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever } : null);
    }
}
