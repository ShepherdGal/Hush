using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hush;

// Home's status tiles (Phase D, owner, November 19, 2026; Expert mode only). Owner's choices:
//  * Junk found: the quiet safe count (the same read Boost uses, a moment after start) until a scan has run, then everything the scan found.
//  * Startup items: ONE quiet background read about 5 s after start (the real app window only); the Startup card keeps it current when it is opened.
//  * Updates (programs and drivers) and Disk health: the last result of Tools > Software updates and Tools > Disk check, kept between starts; "Check" until that tool ran once.
//    Hush never starts the app installer or a disk query by itself for them.
// Each tile opens its page: Junk found -> Clean (or a scan when there is nothing yet), Startup items -> the Startup card, the other two -> their Tools card.
public sealed partial class MainWindow
{
    internal static readonly TimeSpan StartupTileDelay = TimeSpan.FromSeconds(5);
    private int? startupItemCount;

    internal int? StartupItemCount => startupItemCount;

    // One quiet read of what starts with Windows, a few seconds after the real app window starts (never in test windows).
    private void ScheduleStartupTileRead()
    {
        if (Application.Current?.MainWindow != this) return;
        _ = Task.Delay(StartupTileDelay).ContinueWith(_ => Dispatcher.InvokeAsync(async () => await ReadStartupTileAsync()), TaskScheduler.Default);
    }

    internal async Task ReadStartupTileAsync()
    {
        if (closing) return;
        try
        {
            var candidates = await Task.Run(() => Startup.Scan().ToList());
            SetStartupItemCount(candidates.Count(candidate => candidate.StartsNow && !candidate.Locked));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { /* the tile keeps "Check" */ }
    }

    internal void SetStartupItemCount(int count) { startupItemCount = count; UpdateHomeTiles(); }

    // Home's block (heading, gauge, status) sits in the middle between the header and the tiles in every state (owner, November 20, 2026: "center it too").
    // When it grows or shrinks (Take a look and Clean appear, a note wraps) the two equal rows around it move it by half the change; it is drawn where it was
    // and glides there (a transform only, so the gauge and its cue are never laid out or drawn again for it). Instant with Windows' animations off.
    internal const double HomeGlideMilliseconds = 320;
    private void HomeBlock_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || e.PreviousSize.Height <= 0 || !HomeBlock.IsVisible || !Motion.Animates) return;
        double from = HomeBlockShift.Y + (e.NewSize.Height - e.PreviousSize.Height) / 2;   // where it was drawn, seen from its new place
        if (Math.Abs(from) < 0.5) return;
        HomeBlockShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(from, 0, Motion.Span(HomeGlideMilliseconds))
        { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut } });
    }

    // Results the Tools cards found, kept for the tiles.
    internal void RecordAppUpdates(int count)
    {
        prefs.AppUpdatesCount = count; prefs.AppUpdatesCheckedUtc = DateTime.UtcNow;
        SavePreferences(); UpdateHomeTiles();
    }

    internal void RecordDiskHealth(IReadOnlyList<DriveHealth> health)
    {
        if (health.Count == 0) return;
        bool Is(DriveHealth drive, string word) => drive.Health.Contains(word, StringComparison.OrdinalIgnoreCase);
        prefs.DiskHealth = health.Any(drive => Is(drive, "Unhealthy")) ? "Unhealthy" : health.All(drive => Is(drive, "Healthy")) ? "Good" : "Warning";
        prefs.DiskHealthCheckedUtc = DateTime.UtcNow;
        SavePreferences(); UpdateHomeTiles();
    }

    // Fills the four tiles from what Hush already knows (nothing here reads the PC).
    internal void UpdateHomeTiles()
    {
        if (JunkTileValue == null) return;
        // Junk found: after a scan, everything it found; before one, the safe junk ready to clean.
        long found = items.Count > 0 ? items.Sum(item => item.Bytes) : -1;
        if (found >= 0) SetTile("Junk", Sizes.Format(found), "Junk found", "GoodBrush", split: true);
        else if (chipReading != null) SetTile("Junk", chipReading.Files == 0 ? "0 MB" : Sizes.Format(chipReading.Bytes), "Safe junk ready", "GoodBrush", split: true);
        else SetTile("Junk", "—", "Junk found", null);
        SetTile("Startup", startupItemCount is int count ? count.ToString("N0") : "—", "Startup items", null);
        if (prefs.AppUpdatesCount is int updates) SetTile("Updates", updates.ToString("N0"), updates == 0 ? "All up to date" : updates == 1 ? "Update ready" : "Updates ready", updates == 0 ? "GoodBrush" : "MeterWarnBrush");
        else SetTile("Updates", "Check", "Updates", null);
        if (prefs.DiskHealth is string disk) SetTile("Disk", disk, "Disk health", disk == "Good" ? "GoodBrush" : disk == "Unhealthy" ? "CleanTextBrush" : "MeterWarnBrush");
        else SetTile("Disk", "Check", "Disk health", null);
    }

    // value "1.4 GB" is shown as a big "1.4" and a small "GB" (split); the dot shows the tile's state in the risk colors, or nothing.
    private void SetTile(string key, string value, string label, string? dotBrush, bool split = false)
    {
        var valueRun = (System.Windows.Documents.Run)FindName(key + "TileValue");
        var unitRun = (System.Windows.Documents.Run)FindName(key + "TileUnit");
        var labelText = (TextBlock)FindName(key + "TileLabel");
        var dot = (System.Windows.Shapes.Ellipse)FindName(key + "TileDot");
        string number = value, unit = "";
        if (split && value.LastIndexOf(' ') is int space and > 0) { number = value[..space]; unit = value[space..]; }
        if (valueRun.Text != number) valueRun.Text = number;
        if (unitRun.Text != unit) unitRun.Text = unit;
        if (labelText.Text != label) labelText.Text = label;
        dot.Visibility = dotBrush == null ? Visibility.Hidden : Visibility.Visible;
        if (dotBrush != null) dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotBrush);
    }

    private async void JunkTile_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (items.Count > 0 && IsProMode) { LeaveSettings(); ShowReviewPage(true); }
        else await ActivateGaugeAsync();
    }

    private void StartupTile_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !IsProMode) return;
        SettingsStartupSection.IsExpanded = true;
        OpenSettingsPage(tweaks: true, section: SettingsStartupSection);
    }

    private void UpdatesTile_Click(object sender, RoutedEventArgs e) => OpenToolsSection(ToolsUpdatesSection);
    private void DiskTile_Click(object sender, RoutedEventArgs e) => OpenToolsSection(ToolsDiskSection);

    // Opens the Tools page showing that tool in its card (showing a tool is what reads it, as opening a card did).
    private void OpenToolsSection(Expander section)
    {
        if (busy || !IsProMode) return;
        if (ToolsPage.Visibility != Visibility.Visible) Tools_Click(ToolsButton, new RoutedEventArgs());
        ShowTool(section.Name[5..^7]);   // the Tools page shows one tool at a time in its card (Phase D)
    }
}
