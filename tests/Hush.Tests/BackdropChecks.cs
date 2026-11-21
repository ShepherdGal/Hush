using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hush;

// The window background without bands (owner, November 20, 2026: "the background of the app is jagged and not smooth"). Before the fix one 840 px row of the
// dark window held 34 flat bands, the widest 42 px. These checks lock the fix: the dithered picture matches the brushes it replaces on average, holds no long flat
// run, and the real window shows it at its device-pixel size in both themes.
internal static class BackdropChecks
{
    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        // 1. The painter alone: same colors as the brushes (8 x 8 averages within half a level of the exact gradient), and no flat run longer than a few pixels.
        Color from = (Color)ColorConverter.ConvertFromString("#04090F"), to = (Color)ColorConverter.ConvertFromString("#0D1F36"), glow = (Color)ColorConverter.ConvertFromString("#3FC3EE");
        var painted = GradientDither.Paint(840, 600, from, to, glow, 0x1A / 255.0);
        var pixels = new byte[840 * 600 * 4]; painted.CopyPixels(pixels, 840 * 4, 0);
        double worst = 0;
        for (int blockY = 300; blockY < 592; blockY += 8)
            for (int blockX = 520; blockX < 832; blockX += 8)   // away from the glow: only the gradient, which is easy to state exactly
            {
                double sum = 0;
                for (int y = blockY; y < blockY + 8; y++) for (int x = blockX; x < blockX + 8; x++) sum += pixels[(y * 840 + x) * 4];
                double u = (blockX + 4) / 840.0, v = (blockY + 4) / 600.0, exact = from.B + (to.B - from.B) * (u + v) / 2;
                worst = Math.Max(worst, Math.Abs(sum / 64 - exact));
            }
        check(worst < 0.6, $"The dithered background has the brushes' own colors on average (worst 8 x 8 block {worst:0.00} levels off)");
        check(LongestRun(pixels, 840, 450, 0, 840) <= 8, $"The dithered background has no flat band (longest run of one color on a row: {LongestRun(pixels, 840, 450, 0, 840)} px; it was 42 before)");

        // 2. The real window: the layer appears, at the device-pixel size, in Dark and again after a switch to Light.
        string preferences = Path.Combine(fixture, "backdrop-preferences.json");
        new Preferences { TermsAccepted = Terms.Version, RunAsAdmin = false }.Save(preferences);
        var window = new MainWindow(engine, preferencesPath: preferences) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var layer = (Border)window.FindName("BackdropLayer");
            var root = (FrameworkElement)window.Content;
            foreach (bool light in new[] { false, true })
            {
                if (window.IsLightTheme != light) window.ToggleTheme();
                UiLayoutChecks.Wait(500);
                var image = (Image)window.FindName("BackdropImage");
                UiLayoutChecks.PumpUntil(() => layer.Visibility == Visibility.Visible && layer.Opacity >= 1 && image.Source is BitmapSource);
                var bitmap = (BitmapSource)image.Source;
                var dpi = VisualTreeHelper.GetDpi(window);
                check(Math.Abs(bitmap.PixelWidth - layer.ActualWidth * dpi.DpiScaleX) <= 1 && Math.Abs(bitmap.PixelHeight - layer.ActualHeight * dpi.DpiScaleY) <= 1
                    && RenderOptions.GetBitmapScalingMode(image) == BitmapScalingMode.NearestNeighbor && Math.Abs(image.ActualWidth - layer.ActualWidth) < 1,
                    $"The dithered background is shown 1:1 at the window's device-pixel size ({(light ? "Light" : "Dark")}: {bitmap.PixelWidth} x {bitmap.PixelHeight})");
                var size = new Size(920, 820);
                UiLayoutChecks.Layout(root, size);
                var shot = UiLayoutChecks.Render(root, size);
                var rendered = new byte[920 * 820 * 4]; shot.CopyPixels(rendered, 920 * 4, 0);
                int run = LongestRun(rendered, 920, 450, 40, 880);   // Home's open middle, between the status line and the tiles
                check(run <= 8, $"The window's background shows no band ({(light ? "Light" : "Dark")}: longest flat run {run} px)");
                if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string directory)
                    UiLayoutChecks.Save(root, size, Path.Combine(directory, $"backdrop-{(light ? "light" : "dark")}.png"));
            }
        }
        finally { window.Close(); }
    }

    // The longest stretch of one exact color along a row (x from "left" to "right").
    private static int LongestRun(byte[] pixels, int stride, int y, int left, int right)
    {
        int longest = 0, run = 0; int previous = -1;
        for (int x = left; x < right; x++)
        {
            int offset = (y * stride + x) * 4, color = pixels[offset] | pixels[offset + 1] << 8 | pixels[offset + 2] << 16;
            run = color == previous ? run + 1 : 1; previous = color; longest = Math.Max(longest, run);
        }
        return longest;
    }
}
