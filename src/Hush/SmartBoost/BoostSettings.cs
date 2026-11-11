using System.IO;
using System.Text.Json;

namespace Hush;

// Smart Boost choices. Everything is off by default; nothing here ever closes an application, clears RAM or changes a Windows service.
public sealed class BoostSettings
{
    // Look for Safe junk now and then while Hush is open (also minimized) and show what was found. Nothing is deleted.
    public bool MonitorJunk { get; set; }
    // Clean Safe items by itself when the junk passes the threshold. Needs the one-time consent below.
    public bool AutoBoost { get; set; }
    public bool AutoBoostConsent { get; set; }
    public int ThresholdMb { get; set; } = 1000;
    public int CheckEveryMinutes { get; set; } = 60;
    // Hush runs at below-normal priority with Windows efficiency mode and reports progress less often.
    public bool LowResource { get; set; }

    public static readonly int[] ThresholdChoices = [500, 1000, 2000, 5000];
    public static readonly int[] IntervalChoices = [15, 60, 360, 1440];

    // Settings files can be edited by hand: out-of-range values snap to the nearest offered choice (never faster than every 15 minutes),
    // and Automatic Boost without the recorded consent is switched off.
    public BoostSettings Normalized()
    {
        static int Nearest(int value, int[] choices) => choices.OrderBy(choice => Math.Abs(choice - value)).First();
        return new BoostSettings { MonitorJunk = MonitorJunk || (AutoBoost && AutoBoostConsent), AutoBoost = AutoBoost && AutoBoostConsent, AutoBoostConsent = AutoBoostConsent,
            ThresholdMb = Nearest(ThresholdMb, ThresholdChoices), CheckEveryMinutes = Nearest(CheckEveryMinutes, IntervalChoices), LowResource = LowResource };
    }
}

// The one small settings file (theme and Smart Boost). Reads never throw; writes are atomic so a crash cannot leave half a file.
public sealed class Preferences
{
    public bool Light { get; set; }
    // The version of the terms of use the person accepted ("Use at your own risk"); empty until they accept.
    public string TermsAccepted { get; set; } = "";
    // Interface size: "smart" (fits the screen), "75", "100" or "120" (percent). Anything else reads as smart.
    public string UiSize { get; set; } = UiScale.Smart;
    // Pro mode (owner, November 11, 2026): off = Simple mode (the default for everyone, old and new users): Safe items only and a short Settings page; on = everything.
    public bool ProMode { get; set; }
    // The main color of both themes (an id from AccentPalette; anything else reads as the default, the icon's blue).
    public string Accent { get; set; } = AccentPalette.DefaultId;
    // Administrator mode (0.9): null until the person has been asked once; true = Hush always runs elevated, false = never ask again.
    public bool? RunAsAdmin { get; set; }
    // Settings > Administrator (0.9, user request): minimizing hides Hush in the notification area instead of the taskbar, and Hush may open when Windows starts.
    public bool MinimizeToTray { get; set; }
    public bool StartWithWindows { get; set; }
    public BoostSettings Boost { get; set; } = new();

    public static Preferences Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path));
                if (loaded != null) { loaded.Boost = (loaded.Boost ?? new()).Normalized(); loaded.UiSize = UiScale.Normalize(loaded.UiSize); loaded.Accent = AccentPalette.Normalize(loaded.Accent); return loaded; }
            }
        }
        catch { }
        return new();
    }

    // Never throws, but a failed save is noted in the crash log (otherwise the Terms or the administrator question would silently come back next start).
    public void Save(string path, string? dataDirectory = null)
    {
        try
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            if (!CleanupEngine.SafePath(directory, directory)) return;
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, true);
        }
        catch (Exception ex) { CrashLog.Write("settings", ex, dataDirectory); }
    }
}
