using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Hush;

// Interface size (Settings): Smart / 75% / 100% / 120%. The rules are checked with plain numbers; the window is checked with the real MainWindow.
internal static class UiSizeChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var laptop = new Rect(0, 0, 1366, 728);      // a 1366x768 laptop minus the taskbar
        var scaled = new Rect(0, 0, 1280, 680);      // 1920x1080 at 150% Windows scaling
        var desktop = new Rect(0, 0, 1920, 1040);    // 1080p at 100%
        var large = new Rect(0, 0, 2560, 1400);      // 1440p at 100%
        double smartLaptop = UiScale.SmartScale(laptop), smartScaled = UiScale.SmartScale(scaled);
        check(smartLaptop is >= 0.6 and < 1 && smartScaled is >= 0.6 and < 1 && smartScaled <= smartLaptop,
            "Smart shrinks the interface on small screens");
        check(UiScale.SmartScale(desktop) == 1.0 && UiScale.SmartScale(large) == 1.2, "Smart keeps 100% on a normal screen and grows to 120% only on a very large one");
        foreach (var work in new[] { laptop, scaled, desktop, large })
            foreach (string choice in UiScale.Choices)
            {
                double scale = UiScale.Resolve(choice, work);
                var window = UiScale.WindowSize(scale, work);
                var minimum = UiScale.MinimumSize(scale, work);
                check(window.Width <= work.Width && window.Height <= work.Height && minimum.Width <= window.Width && minimum.Height <= window.Height
                    && minimum.Width * 1.0 <= work.Width * 0.97 && minimum.Height <= work.Height * 0.97 && scale is >= 0.5 and <= 1.2,
                    $"{choice} on {work.Width}x{work.Height}: the window and its smallest size always fit the screen");
            }
        check(UiScale.Resolve("120", laptop) < 1.2 && UiScale.Resolve("120", laptop) == UiScale.LargestThatFits(laptop) && UiScale.Resolve("75", laptop) == 0.75,
            "A chosen size that cannot fit is lowered to the largest that does; smaller choices are kept");
        check(UiScale.Normalize("banana") == UiScale.Smart && UiScale.Normalize(null) == UiScale.Smart && UiScale.Normalize("75") == "75"
            && UiScale.Label("smart", 0.9) == "Smart · 90%" && UiScale.Label("120", 1.2) == "120%", "Unknown sizes read as Smart and labels are plain percentages");
        string folder = Path.Combine(Path.GetTempPath(), "hush-ui-size-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "settings.json");
            new Preferences { UiSize = "120" }.Save(path);
            check(Preferences.Load(path).UiSize == "120" && new Preferences().UiSize == UiScale.Smart, "The chosen interface size is remembered and defaults to Smart");
            File.WriteAllText(path, "{\"UiSize\":\"huge\"}");
            check(Preferences.Load(path).UiSize == UiScale.Smart, "A damaged interface size in the settings file reads as Smart");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // Looking at the result is part of the check: with HUSH_PREVIEW_DIRECTORY set, the window is rendered at its current size.
    private static void Capture(MainWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is not string directory) return;
        Directory.CreateDirectory(directory);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(window.Width, window.Height)); root.Arrange(new Rect(0, 0, window.Width, window.Height)); root.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }

    // Real window: the saved size is applied before the window shows, the chips change it live and save it, and nothing escapes the screen.
    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        var work = SystemParameters.WorkArea;
        string preferences = Path.Combine(fixture, "ui-size-preferences.json");
        new Preferences { UiSize = "75" }.Save(preferences);
        var window = new MainWindow(engine, preferencesPath: preferences);
        try
        {
            _ = new WindowInteropHelper(window).EnsureHandle(); // raises SourceInitialized like showing the window does
            double expected = UiScale.Resolve("75", work);
            var surface = (FrameworkElement)window.FindName("WindowSurface");
            check(Math.Abs(window.UiScaleValue - expected) < 0.001 && surface.LayoutTransform is ScaleTransform { ScaleX: var x } && Math.Abs(x - expected) < 0.001,
                "A saved 75% is applied to the whole window before it shows");
            check(Math.Abs(window.Width - UiScale.WindowSize(expected, work).Width) < 1 && Math.Abs(window.MinHeight - UiScale.MinimumSize(expected, work).Height) < 1,
                "The window and its smallest size follow the interface size");
            Capture(window, "ui-size-75");
            var chip120 = (RadioButton)window.FindName("UiSize120");
            chip120.IsChecked = true; chip120.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            double bigger = UiScale.Resolve("120", work);
            check(Math.Abs(window.UiScaleValue - bigger) < 0.001 && Preferences.Load(preferences).UiSize == "120"
                && ((TextBlock)window.FindName("UiSizeSummary")).Text == UiScale.Label("120", bigger), "Choosing 120% rescales at once, saves the choice and updates its summary");
            Capture(window, "ui-size-120");
            var chip100 = (RadioButton)window.FindName("UiSize100");
            chip100.IsChecked = true; chip100.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            check(Math.Abs(window.UiScaleValue - UiScale.Resolve("100", work)) < 0.001 && ((TextBlock)window.FindName("UiSizeNote")).Text.Length > 0, "Returning to 100% restores the design size");
        }
        finally { window.Close(); }
        // A plain test window with the default (Smart) choice stays at 100%: only the real app window sizes itself.
        var plain = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "ui-size-default.json"));
        try
        {
            _ = new WindowInteropHelper(plain).EnsureHandle();
            check(plain.UiScaleValue == 1 && Math.Abs(plain.Width - UiScale.BaseWidth) < 1, "A window that is not the app's main window keeps the 100% design size");
        }
        finally { plain.Close(); }
    }
}
