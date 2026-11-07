using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hush;

// One numeric trajectory traverses the intervening counts. Decimal carry drives
// the digit reels, so a batch of six discoveries rolls 0,1,2,3,4,5,6.
public sealed class RollingFileCount : Control
{
    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(RollingFileCount), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly Dictionary<string, FormattedText> textCache = [];
    private double cachedDpi;
    private double start;
    private int generation;
    private TaskCompletionSource? completion;
    private int count;
    internal int Count => count;
    private double DisplayValue => Math.Clamp((double)GetValue(DisplayValueProperty), 0, count);
    internal int DisplayedCount => (int)Math.Floor(DisplayValue + 0.0000001);
    internal double Phase => count <= start ? 1 : Math.Clamp((DisplayValue - start) / (count - start), 0, 1);
    internal double LastDurationMilliseconds { get; private set; }
    internal bool IsAnimating => HasAnimatedProperties && DisplayValue < count - 0.0000001;
    internal string Summary => $"{count:N0} {(count == 1 ? "file" : "files")} found";

    public RollingFileCount()
    {
        Focusable = false;
        IsHitTestVisible = false;
        Unloaded += (_, _) => Settle();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Settle(); };
    }

    internal void Reset()
    {
        Settle();
        count = 0; start = 0;
        SetValue(DisplayValueProperty, 0.0);
        LastDurationMilliseconds = 0;
        UpdateName(); InvalidateMeasure(); InvalidateVisual();
    }

    internal void SetCount(int value, bool animate = true)
    {
        value = Math.Max(0, value);
        if (value <= count) return;
        start = DisplayValue;
        count = value;
        LastDurationMilliseconds = Math.Clamp((count - start) * 150, 240, 600); // Live: the odometer never trails the real count by more than ~0.6 s.
        UpdateName(); InvalidateMeasure();
        BeginAnimation(DisplayValueProperty, null);
        SetValue(DisplayValueProperty, start);
        // Like the gauge/page motion, this user-requested app animation must not
        // silently disappear when Windows' separate client-area flag is off.
        if (!animate) Settle();
        else
        {
            int version = ++generation;
            // Linear count progression; each individual carry eases in OnRender.
            var animation = new DoubleAnimation(start, count, TimeSpan.FromMilliseconds(LastDurationMilliseconds));
            animation.Completed += (_, _) => { if (generation == version) Settle(); };
            BeginAnimation(DisplayValueProperty, animation);
        }
        InvalidateVisual();
    }

    internal void Settle()
    {
        generation++;
        BeginAnimation(DisplayValueProperty, null);
        SetValue(DisplayValueProperty, (double)count);
        completion?.TrySetResult(); completion = null;
        InvalidateVisual();
    }

    internal Task CompleteAsync() => IsAnimating
        ? (completion ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task : Task.CompletedTask;

    private void UpdateName() => System.Windows.Automation.AutomationProperties.SetName(this, Summary);
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontFamilyProperty || e.Property == FontSizeProperty || e.Property == FontStyleProperty
            || e.Property == FontWeightProperty || e.Property == FontStretchProperty || e.Property == ForegroundProperty || e.Property == FlowDirectionProperty)
        { textCache?.Clear(); InvalidateMeasure(); InvalidateVisual(); }
    }
    private FormattedText Text(string value)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (cachedDpi != dpi) { textCache.Clear(); cachedDpi = dpi; }
        if (!textCache.TryGetValue(value, out var text))
            textCache[value] = text = new(value, CultureInfo.CurrentCulture, FlowDirection,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground ?? Brushes.Gray, dpi);
        return text;
    }
    private double DigitWidth => Enumerable.Range(0, 10).Max(digit => Text(digit.ToString(CultureInfo.CurrentCulture)).WidthIncludingTrailingWhitespace);
    private string Pattern => DisplayedCount.ToString("N0", CultureInfo.CurrentCulture);

    protected override Size MeasureOverride(Size constraint)
    {
        string pattern = Pattern;
        double width = pattern.Sum(character => char.IsDigit(character) ? DigitWidth : Text(character.ToString()).WidthIncludingTrailingWhitespace)
            + Text(DisplayedCount == 1 ? " file found" : " files found").WidthIncludingTrailingWhitespace;
        return new Size(width, Math.Max(22, Math.Ceiling(Text("0").Height + 2)));
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double digitWidth = DigitWidth;
        string pattern = Pattern;
        int whole = DisplayedCount;
        double fraction = Math.Clamp(DisplayValue - whole, 0, 1);
        fraction = fraction * fraction * (3 - 2 * fraction);
        var suffix = Text(whole == 1 ? " file found" : " files found");
        double width = pattern.Sum(character => char.IsDigit(character) ? digitWidth : Text(character.ToString()).WidthIncludingTrailingWhitespace)
            + suffix.WidthIncludingTrailingWhitespace;
        double x = (ActualWidth - width) / 2;
        double height = ActualHeight;
        int remaining = pattern.Count(char.IsDigit);
        foreach (char character in pattern)
        {
            if (!char.IsDigit(character))
            {
                var separator = Text(character.ToString());
                drawing.DrawText(separator, new Point(x, (height - separator.Height) / 2));
                x += separator.WidthIncludingTrailingWhitespace;
                continue;
            }
            int index = --remaining;
            long place = (long)Math.Pow(10, index);
            int current = (int)(whole / place % 10);
            double carry = whole % place == place - 1 ? fraction : 0;
            drawing.PushClip(new RectangleGeometry(new Rect(x, 0, digitWidth, height)));
            DrawDigit(current, -carry * height);
            if (carry > 0.000001) DrawDigit((current + 1) % 10, (1 - carry) * height);
            drawing.Pop();
            x += digitWidth;
        }
        drawing.DrawText(suffix, new Point(x, (height - suffix.Height) / 2));

        void DrawDigit(int digit, double offset)
        {
            var text = Text(digit.ToString(CultureInfo.CurrentCulture));
            drawing.DrawText(text, new Point(x + (digitWidth - text.WidthIncludingTrailingWhitespace) / 2, (height - text.Height) / 2 + offset));
        }
    }
}
