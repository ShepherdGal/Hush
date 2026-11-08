using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hush;

internal static class ThemeMotionChecks
{
    internal static void Run(CleanupEngine engine, string fixture)
    {
        var window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "theme-motion-preferences.json"),
            windowsSettings: new WindowsSettingsService(new ReadOnlyFixtureStore(), Path.Combine(fixture, "theme-windows-history")))
        { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.Width = 800; window.Height = 740;
            var root = (FrameworkElement)window.Content;
            var settings = (Button)window.FindName("SettingsButton");
            void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Color Backdrop()
            {
                root.UpdateLayout();
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(root)
                { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(10, 90, 4, 4), Stretch = Stretch.Fill }, null, new Rect(0, 0, 4, 4));
                var bitmap = new RenderTargetBitmap(4, 4, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                byte[] pixels = new byte[64]; bitmap.CopyPixels(pixels, 16, 0);
                return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
            }
            Color Resource(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;
            foreach (string page in new[] { "Home", "Review", "Settings" })
            {
                if (page == "Review") window.ShowReviewPage(true);
                if (page == "Settings") Click("SettingsButton");
                Wait(400);
                foreach (bool light in new[] { true, false })
                {
                    Color start = Backdrop();
                    string[] roles = ["PanelBrush", "HeadingBrush", "MutedBrush", "AccentHoverBrush", "TealTextBrush"];
                    var roleStarts = roles.ToDictionary(key => key, Resource);
                    var roleFrames = roles.ToDictionary(key => key, _ => new List<Color>());
                    Color target = (Color)ColorConverter.ConvertFromString(light ? "#EFF1F5" : "#11151B");
                    Require(window.IsLightTheme != light, "Theme fixture alternates actual directions");
                    Click("ThemeButton");
                    Color immediate = Backdrop();
                    Require(immediate == start, $"Theme switching must start from displayed pixels instead of snapping the background ({page}, {start} -> {immediate})");
                    var samples = new List<Color>();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                    bool captured = false;
                    var elapsed = Stopwatch.StartNew();
                    timer.Tick += (_, _) =>
                    {
                        samples.Add(Backdrop());
                        foreach (string role in roles) roleFrames[role].Add(Resource(role));
                        if (page == "Settings" && !captured && elapsed.ElapsedMilliseconds >= 90)
                        { captured = true; Capture(root, light ? "theme-to-light-intermediate" : "theme-to-dark-intermediate"); }
                    };
                    timer.Start();
                    try { Wait(430); } finally { timer.Stop(); }
                    Require(samples.Distinct().Count() >= 4 && samples.Any(color => color != start && color != target),
                        $"Both theme directions render several intermediate background colors on every page [{page} light={light} start={start} target={target} n={samples.Count} distinct={string.Join(",", samples.Distinct())}]");
                    for (int index = 1; index < samples.Count; index++)
                        Require(light ? samples[index].R >= samples[index - 1].R : samples[index].R <= samples[index - 1].R,
                            "Theme colors move monotonically toward the selected destination");
                    Require(Backdrop() == target && Resource("WindowBrush") == target, "Theme settles exactly at the selected palette");
                    foreach (string role in roles)
                        Require(roleFrames[role].Distinct().Count() >= 4 && roleFrames[role].Any(color => color != roleStarts[role] && color != Resource(role)),
                            "Surfaces, headings, support text and accents share gradual palette motion");
                    Color accent = (Color)ColorConverter.ConvertFromString(light ? "#4C7BC8" : "#38CFC8");
                    Require((Color)Application.Current.Resources["PrimaryAccentColor"] == accent
                        && Resource("TealTextBrush") == accent && Resource("SafetyGreenBrush") == accent
                        && ((SolidColorBrush)Application.Current.Resources["TealBrush"]).Color == accent,
                        $"Primary accent text, Safe accents and the gauge share one accent per theme: soft blue in Light, Cyan in Dark (light={light})");
                    var reviewAction = (Button)window.FindName("ApplyWindowsSettingsButton");
                    if (page == "Settings")
                    {
                        var fill = (SolidColorBrush)((Border)reviewAction.Template.FindName("Surface", reviewAction)).Background;
                        var ink = (SolidColorBrush)((ContentPresenter)reviewAction.Template.FindName("ButtonContent", reviewAction)).GetValue(System.Windows.Documents.TextElement.ForegroundProperty);
                        Require(SamePixelColor(fill.Color, Resource("ActionFillBrush")) && SamePixelColor(ink.Color, Resource("TealTextBrush")),
                            $"Button wrappers reach exact colors without restarting (fill {fill.Color}/{Resource("ActionFillBrush")}, ink {ink.Color}/{Resource("TealTextBrush")})");
                        Capture(root, light ? "theme-settings-light" : "theme-settings-dark");
                    }
                    Require(window.FindName("ThemeSnapshot") == null && window.FindName("PageSnapshot") == null
                        && root.RenderTransform.Value.IsIdentity, "Theme motion never uses snapshots or scales the app");
                }
            }
            Click("SettingsBackButton"); Wait(350);
            Click("ThemeButton"); Wait(90);
            Color displayed = Backdrop(); Click("ThemeButton");
            Require(Backdrop() == displayed, "Rapid theme reversal retargets displayed pixels continuously");
            Wait(430); Require(Backdrop() == (Color)ColorConverter.ConvertFromString("#11151B"), "Rapid reversal settles in the latest theme");
            Click("ThemeButton"); Wait(65); Click("AboutButton"); Wait(430);
            Require(((FrameworkElement)window.FindName("AboutOverlay")).Visibility == Visibility.Visible
                && !((UIElement)window.FindName("AppSurface")).IsEnabled && Resource("PanelBrush") == (Color)ColorConverter.ConvertFromString("#F9FAFC"),
                "A popup opened during theme motion stays sharp/modal and adopts the final live palette");
            Capture(root, "theme-about-light");
            Click("CloseAboutButton"); Wait(200);
            Click("ThemeButton"); Wait(430);
            Require(((FrameworkElement)window.FindName("AppSurface")).Opacity == 1
                && settings.RenderTransform.Value.IsIdentity, "Theme completion leaves no dimming or scaling behind");
            Console.WriteLine("PASS: Rendered multi-frame Light/Dark palette motion on Home/Review/Settings, rapid reversal continuity, exact settling and live modal colors without snapshots");
        }
        finally { window.Close(); }
    }
    private static void Capture(FrameworkElement root, string name)
    {
        if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is not string directory) return;
        Directory.CreateDirectory(directory); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class ReadOnlyFixtureStore : IWindowsPolicyStore
    {
        public WindowsEnvironment Environment => new(26200, "Professional", false, false, "theme-test-account");
        public PolicyValue Read(WindowsAdjustment setting, bool machineOverride = false) => PolicyValue.Missing;
        public void Write(WindowsAdjustment setting, PolicyValue value) => throw new InvalidOperationException("Theme verification must never write a Windows policy.");
    }
    // WPF animation arithmetic can retain sub-byte scRGB differences after the
    // displayed 8-bit color has reached its exact target. Assert what is rendered.
    private static bool SamePixelColor(Color left, Color right) => left.A == right.A && left.R == right.R && left.G == right.G && left.B == right.B;
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (clock.ElapsedMilliseconds >= milliseconds) frame.Continue = false; }; timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
}
