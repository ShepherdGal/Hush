using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hush;

// One entry of the Settings section index: a name, a small icon chip, whether it is shown (Simple mode shows fewer), and whether it is the section in view.
internal sealed class SettingsIndexItem(string key, string name, string icon) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public Geometry Icon { get; } = Frozen(icon);
    private bool shown = true, selected;
    public Visibility Shown { get => shown ? Visibility.Visible : Visibility.Collapsed; }
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Changed(nameof(Selected)); } }
    internal bool IsShown { get => shown; set { if (shown == value) return; shown = value; Changed(nameof(Shown)); } }
    private static Geometry Frozen(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}

// The Settings page (Phase D, the approved mockup; owner, November 19, 2026: "no accordions"; owner's picks of November 20, 2026, revised the same day). One
// open page: a section index at the left (a click glides to the section, its pill springs there; scrolling moves it) and every section open at the right.
// Open apps shows its list straight away (read once the page has faded in). Windows Update is not a Settings section (its switch is in Tweaks > Privacy; the
// red line at the top shows only while it is off). About Hush checks for updates right in its Version row; only installing opens the About popup, where the
// download and the restart show.
public partial class MainWindow
{
    private readonly List<SettingsIndexItem> settingsIndex =
    [
        new("appearance", "Appearance", "M10,3 A7,7 0 1 0 10,17 C11,17 11.5,16.3 11.5,15.6 C11.5,14.7 10.8,14.4 10.8,13.6 C10.8,12.8 11.4,12.2 12.2,12.2 H14 A3,3 0 0 0 17,9.2 C17,5.6 13.9,3 10,3 Z"),
        new("admin", "Administrator", "M10,3 L4.5,5 V9.6 C4.5,12.9 6.8,15.6 10,17 C13.2,15.6 15.5,12.9 15.5,9.6 V5 Z"),
        new("boost", "Smart Boost", "M2.5,10.5 H5.7 L7.5,6.5 L10.5,14.5 L12.5,9.5 L13.7,10.5 H17.5"),
        new("browser", "Browser data", "M10,3.25 A6.75,6.75 0 1 1 9.99,3.25 M3.5,10 H16.5 M10,3.5 C12,5.7 12,14.3 10,16.5 M10,3.5 C8,5.7 8,14.3 10,16.5"),
        new("apps", "Open apps", "M3.5,5 H16.5 V15 H3.5 Z M3.5,8 H16.5"),
        new("about", "About Hush", "M10,3.25 A6.75,6.75 0 1 1 9.99,3.25 M10,9 V13.5 M10,6.6 V6.7"),
    ];
    internal IReadOnlyList<SettingsIndexItem> SettingsIndexItems => settingsIndex;

    private FrameworkElement SectionFor(string key) => key switch
    {
        "appearance" => SettingsSizeSection, "admin" => SettingsAdminSection, "boost" => SettingsBoostSection, "browser" => SettingsBrowserSection,
        "apps" => SettingsProcessSection, _ => SettingsAboutGroup
    };

    private void InitializeSettingsPage()
    {
        SettingsIndex.ItemsSource = settingsIndex;
        SettingsVersionText.Text = $"Version {UpdateService.DisplayVersion}";
        RefreshSettingsIndex();
    }

    // Which index entries show (Simple mode hides the Expert sections).
    internal void RefreshSettingsIndex()
    {
        bool expert = SettingsColumns.Visibility == Visibility.Visible;
        foreach (var item in settingsIndex) item.IsShown = item.Key is "appearance" or "about" || expert;
        HighlightSettingsSection();
    }

    // A click jumps to its section (owner, November 20, 2026: the old glide "feels weird and looks glitchy", remade): one fixed 350 ms slow-fast-slow glide
    // (SmoothScroll.GlideTo) and the index highlight moving on the same curve and time (the index SegmentPanel has Jump="True"), so they read as one gesture.
    // The clicked entry stays lit for the whole glide and after it lands, even where the page cannot scroll the section to the top (near the end); only the
    // person scrolling somewhere else moves the highlight again.
    private double? indexGlideTarget;
    internal SettingsIndexItem? IndexClicked { get; private set; }
    private void SettingsIndex_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SettingsIndexItem item) return;
        var section = SectionFor(item.Key);
        if (!section.IsVisible) return;
        double top = section.TranslatePoint(new Point(0, 0), SettingsSectionsPanel).Y;
        IndexClicked = item;
        foreach (var entry in settingsIndex) entry.Selected = entry == item;
        indexGlideTarget = SmoothScroll.GlideTo(WindowsSettingsScroll, top, Motion.JumpMilliseconds);
    }

    private void SettingsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
        if (indexGlideTarget is double target)
        {
            // On its way, or landed where the click sent it (the end re-clamped to the page's current length): the clicked entry stays lit.
            if (SmoothScroll.IsJumping(WindowsSettingsScroll)) return;
            if (Math.Abs(WindowsSettingsScroll.VerticalOffset - Math.Min(target, WindowsSettingsScroll.ScrollableHeight)) < 1) return;
            indexGlideTarget = null;   // the person scrolled somewhere else
        }
        HighlightSettingsSection();
    }

    // Scrolling moves the highlight: the last section whose top has reached the top of the view (the last one when the page is scrolled to its end).
    private void HighlightSettingsSection()
    {
        var shown = settingsIndex.Where(item => item.IsShown && SectionFor(item.Key).IsVisible).ToList();
        if (shown.Count == 0) return;
        var current = shown[0];
        if (WindowsSettingsScroll.ScrollableHeight > 0 && WindowsSettingsScroll.VerticalOffset >= WindowsSettingsScroll.ScrollableHeight - 1) current = shown[^1];
        else
            foreach (var item in shown)
                if (SectionFor(item.Key).TranslatePoint(new Point(0, 0), SettingsSectionsPanel).Y <= WindowsSettingsScroll.VerticalOffset + 32) current = item;
        foreach (var item in settingsIndex) item.Selected = item == current;
    }

    // About Hush > Version (owner, November 20, 2026: "check updates in place"): the check runs right here and says the answer in the row; when a newer
    // version exists the button becomes Update now, which opens the About popup's update flow (it asks first, shows the download and restarts Hush).
    private bool settingsChecking;
    private async void SettingsCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (settingsChecking || updateBusy) return;
        if (knownRelease != null)
        {
            About_Click(sender, e);
            if (AboutOverlay.Visibility == Visibility.Visible) CheckUpdate_Click(CheckUpdateButton, e);
            return;
        }
        settingsChecking = true;
        SettingsCheckUpdateButton.IsEnabled = false;
        SettingsCheckUpdateButton.Content = "Checking…";
        ShowSettingsVersion("Looking for a newer version…", "MutedBrush");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var service = updateFactory();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            AppRelease? release = await Task.Run(() => service.CheckAsync(timeout.Token), timeout.Token);
            var calm = TimeSpan.FromMilliseconds(900) - clock.Elapsed;   // even an instant answer reads as a check, not a flicker
            if (calm > TimeSpan.Zero) await Task.Delay(calm);
            knownRelease = release;
            if (release == null) ShowSettingsVersion($"Version {UpdateService.DisplayVersion} · you have the latest version", "GoodBrush");
            else
            {
                ShowSettingsVersion($"Version {UpdateService.Display(release.Version)} is available (you have {UpdateService.DisplayVersion})", "TealTextBrush");
                UpdateNoticeButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException)
        { ShowSettingsVersion($"Version {UpdateService.DisplayVersion} · couldn't reach the update server", "TextBrush"); }
        catch { ShowSettingsVersion($"Version {UpdateService.DisplayVersion} · couldn't check right now", "TextBrush"); }
        finally
        {
            settingsChecking = false;
            SettingsCheckUpdateButton.IsEnabled = true;
            SettingsCheckUpdateButton.Content = knownRelease != null ? "Update now" : "Check for updates";
            SettingsCheckUpdateButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, knownRelease != null ? "Update Hush now" : "Check for updates");
        }
    }

    private void ShowSettingsVersion(string text, string brush)
    {
        SettingsVersionText.Text = text;
        SettingsVersionText.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
}
