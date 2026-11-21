using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Hush;

// Settings > Windows tweaks (owner, November 19, 2026; Pro only): one-switch changes to how Windows looks and behaves, in three topics the owner chose
// (right-click menu and File Explorer, taskbar and Start, ads and tips). Security-weakening tweaks (UAC, SmartScreen) are deliberately not offered, and
// the ones the Privacy & telemetry card already has (Bing in search, widgets, Edge in the background) are not repeated here.
// Each tweak is a few registry values. Before the first change every original is saved (Windows settings\tweaks-changed.json), the result is read back and
// each change is logged (Logs\tweaks.jsonl). Switching a tweak back off puts the saved originals back exactly (keys Hush created are removed again when
// empty); a tweak that was on before Hush saw it is switched off to Windows' default. Changes go through the one Review changes button with everything else.
internal sealed record TweakValue(bool Machine, string Key, string Name, object? On, object? Off, RegistryValueKind Kind, string? PruneTo = null);
internal sealed record Tweak(string Id, string Topic, string Title, string Help, TweakValue[] Values, bool Explorer, int MinBuild = 0, string? RequiresFile = null)
{
    internal bool NeedsAdministrator => Values.Any(value => value.Machine);
}
internal sealed record TweakState(Tweak Tweak, bool On, bool ChangedByHush, bool Available);
internal sealed record TweakReport(int Done, List<string> Problems)
{
    internal string Text(bool on) => (Done == 0 && Problems.Count == 0 ? "" : $"Windows tweaks: {Done} {(on ? "applied" : "switched back")} and checked.") + (Problems.Count > 0 ? " Could not change: " + string.Join("; ", Problems) + "." : "");
}

internal sealed class WindowsTweaks(RegistryKey user, string userPrefix, RegistryKey machine, string machinePrefix, string historyFolder, string logFile, bool elevated)
{
    internal static WindowsTweaks Create(string historyFolder, string logFile) =>
        new(Registry.CurrentUser, "", RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), "", historyFolder, logFile, AdminMode.Elevation.IsElevated);

    internal const string TopicExplorer = "Right-click menu and File Explorer", TopicTaskbar = "Taskbar, Start and menus", TopicAds = "Ads, tips and suggestions in Windows",
        TopicPerformance = "Performance and visual effects", TopicNetwork = "Gaming and network";
    internal static readonly string[] TopicOrder = [TopicExplorer, TopicTaskbar, TopicAds, TopicPerformance, TopicNetwork];
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Exp = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private static TweakValue U(string key, string name, object? on, object? off, RegistryValueKind kind = RegistryValueKind.DWord, string? pruneTo = null) => new(false, key, name, on, off, kind, pruneTo);
    private static TweakValue M(string key, string name, object? on, object? off, RegistryValueKind kind = RegistryValueKind.DWord, string? pruneTo = null) => new(true, key, name, on, off, kind, pruneTo);
    private static TweakValue[] Verb(string classBase, string verb, string label, string command, string? icon = null)
    {
        string key = $@"Software\Classes\{classBase}\shell\{verb}", prune = $@"Software\Classes\{classBase}\shell";
        var values = new List<TweakValue> { U(key, "MUIVerb", label, null, RegistryValueKind.String, prune), U(key, "HasLUAShield", "", null, RegistryValueKind.String, prune), U(key + @"\command", "", command, null, RegistryValueKind.String, prune) };
        if (icon != null) values.Add(U(key, "Icon", icon, null, RegistryValueKind.String, prune));
        return values.ToArray();
    }
    private const string TakeOwnFile = "powershell.exe -NoProfile -WindowStyle Hidden -Command \"Start-Process cmd.exe -ArgumentList '/c takeown /f \\\"%1\\\" && icacls \\\"%1\\\" /grant *S-1-3-4:F /c /l' -Verb RunAs\"";
    private const string TakeOwnFolder = "powershell.exe -NoProfile -WindowStyle Hidden -Command \"Start-Process cmd.exe -ArgumentList '/c takeown /f \\\"%1\\\" /r /d y && icacls \\\"%1\\\" /grant *S-1-3-4:F /t /c /l /q' -Verb RunAs\"";
    private const string AdminCmd = "powershell.exe -NoProfile -WindowStyle Hidden -Command \"Start-Process cmd.exe -ArgumentList '/s,/k,pushd,\\\"%V\\\"' -Verb RunAs\"";
    private static readonly string PhotoViewer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Photo Viewer", "PhotoViewer.dll");

    internal static readonly Tweak[] Catalog =
    [
        new("classic-menu", TopicExplorer, "Classic right-click menu", "Right-clicking shows the full menu straight away, as in Windows 10, instead of the short Windows 11 menu with \"Show more options\". Shows after File Explorer restarts or you sign out.",
            [U(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", null, RegistryValueKind.String, @"Software\Classes\CLSID")], Explorer: true, MinBuild: 22000),
        new("take-ownership", TopicExplorer, "\"Take ownership\" in the right-click menu", "Adds \"Take ownership\" for files and folders: it gives your account full access to them (Windows asks for permission each time). Useful for files left by an old Windows or another PC; use it with care on system files.",
            [.. Verb("*", "HushTakeOwnership", "Take ownership", TakeOwnFile), .. Verb("Directory", "HushTakeOwnership", "Take ownership", TakeOwnFolder)], Explorer: false),
        new("admin-cmd", TopicExplorer, "\"Command Prompt as administrator\" in the right-click menu", "Adds \"Open Command Prompt here as administrator\" to folders and to the empty space inside a folder (Windows asks for permission each time).",
            [.. Verb("Directory", "HushAdminCmd", "Open Command Prompt here as administrator", AdminCmd, "cmd.exe"), .. Verb(@"Directory\Background", "HushAdminCmd", "Open Command Prompt here as administrator", AdminCmd, "cmd.exe")], Explorer: false),
        new("show-extensions", TopicExplorer, "Show file name extensions", "File Explorer shows the end of file names (.pdf, .exe, .jpg). It helps you spot a program pretending to be a document.",
            [U(Adv, "HideFileExt", 0, 1)], Explorer: true),
        new("drive-letters-first", TopicExplorer, "Drive letters before drive names", "This PC shows \"(C:) Local Disk\" instead of \"Local Disk (C:)\".",
            [U(Exp, "ShowDriveLettersFirst", 4, null)], Explorer: true),
        new("no-shortcut-suffix", TopicExplorer, "No \"- Shortcut\" at the end of new shortcuts", "New shortcuts are named like the thing they open, without \"- Shortcut\" added. Existing shortcuts keep their names.",
            [U(Exp + @"\NamingTemplates", "ShortcutNameTemplate", "%s.lnk", null, RegistryValueKind.String, Exp)], Explorer: true),
        new("no-shortcut-arrow", TopicExplorer, "No arrow on shortcut icons", "Removes the small arrow drawn on shortcut icons, for every account. Shows after File Explorer restarts or you sign out. Needs administrator mode.",
            [M(Exp + @"\Shell Icons", "29", @"%windir%\System32\shell32.dll,-50", null, RegistryValueKind.ExpandString, Exp)], Explorer: true),
        new("photo-viewer", TopicExplorer, "Classic Windows Photo Viewer for pictures", "Makes the classic Windows Photo Viewer available again for JPG, PNG, BMP, GIF and TIFF pictures. Then choose it once in a picture's \"Open with\" menu (Windows only lets you pick the default yourself). Needs administrator mode.",
            [.. new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" }.Select(ext => M(@"Software\Microsoft\Windows Photo Viewer\Capabilities\FileAssociations", ext, "PhotoViewer.FileAssoc.Tiff", null, RegistryValueKind.String))], Explorer: false, RequiresFile: PhotoViewer),
        new("end-task", TopicTaskbar, "\"End task\" when you right-click a taskbar button", "Adds \"End task\" to the menu of an app's taskbar button, to close an app that stopped responding without opening Task Manager.",
            [U(Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, null, RegistryValueKind.DWord, Adv)], Explorer: false, MinBuild: 22631),
        new("clock-seconds", TopicTaskbar, "Seconds in the taskbar clock", "The clock next to the date also shows seconds. It uses a little more power on laptops.",
            [U(Adv, "ShowSecondsInSystemClock", 1, null)], Explorer: true),
        new("align-left", TopicTaskbar, "Taskbar icons on the left", "The Start button and app icons sit at the left of the taskbar, as in Windows 10, instead of in the middle.",
            [U(Adv, "TaskbarAl", 0, null)], Explorer: false, MinBuild: 22000),
        new("classic-alt-tab", TopicTaskbar, "Classic Alt+Tab", "Alt+Tab shows small icons in a grid, as in older Windows, instead of large window previews.",
            [U(Exp, "AltTabSettings", 1, null)], Explorer: true),
        new("faster-menus", TopicTaskbar, "Menus open without the delay", "Sub-menus open after 0.1 seconds instead of 0.4 when you point at them. Takes effect after you sign out.",
            [U(@"Control Panel\Desktop", "MenuShowDelay", "100", "400", RegistryValueKind.String)], Explorer: false),
        new("faster-hover", TopicTaskbar, "Tooltips and taskbar previews appear sooner", "Tooltips and the window previews above taskbar buttons appear after 0.1 seconds instead of about 0.4. Takes effect after you sign out.",
            [U(@"Control Panel\Mouse", "MouseHoverTime", "100", "400", RegistryValueKind.String), U(Adv, "ExtendedUIHoverTime", 100, null)], Explorer: false),
        new("start-ads", TopicAds, "No suggested apps in Start", "Start stops showing suggested apps and recommended content from Microsoft.",
            [U(Cdm, "SystemPaneSuggestionsEnabled", 0, 1), U(Cdm, "SubscribedContent-338388Enabled", 0, 1), U(Adv, "Start_IrisRecommendations", 0, null)], Explorer: true),
        new("lock-screen-ads", TopicAds, "No tips and ads on the lock screen", "The lock screen stops showing fun facts, tips and promotions on top of the picture.",
            [U(Cdm, "RotatingLockScreenOverlayEnabled", 0, 1), U(Cdm, "SubscribedContent-338387Enabled", 0, 1)], Explorer: false),
        new("tips-ads", TopicAds, "No tips, tricks and suggestions", "Windows stops showing tips and suggestions as notifications, in Settings, and File Explorer stops showing offers from storage providers.",
            [U(Cdm, "SoftLandingEnabled", 0, 1), U(Cdm, "SubscribedContent-338389Enabled", 0, 1), U(Cdm, "SubscribedContent-338393Enabled", 0, 1), U(Cdm, "SubscribedContent-353694Enabled", 0, 1), U(Cdm, "SubscribedContent-353696Enabled", 0, 1), U(Adv, "ShowSyncProviderNotifications", 0, null)], Explorer: true),
        // Phase C (owner, November 19, 2026: "only real ones, honestly"): tweaks that measurably change something; each says what it really does and nothing more.
        new("no-transparency", TopicPerformance, "Turn off transparency effects", "Windows draws the taskbar, Start and windows solid instead of see-through. On a weak graphics chip this saves a little work for every frame; on a strong PC you will not notice a difference.",
            [U(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1)], Explorer: false),
        new("no-window-animation", TopicPerformance, "No animation when windows open, minimize or maximize", "Windows appear and disappear at once instead of zooming. It makes Windows feel quicker to respond; it does not make programs run faster. Takes effect after you sign out.",
            [U(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1", RegistryValueKind.String)], Explorer: false),
        new("no-background-apps", TopicPerformance, "Store apps don't run in the background", "Apps from the Microsoft Store can no longer run when you are not using them (no live tiles, background sync or notifications from them). Saves a little battery and memory. Programs you install from the web are not affected. Windows 10 and early Windows 11.",
            [U(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, null)], Explorer: false),
        new("game-mode", TopicNetwork, "Game Mode on", "While a game runs, Windows gives it priority and holds back Windows Update installs and some notifications. It helps most on PCs with few processor cores; it is on by default in most Windows versions.",
            [U(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, null)], Explorer: false),
        new("gpu-scheduling", TopicNetwork, "Hardware-accelerated graphics scheduling", "Lets the graphics card manage its own memory. It can lower delay a little in games on recent graphics cards and drivers; on older ones Windows ignores it. Needs a restart and administrator mode.",
            [M(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, null)], Explorer: false, MinBuild: 19041),
        new("network-throttling", TopicNetwork, "No network slow-down while media plays", "Windows normally holds back other network traffic while audio or video plays. This turns that off, which can help downloads and online games while music plays. It does not make your internet connection faster. Needs administrator mode.",
            [M(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", -1, null)], Explorer: false),
    ];

    private sealed record Original(bool Machine, string Key, string Name, bool Existed, RegistryValueKind Kind, string? Text, long? Number, List<string> CreatedKeys);
    private string HistoryFile => Path.Combine(historyFolder, "tweaks-changed.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private RegistryKey Root(TweakValue value) => value.Machine ? machine : user;
    private string Full(TweakValue value, string key) => (value.Machine ? machinePrefix : userPrefix) + key;
    private static int Build => Environment.OSVersion.Version.Build;

    internal IReadOnlyList<TweakState> Scan()
    {
        var history = LoadHistory();
        return Catalog.Select(tweak => new TweakState(tweak, IsOn(tweak), history.ContainsKey(tweak.Id),
            Build >= tweak.MinBuild && (tweak.RequiresFile == null || File.Exists(tweak.RequiresFile)))).ToList();
    }

    internal bool CanChange(Tweak tweak) => elevated || !tweak.NeedsAdministrator;

    private bool IsOn(Tweak tweak) => tweak.Values.All(value => Matches(value, value.On));

    private bool Matches(TweakValue value, object? wanted)
    {
        try
        {
            using var key = Root(value).OpenSubKey(Full(value, value.Key));
            object? current = key?.GetValue(value.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (wanted == null) return current == null;
            return (wanted, current) switch
            {
                (int w, int c) => w == c,
                (string w, string c) => string.Equals(w, c, StringComparison.Ordinal),
                _ => false
            };
        }
        catch (Exception ex) when (Ignorable(ex)) { return false; }
    }

    // Switches the given tweaks on (on = true) or back off. Only catalog ids; each change is read back.
    internal TweakReport Change(IReadOnlyList<string> ids, bool on)
    {
        var problems = new List<string>();
        int done = 0;
        var history = LoadHistory();
        foreach (string id in ids.Distinct())
        {
            var tweak = Catalog.FirstOrDefault(item => item.Id == id);
            if (tweak == null) { problems.Add(id + " (not a Hush tweak)"); continue; }
            if (!CanChange(tweak)) { problems.Add(tweak.Title + " (needs administrator mode)"); continue; }
            try
            {
                if (on)
                {
                    if (!history.ContainsKey(id)) { history[id] = tweak.Values.Select(Capture).ToList(); SaveHistory(history); } // originals first
                    foreach (var value in tweak.Values) Write(value, value.On);
                }
                else if (history.TryGetValue(id, out var originals))
                {
                    foreach (var original in originals) Restore(original);
                    foreach (var original in originals.Where(o => o.CreatedKeys.Count > 0)) Prune(original);
                    history.Remove(id); SaveHistory(history);
                }
                else
                {
                    foreach (var value in tweak.Values) Write(value, value.Off);
                    foreach (var value in tweak.Values.Where(v => v.PruneTo != null && v.Off == null)) PruneEmpty(value, value.Key);
                }
                bool nowOn = IsOn(tweak);
                if (nowOn != on) { problems.Add(tweak.Title + " (Windows did not keep the change)"); continue; }
                Log(new { time = DateTime.Now, tweak = id, on });
                done++;
            }
            catch (Exception ex) when (Ignorable(ex)) { problems.Add($"{tweak.Title} ({ex.Message})"); }
        }
        return new(done, problems);
    }

    private Original Capture(TweakValue value)
    {
        // Which keys on the way to the value do not exist yet (they are removed again when the tweak is switched back off and they are empty).
        var created = new List<string>();
        string path = value.Key;
        string stop = value.PruneTo ?? value.Key;
        while (path.Length >= stop.Length)
        {
            using (var probe = Root(value).OpenSubKey(Full(value, path))) if (probe == null) created.Add(path);
            int slash = path.LastIndexOf('\\');
            if (slash < 0 || path.Equals(stop, StringComparison.OrdinalIgnoreCase)) break;
            path = path[..slash];
        }
        using var key = Root(value).OpenSubKey(Full(value, value.Key));
        object? current = key?.GetValue(value.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = current == null ? value.Kind : key!.GetValueKind(value.Name);
        return new(value.Machine, value.Key, value.Name, current != null, kind, current as string, current switch { int i => i, long l => l, _ => null }, created);
    }

    private void Write(TweakValue value, object? data)
    {
        if (data == null)
        {
            using var existing = Root(value).OpenSubKey(Full(value, value.Key), writable: true);
            existing?.DeleteValue(value.Name, throwOnMissingValue: false);
            return;
        }
        using var key = Root(value).CreateSubKey(Full(value, value.Key), writable: true) ?? throw new IOException("the key cannot be opened");
        key.SetValue(value.Name, data, value.Kind);
    }

    private void Restore(Original original)
    {
        var root = original.Machine ? machine : user;
        string path = (original.Machine ? machinePrefix : userPrefix) + original.Key;
        if (!original.Existed)
        {
            using var key = root.OpenSubKey(path, writable: true);
            key?.DeleteValue(original.Name, throwOnMissingValue: false);
            return;
        }
        using var target = root.CreateSubKey(path, writable: true) ?? throw new IOException("the key cannot be opened");
        object data = original.Kind switch
        {
            RegistryValueKind.DWord => (int)(original.Number ?? 0),
            RegistryValueKind.QWord => original.Number ?? 0L,
            _ => original.Text ?? ""
        };
        target.SetValue(original.Name, data, original.Kind is RegistryValueKind.DWord or RegistryValueKind.QWord or RegistryValueKind.ExpandString ? original.Kind : RegistryValueKind.String);
    }

    // Removes the keys this tweak created, deepest first, and only while they are empty.
    private void Prune(Original original)
    {
        var root = original.Machine ? machine : user;
        foreach (string key in original.CreatedKeys.OrderByDescending(k => k.Length)) DeleteIfEmpty(root, (original.Machine ? machinePrefix : userPrefix) + key);
    }

    private void PruneEmpty(TweakValue value, string key)
    {
        string path = key;
        while (value.PruneTo != null && path.Length > value.PruneTo.Length && path.StartsWith(value.PruneTo, StringComparison.OrdinalIgnoreCase))
        {
            if (!DeleteIfEmpty(Root(value), Full(value, path))) break;
            path = path[..path.LastIndexOf('\\')];
        }
    }

    private static bool DeleteIfEmpty(RegistryKey root, string path)
    {
        using (var key = root.OpenSubKey(path))
        {
            if (key == null) return true;
            if (key.ValueCount > 0 || key.SubKeyCount > 0) return false;
        }
        int slash = path.LastIndexOf('\\');
        using var parent = root.OpenSubKey(path[..slash], writable: true);
        parent?.DeleteSubKey(path[(slash + 1)..], throwOnMissingSubKey: false);
        return true;
    }

    private Dictionary<string, List<Original>> LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFile) || new FileInfo(HistoryFile).Length > 256 * 1024) return [];
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<Original>>>(File.ReadAllText(HistoryFile)) ?? [];
            // Allow-list: only catalog tweaks, and only the exact places their values live.
            return loaded.Where(pair => Catalog.FirstOrDefault(t => t.Id == pair.Key) is { } tweak
                    && pair.Value.All(o => tweak.Values.Any(v => v.Machine == o.Machine && v.Key == o.Key && v.Name == o.Name)
                        && o.CreatedKeys.All(created => tweak.Values.Any(v => v.Machine == o.Machine && (v.Key.StartsWith(created, StringComparison.OrdinalIgnoreCase) && (v.PruneTo == null || created.Length > v.PruneTo.Length))))))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return []; }
    }

    private void SaveHistory(Dictionary<string, List<Original>> history)
    {
        Directory.CreateDirectory(historyFolder);
        string temp = HistoryFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(history, Json));
        File.Move(temp, HistoryFile, overwrite: true);
    }

    private void Log(object line)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(logFile)!); File.AppendAllText(logFile, JsonSerializer.Serialize(line) + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool Ignorable(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException;
}
