using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hush;

internal static class CleanSelectionChecks
{
    internal static void Run(string fixture)
    {
        string rootPath = Path.Combine(fixture, "clean-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        string first = Path.Combine(rootPath, "selected-synthetic.tmp");
        string second = Path.Combine(rootPath, "unselected-synthetic.tmp");
        foreach (string path in new[] { first, second })
        {
            File.WriteAllText(path, "Unique synthetic clean-selection regression fixture");
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        }
        var engine = new CleanupEngine([new(rootPath, "User temp", CleanupKind.Temp, TimeSpan.FromDays(2))], dataDirectory: Path.Combine(fixture, "clean-selection-audit"));
        var window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "clean-selection-preferences.json"));
        try
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();
            var root = (FrameworkElement)window.Content;
            var primary = (Button)window.FindName("PrimaryButton");
            var homeClean = (Button)window.FindName("HomeCleanButton");
            var global = (CheckBox)window.FindName("SelectAllBox");
            var modal = (FrameworkElement)window.FindName("ModalOverlay");
            void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Layout()
            {
                root.Measure(new Size(920, 820)); root.Arrange(new Rect(0, 0, 920, 820)); root.UpdateLayout();
            }
            void SetChecked(CheckBox box, bool value)
            {
                Require(box.IsEnabled, "Completed-scan checkboxes are enabled for selection");
                for (int attempt = 0; box.IsChecked != value && attempt < 2; attempt++)
                {
                    var peer = new CheckBoxAutomationPeer(box);
                    ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
                    box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    root.UpdateLayout();
                }
                Require(box.IsChecked == value, "Checkbox activation changes its real two-way binding");
            }
            CheckBox FileBox(string path) => Descendants<CheckBox>(root).Single(box => box.DataContext is CleanupItem item && item.Path == path);
            void ExpectClean(int count)
            {
                Require(window.ReviewItems.Count(item => item.Selected) == count && primary.Content.ToString() == "Clean selected"
                    && primary.IsEnabled && primary.Visibility == Visibility.Visible && homeClean.IsEnabled
                    && homeClean.Visibility == Visibility.Visible, "A nonempty selection after completion enables both real Clean entry points");
            }
            void ConfirmThenCancel(Button entry, int count)
            {
                Click(entry);
                Require(modal.Visibility == Visibility.Visible
                    && ((TextBlock)window.FindName("ConfirmSummary")).Text.Contains($"{count} selected"),
                    "Clean confirms the current checkbox selection without rescan or stale counts");
                Click((Button)window.FindName("CancelDeleteButton")); Until(() => modal.Visibility == Visibility.Collapsed);
                Require(File.Exists(first) && File.Exists(second), "Cancel never deletes either fixture");
            }

            var scan = window.ActivateGaugeAsync(); Until(() => scan.IsCompleted); scan.GetAwaiter().GetResult();
            Require(window.CurrentScanState == MainWindow.ScanState.Completed && window.CanSelectCandidates,
                "The positive cleanup test establishes completion through the actual scanner, without forcing UI appearance");
            window.ShowReviewPage(true); Until(() => !window.IsPageTransitioning);
            window.Categories.Single(category => category.Id == "Temp").Expanded = true; Layout(); Wait(340); Layout();
            Require(window.ReviewItems.Count == 2 && window.ReviewItems.All(item => item.Root == rootPath),
                "Only the unique synthetic cleanup root participates in this test");
            foreach (bool light in new[] { false, true })
            {
                if (window.IsLightTheme != light) window.ToggleTheme();
                SetChecked(global, false);
                Require(primary.Content.ToString() == "Clean selected" && !primary.IsEnabled && homeClean.Visibility == Visibility.Visible && !homeClean.IsEnabled
                    && ((Button)window.FindName("RescanButton")).Visibility == Visibility.Visible,
                    "With results but nothing selected, Clean stays on screen dimmed (never disappears) and Scan again sits beside it");
                SetChecked(FileBox(first), true); ExpectClean(1);
                Click((Button)window.FindName("SettingsButton"));
                Click((Button)window.FindName("SettingsBackButton")); Until(() => !window.IsPageTransitioning);
                ExpectClean(1);
                ConfirmThenCancel(primary, 1);
                ConfirmThenCancel(homeClean, 1);
                var categoryBox = Descendants<CheckBox>(root).Single(box => box.DataContext is ReviewCategory);
                SetChecked(categoryBox, true); ExpectClean(2); ConfirmThenCancel(primary, 2);
                SetChecked(global, false); SetChecked(global, true); ExpectClean(2);
                SetChecked(global, false); SetChecked(FileBox(first), true); ExpectClean(1);
                if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string directory)
                {
                    Directory.CreateDirectory(directory); Wait(340); Layout();
                    Require(((FrameworkElement)primary.Template.FindName("OuterGlow", primary)).Clip != null
                        && ((SolidColorBrush)((Border)primary.Template.FindName("Surface", primary)).Background).Color.A <= 16,
                        "The loaded Clean control renders an outer-only glow and a clear interior");
                    var bitmap = new RenderTargetBitmap(920, 820, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"clean-selection-{(light ? "light" : "dark")}.png")); encoder.Save(stream);
                }
            }
            // Exercise the actual confirmation -> engine -> remaining-results flow, on fixtures only.
            Require(window.ReviewItems.All(item => item.Root == rootPath && Path.GetFullPath(item.Path).StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
                "Every candidate is inside the newly created fixture before confirmation is accepted");
            Click(primary); Click((Button)window.FindName("ConfirmDeleteButton"));
            Until(() => !File.Exists(first) && window.ReviewItems.Count == 1 && window.CanSelectCandidates);
            Until(() => !window.IsPageTransitioning); Layout();
            Require(File.Exists(second) && window.ReviewItems[0].Path == second && !window.ReviewItems[0].Selected,
                "Confirmed Clean removes only the selected fixture and preserves the unselected fixture");
            // 0.9 audit (M2): the result of a clean must stay readable; it used to be replaced at once by "Review remaining files."
            string cleanStatus = ((TextBlock)window.FindName("StatusTitle")).Text;
            Require(cleanStatus.StartsWith("Cleanup complete") && cleanStatus.Contains("freed") && cleanStatus.Contains("1 cleaned") && window.LastCleanResult is { Deleted: 1 },
                $"After a clean the status line keeps the result (what was freed and cleaned) instead of being replaced ({cleanStatus})");
            SetChecked(FileBox(second), true); ExpectClean(1);
            Click(homeClean);
            Require(modal.Visibility == Visibility.Visible && ((TextBlock)window.FindName("ConfirmSummary")).Text.Contains("1 selected"),
                "The remaining result can be selected and confirmed for Clean again");
            Click((Button)window.FindName("CancelDeleteButton")); Until(() => modal.Visibility == Visibility.Collapsed);
            Console.WriteLine("PASS: Real checkbox file/category/global selection -> both Clean confirmations -> cancel, both themes, fixture-only selected cleanup and remaining-file reselection");
        }
        finally { window.Close(); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Until(Func<bool> complete)
    {
        var frame = new DispatcherFrame(); var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || clock.ElapsedMilliseconds >= 6000) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Require(complete(), "Clean selection flow did not settle within six seconds");
    }
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false; timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
