using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hush;

// The new look's motion family (owner-approved November 19, 2026, the design rules "New look"; applied everywhere in the clean-up step, November 20, 2026):
// hover 150 ms, switch and check 160 ms, moving things 280 ms, page out 120 / in 200 ms, popups in 200 / out 150 ms, a 97 % press for 100 ms, tooltips after
// 500 ms with a 120 ms fade, and Windows' "Show animations" off snaps every transition.
internal static class MotionTimingChecks
{
    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        // The family, as named constants.
        check(Motion.HoverMilliseconds == 150 && Motion.SwitchMilliseconds == 160 && Motion.PillMoveMilliseconds == 280 && Motion.PageOutMilliseconds == 120
            && Motion.PageInMilliseconds == 200 && Motion.PopupInMilliseconds == 200 && Motion.PopupOutMilliseconds == 150 && Motion.TooltipMilliseconds == 120
            && Motion.TooltipDelayMilliseconds == 500 && Motion.PressMilliseconds == 100 && Motion.PressScale == 0.97 && Motion.SpringMilliseconds == 600,
            "The motion family's values are the approved ones");

        // The spring (owner, November 20, 2026, "lively", then "a bit too strong"): about 4-5 % past the new place, a barely visible bounce back, settled at the end.
        var curve = Enumerable.Range(0, 601).Select(step => Motion.Spring.Ease(step / 600.0)).ToList();
        double peak = curve.Max(); int peakAt = curve.IndexOf(peak);
        double dip = curve.Skip(peakAt).Min();
        check(curve[0] == 0 && curve[^1] == 1 && peak is > 1.03 and < 1.06 && dip is < 1.001 and > 0.99 && curve.Skip(450).All(value => Math.Abs(value - 1) < 0.01),
            $"Selections spring: a gentle overshoot (peak {peak:0.000}), a barely visible bounce back (dip {dip:0.000}), settled within the 600 ms");
        // Landing (owner: "not aligned correctly when landing"): the last frames arrive without a jump. Across 304 DIP (the tab bar) the final step is under a quarter pixel.
        double lastStep = Math.Abs(curve[^1] - curve[^2]) * 304, shortBy = Math.Abs(1 - curve[^2]) * 304;
        check(lastStep < 0.25 && shortBy < 0.25, $"The spring lands on its place without a snap (last step {lastStep:0.000} px, {shortBy:0.000} px short one frame before the end)");

        // The scan specks' touch on the ring (owner, November 20, 2026: "ring pulse, a bit brighter", springy but subtle): starts and ends at rest, one swell,
        // one small dip, settled within ScanRadar.TouchSeconds.
        var touch = Enumerable.Range(0, 121).Select(step => ScanRadar.TouchScale(ScanRadar.TouchSeconds * step / 120)).ToList();
        double swell = touch.Max(), settle = touch.Skip(touch.IndexOf(swell)).Min();
        check(touch[0] == 1 && touch[^1] == 1 && swell is > 1.5 and < 1.9 && settle is > 0.8 and < 0.95 && ScanRadar.TouchScale(1.0) == 1,
            $"A speck's touch swells the ring with a spring (peak {swell:0.00}, dip {settle:0.00}) and rests again within {ScanRadar.TouchSeconds} s");

        // Every XAML animation uses the timing of its role: hover 0.15, check/switch 0.16, a turning chevron 0.28, the tooltip fade 0.12; nothing else.
        string testsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string source = Path.GetFullPath(Path.Combine(testsDirectory, "..", "..", "src", "Hush"));
        string xaml = File.ReadAllText(Path.Combine(source, "App.xaml")) + File.ReadAllText(Path.Combine(source, "MainWindow.xaml"));
        var hover = new[] { "HoverRing", "ChevronHover", "ThumbFill" };
        var toggle = new[] { "Tick", "Mixed", "CheckedRing", "MixedRing", "OnFill", "KnobOn", "KnobShift" };
        var wrong = new List<string>();
        foreach (Match animation in Regex.Matches(xaml, "<DoubleAnimation\\b[^>]*?Duration=\"([^\"]+)\"[^>]*>"))
        {
            string text = animation.Value, duration = animation.Groups[1].Value;
            string target = Regex.Match(text, "TargetName=\"([^\"]+)\"").Groups[1].Value;
            string expected = hover.Contains(target) || target.EndsWith("Hover") ? "0:0:0.15" : toggle.Contains(target) ? "0:0:0.16"
                : text.Contains("Angle") ? "0:0:0.28" : text.Contains("TargetProperty=\"Opacity\" From=\"0\" To=\"1\"") && target.Length == 0 ? "0:0:0.12" : "?";
            if (duration != expected) wrong.Add($"{(target.Length > 0 ? target : "untargeted")} {duration}");
        }
        check(wrong.Count == 0, "Every XAML animation uses its role's timing (hover 150, check and switch 160, chevron turn 280, tooltip fade 120)" + (wrong.Count > 0 ? ": " + string.Join(", ", wrong) : ""));
        string motion = File.ReadAllText(Path.Combine(source, "UI", "Motion.cs"));
        check(!motion.Contains("InteractionMilliseconds") && !motion.Contains("PageFadeMilliseconds") && !Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Any(file => File.ReadAllText(file).Contains("PageFadeMilliseconds")), "The old 280 ms interaction and 320 ms page-fade constants are gone");

        // Tooltips: the shared look and the 500 ms wait.
        var tipStyle = Application.Current.TryFindResource(typeof(ToolTip)) as Style;
        check(tipStyle != null && tipStyle.Setters.OfType<Setter>().Any(setter => setter.Property == ToolTip.HasDropShadowProperty && Equals(setter.Value, false)),
            "Tooltips have one shared style without the square system shadow");
        check(ToolTipService.GetInitialShowDelay(new Border()) == 500 && ToolTipService.GetBetweenShowDelay(new Border()) >= 1000,
            "Tooltips wait 500 ms, and the next ones show at once");

        // Show animations off: a transition snaps.
        Motion.AnimationsForTests = false;
        try { check(Motion.Tween(0, 1, 200).Duration.TimeSpan == TimeSpan.Zero && Motion.Span(150) == TimeSpan.Zero, "With Windows' animations off every transition snaps"); }
        finally { Motion.AnimationsForTests = null; }

        string preferences = Path.Combine(fixture, "motion-timing-preferences.json");
        new Preferences { TermsAccepted = Terms.Version, RunAsAdmin = false, ProMode = true }.Save(preferences);
        var window = new MainWindow(engine, preferencesPath: preferences, serviceSystem: ServiceChecks.Pc(), taskSystem: TaskChecks.Pc()) { ShowActivated = false, ShowInTaskbar = false };
        Motion.AnimationsForTests = true; Motion.SkipPageFadeOutForTests = false;
        try
        {
            window.Show();
            UiLayoutChecks.Wait(400);
            T Find<T>(string name) where T : class => (T)window.FindName(name);
            void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var content = Find<FrameworkElement>("MainContent");

            // A page change: the page on screen fades out first (the change waits for it), then the new page fades in.
            Click("SettingsButton");
            check(window.IsPageTransitioning && Find<FrameworkElement>("SettingsPage").Visibility != Visibility.Visible, "A page change first fades the page on screen out, before the new page is shown");
            UiLayoutChecks.Wait(170);
            check(Find<FrameworkElement>("SettingsPage").Visibility == Visibility.Visible && window.IsPageTransitioning, "After the 120 ms fade-out the new page is shown and fades in");
            UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            check(content.Opacity == 1 && Find<FrameworkElement>("ScanPage").Visibility != Visibility.Visible, "The new page ends fully shown, the old one gone");

            // Two changes in a row: only the newest is made.
            Click("HomeTabButton"); Click("ToolsButton");
            UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning); UiLayoutChecks.Wait(50);
            check(Find<FrameworkElement>("ToolsPage").Visibility == Visibility.Visible && Find<FrameworkElement>("ScanPage").Visibility != Visibility.Visible
                && Find<FrameworkElement>("SettingsPage").Visibility != Visibility.Visible, "A second page change during a fade wins; the first is never shown");

            // From the keyboard (Ctrl+1 to Ctrl+5) the change is made at once.
            Motion.InstantPageChange = true;
            try { Click("HomeTabButton"); }
            finally { Motion.InstantPageChange = false; }
            check(!window.IsPageTransitioning && Find<FrameworkElement>("ScanPage").Visibility == Visibility.Visible && content.Opacity == 1, "A keyboard tab change is instant: no fade");

            // Home (owner, November 20, 2026: "center it too"): the block is in the middle in every state, and glides when the buttons appear.
            var block = Find<FrameworkElement>("HomeBlock"); var tilesRow = Find<FrameworkElement>("StatusTiles"); var page = Find<FrameworkElement>("ScanPage");
            (double Above, double Below) Gaps()
            {
                double top = block.TranslatePoint(new Point(0, 0), page).Y - ((TranslateTransform)block.RenderTransform).Y;
                return (top, tilesRow.TranslatePoint(new Point(0, 0), page).Y - tilesRow.Margin.Top - (top + block.ActualHeight));
            }
            UiLayoutChecks.Wait(200);
            var (aboveBefore, belowBefore) = Gaps();
            check(Math.Abs(aboveBefore - belowBefore) < 1.5, $"Before a scan the heading, gauge and text sit in the middle (gap above {aboveBefore:0}, below {belowBefore:0})");
            Find<Button>("CompletionLinkButton").Visibility = Visibility.Visible; Find<Button>("HomeCleanButton").Visibility = Visibility.Visible;
            block.UpdateLayout(); UiLayoutChecks.Wait(40);   // an animation is read only after its clock has ticked once
            var shift = (TranslateTransform)block.RenderTransform;
            check(shift.Y is > 5 and < 23.5, $"When Take a look and Clean appear the block is drawn where it was and glides (starts {shift.Y:0} DIP lower)");
            UiLayoutChecks.Wait((int)MainWindow.HomeGlideMilliseconds + 150);
            var (aboveAfter, belowAfter) = Gaps();
            check(Math.Abs(shift.Y) < 0.01 && Math.Abs(aboveAfter - belowAfter) < 1.5 && aboveAfter < aboveBefore,
                $"It lands in the middle again (gap above {aboveAfter:0}, below {belowAfter:0})");
            Find<Button>("CompletionLinkButton").Visibility = Visibility.Collapsed; Find<Button>("HomeCleanButton").Visibility = Visibility.Collapsed;
            UiLayoutChecks.Wait((int)MainWindow.HomeGlideMilliseconds + 150);

            // The press: 97 % while held, back to full size on release; the gauge never moves.
            var scan = Find<Button>("BoostButton");
            Motion.PreviewInteraction(scan, hover: true, pressed: true); UiLayoutChecks.Wait(220);
            check(scan.RenderTransform is ScaleTransform pressedScale && Math.Abs(pressedScale.ScaleX - 0.97) < 0.002 && Math.Abs(pressedScale.ScaleY - 0.97) < 0.002,
                "A pressed button is drawn at 97 %");
            Motion.PreviewInteraction(scan, hover: true, pressed: false); UiLayoutChecks.Wait(220);
            check(scan.RenderTransform is ScaleTransform released && released.ScaleX == 1 && released.ScaleY == 1, "On release it returns to full size");
            var gauge = Find<Button>("ScanCircleButton");
            Motion.PreviewInteraction(gauge, hover: true, pressed: true); UiLayoutChecks.Wait(220);
            check(ReferenceEquals(gauge.RenderTransform, Transform.Identity), "The gauge is never pressed smaller (it never moves)");
            Motion.PreviewInteraction(gauge, hover: false, pressed: false);

            // A bar springs: picking another Interface size moves the one selected pill there with the spring, and it lands exactly on the chip.
            Motion.InstantPageChange = true;
            try { Click("SettingsButton"); } finally { Motion.InstantPageChange = false; }
            UiLayoutChecks.Wait(300);
            var chips = (SegmentPanel)VisualTreeHelper.GetParent(Find<RadioButton>("UiSize75"));
            var before = chips.Shown;
            var picked = Find<RadioButton>(Find<RadioButton>("UiSize75").IsChecked == true ? "UiSize100" : "UiSize75");
            picked.IsChecked = true;
            UiLayoutChecks.Wait(80);
            check(before != null && chips.IsSpringing && chips.Shown != before, "Picking a chip starts the spring of the bar's one selected pill");
            UiLayoutChecks.PumpUntil(() => !chips.IsSpringing);
            var landed = chips.Shown!.Value; var chip = picked.TranslatePoint(new Point(0, 0), chips);
            check(Math.Abs(landed.X - chip.X) < 0.5 && Math.Abs(landed.Width - picked.ActualWidth) < 0.5, "The pill lands exactly on the picked chip");

            // The tooltip look, rendered in both themes for a person to look at (it opens as its own popup window, so no page preview shows it).
            if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string directory)
            {
                Directory.CreateDirectory(directory);
                foreach (bool light in new[] { window.IsLightTheme, !window.IsLightTheme })
                {
                    if (light != window.IsLightTheme) { Click("ThemeButton"); UiLayoutChecks.Wait(500); }
                    // A ToolTip can only live in its own popup: open a real one over the Boost button and render it where it is.
                    var tip = new ToolTip { Content = "Stops Windows from showing tips and suggestions. Undo brings it back.", PlacementTarget = scan,
                        Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                    tip.IsOpen = true; UiLayoutChecks.Wait(350);
                    check(tip.IsOpen && tip.ActualWidth is > 40 and <= 321 && tip.Opacity == 1, "A tooltip opens in the shared look, at most 320 wide, faded fully in");
                    UiLayoutChecks.Save(tip, new Size(Math.Ceiling(tip.ActualWidth), Math.Ceiling(tip.ActualHeight)), Path.Combine(directory, $"tooltip-{(window.IsLightTheme ? "light" : "dark")}.png"));
                    tip.IsOpen = false;
                }
            }
        }
        finally { Motion.AnimationsForTests = null; Motion.InstantPageChange = false; Motion.SkipPageFadeOutForTests = true; window.Close(); }
    }
}
