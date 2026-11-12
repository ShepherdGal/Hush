using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hush;

// A tight glow around every LETTER of a short text (the "Pro" word and the Hush wordmark while Pro mode is on; owner, November 12, 2026: "style B, tight letter glow").
// The glow is drawn ONCE as a picture and the window just shows that picture behind the real text, so it costs nothing while the app runs: no blur effect, no shadow
// effect, nothing computed per frame. How the picture is made: the text is rendered white on transparent at 3x size, that mask is blurred twice in code (a narrow
// gaussian and a wider, fainter one: the same two-layer "tight" glow the owner chose from the previews), the two layers are combined and tinted with the main color.
// Because it is drawn at 3x and shown at the element's size, it stays smooth at any screen scale and interface size (no stepped, jagged edge, which the earlier
// stacked-blob halo had). It is drawn again only when something that changes it changes: the text, the font, or the main color / theme (the window calls Refresh).
// The element is as big as the text; the glow extends past its edges (nothing clips it), so it is placed in the same spot and alignment as the text it lights.
public sealed class LetterGlow : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(LetterGlow),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (sender, _) => ((LetterGlow)sender).baked = null));
    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(nameof(FontFamily), typeof(FontFamily), typeof(LetterGlow),
        new FrameworkPropertyMetadata(new FontFamily("Segoe UI"), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (sender, _) => ((LetterGlow)sender).baked = null));
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(LetterGlow),
        new FrameworkPropertyMetadata(15.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (sender, _) => ((LetterGlow)sender).baked = null));
    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(nameof(FontWeight), typeof(FontWeight), typeof(LetterGlow),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (sender, _) => ((LetterGlow)sender).baked = null));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

    // How large the picture is drawn compared with its size on screen, and the two glow layers of "tight" (a gaussian sigma in DIP for a 33 DIP wordmark, and how
    // strong each layer is). Smaller text gets a proportionally smaller glow (never below half).
    private const int Supersample = 3;
    private const double NarrowSigma = 1.5, WideSigma = 3.5, NarrowStrength = 0.95, WideStrength = 0.45, ReferenceSize = 33;

    private BitmapSource? baked;
    private double bakedPad;
    private FormattedText? Format() => string.IsNullOrEmpty(Text) ? null : new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Format();
        return text == null ? new Size(0, 0) : new Size(Math.Ceiling(text.WidthIncludingTrailingWhitespace), Math.Ceiling(text.Height));
    }

    // Draw again with the current main color (called by the window after a theme or main color change).
    internal void Refresh() { baked = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext drawing)
    {
        baked ??= Bake();
        if (baked != null) drawing.DrawImage(baked, new Rect(-bakedPad, -bakedPad, baked.PixelWidth / (double)Supersample, baked.PixelHeight / (double)Supersample));
    }

    internal BitmapSource? Bake()
    {
        var text = Format();
        if (text == null) return null;
        double radius = Math.Clamp(FontSize / ReferenceSize, 0.5, 1), narrow = NarrowSigma * radius, wide = WideSigma * radius;
        bakedPad = Math.Ceiling(wide * 3) + 2;
        int width = (int)Math.Ceiling((Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 2 * bakedPad) * Supersample), height = (int)Math.Ceiling((Math.Ceiling(text.Height) + 2 * bakedPad) * Supersample);

        // The letters as a white mask on transparent.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) { context.PushTransform(new ScaleTransform(Supersample, Supersample)); context.DrawText(text, new Point(bakedPad, bakedPad)); }
        var mask = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        mask.Render(visual);
        var pixels = new byte[width * height * 4];
        mask.CopyPixels(pixels, width * 4, 0);
        var alpha = new float[width * height];
        for (int index = 0; index < alpha.Length; index++) alpha[index] = pixels[index * 4 + 3] / 255f;

        // Two blurs of the mask, combined like two stacked shadows: 1 - (1 - a1)(1 - a2).
        var near = Blur(alpha, width, height, narrow * Supersample);
        var far = Blur(alpha, width, height, wide * Supersample);
        var tint = TryFindResource("PrimaryAccentColor") is Color color ? color : Color.FromRgb(63, 195, 238);
        for (int index = 0; index < alpha.Length; index++)
        {
            float glow = 1 - (1 - (float)NarrowStrength * near[index]) * (1 - (float)WideStrength * far[index]);
            pixels[index * 4] = (byte)(tint.B * glow + 0.5f); pixels[index * 4 + 1] = (byte)(tint.G * glow + 0.5f); pixels[index * 4 + 2] = (byte)(tint.R * glow + 0.5f);
            pixels[index * 4 + 3] = (byte)(glow * 255 + 0.5f);   // premultiplied
        }
        var picture = BitmapSource.Create(width, height, 96 * Supersample, 96 * Supersample, PixelFormats.Pbgra32, null, pixels, width * 4);
        picture.Freeze();
        return picture;
    }

    // A separable gaussian blur (sigma in pixels), clamped at the edges (the picture has a margin of three sigma, so nothing reaches them).
    private static float[] Blur(float[] source, int width, int height, double sigma)
    {
        int reach = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[reach * 2 + 1]; float total = 0;
        for (int offset = -reach; offset <= reach; offset++) { kernel[offset + reach] = (float)Math.Exp(-(offset * offset) / (2 * sigma * sigma)); total += kernel[offset + reach]; }
        for (int index = 0; index < kernel.Length; index++) kernel[index] /= total;
        var across = new float[source.Length]; var result = new float[source.Length];
        for (int row = 0; row < height; row++)
            for (int column = 0; column < width; column++)
            {
                float sum = 0;
                for (int offset = -reach; offset <= reach; offset++) { int at = Math.Clamp(column + offset, 0, width - 1); sum += source[row * width + at] * kernel[offset + reach]; }
                across[row * width + column] = sum;
            }
        for (int row = 0; row < height; row++)
            for (int column = 0; column < width; column++)
            {
                float sum = 0;
                for (int offset = -reach; offset <= reach; offset++) { int at = Math.Clamp(row + offset, 0, height - 1); sum += across[at * width + column] * kernel[offset + reach]; }
                result[row * width + column] = sum;
            }
        return result;
    }
}
