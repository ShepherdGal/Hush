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
            // Phase D (owner, November 19, 2026: "no accordions"): Settings is one open page; the theme still lives only in the top bar.
            Require(boost.IsExpanded && ((FrameworkElement)window.FindName("SettingsIndex")).Visibility == Visibility.Visible && window.FindName("SettingsLightBox") == null && window.FindName("SettingsAppearanceSection") == null,
                "Settings is one open page with a section index, every section shown without being opened, and no theme switch of its own (the theme lives in the top bar)");
            Layout(new Size(920, 820)); Wait(380);
            foreach (bool light in new[] { false, true })
            {
                if (window.IsLightTheme != light) window.ToggleTheme();
                Wait(700);   // the theme thumb springs for 600 ms
                // Phase D (owner, November 19, 2026): the theme is a sun / full-moon slider; the accent thumb sits under the current theme's icon.
                Require(Math.Abs(((TranslateTransform)window.FindName("ThemeThumbShift")).X - (light ? 0 : MainWindow.ThemeThumbTravel)) < 0.01
                    && themeButton.Content is Grid && window.FindName("ThemeMoonDisc") is Ellipse, "The theme slider's thumb sits under the sun in Light and under the full moon in Dark");
                Layout(new Size(920, 820)); Pixels(); Wait(340);
                foreach (var size in new[] { new Size(920, 820), new Size(800, 740), new Size(1100, 900), new Size(800, 740) })
                {
                    Layout(size);
                    Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                    var left = Bounds((FrameworkElement)window.FindName("HeaderLeft")); var tabs = Bounds((FrameworkElement)window.FindName("TabBar"));
                    var right = Bounds((FrameworkElement)window.FindName("CaptionControls"));
                    Require(Math.Abs(tabs.Left + tabs.Width / 2 - size.Width / 2) < 1.5, $"The tab bar sits exactly in the window's center at {size.Width} wide (owner, November 19, 2026)");
                    Require(left.Right <= tabs.Left - 4 && tabs.Right <= right.Left - 4 && right.Right <= size.Width && left.Left >= 0,
                        $"Wordmark and mode pill, tabs, and theme and window buttons never touch at {size.Width} wide ({left.Right:0}/{tabs.Left:0}, {tabs.Right:0}/{right.Left:0})");
                    var controls = new[] { themeButton, (Button)window.FindName("MinimizeButton"), (Button)window.FindName("CloseButton") };
                    double? center = null; double previousRight = 0;
                    foreach (var button in controls)
                    {
                        Motion.Ensure(button);
                        var bounds = Bounds(button);
                        center ??= bounds.Top + bounds.Height / 2;
                        Require(Math.Abs(bounds.Top + bounds.Height / 2 - center.Value) < 0.6 && bounds.Left >= previousRight && bounds.Right <= size.Width,
                            "The theme slider and the window buttons share one center line at every size");
                        Require(button.RenderTransform.Value.IsIdentity && Motion.GetGlow(button) == 0
                            && ((SolidColorBrush)button.Background).Color.A == 0, "Header controls stay seamless, glow-free and unscaled");
                        previousRight = bounds.Right;
                    }
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
            // The tabs (Phase D): each page lights its own tab and the pill moves under it; Ctrl+1 to Ctrl+5 move at once.
            Click((Button)window.FindName("HomeTabButton")); Settle(window);
            Require(home.Visibility == Visibility.Visible && window.ActiveTab == "Home" && (string)((Button)window.FindName("HomeTabButton")).Tag == "On", "The Home tab opens Home and lights itself");
            Click(settingsButton); Settle(window); Wait(700);   // the tab pill springs for 600 ms
            Require(settings.Visibility == Visibility.Visible && window.ActiveTab == "Settings" && (string)settingsButton.Tag == "On" && (string)((Button)window.FindName("HomeTabButton")).Tag == "Off",
                "The Settings tab opens Settings, lights itself and unlights Home");
            int visibleTabs = ((StackPanel)window.FindName("TabStrip")).Children.OfType<Button>().Count(tab => tab.Visibility == Visibility.Visible);
            Require(Math.Abs(((TranslateTransform)window.FindName("TabPillShift")).X - (visibleTabs - 1) * MainWindow.TabWidth) < 0.5, "The selected pill ends under the Settings tab (the last tab)");
            Console.WriteLine("PASS: No outgoing-page pixels or snapshots, rapid navigation, Settings return/theme behavior, a centered tab bar that never touches its neighbors, the theme slider and the tabs");
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
