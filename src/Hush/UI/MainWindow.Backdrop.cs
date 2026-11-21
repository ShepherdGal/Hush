using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Hush;

// The window's background without bands (owner, November 20, 2026: "the background of the app is jagged and not smooth"). WPF draws the navy gradient and the
// corner glow straight into 8-bit color, and a dark gradient across a big window has only a handful of levels per channel: measured, 34 flat bands across one
// 840 px row, some 42 px wide, with red, green and blue stepping at different places (a faint colored staircase). Noise laid over the finished picture only
// roughens the steps (tried and measured), so Hush paints the background itself: every pixel of the same gradient and glow is worked out in full precision
// and rounded with a tiny random nudge (triangular, within one level), which spreads each step into a smooth slope the eye cannot split into bands.
//
// The bitmap matches the brushes under it exactly (WindowGradientBrush, WindowGlowBrush), is made at the window's own device-pixel size so it is shown 1:1
// (nearest-neighbour, never resampled), is painted on a worker thread and only when something changed (size, theme, main color, DPI, interface size), and is
// shown by one plain Image (the renderer's fast 1:1 copy; an ImageBrush on a rounded Border doubled the cost of scrolling on software rendering) above the brushes. While a theme change animates the brushes (320 ms) the bitmap fades out with the same easing and comes back, freshly painted.
// No effect, no timer, no animation while idle.
internal static class GradientDither
{
    // The window gradient runs from the top-left to the bottom-right corner of the window's unit square (t = (u + v) / 2, the LinearGradientBrush from (0,0) to
    // (1,1)); the glow is the accent at "glowAlpha" in the top-left corner, fading linearly to nothing at 0.6 of the window in each direction.
    internal static BitmapSource Paint(int width, int height, Color from, Color to, Color glow, double glowAlpha, double dpiX = 96, double dpiY = 96)
    {
        var pixels = new byte[width * height * 4];   // Bgr32: opaque, so the renderer copies it instead of blending every pixel
        uint state = 0x9E3779B9;
        double Noise()
        {
            // Two uniform numbers added: a triangular nudge between -1 and +1 level, centred on 0 (xorshift: fast and the same every time).
            state ^= state << 13; state ^= state >> 17; state ^= state << 5; double a = state / 4294967296.0;
            state ^= state << 13; state ^= state >> 17; state ^= state << 5; double b = state / 4294967296.0;
            return a + b - 1;
        }
        static byte Round(double value) => (byte)Math.Clamp((int)Math.Floor(value + 0.5), 0, 255);
        for (int y = 0; y < height; y++)
        {
            double v = (y + 0.5) / height, glowV = v / 0.6;
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                double u = (x + 0.5) / width, t = (u + v) / 2;
                double r = from.R + (to.R - from.R) * t, g = from.G + (to.G - from.G) * t, b = from.B + (to.B - from.B) * t;
                double glowU = u / 0.6, distance = Math.Sqrt(glowU * glowU + glowV * glowV);
                if (distance < 1)
                {
                    double a = glowAlpha * (1 - distance);
                    r += (glow.R - r) * a; g += (glow.G - g) * a; b += (glow.B - b) * a;
                }
                int index = row + x * 4;
                pixels[index] = Round(b + Noise()); pixels[index + 1] = Round(g + Noise()); pixels[index + 2] = Round(r + Noise()); pixels[index + 3] = 0;
            }
        }
        var bitmap = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgr32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

public partial class MainWindow
{
    private bool backdropQueued;
    private int backdropRequest;
    // Test seam: off makes every window keep the plain brushes (nothing else changes).
    internal static bool BackdropDitherEnabled = true;

    // Any number of requests in one moment become one paint (a resize sends many): it runs when the dispatcher is otherwise idle.
    internal void RequestBackdrop()
    {
        if (!BackdropDitherEnabled || backdropQueued) return;
        backdropQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { backdropQueued = false; _ = PaintBackdropAsync(); }));
    }

    private async Task PaintBackdropAsync()
    {
        var layer = BackdropLayer;
        if (layer.ActualWidth < 1 || layer.ActualHeight < 1) return;
        // Device pixels: the layer's size times the interface size (a layout transform on WindowSurface) times the monitor's DPI.
        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = WindowSurface.LayoutTransform is ScaleTransform zoom ? zoom.ScaleX : 1;
        int width = (int)Math.Round(layer.ActualWidth * scale * dpi.DpiScaleX), height = (int)Math.Round(layer.ActualHeight * scale * dpi.DpiScaleY);
        if (width < 1 || height < 1 || width > 8000 || height > 8000) return;
        Color from = (Color)ColorConverter.ConvertFromString(light ? "#F5F8FD" : "#04090F"), to = (Color)ColorConverter.ConvertFromString(light ? "#E9EDF8" : "#0D1F36");
        Color glow = (Color)ColorConverter.ConvertFromString(AccentColor(light));
        double glowAlpha = (light ? 0x14 : 0x1A) / 255.0;   // the same strengths as WindowGlowBrush in ApplyTheme
        int request = ++backdropRequest;
        // The bitmap's DPI makes its size in DIPs exactly the layer's, so the Image shows it 1:1 on the device pixels.
        double dpiX = 96 * scale * dpi.DpiScaleX, dpiY = 96 * scale * dpi.DpiScaleY;
        var bitmap = await Task.Run(() => GradientDither.Paint(width, height, from, to, glow, glowAlpha, dpiX, dpiY));
        if (request != backdropRequest) return;   // something changed while it was painting: the newer paint wins
        BackdropImage.Source = bitmap;
        backdropCovers = (from, to);
        if (layer.Visibility != Visibility.Visible) { layer.Opacity = 0; layer.Visibility = Visibility.Visible; }
        if (layer.Opacity < 1)
        {
            var show = new DoubleAnimation(1, TimeSpan.FromMilliseconds(Motion.HoverMilliseconds));
            show.Completed += (_, _) => { if (request == backdropRequest) Underlay(covered: true); };
            layer.BeginAnimation(OpacityProperty, show);
        }
        else Underlay(covered: true);
    }

    private (Color From, Color To) backdropCovers;
    // While the opaque bitmap covers the window, the gradient and the glow under it are invisible but still cost a full redraw behind anything that moves
    // (a radial gradient is expensive on software rendering): they give way to one plain fill, and come back for theme changes, which animate them.
    private void Underlay(bool covered)
    {
        if (covered)
        {
            WindowGlowLayer.Visibility = Visibility.Hidden;
            var (from, to) = backdropCovers;
            var plain = new SolidColorBrush(Color.FromRgb((byte)((from.R + to.R) / 2), (byte)((from.G + to.G) / 2), (byte)((from.B + to.B) / 2)));
            plain.Freeze();
            WindowSurface.Background = plain;
        }
        else
        {
            WindowGlowLayer.Visibility = Visibility.Visible;
            WindowSurface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "WindowGradientBrush");
        }
    }

    // A theme or main-color change: the brushes under the bitmap animate, so the bitmap steps aside now and is painted again once they have arrived.
    private async void RefreshBackdropForTheme(bool animate)
    {
        if (!BackdropDitherEnabled) return;
        ++backdropRequest;   // a paint already on its way belongs to the old colors
        Underlay(covered: false);   // the animated brushes are what shows while the colors change
        if (animate && BackdropLayer.Visibility == Visibility.Visible)
        {
            // Fade out with the brushes' own timing and easing, so the first frame is exactly what was on screen and the change stays one continuous blend.
            BackdropLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(ThemePalette.Milliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            await Task.Delay(TimeSpan.FromMilliseconds(ThemePalette.Milliseconds + 40));
        }
        RequestBackdrop();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RequestBackdrop();
    }
}
