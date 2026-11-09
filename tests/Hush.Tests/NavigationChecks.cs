using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hush;

internal static class NavigationChecks
{
    internal static void Run(MainWindow window, FrameworkElement root, string fixture)
    {
        var home = (Grid)window.FindName("ScanPage");
        var review = (Grid)window.FindName("ReviewPage");
        var settings = (Grid)window.FindName("SettingsPage");
        var settingsButton = (Button)window.FindName("SettingsButton");
        var settingsBack = (Button)window.FindName("SettingsBackButton");
        var themeButton = (Button)window.FindName("ThemeButton");
        bool originalTheme = window.IsLightTheme;
        bool originallyReview = review.Visibility == Visibility.Visible;
        var selection = window.ReviewItems.Select(item => item.Selected).ToArray();
        var expansion = window.Categories.Select(category => category.Expanded).ToArray();
        var sentinel = new Border { Width = 16, Height = 16, Background = Brushes.Magenta,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4) };
        Panel.SetZIndex(sentinel, 100); home.Children.Add(sentinel);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Layout(Size size) { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
        byte[] Pixels()
        {
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
        }
        try
        {
            window.ShowReviewPage(false); Settle(window); Layout(new Size(920, 820));
            bool HasMarker()
            {
                var bytes = Pixels();
                for (int index = 0; index < bytes.Length; index += 4)
                    if (bytes[index] > 120 && bytes[index + 2] > 120 && bytes[index + 1] < 60) return true;
                return false;
            }
            Require(HasMarker(), "The outgoing-page pixel fixture is actually rendered before navigation");
            window.ShowReviewPage(true); root.UpdateLayout();
            Require(home.Visibility == Visibility.Collapsed && !HasMarker(),
                "The outgoing page's rendered pixels disappear immediately, rather than fading over the new page");
            window.ShowReviewPage(false); window.ShowReviewPage(true); Click(settingsButton);
            Require(home.Visibility == Visibility.Collapsed && review.Visibility == Visibility.Collapsed
                && settings.Visibility == Visibility.Visible && !HasMarker(), "Rapid page changes keep only the current page visible");
            Settle(window);
            Require(window.FindName("PageSnapshot") == null && window.FindName("ThemeSnapshot") == null,
                "No page or theme screenshot layer can leave ghost content behind");
            Click(settingsBack); Settle(window);
            Require(review.Visibility == Visibility.Visible && window.ReviewItems.Select(item => item.Selected).SequenceEqual(selection)
                && window.Categories.Select(category => category.Expanded).SequenceEqual(expansion), "Settings Back restores Review without losing selection or expansion");
            window.ShowReviewPage(false); Click(settingsButton); Click(settingsBack); Settle(window);
            Require(home.Visibility == Visibility.Visible, "Settings Back also restores Home when opened there");
            Click(settingsButton); Settle(window); home.Children.Remove(sentinel);
            var boost = (Expander)window.FindName("SettingsBoostSection");
            Require(!boost.IsExpanded && window.FindName("SettingsLightBox") == null && window.FindName("SettingsAppearanceSection") == null, "Settings sections start collapsed and there is no Appearance section (the theme lives in the top bar)");
            Layout(new Size(920, 820)); Wait(380);
            foreach (bool light in new[] { false, true })
            {
                if (window.IsLightTheme != light) window.ToggleTheme();
                Require(((Ellipse)window.FindName("ThemeSunCore")).Opacity == (light ? 1 : 0.3)
                    && themeButton.Content is Grid, "Theme indicator uses bright/dim vector sunlight");
                Layout(new Size(920, 820)); Pixels(); Wait(340);
                foreach (var size in new[] { new Size(920, 820), new Size(800, 740), new Size(1100, 900), new Size(800, 740) })
                {
                    Layout(size);
                    var controls = new[] { settingsButton, themeButton, (Button)window.FindName("MinimizeButton"), (Button)window.FindName("CloseButton") };
                    double? center = null; double previousRight = 0;
                    foreach (var button in controls)
                    {
                        Motion.Ensure(button);
                        var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                        center ??= bounds.Top + bounds.Height / 2;
                        Require(button.Width == 40 && button.Height == 40 && Math.Abs(bounds.Top + bounds.Height / 2 - center.Value) < 0.1
                            && bounds.Left >= previousRight && bounds.Right <= size.Width, "All four caption controls share aligned 40 DIP click areas at every size");
                        Require(button.RenderTransform.Value.IsIdentity && Motion.GetGlow(button) == 0
                            && ((SolidColorBrush)button.Background).Color.A == 0, "Caption controls stay seamless, glow-free and unscaled");
                        previousRight = bounds.Right;
                    }
                    Require(settingsBack.ActualWidth > 0 && new Rect(root.RenderSize).Contains(settingsBack.TransformToAncestor(root).TransformBounds(new Rect(settingsBack.RenderSize))),
                        "Settings Back remains reachable at every window size");
                    if (size.Width is 920 or 800 && Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string directory)
                    {
                        Directory.CreateDirectory(directory);
                        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(System.IO.Path.Combine(directory, $"settings-{(light ? "light" : "dark")}-{size.Width}.png")); encoder.Save(stream);
                    }
                }
            }
            bool before = window.IsLightTheme;
            Click(themeButton);
            Require(window.IsLightTheme != before && File.Exists(System.IO.Path.Combine(fixture, "preferences.json")),
                "The top-bar theme button changes and persists only fixture-owned app preferences");
            if (window.IsLightTheme != originalTheme) Click(themeButton);
            Click(settingsBack); window.ShowReviewPage(originallyReview); Settle(window);
            Console.WriteLine("PASS: No outgoing-page pixels or snapshots, rapid navigation, Settings return/theme behavior, aligned vector caption controls and responsive layouts");
        }
        finally
        {
            home.Children.Remove(sentinel);
            if (window.IsLightTheme != originalTheme) window.ToggleTheme();
        }
    }

    private static void Settle(MainWindow window)
    {
        var frame = new DispatcherFrame(); var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (!window.IsPageTransitioning || clock.ElapsedMilliseconds > 2000) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Require(!window.IsPageTransitioning, "Incoming page fade settles without holding a snapshot");
    }
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false; timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
