using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Hush;

// A line icon drawn on a 24 x 24 grid and scaled to the element's size (owner, November 20, 2026: Software Updates shows a Bluetooth icon for Bluetooth, a
// monitor for graphics and so on). One element draws its geometry once with a rounded 1.7 pen; no effect, no bitmap, nothing animated. Its Stroke is a theme
// brush, so it follows both themes and the main color.
public sealed class GlyphIcon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        if (Data is not Geometry geometry || Stroke is not Brush stroke || ActualWidth <= 0 || ActualHeight <= 0) return;
        double scale = Math.Min(ActualWidth, ActualHeight) / 24;
        drawing.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        drawing.DrawGeometry(null, new Pen(stroke, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
        drawing.Pop();
        drawing.Pop();
    }
}

// The icon of a group heading from its name (Browsers, Chat and meetings ...), for a template that only has the group's name.
public sealed class KindIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => UpdateIcons.Kind(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// The icon tile of a Tweaks row (owner, November 20, 2026: the Software Updates layout for Privacy, Windows tweaks, Services, Tasks and Startup): chosen
// from the row's own topic by plain keywords, with the list's own icon when nothing matches. The whole row is the binding source.
public sealed class TopicIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => UpdateIcons.Of(TopicIcon.For(value));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

internal static class TopicIcon
{
    // Keyword → icon, first match wins (topics are the catalogs' own names, so the words are stable).
    private static readonly (string Word, string Icon)[] Words =
    [
        ("left behind", "Leftover"), ("removed program", "Leftover"),
        ("xbox", ProgramKinds.Games), ("gaming", ProgramKinds.Games), ("games", ProgramKinds.Games),
        ("telemetry", "Telemetry"), ("diagnostic", "Telemetry"), ("error report", "Telemetry"), ("feedback", "Telemetry"),
        ("bluetooth", "Bluetooth"), ("phone", "Phone"), ("sms", "Phone"), ("nearby", "Phone"), ("sensor", "Phone"),
        ("print", "Print"), ("scan", "Print"),
        ("network", "Network"), ("remote", "Network"), ("peer", "Network"), ("sharing", "Network"),
        ("updater", "Updater"), ("update", "Updater"),
        ("search", "Search"), ("ads", "Ads"), ("tips", "Ads"), ("suggestion", "Ads"),
        ("right-click", "Menu"), ("explorer", "Menu"), ("taskbar", "Taskbar"), ("start", "Taskbar"),
        ("performance", "Tweaks"), ("visual", "Tweaks"),
        ("registry", "Registry"), ("folder", "Folder"), ("store", "Store"), ("sign-in", "Tasks"), ("task", "Tasks"), ("service", "Services"),
        ("policy", "Locked"), ("set by windows", "Locked"),
        ("privacy", "Privacy"), ("location", "Privacy"), ("camera", "Privacy"), ("activity", "Privacy"),
    ];

    internal static string For(object? row)
    {
        var (topic, fallback) = row switch
        {
            ServiceChoice service => (service.Candidate.Topic, "Services"),
            TaskChoice task => (task.Candidate.Topic, "Tasks"),
            StartupItemChoice startup => (startup.Candidate.Topic, "Startup"),
            TweakChoice tweak => (tweak.TopicName, "Tweaks"),
            AdjustmentChoice choice => (choice.Topic, "Privacy"),
            _ => ("", "Tweaks")
        };
        string text = topic.ToLowerInvariant();
        foreach (var (word, icon) in Words) if (text.Contains(word, StringComparison.Ordinal)) return icon;
        return fallback;
    }
}

// The icons of Software Updates (24 x 24 grid): one per device type, one for Programs, one per kind of program.
internal static class UpdateIcons
{
    private static readonly Dictionary<string, Geometry> Cache = [];

    private const string Apps = "M5.8 4 H9.2 A1.8 1.8 0 0 1 11 5.8 V9.2 A1.8 1.8 0 0 1 9.2 11 H5.8 A1.8 1.8 0 0 1 4 9.2 V5.8 A1.8 1.8 0 0 1 5.8 4 Z "
        + "M14.8 4 H18.2 A1.8 1.8 0 0 1 20 5.8 V9.2 A1.8 1.8 0 0 1 18.2 11 H14.8 A1.8 1.8 0 0 1 13 9.2 V5.8 A1.8 1.8 0 0 1 14.8 4 Z "
        + "M5.8 13 H9.2 A1.8 1.8 0 0 1 11 14.8 V18.2 A1.8 1.8 0 0 1 9.2 20 H5.8 A1.8 1.8 0 0 1 4 18.2 V14.8 A1.8 1.8 0 0 1 5.8 13 Z "
        + "M14.8 13 H18.2 A1.8 1.8 0 0 1 20 14.8 V18.2 A1.8 1.8 0 0 1 18.2 20 H14.8 A1.8 1.8 0 0 1 13 18.2 V14.8 A1.8 1.8 0 0 1 14.8 13 Z";

    private static readonly Dictionary<string, string> Data = new()
    {
        ["Programs"] = Apps,
        ["Graphics"] = "M5.2 4.5 H18.8 A2.2 2.2 0 0 1 21 6.7 V14.3 A2.2 2.2 0 0 1 18.8 16.5 H5.2 A2.2 2.2 0 0 1 3 14.3 V6.7 A2.2 2.2 0 0 1 5.2 4.5 Z M8.5 20.5 H15.5 M12 16.5 V20.5",
        ["Bluetooth"] = "M7 7 L17 17 L12 22 V2 L17 7 L7 17",
        ["Audio"] = "M4 9.5 V14.5 H7.5 L13 19 V5 L7.5 9.5 Z M16.3 9 A4.2 4.2 0 0 1 16.3 15 M18.6 6.6 A7.6 7.6 0 0 1 18.6 17.4",
        ["Network"] = "M2.5 9.2 A14 14 0 0 1 21.5 9.2 M5.4 12.4 A9.6 9.6 0 0 1 18.6 12.4 M8.4 15.6 A5.2 5.2 0 0 1 15.6 15.6 M12 19 L12.01 19",
        ["Other devices"] = "M8.7 6.5 H15.3 A2.2 2.2 0 0 1 17.5 8.7 V15.3 A2.2 2.2 0 0 1 15.3 17.5 H8.7 A2.2 2.2 0 0 1 6.5 15.3 V8.7 A2.2 2.2 0 0 1 8.7 6.5 Z "
            + "M10 3.5 V6.5 M14 3.5 V6.5 M10 17.5 V20.5 M14 17.5 V20.5 M3.5 10 H6.5 M3.5 14 H6.5 M17.5 10 H20.5 M17.5 14 H20.5",
        // The Tweaks tabs (owner, November 20, 2026: the Software Updates layout reused).
        ["Privacy"] = "M12 3 L19.5 6 V11.5 C19.5 16 16.3 19.6 12 21 C7.7 19.6 4.5 16 4.5 11.5 V6 Z M9 12 L11.2 14.2 L15.2 10",
        ["Tweaks"] = "M4 7 H13 M17 7 H20 M15 4.8 V9.2 M4 17 H7 M11 17 H20 M9 14.8 V19.2",
        ["Services"] = "M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12 Z M12 3.5 V6 M12 18 V20.5 M3.5 12 H6 M18 12 H20.5 M6 6 L7.8 7.8 M16.2 16.2 L18 18 M6 18 L7.8 16.2 M16.2 7.8 L18 6",
        ["Tasks"] = "M20.5 12 A8.5 8.5 0 1 1 3.5 12 A8.5 8.5 0 1 1 20.5 12 Z M12 7 V12 L15.5 14",
        ["Startup"] = "M12 3.5 V11 M7.4 6.4 A7.5 7.5 0 1 0 16.6 6.4",
        // The Tweaks rows' topics.
        ["Telemetry"] = "M4 18 L9 12 L13 15 L20 6 M15.5 6 H20 V10.5",
        ["Phone"] = "M8.2 3 H15.8 A1.7 1.7 0 0 1 17.5 4.7 V19.3 A1.7 1.7 0 0 1 15.8 21 H8.2 A1.7 1.7 0 0 1 6.5 19.3 V4.7 A1.7 1.7 0 0 1 8.2 3 Z M11 18 H13",
        ["Print"] = "M7 9 V3.5 H17 V9 M7 17 H4.5 V10.5 A1.5 1.5 0 0 1 6 9 H18 A1.5 1.5 0 0 1 19.5 10.5 V17 H17 M7 14 H17 V20.5 H7 Z",
        ["Updater"] = "M12 4 V15 M7.5 10.5 L12 15 L16.5 10.5 M5 19.5 H19",
        ["Leftover"] = "M4.5 9 H19.5 V19.5 H4.5 Z M3.5 5 H20.5 V9 H3.5 Z M10 13 H14",
        ["Search"] = "M18 11 A7 7 0 1 1 4 11 A7 7 0 1 1 18 11 Z M16 16 L20.5 20.5",
        ["Ads"] = "M4 10 V14 H7 L13 18 V6 L7 10 Z M16.5 9.5 A3.5 3.5 0 0 1 16.5 14.5",
        ["Menu"] = "M5.5 4.5 H18.5 A1 1 0 0 1 19.5 5.5 V18.5 A1 1 0 0 1 18.5 19.5 H5.5 A1 1 0 0 1 4.5 18.5 V5.5 A1 1 0 0 1 5.5 4.5 Z M8 9 H16 M8 12 H16 M8 15 H13",
        ["Taskbar"] = "M3.5 5 H20.5 V19 H3.5 Z M3.5 15.5 H20.5 M7 17.25 H9",
        // The Startup card's sources.
        ["Registry"] = "M12 12 A4 4 0 1 1 4 12 A4 4 0 1 1 12 12 Z M12 12 H20.5 M17.5 12 V15 M20.5 12 V14.5",
        ["Folder"] = "M3.5 7 A2 2 0 0 1 5.5 5 H9.5 L11.5 7 H18.5 A2 2 0 0 1 20.5 9 V17 A2 2 0 0 1 18.5 19 H5.5 A2 2 0 0 1 3.5 17 Z",
        ["Store"] = "M5 8 H19 L18 20 H6 Z M9 8 V6.5 A3 3 0 0 1 15 6.5 V8",
        ["Locked"] = "M6.5 11 H17.5 A1.5 1.5 0 0 1 19 12.5 V18.5 A1.5 1.5 0 0 1 17.5 20 H6.5 A1.5 1.5 0 0 1 5 18.5 V12.5 A1.5 1.5 0 0 1 6.5 11 Z M8.5 11 V8 A3.5 3.5 0 0 1 15.5 8 V11",
        // Kinds of program (the Programs tab, sorted by kind).
        [ProgramKinds.Browsers] = "M20.5 12 A8.5 8.5 0 1 1 3.5 12 A8.5 8.5 0 1 1 20.5 12 Z M3.5 12 H20.5 M12 3.5 C14.5 6.1 15.7 8.9 15.7 12 C15.7 15.1 14.5 17.9 12 20.5 M12 3.5 C9.5 6.1 8.3 8.9 8.3 12 C8.3 15.1 9.5 17.9 12 20.5",
        [ProgramKinds.Chat] = "M4.5 6.5 A2 2 0 0 1 6.5 4.5 H17.5 A2 2 0 0 1 19.5 6.5 V14.5 A2 2 0 0 1 17.5 16.5 H10 L5.5 20 V16.5 H6.5 A2 2 0 0 1 4.5 14.5 Z",
        [ProgramKinds.Media] = "M20.5 12 A8.5 8.5 0 1 1 3.5 12 A8.5 8.5 0 1 1 20.5 12 Z M10 8.6 L15.5 12 L10 15.4 Z",
        [ProgramKinds.Games] = "M7 8 H17 A4 4 0 0 1 21 12 V13 A3 3 0 0 1 15.9 15.1 L15 14 H9 L8.1 15.1 A3 3 0 0 1 3 13 V12 A4 4 0 0 1 7 8 Z M8 10.5 V13 M6.750 11.750 H9.250 M16 11 L16.01 11 M18 13 L18.01 13",
        [ProgramKinds.Developer] = "M8.5 7.5 L4 12 L8.5 16.5 M15.5 7.5 L20 12 L15.5 16.5 M13.5 5 L10.5 19",
        [ProgramKinds.Utilities] = "M14.5 5.5 A4.5 4.5 0 0 0 20.1 11.1 L11 20.2 A2.1 2.1 0 0 1 8 17.2 L16.5 8.7 Z",
        [ProgramKinds.Other] = Apps,
    };

    internal static Geometry? Of(string key)
    {
        if (!Data.TryGetValue(key, out string? text)) return null;
        if (Cache.TryGetValue(key, out var made)) return made;
        var geometry = Geometry.Parse(text);
        geometry.Freeze();
        return Cache[key] = geometry;
    }

    internal static Geometry? Kind(string name) => Of(name) ?? Of(ProgramKinds.Other);
}

// What kind of program an update is for (owner, November 20, 2026: "by kind of program" instead of the A–E, F–K letter groups). Recognized from the program's
// id (Publisher.Product) and its name; words that start with a keyword count ("visualstudiocode" starts with "visualstudio"), words ending in $ must match exactly.
// Anything not recognized is "Other"; nothing is ever guessed from the publisher alone.
internal static class ProgramKinds
{
    internal const string Browsers = "Browsers", Chat = "Chat and meetings", Media = "Media", Games = "Games", Developer = "Developer tools", Utilities = "Utilities", Other = "Other";
    internal static readonly string[] Order = [Browsers, Chat, Media, Games, Developer, Utilities, Other];

    private static readonly (string Kind, string[] Words)[] Rules =
    [
        (Browsers, ["chrome", "firefox", "edge$", "brave", "opera", "vivaldi", "librewolf", "waterfox", "browser", "tor$"]),
        (Chat, ["discord", "slack", "zoom", "teams", "skype", "telegram", "whatsapp", "signal$", "webex", "element$", "thunderbird", "mattermost", "viber", "messenger", "meet$"]),
        (Media, ["vlc", "spotify", "itunes", "obs$", "obsstudio", "audacity", "handbrake", "foobar", "mpv$", "kodi", "plex", "potplayer", "mediaplayer", "musicbee", "winamp", "gimp", "inkscape", "krita", "blender", "paint$", "photo", "video", "music", "player", "ffmpeg", "yt-dlp"]),
        (Games, ["steam", "epicgames", "gog", "battle.net", "battlenet", "ubisoft", "eaapp", "origin$", "minecraft", "riot", "valve", "game", "launcher", "xbox"]),
        (Developer, ["git$", "github", "gitlab", "visualstudio", "vscode", "vscodium", "python", "node", "openjs", "java", "jdk", "jre", "jetbrains", "intellij", "pycharm", "webstorm", "rider", "docker", "postman", "cmake", "rust", "golang", "dotnet", "sdk", "powershell", "windowsterminal", "putty", "winscp", "sublime", "notepad++", "notepadplusplus", "insomnia", "mingw", "msys", "llvm", "ruby", "php", "mysql", "postgres", "mongodb", "sqlite", "wireshark", "android", "studio$"]),
        (Utilities, ["7zip", "7-zip", "winrar", "peazip", "everything", "powertoys", "sharex", "treesize", "ccleaner", "bitwarden", "keepass", "1password", "teamviewer", "anydesk", "dropbox", "onedrive", "drive$", "googledrive", "nextcloud", "syncthing", "revo", "rufus", "etcher", "filezilla", "greenshot", "lightshot", "autohotkey", "ditto", "clip", "adobe", "acrobat", "reader", "foxit", "sumatra", "libreoffice", "openoffice", "notepad", "vpn", "nord", "proton", "windirstat", "crystaldisk", "hwinfo", "cpuz", "gpuz", "afterburner", "logitech", "razer", "corsair", "nvidia", "intel", "amd"]),
    ];

    internal static string Of(string name, string id)
    {
        var words = (id + "." + name).ToLowerInvariant().Split(new[] { '.', '-', '_', ' ', '(', ')', '+' }, StringSplitOptions.RemoveEmptyEntries);
        string joined = (id + name).ToLowerInvariant().Replace(" ", "");
        foreach (var (kind, keywords) in Rules)
            foreach (string keyword in keywords)
            {
                bool exact = keyword.EndsWith('$');
                string key = exact ? keyword[..^1] : keyword;
                if (words.Any(word => exact ? word == key : word.StartsWith(key, StringComparison.Ordinal))) return kind;
                if (!exact && key.Length >= 6 && joined.Contains(key, StringComparison.Ordinal)) return kind;   // long names glued together ("visualstudiocode")
            }
        return Other;
    }

    internal static int OrderOf(string kind) => Math.Max(0, Array.IndexOf(Order, kind));
}
