using System.IO;
using System.Windows;
using Hush;

// The hover on the big gauge: one style before a scan, another after it, finite, and gone while Hush works.
internal static class GaugeHoverChecks
{
    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        string preferences = Path.Combine(fixture, "gauge-hover-preferences.json");
        new Preferences { TermsAccepted = Terms.Version, RunAsAdmin = false }.Save(preferences);
        var window = new MainWindow(engine, preferencesPath: preferences) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            UiLayoutChecks.Layout(root, new Size(920, 820));
            var cue = window.GaugeHover;
            string? shots = Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY");
            if (shots != null) Directory.CreateDirectory(shots);
            check(cue.Current == GaugeHoverStyle.Off && cue.TrackTint == 0 && cue.InnerGlow == 0 && !cue.IsPlaying && cue.Visibility == Visibility.Collapsed, "At rest the gauge hover shows and runs nothing, and is collapsed so it costs the gauge nothing");

            foreach (bool light in new[] { false, true })
            {
                if (window.IsLightTheme != light) window.ToggleTheme();
                UiLayoutChecks.Wait(420);
                string theme = light ? "light" : "dark";

                // Before a scan: the ring wakes up (comet lap, one ring breathing out, a tint on the track that stays while the pointer does).
                cue.Enter(GaugeHoverStyle.Ready);
                UiLayoutChecks.Wait(380);
                check(cue.Current == GaugeHoverStyle.Ready && cue.IsPlaying && cue.TrackTint > 0.15, $"Hovering before a scan wakes the ring: the comet is under way and the track takes its tint ({theme})");
                if (shots != null) UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, $"gauge-hover-ready-{theme}.png"));
                UiLayoutChecks.Wait(1700);
                check(Math.Abs(cue.TrackTint - 0.34) < 0.02 && cue.InnerGlow < 0.01, $"Once the sweep is done the track keeps a soft tint while the pointer stays ({theme})");
                cue.Leave(); UiLayoutChecks.Wait(700);
                check(cue.Current == GaugeHoverStyle.Off && cue.TrackTint < 0.01 && cue.Visibility == Visibility.Collapsed, $"Leaving fades the tint out and the cue collapses again ({theme})");

                // After a scan: a different style (inside glow, rings contracting onto the gauge, a glint on the finished ring).
                window.GaugeArc.Data = ProgressRing.Create(100);
                cue.Enter(GaugeHoverStyle.Complete);
                UiLayoutChecks.Wait(520);
                check(cue.Current == GaugeHoverStyle.Complete && cue.IsPlaying && cue.InnerGlow > 0.05 && cue.TrackTint > 0.05 && cue.TrackTint < 0.2, $"Hovering after a scan plays the other style: an inner glow and a lighter track tint ({theme})");
                if (shots != null) UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(shots, $"gauge-hover-complete-{theme}.png"));
                UiLayoutChecks.Wait(1700);
                cue.Leave(); UiLayoutChecks.Wait(700);
                check(cue.InnerGlow < 0.01 && cue.TrackTint < 0.01, $"Leaving fades the inner glow out ({theme}; glow {cue.InnerGlow:0.000}, tint {cue.TrackTint:0.000}, style {cue.Current})");
                window.GaugeArc.Data = ProgressRing.Create(0);
            }

            // Switching style under a resting pointer (a scan that ends while the pointer is on the gauge) hands over without a pop.
            cue.Enter(GaugeHoverStyle.Ready); UiLayoutChecks.Wait(500);
            cue.Enter(GaugeHoverStyle.Complete); UiLayoutChecks.Wait(500);
            check(cue.Current == GaugeHoverStyle.Complete && cue.InnerGlow > 0.03 && cue.TrackTint < 0.25, "A change of state under the pointer hands the hover over to the other style");
            // Busy (a click starts a scan): everything goes at once and no clock is left running.
            cue.Stop();
            check(cue.Visibility == Visibility.Collapsed, "Stopping collapses the cue");
            check(cue.Current == GaugeHoverStyle.Off && cue.TrackTint == 0 && cue.InnerGlow == 0 && !cue.IsPlaying, "When Hush starts working the hover is removed at once");
            UiLayoutChecks.Wait(200);
            check(cue.TrackTint == 0 && cue.InnerGlow == 0, "Nothing comes back after it was stopped");
            // The whole cue is made of finished-by-themselves animations: no endless clock anywhere in the source (also locked by the performance rules).
            string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Hush", "UI", "GaugeHoverCue.cs"));
            check(!source.Contains("RepeatBehavior.Forever") && !source.Contains("DispatcherTimer") && !source.Contains("DropShadowEffect") && !source.Contains("OpacityMask"), "The hover cue repeats nothing, has no timer, no effect and no mask");
        }
        finally { window.Close(); }
    }
}
