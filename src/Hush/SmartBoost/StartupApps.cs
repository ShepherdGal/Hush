using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Hush;

internal enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

internal sealed record StartupEntry(string Name, string Command, StartupSource Source, bool Enabled)
{
    public string Id => $"{Source}|{Name}";
    public string Scope => Source is StartupSource.UserRun or StartupSource.UserFolder ? "Current user" : "All users";
    public string Where => Source is StartupSource.UserFolder or StartupSource.CommonFolder ? "Startup folder" : "Registry";
}

// Where Windows keeps startup entries, injectable so tests use a fixture registry key and temp folders instead of the real PC.
internal sealed record StartupLocations(RegistryKey UserRoot, string UserRunPath, RegistryKey MachineRoot, string MachineRunPath, string MachineRun32Path,
    string ApprovedPath, string UserFolder, string CommonFolder)
{
    public static StartupLocations Default => new(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run",
        Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved",
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
}

// Lists the Run-key and Startup-folder programs that start with Windows and turns them on or off the way Task Manager does: a small flag under
// ...\Explorer\StartupApproved, which Windows reads at sign-in. Nothing is deleted or uninstalled and the original entry (Run value or shortcut) is never
// touched, so switching it on again restores exactly what was there. The flag lives where Task Manager keeps it (checked on the owner's PC, November 14,
// 2026): HKCU for this account's entries, HKLM for all-users entries (that needs administrator rights). Older Hush versions wrote all-users flags to HKCU,
// so an entry also counts as off when that old HKCU flag says so, and turning it back on clears it. Used by the Settings > Startup card (StartupTuning).
internal sealed class StartupAppsService(StartupLocations locations, string? logDirectory = null)
{
    private static readonly string[] IgnoredFiles = ["desktop.ini"];

    public IReadOnlyList<StartupEntry> List()
    {
        var entries = new List<StartupEntry>();
        void ReadRun(RegistryKey root, string path, StartupSource source)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key == null) return;
                foreach (string name in key.GetValueNames())
                {
                    if (name.Length == 0 || key.GetValue(name) is not string command) continue;
                    entries.Add(new(name, command, source, !IsOff(source, name)));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
        void ReadFolder(string folder, StartupSource source)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    string name = Path.GetFileName(file);
                    if (IgnoredFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    entries.Add(new(name, file, source, !IsOff(source, name)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        ReadRun(locations.UserRoot, locations.UserRunPath, StartupSource.UserRun);
        ReadRun(locations.MachineRoot, locations.MachineRunPath, StartupSource.MachineRun);
        ReadRun(locations.MachineRoot, locations.MachineRun32Path, StartupSource.MachineRun32);
        ReadFolder(locations.UserFolder, StartupSource.UserFolder);
        ReadFolder(locations.CommonFolder, StartupSource.CommonFolder);
        return entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static bool IsMachine(StartupSource source) => source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder;

    private bool IsOff(StartupSource source, string name) =>
        IsDisabled(ReadFlag(source, name)) || IsMachine(source) && IsDisabled(ReadFlag(source, name, legacyUserFlag: true));

    // Only an entry that is listed right now can be switched, and only its own flag is written (nothing else in the registry).
    public bool SetEnabled(string id, bool enabled)
    {
        var entry = List().FirstOrDefault(candidate => candidate.Id == id);
        if (entry == null) return false;
        try
        {
            WriteFlag(entry.Source, entry.Name, enabled ? EnabledFlag() : DisabledFlag());
            bool confirmed = !IsOff(entry.Source, entry.Name) == enabled;
            Log(entry, enabled, confirmed);
            return confirmed;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { Log(entry, enabled, false); return false; }
    }

    // The flag as it is now, from the hive Task Manager uses (legacyUserFlag: the HKCU copy older Hush versions wrote for all-users entries).
    internal byte[]? ReadFlag(StartupSource source, string name, bool legacyUserFlag = false)
    {
        try
        {
            using var key = (legacyUserFlag ? locations.UserRoot : RootFor(source)).OpenSubKey(locations.ApprovedPath + "\\" + ApprovedSubKey(source));
            return key?.GetValue(name) as byte[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { return null; }
    }

    // Writes (or, with null, removes) the flag in the hive Task Manager uses; turning an all-users entry on also clears an old HKCU "off" flag.
    // Throws when Windows refuses (an all-users entry without administrator rights).
    internal void WriteFlag(StartupSource source, string name, byte[]? value)
    {
        using (var key = RootFor(source).CreateSubKey(locations.ApprovedPath + "\\" + ApprovedSubKey(source)))
        {
            if (value == null) key.DeleteValue(name, false); else key.SetValue(name, value, RegistryValueKind.Binary);
        }
        if (IsMachine(source) && (value == null || !IsDisabled(value)) && IsDisabled(ReadFlag(source, name, legacyUserFlag: true)))
        {
            using var legacy = locations.UserRoot.OpenSubKey(locations.ApprovedPath + "\\" + ApprovedSubKey(source), writable: true);
            legacy?.DeleteValue(name, false);
        }
    }

    private RegistryKey RootFor(StartupSource source) => IsMachine(source) ? locations.MachineRoot : locations.UserRoot;

    private static string ApprovedSubKey(StartupSource source) => source switch
    {
        StartupSource.MachineRun32 => "Run32",
        StartupSource.UserFolder or StartupSource.CommonFolder => "StartupFolder",
        _ => "Run"
    };

    // Task Manager's format: 12 bytes. The first byte is 2 (or 6) when enabled and 3 when disabled; the last 8 bytes are the time of the change.
    internal static bool IsDisabled(byte[]? value) => value is { Length: >= 4 } && (value[0] & 1) == 1;
    internal static byte[] EnabledFlag() => [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    internal static byte[] DisabledFlag()
    {
        var flag = new byte[12];
        flag[0] = 3;
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(flag, 4);
        return flag;
    }

    internal StartupLocations Locations => locations;

    private void Log(StartupEntry entry, bool enabled, bool confirmed)
    {
        try
        {
            string directory = Path.Combine(logDirectory ?? CleanupEngine.DataDirectory, "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup-changes.jsonl"),
                JsonSerializer.Serialize(new { utc = DateTime.UtcNow, entry.Name, entry.Command, source = entry.Source.ToString(), enabled, confirmed }) + Environment.NewLine);
        }
        catch { }
    }
}
