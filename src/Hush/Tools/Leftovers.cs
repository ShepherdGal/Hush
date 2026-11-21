using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Hush;

// What a program left behind after its own uninstaller ran (owner, November 19, 2026; Pro only). Three depths, chosen in the popup (Moderate by default):
//   Safe     - its install folder, folders named exactly like it in the usual places, its own Start menu entries and its Uninstall entry if it stayed.
//   Moderate - also its registry settings named exactly like it (and its publisher's folder or key when nothing else of that publisher is left),
//              and start-up entries that point into its folder.
//   Deep     - also folders and keys whose name only starts with or contains its name: more to read, nothing of it selected for you.
// Only Safe findings start ticked. Never offered at any depth: a drive root, Windows, Program Files or AppData themselves, personal folders, anything another
// installed program still uses, and the registry's Microsoft, Windows, Classes and Policies keys.
internal enum LeftoverDepth { Safe, Moderate, Deep }
internal enum LeftoverKind { Folder, File, RegistryKey, RunValue }

internal sealed record Leftover(LeftoverKind Kind, string Path, long Bytes, bool Safe, string Why)
{
    internal string Group => Kind switch { LeftoverKind.Folder or LeftoverKind.File => "Files and folders", LeftoverKind.RegistryKey => "Registry keys", _ => "Startup entries" };
    // Changing this needs administrator rights: all-users registry, Program Files, ProgramData and the all-users Start menu.
    internal bool NeedsAdministrator => Kind is LeftoverKind.RegistryKey or LeftoverKind.RunValue
        ? Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)
        : LeftoverPlaces.AdminFolders.Any(root => root.Length > 0 && LeftoverPlaces.IsInside(Path, root));
}

// Where leftovers are looked for. The real places, or temp folders and a fixture registry key in tests.
internal sealed record LeftoverPlaces(IReadOnlyList<string> Folders, IReadOnlyList<string> ShortcutFolders, IReadOnlyList<string> RegistrySoftware, IReadOnlyList<string> RunKeys, IReadOnlyList<string> Protected)
{
    private static string Special(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
    internal static string[] AdminFolders => [Special(Environment.SpecialFolder.ProgramFiles), Special(Environment.SpecialFolder.ProgramFilesX86), Special(Environment.SpecialFolder.CommonApplicationData), Special(Environment.SpecialFolder.CommonPrograms), Special(Environment.SpecialFolder.CommonDesktopDirectory)];

    internal static LeftoverPlaces Default()
    {
        string local = Special(Environment.SpecialFolder.LocalApplicationData);
        string[] folders = [Special(Environment.SpecialFolder.ProgramFiles), Special(Environment.SpecialFolder.ProgramFilesX86), Special(Environment.SpecialFolder.CommonProgramFiles), Special(Environment.SpecialFolder.CommonApplicationData),
            Special(Environment.SpecialFolder.ApplicationData), local, Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"), Path.Combine(local, "Programs")];
        string[] shortcuts = [Special(Environment.SpecialFolder.Programs), Special(Environment.SpecialFolder.CommonPrograms), Special(Environment.SpecialFolder.DesktopDirectory), Special(Environment.SpecialFolder.CommonDesktopDirectory)];
        string[] software = [@"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node"];
        string[] run = [@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"];
        string profile = Special(Environment.SpecialFolder.UserProfile);
        string[] protectedFolders = [.. folders, .. shortcuts, profile, Path.GetDirectoryName(profile) ?? profile, Special(Environment.SpecialFolder.Windows), Special(Environment.SpecialFolder.System),
            Special(Environment.SpecialFolder.MyDocuments), Special(Environment.SpecialFolder.MyPictures), Special(Environment.SpecialFolder.MyMusic), Special(Environment.SpecialFolder.MyVideos), Special(Environment.SpecialFolder.Desktop),
            Path.Combine(profile, "Downloads"), Path.Combine(profile, "OneDrive"), Path.Combine(local, "Microsoft"), Path.Combine(Special(Environment.SpecialFolder.ApplicationData), "Microsoft"), Path.Combine(local, "Temp"), CleanupEngine.DataDirectory];
        return new(folders.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), shortcuts.Where(f => f.Length > 0).ToList(), software, run, protectedFolders.Where(f => f.Length > 0).ToList());
    }

    internal static bool IsInside(string path, string root)
    {
        string full = Normalize(path), folder = Normalize(root);
        return full.Length > folder.Length && full.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
    }

    internal static string Normalize(string path) { try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; } }
}

internal sealed class LeftoverFinder(LeftoverPlaces places)
{
    private static readonly string[] NeverKeys = ["Microsoft", "Windows", "Classes", "Policies", "Clients", "RegisteredApplications", "WOW6432Node", "Wow6432Node", "Hush", "Hush.TestFixtures"];
    private static readonly Regex Noise = new(@"\(.*?\)|\b(x64|x86|64-bit|32-bit|amd64|win64|win32|version|v?\d+(\.\d+)+)\b", RegexOptions.IgnoreCase);
    private static readonly Regex CompanyTail = new(@"[,.]?\s*\b(inc|ltd|llc|limited|corp|corporation|gmbh|co|s\.?a|b\.?v|ag|pty|plc)\b\.?$", RegexOptions.IgnoreCase);

    // remaining: the programs still installed, so nothing they use is ever offered.
    internal List<Leftover> Find(InstalledProgram program, LeftoverDepth depth, IReadOnlyList<InstalledProgram> remaining, bool uninstallEntryLeft)
    {
        var found = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);
        var names = Names(program);
        string publisher = CleanPublisher(program.Publisher);
        bool publisherShared = publisher.Length > 0 && remaining.Any(other => CleanPublisher(other.Publisher).Equals(publisher, StringComparison.OrdinalIgnoreCase));
        var otherLocations = remaining.Select(other => other.InstallLocation).OfType<string>().Where(path => path.Length > 3).Select(LeftoverPlaces.Normalize).ToList();
        var otherNames = remaining.SelectMany(Names).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? location = program.InstallLocation is { } given && Directory.Exists(given) ? LeftoverPlaces.Normalize(given) : null;

        void Add(Leftover item)
        {
            if (!found.ContainsKey(item.Path) && Allowed(item)) found[item.Path] = item;
        }
        bool Allowed(Leftover item)
        {
            if (item.Kind is LeftoverKind.Folder or LeftoverKind.File)
            {
                string path = LeftoverPlaces.Normalize(item.Path);
                if (Path.GetPathRoot(path) is { } root && LeftoverPlaces.Normalize(root).Equals(path, StringComparison.OrdinalIgnoreCase)) return false;
                // Never a protected folder itself or one that holds a protected folder; never anything inside Microsoft's, Temp or Hush's own folders.
                if (places.Protected.Any(p => LeftoverPlaces.Normalize(p).Equals(path, StringComparison.OrdinalIgnoreCase) || LeftoverPlaces.IsInside(p, path))) return false;
                if (places.Protected.Select(LeftoverPlaces.Normalize).Where(p => p.EndsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase) || p.EndsWith("\\Temp", StringComparison.OrdinalIgnoreCase) || p.EndsWith("\\Hush", StringComparison.OrdinalIgnoreCase))
                    .Any(p => LeftoverPlaces.IsInside(path, p))) return false;
                // Never something another installed program lives in, or a folder that holds one.
                if (otherLocations.Any(other => other.Equals(path, StringComparison.OrdinalIgnoreCase) || LeftoverPlaces.IsInside(other, path) || LeftoverPlaces.IsInside(path, other))) return false;
                return true;
            }
            if (item.Kind == LeftoverKind.RunValue) return true;
            string last = item.Path[(item.Path.LastIndexOf('\\') + 1)..];
            return !NeverKeys.Contains(last, StringComparer.OrdinalIgnoreCase) && item.Path.Count(c => c == '\\') >= 2;
        }

        // 1. Its install folder and its own Uninstall entry.
        if (location != null) Add(new(LeftoverKind.Folder, location, FolderBytes(location), true, "Its install folder"));
        if (uninstallEntryLeft) Add(new(LeftoverKind.RegistryKey, program.Id, 0, true, "Its entry in the list of installed programs"));
        // 2. Folders named exactly like it in the usual places, also under its publisher's folder.
        foreach (string root in places.Folders)
        {
            foreach (string name in names)
            {
                string direct = Path.Combine(root, name);
                if (Directory.Exists(direct)) Add(new(LeftoverKind.Folder, direct, FolderBytes(direct), true, "A folder with its name"));
                if (publisher.Length == 0) continue;
                string underPublisher = Path.Combine(root, publisher, name);
                if (Directory.Exists(underPublisher)) Add(new(LeftoverKind.Folder, underPublisher, FolderBytes(underPublisher), true, "A folder with its name, in its publisher's folder"));
            }
        }
        // 3. Its Start menu folder and shortcuts: named like it, or pointing into its install folder at a file that is gone.
        foreach (string root in places.ShortcutFolders.Where(Directory.Exists))
        {
            foreach (string name in names)
            {
                string folder = Path.Combine(root, name), link = Path.Combine(root, name + ".lnk");
                if (Directory.Exists(folder)) Add(new(LeftoverKind.Folder, folder, FolderBytes(folder), true, "Its Start menu folder"));
                if (File.Exists(link)) Add(new(LeftoverKind.File, link, SafeLength(link), true, "A shortcut with its name"));
            }
            if (location == null && program.InstallLocation == null) continue;
            string target = location ?? LeftoverPlaces.Normalize(program.InstallLocation!);
            foreach (string link in SafeFiles(root, "*.lnk", depth: 2))
                if (ShortcutTarget.Read(link) is { } points && LeftoverPlaces.IsInside(points, target) && !File.Exists(points))
                    Add(new(LeftoverKind.File, link, SafeLength(link), true, "A shortcut to a file that was removed"));
        }
        if (depth == LeftoverDepth.Safe) return Sorted(found.Values);

        // 4. Moderate: its registry settings named exactly like it.
        foreach (string software in places.RegistrySoftware)
        {
            foreach (string name in names)
            {
                if (RegistryPaths.Exists(software + "\\" + name)) Add(new(LeftoverKind.RegistryKey, software + "\\" + name, 0, false, "Settings with its name"));
                if (publisher.Length > 0 && RegistryPaths.Exists(software + "\\" + publisher + "\\" + name)) Add(new(LeftoverKind.RegistryKey, software + "\\" + publisher + "\\" + name, 0, false, "Settings with its name, under its publisher"));
            }
            // The publisher's own key, only when nothing else of that publisher is installed and the key holds nothing but this program.
            if (publisher.Length > 0 && !publisherShared && !otherNames.Contains(publisher) && KeyHoldsOnly(software + "\\" + publisher, names))
                Add(new(LeftoverKind.RegistryKey, software + "\\" + publisher, 0, false, "Its publisher's settings (nothing else of that publisher is installed)"));
        }
        if (publisher.Length > 0 && !publisherShared && !otherNames.Contains(publisher))
            foreach (string root in places.Folders)
            {
                string folder = Path.Combine(root, publisher);
                if (Directory.Exists(folder) && FolderHoldsOnly(folder, names)) Add(new(LeftoverKind.Folder, folder, FolderBytes(folder), false, "Its publisher's folder (nothing else of that publisher is installed)"));
            }
        // Start-up entries that point into its folder, or at a missing file named like it.
        foreach (string run in places.RunKeys)
        {
            using var key = RegistryPaths.Open(run);
            if (key == null) continue;
            foreach (string value in key.GetValueNames())
            {
                if (key.GetValue(value) is not string command || UninstallCommand.Split(Environment.ExpandEnvironmentVariables(command)) is not { } split) continue;
                bool intoFolder = program.InstallLocation is { Length: > 3 } folder && LeftoverPlaces.IsInside(split.Exe, folder);
                bool named = !File.Exists(split.Exe) && names.Any(name => split.Exe.Contains(name, StringComparison.OrdinalIgnoreCase) || value.Equals(name, StringComparison.OrdinalIgnoreCase));
                if ((intoFolder || named) && !File.Exists(split.Exe)) Add(new(LeftoverKind.RunValue, run + "|" + value, 0, false, "Starts with Windows, but its program is gone"));
            }
        }
        if (depth == LeftoverDepth.Moderate) return Sorted(found.Values);

        // 5. Deep: names that start with or contain its name (at least five letters, and not the name of something still installed).
        var tokens = names.Select(name => name.Replace(" ", "")).Where(token => token.Length >= 5).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool Matches(string candidate) { string flat = candidate.Replace(" ", ""); return tokens.Any(token => flat.Contains(token, StringComparison.OrdinalIgnoreCase)) && !otherNames.Contains(candidate); }
        foreach (string root in places.Folders.Where(Directory.Exists))
            foreach (string folder in SafeDirectories(root).Where(folder => Matches(Path.GetFileName(folder))))
                Add(new(LeftoverKind.Folder, folder, FolderBytes(folder), false, "A folder whose name matches it"));
        foreach (string software in places.RegistrySoftware)
        {
            using var key = RegistryPaths.Open(software);
            if (key == null) continue;
            foreach (string name in SafeKeyNames(key).Where(Matches)) Add(new(LeftoverKind.RegistryKey, software + "\\" + name, 0, false, "Settings whose name matches it"));
        }
        return Sorted(found.Values);
    }

    private static List<Leftover> Sorted(IEnumerable<Leftover> items) => items.OrderBy(item => item.Kind).ThenByDescending(item => item.Safe).ThenByDescending(item => item.Bytes).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();

    // The names a program's leftovers are found by: its name without version or bitness, its registry key (unless a product code), its install folder's name.
    internal static List<string> Names(InstalledProgram program)
    {
        var names = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = Regex.Replace(value.Trim(), @"\s+", " ").Trim(' ', '-', '_', '.');
            if (value.Length < 3 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            if (!names.Contains(value, StringComparer.OrdinalIgnoreCase)) names.Add(value);
        }
        Add(program.Name);
        Add(Noise.Replace(program.Name, " "));
        if (!(program.KeyName.StartsWith('{') && program.KeyName.EndsWith('}'))) Add(Regex.Replace(program.KeyName, @"(_is1|\.exe)$", "", RegexOptions.IgnoreCase));
        if (program.InstallLocation is { Length: > 3 } location) Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(location)));
        return names;
    }

    internal static string CleanPublisher(string publisher)
    {
        string clean = publisher.Trim();
        for (int i = 0; i < 3; i++) clean = CompanyTail.Replace(clean, "").Trim(' ', ',', '.');
        return clean.Length >= 3 && clean.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? clean : "";
    }

    private static bool KeyHoldsOnly(string path, List<string> names)
    {
        using var key = RegistryPaths.Open(path);
        if (key == null) return false;
        var subKeys = SafeKeyNames(key);
        return key.ValueCount == 0 && subKeys.Length > 0 && subKeys.All(sub => names.Contains(sub, StringComparer.OrdinalIgnoreCase));
    }

    private static bool FolderHoldsOnly(string path, List<string> names)
    {
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).ToList();
            return entries.All(entry => entry != null && names.Contains(entry, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static long FolderBytes(string path)
    {
        long total = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options)) total += file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    private static long SafeLength(string file) { try { return new FileInfo(file).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; } }
    private static string[] SafeKeyNames(RegistryKey key) { try { return key.GetSubKeyNames(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; } }
    private static IEnumerable<string> SafeDirectories(string root) { try { return Directory.EnumerateDirectories(root).ToList(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; } }
    private static IEnumerable<string> SafeFiles(string root, string pattern, int depth)
    {
        try { return new DirectoryInfo(root).EnumerateFiles(pattern, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Select(f => f.FullName).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

// The target of a .lnk file, read through the shell's own link object (no file is started).
internal static class ShortcutTarget
{
    internal static string? Read(string link)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (type == null || Activator.CreateInstance(type) is not IShellLinkW shell) return null;
            try
            {
                ((IPersistFile)shell).Load(link, 0);
                var buffer = new char[1024];
                shell.GetPath(buffer, buffer.Length, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
                string path = new string(buffer).TrimEnd('\0');
                return path.Length == 0 ? null : Environment.ExpandEnvironmentVariables(path);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException) { return null; }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] file, int max, IntPtr data, int flags);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, int mode);
    }
}

// Removing what the person ticked: everything is backed up first (files into a zip, registry keys exported, start-up values recorded), each item is checked
// again just before it goes, links are removed as links, a file in use is deleted at the next restart when Hush is administrator, and one line per item is
// logged to Logs\uninstall.jsonl. A backup can be restored from the Programs card.
internal sealed record LeftoverReport(int Removed, int AtRestart, int Failed, long Bytes, string? BackupFolder, IReadOnlyList<string> Problems)
{
    internal string Text => Removed + AtRestart == 0 && Failed == 0 ? "Nothing was removed."
        : $"Removed {Removed} item{(Removed == 1 ? "" : "s")}" + (Bytes > 0 ? $" ({InstalledProgram.FormatBytes(Bytes)})" : "")
          + (AtRestart > 0 ? $"; {AtRestart} in use will go at the next restart" : "")
          + (Failed > 0 ? $"; {Failed} could not be removed" : "") + ". A backup was kept, so it can be restored from the Programs card.";
}

internal sealed record LeftoverBackup(string Folder, string Program, DateTime When, long Bytes, int Items, bool Restored);

internal sealed class LeftoverRemover(string backupRoot, string logFile, LeftoverPlaces places, bool elevated)
{
    private sealed record Entry(LeftoverKind Kind, string Path, string? Zip, string? Reg, string? ValueName, string? ValueData, bool Expand);
    private sealed record Manifest(string Program, DateTime When, long Bytes, List<Entry> Entries, bool Restored);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal LeftoverReport Remove(string programName, IReadOnlyList<Leftover> chosen)
    {
        var problems = new List<string>();
        int removed = 0, atRestart = 0, failed = 0; long bytes = 0;
        if (chosen.Count == 0) return new(0, 0, 0, 0, null, problems);
        string folder = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + " " + SafeName(programName));
        Directory.CreateDirectory(folder);
        var entries = new List<Entry>();
        // 1. Back everything up first; an item whose backup fails is not removed.
        var ready = new List<(Leftover Item, Entry Entry)>();
        using (var zip = ZipFile.Open(Path.Combine(folder, "files.zip"), ZipArchiveMode.Create))
        {
            for (int i = 0; i < chosen.Count; i++)
            {
                var item = chosen[i];
                if (!StillAllowed(item)) { problems.Add(item.Path + ": no longer safe to remove, left alone"); failed++; continue; }
                try
                {
                    Entry entry = item.Kind switch
                    {
                        LeftoverKind.Folder => BackupFolder(zip, i, item.Path),
                        LeftoverKind.File => BackupFile(zip, i, item.Path),
                        LeftoverKind.RegistryKey => BackupKey(folder, i, item.Path),
                        _ => BackupValue(item.Path)
                    };
                    entries.Add(entry); ready.Add((item, entry));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                { problems.Add(item.Path + ": backup failed (" + ex.Message + "), left alone"); failed++; }
            }
        }
        long backedUp = new FileInfo(Path.Combine(folder, "files.zip")).Length;
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new Manifest(programName, DateTime.Now, backedUp, entries, false), Json));
        // 2. Remove, checking each item again just before.
        foreach (var (item, _) in ready)
        {
            string outcome;
            try
            {
                switch (item.Kind)
                {
                    case LeftoverKind.Folder: int later = DeleteTree(item.Path); outcome = later > 0 ? "at restart" : "removed"; if (later > 0) atRestart++; else removed++; bytes += item.Bytes; break;
                    case LeftoverKind.File: if (!DeleteFile(item.Path)) { atRestart++; outcome = "at restart"; } else { removed++; outcome = "removed"; } bytes += item.Bytes; break;
                    case LeftoverKind.RegistryKey: DeleteKey(item.Path); removed++; outcome = "removed"; break;
                    default: DeleteValue(item.Path); removed++; outcome = "removed"; break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            { failed++; outcome = "failed: " + ex.Message; problems.Add(item.Path + ": " + ex.Message); }
            Log(new { time = DateTime.Now, program = programName, kind = item.Kind.ToString(), path = item.Path, outcome, backup = folder });
        }
        return new(removed, atRestart, failed, bytes, folder, problems);
    }

    private bool StillAllowed(Leftover item) => item.Kind switch
    {
        LeftoverKind.Folder => Directory.Exists(item.Path) && !places.Protected.Any(p => LeftoverPlaces.Normalize(p).Equals(LeftoverPlaces.Normalize(item.Path), StringComparison.OrdinalIgnoreCase) || LeftoverPlaces.IsInside(p, item.Path)),
        LeftoverKind.File => File.Exists(item.Path),
        LeftoverKind.RegistryKey => RegistryPaths.Exists(item.Path) && item.Path.Count(c => c == '\\') >= 2,
        _ => true
    };

    private static Entry BackupFolder(ZipArchive zip, int index, string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            try { zip.CreateEntryFromFile(file.FullName, $"{index}/{Path.GetRelativePath(folder, file.FullName).Replace('\\', '/')}", CompressionLevel.Fastest); }
            catch (IOException) { /* a file in use cannot be read: it is skipped in the backup and usually deleted at restart */ }
        }
        return new(LeftoverKind.Folder, folder, index.ToString(), null, null, null, false);
    }

    private static Entry BackupFile(ZipArchive zip, int index, string file)
    {
        zip.CreateEntryFromFile(file, $"{index}/{Path.GetFileName(file)}", CompressionLevel.Fastest);
        return new(LeftoverKind.File, file, index.ToString(), null, null, null, false);
    }

    private static Entry BackupKey(string folder, int index, string key)
    {
        string file = Path.Combine(folder, index + ".reg");
        if (RegTool("export", $"\"{key}\" \"{file}\" /y /reg:64") != 0 || !File.Exists(file)) throw new IOException("the registry key could not be exported");
        return new(LeftoverKind.RegistryKey, key, null, index + ".reg", null, null, false);
    }

    private static Entry BackupValue(string path)
    {
        var (keyPath, name) = SplitValue(path);
        using var key = RegistryPaths.Open(keyPath) ?? throw new IOException("the start-up key is gone");
        var kind = key.GetValueKind(name);
        string data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? throw new IOException("the start-up value is not text");
        return new(LeftoverKind.RunValue, path, null, null, name, data, kind == RegistryValueKind.ExpandString);
    }

    // Deletes a folder tree: links are removed as links and never entered. Returns how many files were left for the next restart.
    private int DeleteTree(string path)
    {
        int later = 0;
        void Walk(DirectoryInfo directory)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { if (entry is DirectoryInfo link) link.Delete(false); else entry.Delete(); continue; }
                if (entry is DirectoryInfo child) Walk(child);
                else if (!DeleteFile(entry.FullName)) later++;
            }
            try { directory.Delete(false); }
            catch (IOException) when (later > 0) { if (elevated) MoveFileEx(directory.FullName, null, 4); }
        }
        var root = new DirectoryInfo(path);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0) { root.Delete(false); return 0; }
        Walk(root);
        if (later > 0 && !elevated) throw new IOException($"{later} file(s) are in use; close the program or turn on administrator mode to remove them at the next restart");
        return later;
    }

    // True when the file is gone now; false when it is in use and will be deleted at the next restart.
    private bool DeleteFile(string file)
    {
        try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); return true; }
        catch (IOException) when (elevated && MoveFileEx(file, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */)) { return false; }
        catch (IOException) when (!elevated) { return false; }
    }

    private static void DeleteKey(string path)
    {
        int slash = path.LastIndexOf('\\');
        using var parent = RegistryPaths.Open(path[..slash], writable: true) ?? throw new IOException("its parent key cannot be opened");
        parent.DeleteSubKeyTree(path[(slash + 1)..], throwOnMissingSubKey: false);
    }

    private static void DeleteValue(string path)
    {
        var (keyPath, name) = SplitValue(path);
        using var key = RegistryPaths.Open(keyPath, writable: true) ?? throw new IOException("the start-up key cannot be opened");
        key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static (string Key, string Name) SplitValue(string path) { int bar = path.LastIndexOf('|'); return (path[..bar], path[(bar + 1)..]); }

    internal IReadOnlyList<LeftoverBackup> Backups()
    {
        if (!Directory.Exists(backupRoot)) return [];
        var list = new List<LeftoverBackup>();
        foreach (string folder in Directory.EnumerateDirectories(backupRoot))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(folder, "manifest.json")));
                if (manifest != null) list.Add(new(folder, manifest.Program, manifest.When, manifest.Bytes, manifest.Entries.Count, manifest.Restored));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(backup => backup.When).ToList();
    }

    // Puts a backup back: files only where nothing exists now (never overwrites), registry keys through reg.exe import, start-up values as they were.
    internal LeftoverReport Restore(string folder)
    {
        string full = LeftoverPlaces.Normalize(folder);
        if (!LeftoverPlaces.IsInside(full, backupRoot)) throw new IOException("That backup is not one of Hush's.");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(full, "manifest.json"))) ?? throw new InvalidDataException("The backup's list is empty.");
        var problems = new List<string>();
        int restored = 0, failed = 0;
        using var zip = File.Exists(Path.Combine(full, "files.zip")) ? ZipFile.OpenRead(Path.Combine(full, "files.zip")) : null;
        foreach (var entry in manifest.Entries)
        {
            try
            {
                if (!Path.IsPathFullyQualified(entry.Path.Split('|')[0]) && entry.Kind is LeftoverKind.Folder or LeftoverKind.File) throw new InvalidDataException("the original place is not a full path");
                switch (entry.Kind)
                {
                    case LeftoverKind.Folder or LeftoverKind.File when zip != null && entry.Zip != null:
                        string prefix = entry.Zip + "/";
                        foreach (var file in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0))
                        {
                            string relative = file.FullName[prefix.Length..].Replace('/', '\\');
                            string target = entry.Kind == LeftoverKind.File ? entry.Path : Path.GetFullPath(Path.Combine(entry.Path, relative));
                            if (entry.Kind == LeftoverKind.Folder && !LeftoverPlaces.IsInside(target, entry.Path)) continue; // never outside the original folder
                            if (File.Exists(target)) continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            file.ExtractToFile(target);
                        }
                        if (entry.Kind == LeftoverKind.Folder) Directory.CreateDirectory(entry.Path);
                        break;
                    case LeftoverKind.RegistryKey when entry.Reg != null:
                        if (RegTool("import", $"\"{Path.Combine(full, entry.Reg)}\" /reg:64") != 0) throw new IOException("the registry key could not be imported");
                        break;
                    case LeftoverKind.RunValue when entry.ValueName != null && entry.ValueData != null:
                        using (var key = RegistryPaths.Open(SplitValue(entry.Path).Key, writable: true) ?? throw new IOException("the start-up key cannot be opened"))
                            key.SetValue(entry.ValueName, entry.ValueData, entry.Expand ? RegistryValueKind.ExpandString : RegistryValueKind.String);
                        break;
                }
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException or ArgumentException)
            { failed++; problems.Add(entry.Path + ": " + ex.Message); }
        }
        File.WriteAllText(Path.Combine(full, "manifest.json"), JsonSerializer.Serialize(manifest with { Restored = true }, Json));
        Log(new { time = DateTime.Now, program = manifest.Program, restored, failed, backup = full });
        return new(restored, 0, failed, 0, full, problems);
    }

    internal void DeleteBackup(string folder)
    {
        string full = LeftoverPlaces.Normalize(folder);
        if (!LeftoverPlaces.IsInside(full, backupRoot)) throw new IOException("That backup is not one of Hush's.");
        Directory.Delete(full, recursive: true);
    }

    private void Log(object line)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(logFile)!); File.AppendAllText(logFile, JsonSerializer.Serialize(line) + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string SafeName(string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length > 60 ? clean[..60] : clean.Length == 0 ? "program" : clean;
    }

    // reg.exe exports and imports exactly what Registry Editor does; it runs hidden and is never used for cleaning.
    private static int RegTool(string verb, string arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"), verb + " " + arguments)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start);
        if (process == null) return -1;
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
        return process.WaitForExit(30_000) ? process.ExitCode : -1;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existing, string? replacement, int flags);
}
