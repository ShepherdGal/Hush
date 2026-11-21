using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Hush;

// Tools > Browser extensions (owner, November 19, 2026; Pro only): the extensions in each browser profile, read from the profile's own files while the card
// is open. Firefox-family extensions are removed by Hush (their package file is backed up, then deleted; the browser drops it at its next start) and only while
// the browser is closed. Chromium browsers sign their extension settings, so a file removed behind their back can come back "repaired": for those Hush opens
// the browser's own Extensions page at that extension, where one click removes it for good. Extensions a policy installed are shown but locked.
internal sealed record BrowserExtension(string Browser, string Profile, string Id, string Name, string Version, string Location, bool Chromium, bool ByPolicy, string ProfilePath, string Process)
{
    internal string Detail => string.Join(" · ", new[] { Browser, Profile, Version.Length > 0 ? "version " + Version : "", ByPolicy ? "installed by a policy" : "" }.Where(part => part.Length > 0));
}

internal sealed record BrowserRoot(string Browser, string Folder, bool Chromium, string Process, string? Exe);

internal sealed class BrowserExtensionFinder(IReadOnlyList<BrowserRoot> roots)
{
    internal static IReadOnlyList<BrowserRoot> DefaultRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            new("Chrome", Path.Combine(local, @"Google\Chrome\User Data"), true, "chrome", AppPath("chrome.exe")),
            new("Edge", Path.Combine(local, @"Microsoft\Edge\User Data"), true, "msedge", AppPath("msedge.exe")),
            new("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), true, "brave", AppPath("brave.exe")),
            new("Vivaldi", Path.Combine(local, @"Vivaldi\User Data"), true, "vivaldi", AppPath("vivaldi.exe")),
            new("Opera", Path.Combine(roaming, @"Opera Software\Opera Stable"), true, "opera", AppPath("opera.exe")),
            new("Firefox", Path.Combine(roaming, @"Mozilla\Firefox\Profiles"), false, "firefox", null),
            new("Waterfox", Path.Combine(roaming, @"Waterfox\Profiles"), false, "waterfox", null),
            new("LibreWolf", Path.Combine(roaming, @"librewolf\Profiles"), false, "librewolf", null),
        ];
    }

    private static string? AppPath(string exe)
    {
        foreach (string root in new[] { "HKCU", "HKLM" })
        {
            using var key = RegistryPaths.Open($@"{root}\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
        }
        return null;
    }

    internal IReadOnlyList<BrowserExtension> List()
    {
        var found = new List<BrowserExtension>();
        foreach (var root in roots.Where(root => Directory.Exists(root.Folder)))
        {
            try { found.AddRange(root.Chromium ? Chromium(root) : Firefox(root)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return found.OrderBy(e => e.Browser).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<BrowserExtension> Chromium(BrowserRoot root)
    {
        // A profile folder holds "Preferences"; Opera keeps one profile in the root itself.
        var profiles = Directory.EnumerateDirectories(root.Folder).Where(d => File.Exists(Path.Combine(d, "Preferences"))).ToList();
        if (File.Exists(Path.Combine(root.Folder, "Preferences"))) profiles.Insert(0, root.Folder);
        foreach (string profile in profiles)
        {
            string extensions = Path.Combine(profile, "Extensions");
            if (!Directory.Exists(extensions)) continue;
            var settings = ChromiumSettings(profile);
            string profileName = ChromiumProfileName(root.Folder, profile);
            foreach (string folder in Directory.EnumerateDirectories(extensions))
            {
                string id = Path.GetFileName(folder);
                if (id.Length != 32 || id.Equals("Temp", StringComparison.OrdinalIgnoreCase)) continue;
                string? versionFolder = Directory.EnumerateDirectories(folder).Where(d => File.Exists(Path.Combine(d, "manifest.json"))).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
                if (versionFolder == null) continue;
                var (name, version) = Manifest(versionFolder);
                if (name == null) continue;
                settings.TryGetValue(id, out int location);
                // Locations: 5 and 10 are the browser's own components (never listed); 6 and 9 mean a policy installed it.
                if (location is 5 or 10) continue;
                yield return new(root.Browser, profileName, id, name, version, folder, true, location is 6 or 9, profile, root.Process);
            }
        }
    }

    private static Dictionary<string, int> ChromiumSettings(string profile)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in new[] { "Secure Preferences", "Preferences" })
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, file)));
                if (!json.RootElement.TryGetProperty("extensions", out var ext) || !ext.TryGetProperty("settings", out var all)) continue;
                foreach (var entry in all.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("location", out var location) && location.TryGetInt32(out int value)) map[entry.Name] = value;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return map;
    }

    private static string ChromiumProfileName(string root, string profile)
    {
        if (profile == root) return "Default";
        string folder = Path.GetFileName(profile);
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Local State")));
            if (json.RootElement.GetProperty("profile").GetProperty("info_cache").TryGetProperty(folder, out var info) && info.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } text) return text;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException) { }
        return folder;
    }

    // The name and version from manifest.json; a "__MSG_x__" name is looked up in its default locale.
    private static (string? Name, string Version) Manifest(string folder)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = json.RootElement;
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            if (name.StartsWith("__MSG_", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal))
            {
                string key = name[6..^2], locale = root.TryGetProperty("default_locale", out var l) ? l.GetString() ?? "en" : "en";
                string messages = Path.Combine(folder, "_locales", locale, "messages.json");
                name = key;
                if (File.Exists(messages))
                {
                    using var text = JsonDocument.Parse(File.ReadAllText(messages), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    foreach (var entry in text.RootElement.EnumerateObject())
                        if (entry.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && entry.Value.TryGetProperty("message", out var message)) { name = message.GetString() ?? key; break; }
                }
            }
            return (name.Length == 0 ? null : name, version);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { return (null, ""); }
    }

    private static IEnumerable<BrowserExtension> Firefox(BrowserRoot root)
    {
        foreach (string profile in Directory.EnumerateDirectories(root.Folder))
        {
            string file = Path.Combine(profile, "extensions.json");
            if (!File.Exists(file)) continue;
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            if (!json.RootElement.TryGetProperty("addons", out var addons)) continue;
            string profileName = Path.GetFileName(profile);
            int dot = profileName.IndexOf('.');
            if (dot >= 0) profileName = profileName[(dot + 1)..];
            foreach (var addon in addons.EnumerateArray())
            {
                string Get(string property) => addon.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                if (Get("type") != "extension") continue;
                string location = Get("location");
                if (location is "app-builtin" or "app-temporary" || location.StartsWith("app-system", StringComparison.Ordinal)) continue; // built into the browser ("app-profile" is the person's own)
                string name = addon.TryGetProperty("defaultLocale", out var locale) && locale.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string path = Get("path");
                if (name.Length == 0 || path.Length == 0) continue;
                yield return new(root.Browser, profileName, Get("id"), name, Get("version"), path, false, location.Contains("policy", StringComparison.OrdinalIgnoreCase), profile, root.Process);
            }
        }
    }

    internal static bool BrowserRunning(string process) { var running = Process.GetProcessesByName(process); foreach (var p in running) p.Dispose(); return running.Length > 0; }

    // Firefox family: back up the package file, then delete it (the browser forgets an extension whose file is gone at its next start).
    internal static string? Remove(BrowserExtension extension, string backupRoot, Func<string, bool> running)
    {
        if (extension.Chromium) return "Chromium browsers remove their own extensions.";
        if (extension.ByPolicy) return "A policy installed it, so only that policy can remove it.";
        if (running(extension.Process)) return $"{extension.Browser} is open. Close it first, then remove the extension.";
        string location = extension.Location;
        if (!LeftoverPlaces.IsInside(location, extension.ProfilePath)) return "Its file is outside the browser profile, so Hush leaves it alone.";
        try
        {
            string backup = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + " " + extension.Browser);
            Directory.CreateDirectory(backup);
            if (File.Exists(location)) { File.Copy(location, Path.Combine(backup, Path.GetFileName(location))); File.Delete(location); }
            else if (Directory.Exists(location)) { Directory.Move(location, Path.Combine(backup, Path.GetFileName(location))); }
            else return "Its file is already gone.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "It could not be removed: " + ex.Message; }
    }
}
