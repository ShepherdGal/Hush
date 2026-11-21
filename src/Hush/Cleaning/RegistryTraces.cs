using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Hush;

// Pro-mode registry cleaning (owner, November 14, 2026: "more privacy traces", "registry issues"; user policy: Pro may offer what the
// old never-touch rules forbade, always Advanced, opt-in, with a backup). Everything here is in the current user's registry (HKCU, no administrator rights):
//   - privacy traces: history lists Windows and common apps keep (Explorer's Run box, address bar and search history, open/save dialog history, Regedit's last
//     key, MMC, Paint, WordPad, Media Player, WinRAR, 7-Zip and MPC recent lists). The list is cleared; the files it names are never touched;
//   - registry issues: entries that point at a file or program that is CERTAINLY gone (fully qualified path, ready fixed drive, file missing, and for COM
//     classes its folder too): the program-name cache, sound events, ActiveX/COM classes, and file types whose program type no longer exists. A path with an
//     unexpanded variable, on a network or removable drive, or under the Windows folder counts as "cannot tell" and is left alone.
// Like the App Paths leftovers: the scan exports exactly what would go, the clean re-checks the rule and the finding, writes the export as a .reg backup
// (double-click restores it), checks again that nothing changed, and only then deletes. A rule id the catalog does not know can never delete anything.
internal enum TraceOp { ClearValues, ClearTree, DeleteValue, DeleteKey }

// Machine: the rule reads the all-users registry (HKLM, owner, November 19, 2026: "this account + all users"). Its findings need administrator mode to be
// removed; without it they are listed but locked (they cannot be ticked) and say so.
internal sealed record TraceRule(string Id, string Category, string Group, string KeyPath, TraceOp Op, string[]? ValueNames = null, bool Machine = false);

internal sealed class RegistryTraces(RegistryKey root, string prefix = "", RegistryKey? machine = null, string machinePrefix = "", bool elevated = false)
{
    private static readonly Lazy<RegistryKey> MachineView = new(() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64));
    internal static RegistryTraces Real => new(Registry.CurrentUser, "", MachineView.Value, "", AdminMode.Elevation.IsElevated);
    private RegistryKey? Open(TraceRule rule, string path, bool writable = false) => rule.Machine ? machine?.OpenSubKey(machinePrefix + path, writable) : root.OpenSubKey(prefix + path, writable);
    private static string Hive(TraceRule rule) => rule.Machine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall", RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where right-click commands for every file, folder, drive and the desktop live (a program's own file type is left to its file-type rule).
    private static readonly string[] MenuBases = ["*", "Directory", @"Directory\Background", "Folder", "Drive", "AllFilesystemObjects", "DesktopBackground"];
    // Keys under Software that are never "empty program keys", whatever they hold.
    private static readonly HashSet<string> NeverEmpty = new(StringComparer.OrdinalIgnoreCase) { "Microsoft", "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications", "AppDataLow", "Hush", "Hush.TestFixtures", "Windows", "Intel", "AMD", "NVIDIA Corporation" };
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";
    private const string Applets = @"Software\Microsoft\Windows\CurrentVersion\Applets\";

    // Privacy lists: one item per list that holds something.
    internal static readonly TraceRule[] Lists =
    [
        new("run-mru", "ExplorerHistory", "Run box history", Explorer + "RunMRU", TraceOp.ClearValues),
        new("typed-paths", "ExplorerHistory", "File Explorer address bar history", Explorer + "TypedPaths", TraceOp.ClearValues),
        new("word-wheel", "ExplorerHistory", "File Explorer search history", Explorer + "WordWheelQuery", TraceOp.ClearTree),
        new("opensave", "ExplorerHistory", "Open and Save dialog history", Explorer + @"ComDlg32\OpenSavePidlMRU", TraceOp.ClearTree),
        new("last-visited", "ExplorerHistory", "Open and Save dialog folders", Explorer + @"ComDlg32\LastVisitedPidlMRU", TraceOp.ClearValues),
        new("cid-size", "ExplorerHistory", "Programs that used the Open dialog", Explorer + @"ComDlg32\CIDSizeMRU", TraceOp.ClearValues),
        new("regedit-last", "ExplorerHistory", "Registry Editor's last key", Applets + "Regedit", TraceOp.ClearValues, ["LastKey"]),
        new("mmc", "ExplorerHistory", "Management Console recent consoles", @"Software\Microsoft\Microsoft Management Console\Recent File List", TraceOp.ClearValues),
        new("paint", "AppRecentLists", "Paint", Applets + @"Paint\Recent File List", TraceOp.ClearValues),
        new("wordpad", "AppRecentLists", "WordPad", Applets + @"Wordpad\Recent File List", TraceOp.ClearValues),
        new("wmp-files", "AppRecentLists", "Windows Media Player", @"Software\Microsoft\MediaPlayer\Player\RecentFileList", TraceOp.ClearValues),
        new("wmp-urls", "AppRecentLists", "Windows Media Player (web addresses)", @"Software\Microsoft\MediaPlayer\Player\RecentURLList", TraceOp.ClearValues),
        new("winrar", "AppRecentLists", "WinRAR", @"Software\WinRAR\ArcHistory", TraceOp.ClearValues),
        new("winrar-names", "AppRecentLists", "WinRAR (archive names)", @"Software\WinRAR\DialogEditHistory\ArcName", TraceOp.ClearValues),
        new("winrar-paths", "AppRecentLists", "WinRAR (extract folders)", @"Software\WinRAR\DialogEditHistory\ExtrPath", TraceOp.ClearValues),
        new("7zip-archives", "AppRecentLists", "7-Zip (archives)", @"Software\7-Zip\Compression", TraceOp.ClearValues, ["ArcHistory"]),
        new("7zip-paths", "AppRecentLists", "7-Zip (extract folders)", @"Software\7-Zip\Extraction", TraceOp.ClearValues, ["PathHistory"]),
        new("7zip-folders", "AppRecentLists", "7-Zip (folders)", @"Software\7-Zip\FM", TraceOp.ClearValues, ["FolderHistory", "CopyHistory"]),
        new("mpc-hc", "AppRecentLists", "Media Player Classic", @"Software\MPC-HC\MPC-HC\Recent File List", TraceOp.ClearValues),
        new("mpc-be", "AppRecentLists", "MPC-BE", @"Software\MPC-BE\Recent File List", TraceOp.ClearValues),
        // Phase C (owner, November 19, 2026): File Explorer's own caches. Explorer rebuilds them; folder views go back to their defaults.
        new("shellbags", "ExplorerCaches", "Folder view settings", @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU", TraceOp.ClearTree),
        new("shellbags-views", "ExplorerCaches", "Folder view settings (views)", @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags", TraceOp.ClearTree),
        new("menu-order", "ExplorerCaches", "Start menu and favourites order", Explorer + "MenuOrder", TraceOp.ClearTree),
        new("tray-icons", "ExplorerCaches", "Past notification-area icons", @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify", TraceOp.ClearValues, ["IconStreams", "PastIconsStream"]),
        new("user-assist", "ExplorerCaches", "Program usage history", Explorer + "UserAssist", TraceOp.ClearTree),
    ];

    // Registry issues: one item per finding. KeyPath is where the findings live; a finding is a value of that key (DeleteValue) or a key under it (DeleteKey).
    internal static readonly TraceRule[] Issues =
    [
        new("mui-cache", "RegMuiCache", "Program names of removed programs", @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", TraceOp.DeleteValue),
        new("sounds", "RegSounds", "Sounds that point to missing files", @"AppEvents\Schemes\Apps", TraceOp.DeleteValue),
        new("com", "RegComClasses", "ActiveX and COM leftovers", @"Software\Classes\CLSID", TraceOp.DeleteKey),
        new("file-types", "RegFileTypes", "File types of removed programs", @"Software\Classes", TraceOp.DeleteKey),
        // Phase B (owner, November 19, 2026): more issue types, in this account and (Machine) for all users.
        new("uninstall", "RegUninstall", "Uninstall entries of removed programs", UninstallKey, TraceOp.DeleteKey),
        new("uninstall-m", "RegUninstall", "Uninstall entries of removed programs (all users)", UninstallKey, TraceOp.DeleteKey, Machine: true),
        new("uninstall-m32", "RegUninstall", "Uninstall entries of removed programs (all users, 32-bit)", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", TraceOp.DeleteKey, Machine: true),
        new("shared-dlls", "RegSharedDlls", "Shared DLLs that no longer exist", @"Software\Microsoft\Windows\CurrentVersion\SharedDLLs", TraceOp.DeleteValue, Machine: true),
        new("installer-folders", "RegInstallerFolders", "Installer folders that no longer exist", @"Software\Microsoft\Windows\CurrentVersion\Installer\Folders", TraceOp.DeleteValue, Machine: true),
        new("context-menu", "RegContextMenu", "Right-click commands of removed programs", @"Software\Classes", TraceOp.DeleteKey),
        new("context-menu-m", "RegContextMenu", "Right-click commands of removed programs (all users)", @"Software\Classes", TraceOp.DeleteKey, Machine: true),
        new("run-missing", "RegStartupMissing", "Startup entries whose program is gone", RunKey, TraceOp.DeleteValue),
        new("run-missing-m", "RegStartupMissing", "Startup entries whose program is gone (all users)", RunKey, TraceOp.DeleteValue, Machine: true),
        new("run-missing-m32", "RegStartupMissing", "Startup entries whose program is gone (all users, 32-bit)", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", TraceOp.DeleteValue, Machine: true),
        new("empty-keys", "RegEmptyKeys", "Empty program keys", "Software", TraceOp.DeleteKey),
    ];

    internal static IEnumerable<TraceRule> All => Lists.Concat(Issues);
    internal const string RootPrefix = "reg:";
    private const int MaxFindingsPerRule = 2000;

    // ----- scanning (read-only) -----

    internal void Scan(List<CleanupItem> found, CancellationToken token)
    {
        foreach (var rule in Lists)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                using var key = Open(rule, rule.KeyPath);
                if (key == null) continue;
                int count = CountList(key, rule);
                if (count == 0) continue;
                found.Add(Item(rule, rule.KeyPath, null, Export(key, rule.KeyPath, rule), count, null));
            }
            catch (Exception ex) when (Ignorable(ex)) { }
        }
        foreach (var rule in Issues)
        {
            if (token.IsCancellationRequested) return;
            if (rule.Machine && machine == null) continue;
            try { foreach (var item in Findings(rule, token).Take(MaxFindingsPerRule)) found.Add(item); }
            catch (Exception ex) when (Ignorable(ex)) { }
        }
    }

    private CleanupItem Item(TraceRule rule, string keyPath, string? valueName, string snapshot, long count, string? missing) => new()
    {
        Path = (rule.Machine ? @"HKLM\" : @"HKCU\") + keyPath + (valueName == null ? "" : @"\" + (valueName.Length == 0 ? "(Default)" : valueName)),
        Root = RootPrefix + rule.Id, Group = rule.Group, Kind = CleanupKind.Registry, CategoryId = rule.Category, ItemCount = count,
        RegistrySnapshot = snapshot, RegistryExecutable = missing, RegistryValue = valueName, RegistryKeyPath = keyPath, Selected = false,
        Locked = rule.Machine && !elevated,
        Detail = (missing != null ? "Points to " + missing : count == 1 ? "1 entry" : $"{count:N0} entries") + (rule.Machine && !elevated ? " · needs administrator mode" : ""),
    };

    private static int CountList(RegistryKey key, TraceRule rule) => rule.ValueNames is { } names
        ? names.Count(name => key.GetValue(name) != null)
        : key.GetValueNames().Count(name => name.Length > 0) + (rule.Op == TraceOp.ClearTree ? key.SubKeyCount : 0);

    private IEnumerable<CleanupItem> Findings(TraceRule rule, CancellationToken token)
    {
        using var key = Open(rule, rule.KeyPath);
        if (key == null) yield break;
        string hive = Hive(rule);
        switch (rule.Id)
        {
            case "uninstall" or "uninstall-m" or "uninstall-m32":
                foreach (string name in key.GetSubKeyNames())
                {
                    if (token.IsCancellationRequested) yield break;
                    if (name.IndexOfAny(['\r', '\n', '[', ']']) >= 0) continue;
                    using var entry = key.OpenSubKey(name);
                    if (entry == null || BrokenUninstall(name, entry) is not { } missingFile) continue;
                    string path = rule.KeyPath + @"\" + name;
                    if (TryExportTree(entry, path, hive) is { } snapshot) yield return Item(rule, path, null, snapshot, 1, missingFile);
                }
                break;
            case "shared-dlls" or "installer-folders":
                foreach (string name in key.GetValueNames())
                {
                    if (token.IsCancellationRequested) yield break;
                    if (name.Length > 3 && CertainlyMissing(name.TrimEnd('\\'), folderToo: false))
                        yield return Item(rule, rule.KeyPath, name, ExportValue(key, rule.KeyPath, name, hive), 1, name);
                }
                break;
            case "context-menu" or "context-menu-m":
                foreach (string menuBase in MenuBases)
                {
                    using var shell = key.OpenSubKey(menuBase + @"\shell");
                    if (shell == null) continue;
                    foreach (string verb in shell.GetSubKeyNames())
                    {
                        if (token.IsCancellationRequested) yield break;
                        if (verb.IndexOfAny(['\\', '\r', '\n', '[', ']']) >= 0) continue;
                        using var verbKey = shell.OpenSubKey(verb);
                        if (verbKey == null || BrokenCommand(verbKey) is not { } missingFile) continue;
                        string path = rule.KeyPath + @"\" + menuBase + @"\shell\" + verb;
                        if (TryExportTree(verbKey, path, hive) is { } snapshot) yield return Item(rule, path, null, snapshot, 1, missingFile);
                    }
                }
                break;
            case "run-missing" or "run-missing-m" or "run-missing-m32":
                foreach (string name in key.GetValueNames())
                {
                    if (token.IsCancellationRequested) yield break;
                    if (name.Length == 0 || name.Equals(RunKeyStartup.ValueName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (key.GetValue(name) is string command && ServiceTuning.ExecutablePath(command) is { } exe && CertainlyMissing(exe, folderToo: false))
                        yield return Item(rule, rule.KeyPath, name, ExportValue(key, rule.KeyPath, name, hive), 1, exe);
                }
                break;
            case "empty-keys":
                foreach (string name in key.GetSubKeyNames())
                {
                    if (token.IsCancellationRequested) yield break;
                    if (!EmptyCandidate(name)) continue;
                    using var sub = key.OpenSubKey(name);
                    if (sub == null || sub.ValueCount != 0 || sub.SubKeyCount != 0) continue;
                    string path = rule.KeyPath + @"\" + name;
                    yield return Item(rule, path, null, Header(path, hive), 1, null);
                }
                break;
            case "mui-cache":
                foreach (string name in key.GetValueNames())
                {
                    if (token.IsCancellationRequested) yield break;
                    if (MuiProgram(name) is { } program && CertainlyMissing(program, folderToo: false))
                        yield return Item(rule, rule.KeyPath, name, ExportValue(key, rule.KeyPath, name), 1, program);
                }
                break;
            case "sounds":
                foreach (string app in key.GetSubKeyNames())
                {
                    using var appKey = key.OpenSubKey(app);
                    if (appKey == null) continue;
                    foreach (string sound in appKey.GetSubKeyNames())
                    {
                        if (token.IsCancellationRequested) yield break;
                        string path = rule.KeyPath + @"\" + app + @"\" + sound + @"\.Current";
                        using var current = root.OpenSubKey(prefix + path);
                        if (current?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string file && SoundFile(file) is { } full && CertainlyMissing(full, folderToo: false))
                            yield return Item(rule, path, "", ExportValue(current, path, ""), 1, full);
                    }
                }
                break;
            case "com":
                foreach (string clsid in key.GetSubKeyNames())
                {
                    if (token.IsCancellationRequested) yield break;
                    if (!IsGuid(clsid)) continue;
                    using var classKey = key.OpenSubKey(clsid);
                    if (classKey == null || ComServer(classKey) is not { } server || !CertainlyMissing(server, folderToo: true)) continue;
                    string path = rule.KeyPath + @"\" + clsid;
                    if (TryExportTree(classKey, path) is { } snapshot) yield return Item(rule, path, null, snapshot, 1, server);
                }
                break;
            case "file-types":
                foreach (string extension in key.GetSubKeyNames())
                {
                    if (token.IsCancellationRequested) yield break;
                    if (!IsExtension(extension)) continue;
                    using var extensionKey = key.OpenSubKey(extension);
                    if (extensionKey == null || !OrphanFileType(extensionKey, key)) continue;
                    string path = rule.KeyPath + @"\" + extension;
                    if (TryExportTree(extensionKey, path) is { } snapshot) yield return Item(rule, path, null, snapshot, 1, (string)extensionKey.GetValue("")!);
                }
                break;
        }
    }

    // "C:\Program Files\Old\old.exe.FriendlyAppName" -> "C:\Program Files\Old\old.exe" (the cache names a program, then what it stores about it).
    internal static string? MuiProgram(string valueName)
    {
        int at = valueName.LastIndexOf(".exe.", StringComparison.OrdinalIgnoreCase);
        return at > 0 ? valueName[..(at + 4)] : null;
    }

    // A sound value is either a full path or a file name inside Windows' Media folder (those are Windows' own and never "missing" here: left alone).
    private static string? SoundFile(string value)
    {
        string trimmed = value.Trim().Trim('"');
        return trimmed.Length == 0 || trimmed.Contains('%') || !Path.IsPathFullyQualified(trimmed) ? null : trimmed;
    }

    private static string? ComServer(RegistryKey classKey)
    {
        // InprocServer32 holds a plain DLL path (spaces allowed, sometimes quoted); LocalServer32 a command line (a quoted or plain .exe, maybe with arguments).
        using (var inproc = classKey.OpenSubKey("InprocServer32"))
            if (inproc?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string dll && dll.Trim().Trim('"') is { Length: > 0 } path) return path;
        using (var local = classKey.OpenSubKey("LocalServer32"))
            if (local?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command && ServiceTuning.ExecutablePath(command) is { } exe) return exe;
        return null;
    }

    // A file type of this account whose program type exists nowhere (neither here nor for all users), and that holds nothing else (one value, no sub-keys).
    private static bool OrphanFileType(RegistryKey extensionKey, RegistryKey classes)
    {
        if (extensionKey.SubKeyCount != 0 || extensionKey.ValueCount != 1 || extensionKey.GetValue("") is not string progId || progId.Trim().Length == 0) return false;
        if (progId.IndexOfAny(['\\', '/', '\r', '\n']) >= 0) return false;
        using var own = classes.OpenSubKey(progId);
        if (own != null) return false;
        using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\" + progId);
        return machine == null;
    }

    // An Uninstall entry whose uninstaller file is certainly gone, and whose install folder (when it names one) is gone too. Windows Installer entries,
    // Windows' own components, updates of another program and Hush itself are never "broken" here. Returns the missing file.
    internal static string? BrokenUninstall(string name, RegistryKey entry)
    {
        if (name.Equals("Hush", StringComparison.OrdinalIgnoreCase)) return null;
        if (entry.GetValue("SystemComponent") is int system && system == 1) return null;
        if (entry.GetValue("WindowsInstaller") is int msi && msi == 1) return null;
        if (entry.GetValue("ParentKeyName") is string parent && parent.Length > 0) return null;
        if (entry.GetValue("UninstallString") is not string command || ServiceTuning.ExecutablePath(command) is not { } exe) return null;
        if (UninstallCommand.IsMsiExec(exe) || !CertainlyMissing(exe, folderToo: false)) return null;
        if (entry.GetValue("InstallLocation") is string location && location.Trim().Trim('"') is { Length: > 3 } folder && !CertainlyMissing(folder.TrimEnd('\\'), folderToo: false)) return null;
        return exe;
    }

    // A right-click command whose program file is certainly gone (a verb that uses DelegateExecute or has no command is left alone).
    private static string? BrokenCommand(RegistryKey verbKey)
    {
        using var command = verbKey.OpenSubKey("command");
        if (command == null || command.GetValue("DelegateExecute") != null) return null;
        return command.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string text && ServiceTuning.ExecutablePath(text) is { } exe && CertainlyMissing(exe, folderToo: false) ? exe : null;
    }

    private static bool EmptyCandidate(string name) => name.Length > 1 && name.IndexOfAny(['\\', '\r', '\n', '[', ']']) < 0 && !NeverEmpty.Contains(name) && !name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    private static bool IsGuid(string name) => name.Length == 38 && name[0] == '{' && name[^1] == '}' && Guid.TryParse(name, out _);
    private static bool IsExtension(string name) => name.Length is > 1 and < 32 && name[0] == '.' && name.IndexOfAny(['\\', ' ', '\r', '\n']) < 0;

    // Certainly gone: a fully qualified local path on a ready fixed drive, outside the Windows folder, the file is missing (and, folderToo, its folder too).
    internal static bool CertainlyMissing(string value, bool folderToo)
    {
        try
        {
            string path = value.Trim().Trim('"');
            if (path.Contains('%') || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            path = Path.GetFullPath(path);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
            if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return false;
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return false;
            if (!Missing(path)) return false;
            return !folderToo || Path.GetDirectoryName(path) is { } folder && Missing(folder);
        }
        catch { return false; }
    }

    private static bool Missing(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }

    // ----- deleting (re-checked, backed up, checked again) -----

    internal void Delete(CleanupItem item, string backupsDirectory, string dataDirectory)
    {
        var rule = All.FirstOrDefault(candidate => RootPrefix + candidate.Id == item.Root) ?? throw new IOException("Not an approved registry entry.");
        string keyPath = item.RegistryKeyPath ?? throw new IOException("Not an approved registry entry.");
        if (item.RegistrySnapshot == null || keyPath.IndexOfAny(['\r', '\n', '[', ']']) >= 0 || keyPath.Contains(@"\\") || keyPath.Contains(".."))
            throw new IOException("Not an approved registry entry.");
        // The place must be exactly the rule's list, or (for an issue) a key or value the rule's own search can produce.
        bool shapeOk = rule.Op switch
        {
            TraceOp.ClearValues or TraceOp.ClearTree => keyPath == rule.KeyPath && item.RegistryValue == null,
            TraceOp.DeleteValue when rule.Id == "mui-cache" => keyPath == rule.KeyPath && item.RegistryValue is { } name && MuiProgram(name) is { } program && CertainlyMissing(program, folderToo: false),
            TraceOp.DeleteValue when rule.Id == "sounds" => item.RegistryValue == "" && keyPath.StartsWith(rule.KeyPath + @"\", StringComparison.Ordinal) && keyPath.EndsWith(@"\.Current", StringComparison.Ordinal)
                && keyPath[(rule.KeyPath.Length + 1)..].Split('\\').Length == 3,
            TraceOp.DeleteKey when rule.Id == "com" => item.RegistryValue == null && keyPath.StartsWith(rule.KeyPath + @"\", StringComparison.Ordinal) && IsGuid(keyPath[(rule.KeyPath.Length + 1)..]),
            TraceOp.DeleteKey when rule.Id == "file-types" => item.RegistryValue == null && keyPath.StartsWith(rule.KeyPath + @"\", StringComparison.Ordinal) && IsExtension(keyPath[(rule.KeyPath.Length + 1)..]),
            TraceOp.DeleteKey when rule.Id.StartsWith("uninstall", StringComparison.Ordinal) => item.RegistryValue == null && OneLevelBelow(keyPath, rule.KeyPath),
            TraceOp.DeleteKey when rule.Id.StartsWith("context-menu", StringComparison.Ordinal) => item.RegistryValue == null && keyPath.StartsWith(rule.KeyPath + @"\", StringComparison.Ordinal)
                && MenuBases.Any(menuBase => keyPath.StartsWith(rule.KeyPath + @"\" + menuBase + @"\shell\", StringComparison.Ordinal) && keyPath[(rule.KeyPath.Length + menuBase.Length + 8)..] is { Length: > 0 } verb && !verb.Contains('\\')),
            TraceOp.DeleteKey when rule.Id == "empty-keys" => item.RegistryValue == null && OneLevelBelow(keyPath, rule.KeyPath) && EmptyCandidate(keyPath[(rule.KeyPath.Length + 1)..]),
            TraceOp.DeleteValue when rule.Id is "shared-dlls" or "installer-folders" => keyPath == rule.KeyPath && item.RegistryValue is { Length: > 3 } path && CertainlyMissing(path.TrimEnd('\\'), folderToo: false),
            TraceOp.DeleteValue when rule.Id.StartsWith("run-missing", StringComparison.Ordinal) => keyPath == rule.KeyPath && item.RegistryValue is { Length: > 0 } value && !value.Equals(RunKeyStartup.ValueName, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!shapeOk) throw new IOException("Not an approved registry entry.");
        if (rule.Machine && !elevated) throw new IOException("Needs administrator mode (an all-users registry entry).");
        string hive = Hive(rule);

        string Current()
        {
            using var key = Open(rule, keyPath) ?? throw new IOException("The registry entry no longer exists.");
            return rule.Op switch
            {
                TraceOp.DeleteValue => ExportValue(key, keyPath, item.RegistryValue!, hive),
                TraceOp.DeleteKey when rule.Id == "empty-keys" => key.ValueCount == 0 && key.SubKeyCount == 0 ? Header(keyPath, hive) : "changed",
                TraceOp.DeleteKey => TryExportTree(key, keyPath, hive) ?? throw new IOException("Unusual registry value; left untouched."),
                _ => Export(key, keyPath, rule)
            };
        }
        bool StillAnIssue()
        {
            using var key = Open(rule, keyPath);
            if (key == null) return false;
            return rule.Id switch
            {
                "mui-cache" => true, // checked in shapeOk
                "sounds" => key.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string file && SoundFile(file) is { } full && CertainlyMissing(full, folderToo: false),
                "com" => ComServer(key) is { } server && CertainlyMissing(server, folderToo: true),
                "file-types" => root.OpenSubKey(prefix + rule.KeyPath) is { } classes && Using(classes, c => OrphanFileType(key, c)),
                "uninstall" or "uninstall-m" or "uninstall-m32" => BrokenUninstall(keyPath[(keyPath.LastIndexOf('\\') + 1)..], key) != null,
                "context-menu" or "context-menu-m" => BrokenCommand(key) != null,
                "run-missing" or "run-missing-m" or "run-missing-m32" => key.GetValue(item.RegistryValue!) is string command && ServiceTuning.ExecutablePath(command) is { } exe && CertainlyMissing(exe, folderToo: false),
                "empty-keys" => key.ValueCount == 0 && key.SubKeyCount == 0,
                _ => true
            };
        }
        if (Current() != item.RegistrySnapshot || !StillAnIssue()) throw new IOException("The registry entry changed since the scan; left untouched.");
        Directory.CreateDirectory(backupsDirectory);
        if (!CleanupEngine.SafePath(backupsDirectory, dataDirectory)) throw new IOException("The backup directory must not be a link.");
        string backup = Path.Combine(backupsDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{rule.Id}-{Guid.NewGuid():N}.reg");
        using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Encoding.Unicode))
        { writer.Write(item.RegistrySnapshot); writer.Flush(); stream.Flush(true); }
        if (Current() != item.RegistrySnapshot) throw new IOException("The registry entry changed while backing up; left untouched.");

        switch (rule.Op)
        {
            case TraceOp.ClearValues:
            case TraceOp.ClearTree:
            {
                using var key = Open(rule, keyPath, writable: true) ?? throw new IOException("The registry entry no longer exists.");
                foreach (string name in rule.ValueNames ?? key.GetValueNames().Where(name => name.Length > 0).ToArray()) key.DeleteValue(name, false);
                if (rule.Op == TraceOp.ClearTree) foreach (string sub in key.GetSubKeyNames()) key.DeleteSubKeyTree(sub, false);
                break;
            }
            case TraceOp.DeleteValue:
            {
                using var key = Open(rule, keyPath, writable: true) ?? throw new IOException("The registry entry no longer exists.");
                key.DeleteValue(item.RegistryValue!, false);
                break;
            }
            case TraceOp.DeleteKey:
            {
                int split = keyPath.LastIndexOf('\\');
                using var parent = Open(rule, keyPath[..split], writable: true) ?? throw new IOException("The registry entry no longer exists.");
                parent.DeleteSubKeyTree(keyPath[(split + 1)..], false);
                break;
            }
        }
    }

    private static bool OneLevelBelow(string keyPath, string parent) => keyPath.StartsWith(parent + @"\", StringComparison.Ordinal) && keyPath[(parent.Length + 1)..] is { Length: > 0 } rest && !rest.Contains('\\');

    private static T Using<T>(RegistryKey key, Func<RegistryKey, T> use) { using (key) return use(key); }

    // ----- exports (.reg text, the same format the App Paths leftovers use; double-clicking the backup restores it) -----

    private string Export(RegistryKey key, string keyPath, TraceRule rule)
    {
        if (rule.Op == TraceOp.ClearTree) return TryExportTree(key, keyPath) ?? throw new IOException("Unusual registry value; left untouched.");
        var text = new StringBuilder(Header(keyPath));
        foreach (string name in (rule.ValueNames ?? key.GetValueNames()).Where(name => name.Length > 0 && key.GetValue(name) != null).Order(StringComparer.Ordinal)) text.Append(Line(key, name));
        return text.ToString();
    }

    private static string ExportValue(RegistryKey key, string keyPath, string name, string hive = "HKEY_CURRENT_USER") => Header(keyPath, hive) + Line(key, name);

    private static string? TryExportTree(RegistryKey key, string keyPath, string hive = "HKEY_CURRENT_USER")
    {
        try
        {
            var text = new StringBuilder(Header(keyPath, hive));
            void Walk(RegistryKey current, string path, int depth)
            {
                if (depth > 0) text.Append("\r\n[").Append(hive).Append('\\').Append(path).Append("]\r\n");
                foreach (string name in current.GetValueNames().Order(StringComparer.Ordinal)) text.Append(Line(current, name));
                if (depth > 64) throw new IOException("Too deep.");
                foreach (string sub in current.GetSubKeyNames().Order(StringComparer.Ordinal))
                {
                    if (sub.IndexOfAny(['\r', '\n', '[', ']']) >= 0) throw new IOException("Unusual key name.");
                    using var child = current.OpenSubKey(sub) ?? throw new IOException("Key vanished.");
                    Walk(child, path + "\\" + sub, depth + 1);
                }
            }
            Walk(key, keyPath, 0);
            return text.ToString();
        }
        catch (Exception ex) when (Ignorable(ex)) { return null; }
    }

    private static string Header(string keyPath, string hive = "HKEY_CURRENT_USER") => "Windows Registry Editor Version 5.00\r\n\r\n[" + hive + "\\" + keyPath + "]\r\n";

    private static string Line(RegistryKey key, string name)
    {
        string label = name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";
        var kind = key.GetValueKind(name);
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        string encoded = kind switch
        {
            RegistryValueKind.String => "\"" + Escape((string)value!) + "\"",
            RegistryValueKind.ExpandString => "hex(2):" + Hex(Encoding.Unicode.GetBytes((string)value! + "\0")),
            RegistryValueKind.MultiString => "hex(7):" + Hex(Encoding.Unicode.GetBytes(string.Join('\0', (string[])value!) + "\0\0")),
            RegistryValueKind.DWord => "dword:" + unchecked((uint)(int)value!).ToString("x8"),
            RegistryValueKind.QWord => "hex(b):" + Hex(BitConverter.GetBytes((long)value!)),
            RegistryValueKind.Binary => "hex:" + Hex((byte[])value!),
            RegistryValueKind.None => "hex(0):" + Hex(value as byte[] ?? []),
            _ => throw new IOException("Unsupported registry value; left untouched.")
        };
        return label + "=" + encoded + "\r\n";
    }

    private static string Escape(string value)
    {
        if (value.Contains('\r') || value.Contains('\n') || value.Contains('\0')) throw new IOException("Unusual registry value; left untouched.");
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
    private static string Hex(byte[] bytes) => string.Join(',', bytes.Select(b => b.ToString("x2")));
    private static bool Ignorable(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException;
}
