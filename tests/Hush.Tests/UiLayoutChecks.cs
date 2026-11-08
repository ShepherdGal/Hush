using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hush;

internal static class UiLayoutChecks
{
    internal static void Run(bool forceFailure = false, bool buttonsOnly = false, bool navigationOnly = false, bool cleanSelectionOnly = false, bool updatesOnly = false, bool windowsSettingsOnly = false, bool themeMotionOnly = false, bool setupOnly = false, bool smartBoostOnly = false)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            App? app = null;
            MainWindow? window = null;
            string testsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            string fixture = Path.Combine(testsRoot, ".ui-fixture-" + Guid.NewGuid().ToString("N"));
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                Directory.CreateDirectory(fixture);
                string old = Path.Combine(fixture, "old-synthetic.tmp");
                File.WriteAllText(old, "synthetic UI scan fixture");
                File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-10));
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
                var engine = new CleanupEngine([new(fixture, "User temp", CleanupKind.Temp, TimeSpan.FromDays(2))], dataDirectory: Path.Combine(fixture, "audit"));
                app = new App(); app.InitializeComponent();
                window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "preferences.json"));
                Require(!forceFailure, "Intentional UI test failure to verify graceful reporting and shutdown.");
                if (setupOnly) { SetupChecks.Run(fixture); return; }
                if (themeMotionOnly) { ThemeMotionChecks.Run(engine, fixture); return; }
                if (windowsSettingsOnly) { WindowsSettingsChecks.Run(engine, fixture); return; }
                if (smartBoostOnly) { SmartBoostChecks.RunUi(fixture); return; }
                if (updatesOnly) { UpdateFlowChecks.Run(engine, fixture); return; }
                if (cleanSelectionOnly) { CleanSelectionChecks.Run(fixture); return; }
                if (buttonsOnly) { ButtonVisualChecks.Run(window); return; }
                if (navigationOnly) { NavigationChecks.Run(window, (FrameworkElement)window.Content, fixture); return; }
                var root = (FrameworkElement)window.Content;
                var circle = (Button)window.FindName("ScanCircleButton");
                var results = (Button)window.FindName("CompletionLinkButton");
                var primary = (Button)window.FindName("PrimaryButton");
                var scanPage = (FrameworkElement)window.FindName("ScanPage");
                var reviewPage = (FrameworkElement)window.FindName("ReviewPage");
                Motion.Ensure(circle);
                Require(circle.Template.Triggers.Count == 0 && circle.Opacity == 1, "Gauge has no focus dimming");
                Require(circle.RenderTransform.Value.IsIdentity, "Gauge hover does not scale its content");
                Require(((TextBlock)window.FindName("Headline")).Text == "Make more room to breathe", "Main heading uses the requested wording");
                Require(window.FindName("HeroDescription") == null, "Home has no subtitle");
                Require(window.GaugePercent == 0 && ((TextBlock)window.FindName("GaugeValue")).Text == "0%"
                    && ((System.Windows.Shapes.Path)window.FindName("GaugeArc")).Data.IsEmpty(), "Initial gauge is truly empty at zero percent");
                Require(window.FindName("WindowsCleanupButton") == null && window.FindName("LandingWindowsButton") == null,
                    "Separate cleanup and utility controls are removed");
                Require(results.Visibility == Visibility.Collapsed, "Results action is hidden before scanning");
                Require(window.AllowsTransparency && System.Windows.Shell.WindowChrome.GetWindowChrome(window) == null, "No native frame can leave bright corners");
                foreach (var size in new[] { new Size(920, 820), new Size(800, 740) })
                {
                    Layout(root, size);
                    var before = Bounds(circle, root);
                    Require(Math.Abs(before.Left + before.Width / 2 - size.Width / 2) < 1 && before.Width == 320, "Gauge stays centered at both window sizes");
                    ((TextBlock)window.FindName("StatusTitle")).Text = "Scan complete. Take a look.";
                    results.Visibility = Visibility.Visible;
                    ((TextBlock)window.FindName("StatusTitle")).Visibility = Visibility.Collapsed;
                    Layout(root, size);
                    Require(Bounds(circle, root) == before, "Completion message cannot shift the gauge");
                    results.Visibility = Visibility.Collapsed;
                    ((TextBlock)window.FindName("StatusTitle")).Visibility = Visibility.Visible;
                    var bitmap = Render(root, size);
                    var pixels = new byte[(int)size.Width * (int)size.Height * 4];
                    bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
                    Require(pixels[3] == 0 && pixels[((int)size.Width - 1) * 4 + 3] == 0
                        && pixels[^1] == 0 && pixels[pixels.Length - (int)size.Width * 4 + 3] == 0, "All four window corners are transparent");
                }
                window.GaugePercent = 25;
                var quarter = PathGeometry.CreateFromGeometry(((System.Windows.Shapes.Path)window.FindName("GaugeArc")).Data);
                Require(quarter.Figures[0].StartPoint == new Point(79, 16) && Math.Abs(quarter.Bounds.Right - 142) < 0.1, "Arc starts at twelve o'clock and fills clockwise");
                window.ResetGauge(); window.AnimateGaugeTo(50);
                PumpUntil(() => window.GaugePercent >= 5);
                window.FreezeGauge();
                double frozen = window.GaugePercent;
                Wait(80);
                Require(window.GaugePercent == frozen && ((TextBlock)window.FindName("GaugeValue")).Text.EndsWith('%'), "Stop freezes the visible percentage");
                var interrupted = window.CompleteGauge(); window.FreezeGauge();
                PumpUntil(() => interrupted.IsCompleted); interrupted.GetAwaiter().GetResult();

                // Exercise the actual gauge route with a synthetic target, never the user's profile.
                var firstScan = window.ActivateGaugeAsync();
                var liveCounter = (RollingFileCount)window.FindName("LiveFileCounter");
                var statusDetail = (TextBlock)window.FindName("StatusDetail");
                Require(liveCounter.Visibility == Visibility.Visible && liveCounter.Count == 0
                    && !((ScanActivity)window.FindName("FileScanActivity")).IsRunning,
                    "Scan replaces the document animation with a genuine zero-start live counter");
                Require(liveCounter.FontFamily.Equals(statusDetail.FontFamily) && liveCounter.FontSize == statusDetail.FontSize
                    && liveCounter.FontWeight == statusDetail.FontWeight && liveCounter.Foreground.ToString() == statusDetail.Foreground.ToString(),
                    "Live count preserves the previous scan-detail font, size, weight and muted color");
                PumpUntil(() => liveCounter.Count == 1);
                Require(!firstScan.IsCompleted && liveCounter.Summary == "1 file found" && statusDetail.Visibility == Visibility.Hidden,
                    "Eligible file count appears before completion while byte totals remain hidden");
                PumpUntil(() => firstScan.IsCompleted); firstScan.GetAwaiter().GetResult();
                Require(window.GaugePercent == 100 && scanPage.Visibility == Visibility.Visible && results.Visibility == Visibility.Visible,
                    "Completed scan remains on Home with one results action");
                Require(window.ReviewItems.Count == 1 && File.Exists(old), "A scan is read-only and returns the fixture");
                Require(((ItemsControl)window.FindName("CategoriesList")).Items.Count == 1,
                    "A temp-only scan renders one detected category, not seven empty sections");
                Require(((TextBlock)window.FindName("StatusDetail")).Text == $"1 file found · {Sizes.Format(window.ReviewItems[0].Bytes)}",
                    "Home reports the real file count and total bytes");
                Require(liveCounter.Count == 1 && !liveCounter.IsAnimating && liveCounter.Visibility == Visibility.Hidden,
                    "Live count settles exactly to the result and hides when scanning ends");
                liveCounter.SetCount(99, false);
                liveCounter.SetCount(100); Wait(30);
                liveCounter.SetCount(143); Wait(30);
                liveCounter.SetCount(212);
                liveCounter.SetCount(180);
                Require(liveCounter.Count == 212 && liveCounter.LastDurationMilliseconds <= 1800,
                    "Rapid discoveries coalesce to the newest count without regression or a long animation queue");
                liveCounter.Settle();
                Require(!liveCounter.IsAnimating && liveCounter.Summary == "212 files found", "Stopping settles the actual latest count immediately");
                VerifyOdometerMotion(liveCounter);
                Wait(1900);
                var finishGlow = (DropShadowEffect)window.FindName("GaugeFinishGlow");
                Require(Math.Abs(finishGlow.Opacity - 0.5) < 0.01 && finishGlow.BlurRadius >= 16,
                    "Completion bloom settles into a persistent stronger aqua finish");
                Require(finishGlow.Color == ((SolidColorBrush)app.Resources["TealBrush"]).Color,
                    "Gauge stroke and completion glow share the primary aqua accent");
                var homeClean = (Button)window.FindName("HomeCleanButton");
                Require(homeClean.Visibility == Visibility.Visible && homeClean.IsEnabled, "Home offers Clean after scanning");
                homeClean.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(330);
                var modal = (FrameworkElement)window.FindName("ModalOverlay");
                var surface = (FrameworkElement)window.FindName("AppSurface");
                var blur = (BlurEffect)window.FindName("BackgroundBlur");
                Require(modal.Visibility == Visibility.Visible && !surface.IsEnabled && Math.Abs(blur.Radius - 7) < 0.01,
                    "Home Clean opens confirmation with the entire app disabled and blurred");
                Require(((TextBlock)window.FindName("ConfirmDetail")).Text.Contains("permanently deleted"), "Confirmation explains permanent deletion");
                foreach (string name in new[] { "CancelDeleteButton", "ConfirmDeleteButton" })
                {
                    var action = (Button)window.FindName(name);
                    var label = Descendants<TextBlock>(action).Single();
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Require(label.DesiredSize.Height <= action.Height - action.Padding.Top - action.Padding.Bottom,
                        "Confirmation button labels fit their 44 DIP height without clipping");
                }
                ((Button)window.FindName("CancelDeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => modal.Visibility == Visibility.Collapsed);
                Require(surface.IsEnabled && blur.Radius < 0.01 && File.Exists(old), "Cancel restores the app without deleting anything");
                ((Button)window.FindName("AboutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(350);
                var about = (FrameworkElement)window.FindName("AboutOverlay");
                Require(about.Visibility == Visibility.Visible && !surface.IsEnabled && blur.Radius > 6.9
                    && ((TextBlock)window.FindName("AboutVersion")).Text.Contains(UpdateService.DisplayVersion), "Title opens the versioned About popup with shared blur");
                var aboutCard = (Border)window.FindName("AboutCard");
                var updatesBody = (AccordionBody)window.FindName("AboutUpdatesBody");
                Layout(root, new Size(800, 740));
                Require(aboutCard.ActualHeight < 350 && !updatesBody.Expanded && !updatesBody.IsEnabled && updatesBody.Visibility == Visibility.Collapsed,
                    "About opens as a compact readable summary with hidden, disabled update content");
                ((Button)window.FindName("AboutUpdatesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(360); root.UpdateLayout();
                Layout(root, new Size(800, 740));
                Require(updatesBody.Expanded && ((Button)window.FindName("CheckUpdateButton")).IsEnabled && window.FindName("ConnectGitHubButton") == null && window.FindName("UpdateToken") == null
                    && new Rect(root.RenderSize).Contains(Bounds(aboutCard, root)), "Updates are available on demand and fit the minimum window");
                ((Button)window.FindName("CloseAboutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => about.Visibility == Visibility.Collapsed);
                Require(surface.IsEnabled, "About close restores input");
                ((Button)window.FindName("AboutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(350);
                Layout(root, new Size(800, 740));
                Require(!updatesBody.Expanded && aboutCard.ActualHeight < 350,
                    "Reopening About restores the compact summary");
                ((Button)window.FindName("CloseAboutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => about.Visibility == Visibility.Collapsed);
                var restarted = window.ActivateGaugeAsync();
                Require(liveCounter.Count == 0, "Every new scan resets the live count rather than retaining old totals");
                Require(!restarted.IsCompleted && scanPage.Visibility == Visibility.Visible && reviewPage.Visibility == Visibility.Collapsed,
                    "Clicking a completed gauge restarts scanning instead of navigating");
                PumpUntil(() => window.GaugePercent >= 3);
                window.ActivateGaugeAsync().GetAwaiter().GetResult();
                PumpUntil(() => restarted.IsCompleted); restarted.GetAwaiter().GetResult();
                Require(window.GaugePercent < 100 && File.Exists(old), "Gauge can stop a restarted scan without deleting files");
                Require(window.CurrentScanState == MainWindow.ScanState.Stopped && homeClean.Visibility != Visibility.Visible
                    && primary.Content.ToString() == "Scan again" && ((Button)window.FindName("RescanButton")).Visibility == Visibility.Collapsed,
                    "Cancellation during the final scan animation hides Clean and leaves one Scan again action");
                homeClean.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(modal.Visibility == Visibility.Collapsed, "A stale Home Clean event cannot open confirmation after cancellation");
                results.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(reviewPage.Visibility == Visibility.Visible, "Only the results action opens Review Files");

                var completedRestart = window.ActivateGaugeAsync();
                Require(homeClean.Visibility != Visibility.Visible, "Restart immediately revokes the previous scan's cleanup actions");
                PumpUntil(() => completedRestart.IsCompleted); completedRestart.GetAwaiter().GetResult();
                results.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.LoadReviewItems(PreviewItems());
                Require(window.Categories.Count == ReviewCategory.Definitions.Length && window.Categories.Sum(category => category.Files.Count) == window.ReviewItems.Count,
                    "Every category partitions all candidates without duplicates");
                Require(window.Categories.Single(category => category.Id == "Registry").Files.All(item => !item.Selected), "Registry leftovers remain opt-in");
                var temp = window.Categories.Single(category => category.Id == "Temp");
                Layout(root, new Size(920, 820)); Wait(300);
                var categorySurface = (Border)window.FindName("CategorySurface");
                var tempSection = Descendants<Expander>(root).Single(section => section.DataContext == temp);
                var tempOutline = (Border)tempSection.Template.FindName("SelectionOutline", tempSection);
                var selectedBounds = Bounds(tempSection, root);
                Require(tempOutline.BorderBrush.ToString() == ((Brush)new BrushConverter().ConvertFromString(temp.TextAccent)!).ToString(),
                    "Checked category outline matches its checkbox safety accent");
                temp.Selected = false;
                root.UpdateLayout();
                // Observe a rendered frame rather than assuming one fixed timer tick lands
                // inside the transition when the host is compiling or otherwise busy.
                PumpUntil(() => ((SolidColorBrush)tempOutline.BorderBrush).Color.A is > 0 and < 255);
                var fadingSelection = Render(root, new Size(920, 820));
                var fadingPixels = new byte[920 * 820 * 4]; fadingSelection.CopyPixels(fadingPixels, 920 * 4, 0);
                PumpUntil(() => ((SolidColorBrush)tempOutline.BorderBrush).Color.A == 0);
                var settledSelection = Render(root, new Size(920, 820));
                var settledPixels = new byte[fadingPixels.Length]; settledSelection.CopyPixels(settledPixels, 920 * 4, 0);
                Require(!fadingPixels.SequenceEqual(settledPixels), "Selection transition changes rendered pixels before settling");
                Require(((SolidColorBrush)tempOutline.BorderBrush).Color.A == 0 && Bounds(tempSection, root) == selectedBounds,
                    "Unchecking removes the category outline without shifting the shared surface");
                Require(temp.Files.All(file => !file.Selected) && window.Categories.Single(category => category.Id == "Cache").Selected == true,
                    "Category checkbox changes only its own files");
                temp.Selected = true; temp.Files[0].Selected = false; Wait(300);
                Require(temp.Selected == null, "Partial category selection has an indeterminate checkbox state");
                root.UpdateLayout();
                Require(((SolidColorBrush)tempOutline.BorderBrush).Color.A == 0, "Mixed selection retains the dash without claiming a fully checked outline");
                temp.Files[0].Selected = true;
                temp.Expanded = true;
                Wait(360);
                foreach (var size in new[] { new Size(920, 820), new Size(800, 740), new Size(1100, 900), new Size(800, 740) })
                {
                    Layout(root, size); Wait(30); root.UpdateLayout();
                    foreach (string name in new[] { "SelectAllBox", "CollapseAllButton", "ReviewScroll" })
                    {
                        var control = (FrameworkElement)window.FindName(name);
                        Require(Parent<Border>(control) == categorySurface && new Rect(categorySurface.RenderSize).Contains(Bounds(control, categorySurface)),
                            "Category controls and rows stay inside one continuous surface at every size");
                    }
                    var list = Descendants<ListBox>(root).First(list => list.DataContext == temp);
                    var row = Descendants<Border>(list).First(border => border.ToolTip is string && border.DataContext is CleanupItem);
                    var viewport = Parent<ScrollContentPresenter>(row)!;
                    Require(row.ActualWidth <= viewport.ActualWidth - 5, "Neutral file rows stay inside the scroll viewport");
                    Require(row.Background.ToString() == app.Resources["RaisedBrush"].ToString() && row.Effect == null,
                        "Selectable files stay neutral and have no glow");
                    var item = (CleanupItem)row.DataContext;
                    var filename = Descendants<TextBlock>(row).Single(text => text.Text == item.Name);
                    Require(filename.Foreground.ToString() == ((Brush)new BrushConverter().ConvertFromString(item.SelectionAccent)!).ToString(), "Checked filename uses its safety accent");
                    item.Selected = false; root.UpdateLayout(); Wait(300);
                    Require(filename.Foreground.ToString() == app.Resources["TextBrush"].ToString(), "Unchecked filename returns to normal text");
                    item.Selected = true; Wait(300);
                    foreach (var text in Descendants<TextBlock>(row))
                    {
                        Require(text.Name == "Tick" || text.FontSize >= 14, "Supporting file text uses the shared readable size");
                        Require(Bounds(text, row).Right <= row.ActualWidth + 1, "Filename, folder and size columns do not clip");
                    }
                    foreach (string name in new[] { "PrimaryButton", "ReviewHint", "SelectionSummary", "ResultCount", "BackButton", "CollapseAllButton" })
                    {
                        var control = (FrameworkElement)window.FindName(name);
                        var bound = Bounds(control, root);
                        Require(bound.Right <= size.Width + 1 && bound.Bottom <= size.Height + 1, "Review actions stay within the window");
                    }
                }
                var cache = window.Categories.Single(category => category.Id == "Cache");
                cache.Expanded = true; Wait(360);
                Require(cache.Expanded && temp.Expanded, "Sections can remain open independently");
                var toggle = (Button)window.FindName("CollapseAllButton");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(360);
                Require(window.Categories.Where(category => category.HasFiles).All(category => category.Expanded) && ((TextBlock)window.FindName("SectionToggleLabel")).Text == "Collapse all",
                    "Expand all opens every section and updates its label");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(360);
                Require(window.Categories.Where(category => category.HasFiles).All(category => !category.Expanded) && ((TextBlock)window.FindName("SectionToggleLabel")).Text == "Expand all",
                    "Collapse all closes every section and updates its label");
                Layout(root, new Size(920, 820));
                var reviewScroll = (ScrollViewer)window.FindName("ReviewScroll"); reviewScroll.ScrollToBottom(); root.UpdateLayout();
                var lastSection = Descendants<Expander>(root).Last();
                Require(Bounds(lastSection, reviewScroll).Bottom <= reviewScroll.ActualHeight + 1, "Additional categories remain reachable in the review scroller");
                Require(((Button)window.FindName("BackButton")).Height == toggle.Height, "Back and Expand All share the same button height");
                Require(((Button)window.FindName("BackButton")).FontSize == 16 && toggle.FontSize == 16
                    && ((Button)window.FindName("RescanButton")).FontSize == 16
                    && ((Button)window.FindName("RescanButton")).Content.ToString() == "Scan again",
                    "Navigation actions share larger type and Scan again has a clear text label");
                foreach (var section in Descendants<Expander>(root))
                    Require(section.Effect == null && !Motion.GetEnabled(section), "Category sections have no glow");
                foreach (var header in Descendants<ToggleButton>(root))
                    Require(header.Effect == null && !Motion.GetEnabled(header), "Track-style category headers have no button glow");
                window.ApplyPrimaryAppearance(true);
                Require(primary.Foreground.ToString() == app.Resources["CleanTextBrush"].ToString() && primary.BorderThickness.Left == 1, "Clean actions retain their outlined styling with the Hush accent");
                Require(((SolidColorBrush)primary.Background).Color.A <= 16, "Clean button surfaces stay nearly transparent");
                var back = (Button)window.FindName("BackButton");
                Require(((SolidColorBrush)back.Background).Color.A == 0 && back.Foreground.ToString() == app.Resources["MutedBrush"].ToString(), "Back restores Hush's transparent muted styling");
                var rescan = (Button)window.FindName("RescanButton");
                Require(((SolidColorBrush)rescan.Background).Color.A == 0 && rescan.Foreground.ToString() == app.Resources["MutedBrush"].ToString()
                    && Motion.GetGlow(rescan) == 0, "Bottom Scan again remains neutral and transparent without accent glow");
                foreach (var button in Descendants<Button>(root))
                { Motion.Ensure(button); Require(button.RenderTransform.Value.IsIdentity, "Buttons never enlarge on hover or press"); }
                Wait(300);
                var underlight = (Border)primary.Template.FindName("GlowUnderlay", primary);
                var edge = (DropShadowEffect)((Border)primary.Template.FindName("GlowEdge", primary)).Effect;
                var halo = (DropShadowEffect)((Border)primary.Template.FindName("GlowHalo", primary)).Effect;
                Require(primary.Effect == null && underlight.Visibility == Visibility.Collapsed && edge.ShadowDepth == 0 && halo.ShadowDepth == 0
                    && edge.BlurRadius == 4 && halo.BlurRadius == 12 && edge.Opacity > halo.Opacity && halo.Opacity > 0,
                    "Clean glow is centered on separate outline layers with a crisp edge and soft falloff, leaving labels sharp");
                window.ApplyPrimaryAppearance(false); Wait(300);
                Require(((Grid)primary.Template.FindName("OuterGlow", primary)).Visibility == Visibility.Collapsed && primary.BorderThickness.Left == 0,
                    "Scan action restores neutral styling without the Clean outline or glow");
                Require(((SolidColorBrush)primary.Background).Color.A == 0 && primary.Foreground.ToString() == app.Resources["MutedBrush"].ToString()
                    && Motion.GetGlow(primary) == 0, "Scan again primary fallback uses the same neutral treatment");
                window.ApplyPrimaryAppearance(true); Wait(300);
                primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(330);
                Require(modal.Visibility == Visibility.Visible && ((TextBlock)window.FindName("ConfirmSummary")).Text.Contains("5 selected items"),
                    "Review cleanup confirms selected items only, excluding unselected registry entries");
                ((Button)window.FindName("CancelDeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => modal.Visibility == Visibility.Collapsed);
                window.ToggleTheme(); window.ToggleTheme(); window.ToggleTheme();
                Wait(300);
                Require(window.FindName("PageSnapshot") == null && window.FindName("ThemeSnapshot") == null,
                    "Navigation and themes never retain a screenshot of the previous page");
                Require(primary.Foreground.ToString() == app.Resources["CleanTextBrush"].ToString()
                    && back.Foreground.ToString() == app.Resources["MutedBrush"].ToString(), "Clean and Back accents follow the active theme");
                Require(tempOutline.BorderBrush.ToString() == ((Brush)new BrushConverter().ConvertFromString(temp.TextAccent)!).ToString(),
                    "Selected category outline follows the theme's readable safety accent");
                foreach (var category in window.Categories.Where(category => category.HasFiles))
                {
                    var section = Descendants<Expander>(root).Single(element => element.DataContext == category);
                    var categoryBox = Descendants<CheckBox>(section).First();
                    var safetyLine = Descendants<Border>(section).Single(border => border.Width == 6 && border.CornerRadius == new CornerRadius(3));
                    Require(safetyLine.Background.ToString() == ((Brush)new BrushConverter().ConvertFromString((string)categoryBox.Tag)!).ToString()
                        && safetyLine.Background.ToString() == ((Brush)new BrushConverter().ConvertFromString(category.TextAccent)!).ToString(),
                        "Safety line and checkbox match exactly in each theme");
                }
                foreach (var border in Descendants<Border>(root))
                {
                    if (Parent<CheckBox>(border) == null && !HasInvisibleAncestor(border))
                        Require(border.BorderThickness == new Thickness() || Parent<Button>(border)?.BorderThickness.Left == 1
                            || (border.Name == "SelectionOutline" && Parent<Expander>(border) != null && border.BorderThickness == new Thickness(1)),
                            "Only checkbox, Clean and category-selection outlines have borders");
                }
                var selection = window.ReviewItems.Select(item => item.Selected).ToArray();
                window.ShowReviewPage(false); window.ShowReviewPage(true);
                PumpUntil(() => !window.IsPageTransitioning);
                Require(window.ReviewItems.Select(item => item.Selected).SequenceEqual(selection), "Navigation preserves file selection");
                if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string directory)
                    ExportPreviews(window, root, directory);
                ButtonVisualChecks.Run(window);
                NavigationChecks.Run(window, root, fixture);
                VerifyScanResultStates(engine);
                VerifyScanRipples(engine);
                Console.WriteLine("PASS: The scan cue (soft ripples drifting out from the gauge) is off until a scan runs, moves while it runs and fades out afterwards");
                UiSizeChecks.RunUi(engine, fixture, (ok, message) => Require(ok, message));
                Console.WriteLine("PASS: Interface size (Smart, 75%, 100%, 120%) rescales the real window, is remembered, and always fits the screen");
                Console.WriteLine("PASS: Empty gauge, real live odometer count, safety selection, shared motion, About/update popup, blurred confirmation and responsive layout verified");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { window?.Close(); app?.Shutdown(); } catch (Exception ex) { failure ??= ex; }
                if (Directory.Exists(fixture) && Path.GetFullPath(fixture).StartsWith(testsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(fixture).StartsWith(".ui-fixture-"))
                { foreach (string file in Directory.EnumerateFiles(fixture)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(fixture, true); }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(50))) throw new TimeoutException("The UI verification did not finish within 50 seconds.");
        if (failure != null) throw new Exception("UI layout verification failed", failure);
    }

    // The ripples that drift out from the gauge during a scan: off before, fading in and moving while the scan is held open, off again afterwards.
    private static void VerifyScanRipples(CleanupEngine engine)
    {
        using var release = new ManualResetEventSlim(false);
        var window = new MainWindow(engine, (progress, token) =>
        {
            progress?.Report(new ScanProgress("User temp", 40, 12, 90_000, 0.34));
            release.Wait(10_000);
            return new(PreviewItems().Take(1).ToList(), 0, 0, false);
        });
        try
        {
            window.Show();
            var orbit = (ScanRipples)window.FindName("ScanRipplesCue");
            var root = (FrameworkElement)window.Content;
            Require(!orbit.IsRunning && !orbit.IsRippling && orbit.Opacity == 0, "The scan cue is off until a scan runs");
            MainWindow.StillLookingAfterMs = 600;
            var scanning = window.ActivateGaugeAsync();
            PumpUntil(() => orbit.IsRunning && orbit.IsRippling && orbit.Opacity > 0.95);
            var title = (TextBlock)window.FindName("StatusTitle");
            PumpUntil(() => title.Text.StartsWith("Still looking in"));
            Require(title.Text == "Still looking in User temp…", "A scan that stays in one location names it on the visible status line");
            double first = orbit.Phase; Wait(300);
            Require(Math.Abs(orbit.Phase - first) > 0.002, "The ripples really move while the scan runs");
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string directory)
            {
                foreach (bool light in new[] { false, true })
                {
                    if (window.IsLightTheme != light) window.ToggleTheme();
                    PumpUntil(() => !window.IsPageTransitioning); Wait(400);
                    Layout(root, new Size(920, 820)); Save(root, new Size(920, 820), Path.Combine(directory, $"scan-ripples-{(light ? "light" : "dark")}.png"));
                }
            }
            release.Set();
            PumpUntil(() => scanning.IsCompleted); scanning.GetAwaiter().GetResult();
            PumpUntil(() => !orbit.IsRippling && orbit.Opacity < 0.01);
            Require(!orbit.IsRunning, "The ripples fade out and stop when the scan ends");
        }
        finally { MainWindow.StillLookingAfterMs = 2500; release.Set(); window.Close(); }
    }

    private static void VerifyScanResultStates(CleanupEngine engine)
    {
        Func<IProgress<ScanProgress>?, CancellationToken, ScanResult> response = (_, _) =>
            new(PreviewItems().Take(1).ToList(), 0, 0, true);
        var window = new MainWindow(engine, (progress, token) => response(progress, token));
        try
        {
            var root = (FrameworkElement)window.Content;
            var primary = (Button)window.FindName("PrimaryButton");
            var rescan = (Button)window.FindName("RescanButton");
            var homeClean = (Button)window.FindName("HomeCleanButton");
            var results = (Button)window.FindName("CompletionLinkButton");
            var categories = (ItemsControl)window.FindName("CategoriesList");
            var empty = (FrameworkElement)window.FindName("ReviewEmptyState");
            var modal = (FrameworkElement)window.FindName("ModalOverlay");
            void Finish(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
            void NoCleaning()
            {
                if (window.CurrentScanState != MainWindow.ScanState.Completed)
                {
                    Layout(root, new Size(920, 820));
                    Require(!window.CanSelectCandidates && !((CheckBox)window.FindName("SelectAllBox")).IsEnabled
                        && window.ReviewItems.All(item => !item.Selected)
                        && Descendants<CheckBox>(root).Where(box => box.DataContext is CleanupItem or ReviewCategory).All(box => !box.IsEnabled),
                        "Incomplete results are read-only in their actual controls and never claim selected files");
                }
                Require(homeClean.Visibility != Visibility.Visible && !homeClean.IsEnabled
                    && primary.Content.ToString() == "Scan again" && primary.BorderThickness.Left == 0
                    && rescan.Visibility == Visibility.Collapsed, "Incomplete/empty results expose one neutral rescan and no cleaning actions");
                Require(primary.MinWidth == 0 && primary.Padding == rescan.Padding
                    && primary.Height == rescan.Height && primary.FontSize == rescan.FontSize,
                    "Scan again keeps identical natural sizing and typography in every state");
                homeClean.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(modal.Visibility == Visibility.Collapsed, "Cleanup is guarded even if a hidden action receives a stale event");
            }
            void Capture(string state)
            {
                if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is not string directory) return;
                window.ShowReviewPage(true);
                foreach (bool light in new[] { false, true })
                {
                    if (window.IsLightTheme != light) window.ToggleTheme();
                    PumpUntil(() => !window.IsPageTransitioning);
                    NoCleaning();
                    Layout(root, new Size(800, 740)); Wait(300);
                    Save(root, new Size(800, 740), Path.Combine(directory, $"review-{state}-{(light ? "light" : "dark")}.png"));
                }
            }
            Finish(window.ActivateGaugeAsync());
            Require(window.CurrentScanState == MainWindow.ScanState.Stopped && window.GaugePercent < 100
                && categories.Items.Count == 1, "Partial scans retain only detected categories without completing the gauge");
            NoCleaning();
            var expand = (Button)window.FindName("CollapseAllButton");
            expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.Categories.Count(category => category.Expanded) == 1
                && ((TextBlock)window.FindName("SectionToggleLabel")).Text == "Collapse all",
                "Expand all affects only the detected category and updates its label");
            expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Capture("partial");
            response = (_, _) => new(PreviewItems().Take(1).ToList(), 0, 0, true);
            Finish(window.ActivateGaugeAsync());
            Require(window.CurrentScanState == MainWindow.ScanState.Stopped && categories.Items.Count == 1
                && window.GaugePercent < 100, "Worker-returned stopped batches remain partial and reviewable");
            NoCleaning();
            response = (_, _) => throw new InvalidOperationException("Synthetic scan failure");
            primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => window.CurrentScanState == MainWindow.ScanState.Failed);
            Require(categories.Items.Count == 0 && window.ReviewItems.Count == 0, "A failed rescan clears the previous batch");
            NoCleaning();
            Require(((TextBlock)window.FindName("EmptyStateTitle")).Text == "The scan couldn't finish.",
                "Failed scans never claim that everything is clean");
            response = (_, token) => { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); throw new Exception("Unreachable"); };
            var canceled = window.ActivateGaugeAsync();
            window.GaugePercent = 100; // Boundary: the ring may finish before the live counter.
            window.ActivateGaugeAsync().GetAwaiter().GetResult();
            Finish(canceled);
            Require(window.CurrentScanState == MainWindow.ScanState.Stopped && window.GaugePercent < 100, "A canceled worker is stopped rather than failed and cannot retain a completed ring");
            NoCleaning();
            response = (_, _) => new([], 0, 0, false);
            Finish(window.ActivateGaugeAsync());
            results.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.CurrentScanState == MainWindow.ScanState.Completed && window.GaugePercent == 100
                && empty.Visibility == Visibility.Visible && categories.Items.Count == 0
                && ((FrameworkElement)window.FindName("CategorySurface")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)window.FindName("SelectionDetails")).Visibility == Visibility.Collapsed
                && ((TextBlock)window.FindName("EmptyStateTitle")).Text == "Everything looks clean.",
                "Completed empty scans show a spacious empty state without selection/category controls");
            NoCleaning(); Capture("empty");
            foreach (var size in new[] { new Size(920, 820), new Size(1100, 900), new Size(800, 740) })
            {
                Layout(root, size);
                var center = Bounds(empty, root);
                Require(Math.Abs(center.Left + center.Width / 2 - size.Width / 2) <= 1
                    && Bounds(primary, root).Right <= size.Width, "Empty state stays centered and its sole rescan action fits after resizing");
            }
            response = (_, _) => new([], 1, 1, false);
            Finish(window.ActivateGaugeAsync());
            Require(((TextBlock)window.FindName("EmptyStateTitle")).Text == "No cleanup items found.",
                "Skipped locations do not produce an unqualified clean-state claim");
            NoCleaning();
            response = (_, _) => new(PreviewItems().Where(item => item.Kind == CleanupKind.Registry).ToList(), 0, 0, false);
            Finish(window.ActivateGaugeAsync());
            Require(categories.Items.Count == 1 && empty.Visibility == Visibility.Collapsed && window.ReviewItems.Count == 1,
                "Registry-only results remain reviewable rather than appearing empty");
            NoCleaning();
            window.ReviewItems[0].Selected = true;
            Require(primary.Content.ToString() == "Clean selected" && rescan.Visibility == Visibility.Visible && homeClean.IsEnabled,
                "Only a successful scan with an explicit selection exposes Clean");
            window.LoadReviewItems([]);
            Require(empty.Visibility == Visibility.Visible && categories.Items.Count == 0,
                "Removing the last remaining result restores the empty state");
            NoCleaning();
            Console.WriteLine("PASS: Completed, canceled, partial, failed, empty, skipped-location and registry-only scan states; detected categories; one rescan action");
        }
        finally { window.Close(); }
    }

    private static CleanupItem[] PreviewItems() =>
    [
        new() { Path = @"C:\Users\Example\AppData\Local\Temp\a-very-long-filename-that-must-wrap-without-overlapping-the-size-column.tmp", Root = @"C:\Temp", Group = "User temp", Bytes = 123456789 },
        new() { Path = @"C:\Temp\neutral-selection.tmp", Root = @"C:\Temp", Group = "User temp", Bytes = 1000 },
        new() { Path = @"C:\Browser\Cache\rebuildable-cache.bin", Root = @"C:\Browser", Group = "Brave-Browser cache", Kind = CleanupKind.Cache, Bytes = 16777216 },
        new() { Path = @"HKCU\Software\Example\RemovedApp.exe", Root = "Fixture", Group = "App leftovers", Kind = CleanupKind.Registry, Selected = false },
        new() { Path = @"C:\Windows\Temp\old-workspace.tmp", Root = @"C:\Windows\Temp", Group = "Windows temp", Bytes = 345678 },
        new() { Path = @"C:\Shader\cache.bin", Root = @"C:\Shader", Group = "DirectX cache", Kind = CleanupKind.Cache, Bytes = 2097152 },
        new() { Path = @"C:\CrashDumps\old-report.dmp", Root = @"C:\CrashDumps", Group = "Crash reports", Bytes = 123456, Selected = false },
        new() { Path = @"C:\Diagnostics\old-report.wer", Root = @"C:\Diagnostics", Group = "Windows diagnostics", Kind = CleanupKind.Junk, Bytes = 4000, Selected = false },
        new() { Path = @"C:\Shortcuts\removed-app.lnk", Root = @"C:\Shortcuts", Group = "Broken shortcuts", Kind = CleanupKind.Shortcut, Bytes = 600, Selected = false },
        new() { Path = @"C:\Explorer\thumbcache_256.db", Root = @"C:\Explorer", Group = "Thumbnail cache", Kind = CleanupKind.Cache, CategoryId = "Thumbnails", Bytes = 5242880, Selected = false },
        new() { Path = @"C:\Windows\SoftwareDistribution\Download\old-update.cab", Root = @"C:\Windows\SoftwareDistribution\Download", Group = "Windows Update downloads", CategoryId = "Update", Bytes = 90000000, Selected = false },
        new() { Path = @"C:\Windows\DeliveryOptimization\Cache\old-part", Root = @"C:\Windows\DeliveryOptimization\Cache", Group = "Delivery Optimization cache", Kind = CleanupKind.Cache, CategoryId = "Delivery", Bytes = 30000000, Selected = false },
        new() { Path = @"C:\Windows\Panther\setupact.log", Root = @"C:\Windows\Panther", Group = "Windows setup logs", CategoryId = "Setup", Bytes = 800000, Selected = false },
        new() { Path = "Recycle Bin", Root = "Recycle Bin", Group = "Recycle Bin", Kind = CleanupKind.RecycleBin, CategoryId = "RecycleBin", Bytes = 12000000, ItemCount = 7, Detail = "7 deleted items on all drives", Selected = false },
        new() { Path = @"C:\Users\Example\AppData\Local\npm-cache\_cacache\index", Root = @"C:\Users\Example\AppData\Local\npm-cache", Group = "npm cache", Kind = CleanupKind.Cache, CategoryId = "DevCache", Bytes = 250000000, Selected = false },
        new() { Path = @"C:\Windows\Logs\CBS\CBS.log", Root = @"C:\Windows\Logs\CBS", Group = "Windows servicing logs", CategoryId = "SystemLogs", Bytes = 9000000, Selected = false },
        new() { Path = @"C:\NVIDIA\DisplayDriver\setup.exe", Root = @"C:\NVIDIA", Group = "Driver installer leftovers", CategoryId = "Installer", Bytes = 600000000, Selected = false },
        new() { Path = @"C:\Users\Example\AppData\Roaming\Microsoft\Windows\Recent\report.lnk", Root = @"C:\Users\Example\AppData\Roaming\Microsoft\Windows\Recent", Group = "Recent item shortcuts", CategoryId = "RecentAccess", Bytes = 1500, Selected = false },
        new() { Path = @"C:\Users\Example\AppData\Local\Microsoft\Edge\User Data\Default\Cache\f_000001", Root = @"C:\Users\Example\AppData\Local\Microsoft\Edge\User Data\Default\Cache", Group = "Edge cache", Kind = CleanupKind.Cache, CategoryId = "BrowserEdge", Bytes = 48000000, Selected = false },
        new() { Path = @"C:\Users\Example\AppData\Local\Google\Chrome\User Data\Default\History", Root = @"C:\Users\Example\AppData\Local\Google\Chrome\User Data\Default", Group = "Chrome history", Kind = CleanupKind.Junk, CategoryId = "BrowserHistory", Bytes = 3000000, Selected = false },
        new() { Path = @"C:\Users\Example\AppData\Local\Google\Chrome\User Data\Default\Network\Cookies", Root = @"C:\Users\Example\AppData\Local\Google\Chrome\User Data\Default\Network", Group = "Chrome cookies", Kind = CleanupKind.Junk, CategoryId = "BrowserCookies", Bytes = 90000, Selected = false },
        new() { Path = "Previous Windows installation", Root = "Windows tool", Group = "Previous Windows installation", Kind = CleanupKind.SystemTool, CategoryId = "WindowsOld", ToolId = "windows-old", Bytes = 14000000000, Detail = "Windows.old · removed by Disk Cleanup", Selected = false },
    ];
    private static void VerifyOdometerMotion(RollingFileCount template)
    {
        var counter = new RollingFileCount { FontFamily = template.FontFamily, FontSize = template.FontSize,
            FontWeight = template.FontWeight, Foreground = template.Foreground, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        var surface = new Grid { Background = Brushes.Transparent }; surface.Children.Add(counter);
        var host = new Window { Title = "Hush counter motion verification", Width = 360, Height = 80,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Content = surface };
        try
        {
        host.Show(); Wait(40);
        counter.Visibility = Visibility.Visible;
        counter.Reset(); counter.SetCount(1999, false);
        host.UpdateLayout();
        var size = new Size(Math.Ceiling(surface.ActualWidth), Math.Ceiling(surface.ActualHeight));
        byte[] Pixels()
        {
            counter.UpdateLayout();
            var bitmap = Render(surface, size);
            var pixels = new byte[(int)size.Width * (int)size.Height * 4];
            bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
            return pixels;
        }
        var before = Pixels();
        Wait(350); // Model a slower real discovery report so a mid-roll frame is observable.
        counter.SetCount(2000);
        Require(counter.IsAnimating && counter.Phase < 1,
            $"Explicitly requested odometer motion must animate, including when Windows client-area animation is {SystemParameters.ClientAreaAnimation}");
        PumpUntil(() => counter.Phase >= 0.35);
        var during = Pixels();
        Require(counter.Phase is > 0 and < 1 && !before.SequenceEqual(during), $"Digit pixels move during the live transition; changing only the count is insufficient (phase={counter.Phase}, ink={during.Where((value,index)=>index%4==3&&value>0).Count()}, changed={!before.SequenceEqual(during)})");
        PumpUntil(() => !counter.IsAnimating);
        var settled = Pixels();
        Require(!during.SequenceEqual(settled), "Rendered reels continue moving and settle at the new value");
        counter.SetCount(2000, false);
        Require(settled.SequenceEqual(Pixels()), "An unchanged discovery count leaves the settled pixels still");
        counter.SetCount(2017); Wait(20); counter.SetCount(2042);
        Require(counter.IsAnimating, "A live update retargets an active roll rather than snapping it");
        counter.Settle();
        Require(!counter.IsAnimating, "Stop immediately clears the animation clock");
        VerifySequentialCountAndAlignment(counter, surface);
        counter.Visibility = Visibility.Hidden;
        }
        finally { host.Close(); }
    }

    private static void VerifySequentialCountAndAlignment(RollingFileCount counter, FrameworkElement surface)
    {
        counter.Visibility = Visibility.Visible;
        counter.Reset();
        counter.SetCount(6);
        var observed = new HashSet<int> { 0 };
        var completion = counter.CompleteAsync();
        var sampler = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12) };
        sampler.Tick += (_, _) => observed.Add(counter.DisplayedCount);
        sampler.Start();
        try { PumpUntil(() => completion.IsCompleted); }
        finally { sampler.Stop(); }
        observed.Add(counter.DisplayedCount);
        Require(observed.Order().SequenceEqual(Enumerable.Range(0, 7)),
            "A real batch target of six visibly counts through 0,1,2,3,4,5,6 before completion");
        foreach (int value in new[] { 0, 1, 6, 10, 1000 })
        {
            counter.Reset(); counter.SetCount(value, false);
            surface.UpdateLayout();
            int width = (int)Math.Ceiling(surface.ActualWidth), height = (int)Math.Ceiling(surface.ActualHeight);
            var bitmap = Render(surface, new Size(width, height));
            var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            int left = width, right = -1;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (pixels[(y * width + x) * 4 + 3] > 16) { left = Math.Min(left, x); right = Math.Max(right, x); }
            Require(right >= left && Math.Abs((left + right + 1) / 2.0 - width / 2.0) <= 2,
                $"The visible number and label remain centered without leading placeholder space (count={value})");
        }
        counter.Reset(); counter.SetCount(6);
        var interrupted = counter.CompleteAsync();
        counter.Reset();
        Require(interrupted.IsCompleted && counter.DisplayedCount == 0, "Rescan resolves a pending count-up completion without hanging");
        counter.Visibility = Visibility.Hidden;
    }

    private static void ExportPreviews(MainWindow window, FrameworkElement root, string directory)
    {
        Directory.CreateDirectory(directory);
        var finish = window.CompleteGauge();
        PumpUntil(() => finish.IsCompleted); finish.GetAwaiter().GetResult();
        ((TextBlock)window.FindName("StatusTitle")).Text = "Scan complete. Take a look.";
        window.LoadReviewItems(PreviewItems()); window.ShowReviewPage(true); Wait(360);
        foreach (bool light in new[] { false, true })
        {
            if (window.IsLightTheme != light) window.ToggleTheme();
            PumpUntil(() => !window.IsPageTransitioning);
            foreach (var size in new[] { new Size(920, 820), new Size(800, 740) })
            {
                foreach (var category in window.Categories) category.Expanded = false;
                Wait(360); Layout(root, size); ((ScrollViewer)window.FindName("ReviewScroll")).ScrollToTop(); root.UpdateLayout(); Wait(80);
                Save(root, size, Path.Combine(directory, $"review-{(light ? "light" : "dark")}-{size.Width}.png"));
            }
            var previousSelection = window.ReviewItems.Select(item => item.Selected).ToArray();
            foreach (var item in window.ReviewItems) item.Selected = false;
            Wait(300); Layout(root, new Size(800, 740));
            Require(((Button)window.FindName("PrimaryButton")).Content.ToString() == "Scan again"
                && ((Button)window.FindName("RescanButton")).Visibility == Visibility.Collapsed,
                "Deselecting every result leaves exactly one Scan again action in both themes");
            Save(root, new Size(800, 740), Path.Combine(directory, $"review-no-selection-{(light ? "light" : "dark")}.png"));
            for (int index = 0; index < previousSelection.Length; index++) window.ReviewItems[index].Selected = previousSelection[index];
            window.Categories[0].Expanded = true; Layout(root, new Size(920, 820)); ((ScrollViewer)window.FindName("ReviewScroll")).ScrollToTop(); root.UpdateLayout(); Wait(360);
            Save(root, new Size(920, 820), Path.Combine(directory, $"review-{(light ? "light" : "dark")}-expanded.png"));
            window.ShowReviewPage(false); Wait(300); Layout(root, new Size(920, 820));
            Save(root, new Size(920, 820), Path.Combine(directory, $"home-{(light ? "light" : "dark")}.png"));
            ExportCounterPreviews(window, root, directory, light);
            ((Button)window.FindName("HomeCleanButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(330);
            Save(root, new Size(920, 820), Path.Combine(directory, $"confirmation-{(light ? "light" : "dark")}.png"));
            ((Button)window.FindName("CancelDeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => ((FrameworkElement)window.FindName("ModalOverlay")).Visibility == Visibility.Collapsed);
            ((Button)window.FindName("AboutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(350);
            Save(root, new Size(920, 820), Path.Combine(directory, $"about-{(light ? "light" : "dark")}.png"));
            ((Button)window.FindName("AboutUpdatesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(360);
            Save(root, new Size(920, 820), Path.Combine(directory, $"about-updates-{(light ? "light" : "dark")}.png"));
            ((Button)window.FindName("CloseAboutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => ((FrameworkElement)window.FindName("AboutOverlay")).Visibility == Visibility.Collapsed);
            window.ShowReviewPage(true); Wait(300);
        }
    }
    private static void ExportCounterPreviews(MainWindow window, FrameworkElement root, string directory, bool light)
    {
        var counter = (RollingFileCount)window.FindName("LiveFileCounter");
        var title = (TextBlock)window.FindName("StatusTitle");
        var detail = (TextBlock)window.FindName("StatusDetail");
        var results = (Button)window.FindName("CompletionLinkButton");
        var clean = (Button)window.FindName("HomeCleanButton");
        var label = (TextBlock)window.FindName("GaugeLabel");
        string oldTitle = title.Text, oldLabel = label.Text;
        double oldPercent = window.GaugePercent;
        window.ResetGauge(); window.GaugePercent = 42; label.Text = "Click to stop";
        title.Text = "Searching files…"; title.Visibility = Visibility.Visible;
        detail.Visibility = Visibility.Hidden; results.Visibility = Visibility.Collapsed; clean.Visibility = Visibility.Hidden;
        counter.Visibility = Visibility.Visible;
        counter.Reset(); counter.SetCount(1248, false); counter.SetCount(1293);
        Wait(65); Layout(root, new Size(920, 820));
        Save(root, new Size(920, 820), Path.Combine(directory, $"scan-rolling-{(light ? "light" : "dark")}.png"));
        counter.Settle(); Layout(root, new Size(800, 740));
        Save(root, new Size(800, 740), Path.Combine(directory, $"scan-counter-{(light ? "light" : "dark")}-800.png"));
        counter.Visibility = Visibility.Hidden;
        title.Text = oldTitle; title.Visibility = Visibility.Collapsed;
        detail.Visibility = Visibility.Visible; results.Visibility = Visibility.Visible; clean.Visibility = Visibility.Visible;
        window.GaugePercent = oldPercent; label.Text = oldLabel;
        Layout(root, new Size(920, 820));
    }
    internal static void Save(FrameworkElement root, Size size, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Render(root, size)));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static RenderTargetBitmap Render(FrameworkElement root, Size size)
    { var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root); return bitmap; }
    private static Rect Bounds(FrameworkElement child, FrameworkElement root) => child.TransformToAncestor(root).TransformBounds(new Rect(child.RenderSize));
    internal static void Layout(FrameworkElement root, Size size) { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
    private static T? Parent<T>(DependencyObject child) where T : DependencyObject
    { for (var parent = VisualTreeHelper.GetParent(child); parent != null; parent = VisualTreeHelper.GetParent(parent)) if (parent is T match) return match; return null; }
    private static bool HasInvisibleAncestor(DependencyObject child)
    { for (var parent = VisualTreeHelper.GetParent(child); parent != null; parent = VisualTreeHelper.GetParent(parent)) if (parent is UIElement element && (element.Opacity == 0 || element.Visibility != Visibility.Visible)) return true; return false; }
    internal static void Wait(int milliseconds) { var clock = System.Diagnostics.Stopwatch.StartNew(); PumpUntil(() => clock.ElapsedMilliseconds >= milliseconds); }
    internal static void PumpUntil(Func<bool> complete, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(complete))] string what = "")
    {
        var frame = new DispatcherFrame(); var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || clock.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Require(complete(), "Animation or operation did not finish or cancel within five seconds: " + what);
    }
    internal static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    { for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) { var child = VisualTreeHelper.GetChild(parent, index); if (child is T match) yield return match; foreach (var item in Descendants<T>(child)) yield return item; } }
}
