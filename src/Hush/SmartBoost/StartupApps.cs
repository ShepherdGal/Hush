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

// Lists the programs that start with Windows and turns them on or off the way Task Manager does: a small flag under
// HKCU\...\Explorer\StartupApproved, which Windows reads at sign-in. Nothing is deleted or uninstalled and the original entry (Run value
// or shortcut) is never touched, so switching it on again restores exactly what was there. Scheduled tasks and services are not listed
// and never changed. Works without administrator rights, including for all-users entries (the flag is per user).
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
                    entries.Add(new(name, command, source, !IsDisabled(ApprovedValue(source, name))));
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
                    entries.Add(new(name, file, source, !IsDisabled(ApprovedValue(source, name))));
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

    // Only an entry that is listed right now can be switched, and only its own flag is written (nothing else in the registry).
    public bool SetEnabled(string id, bool enabled)
    {
        var entry = List().FirstOrDefault(candidate => candidate.Id == id);
        if (entry == null) return false;
        string subKey = ApprovedSubKey(entry.Source);
        try
        {
            using var key = locations.UserRoot.CreateSubKey(locations.ApprovedPath + "\\" + subKey);
            key.SetValue(entry.Name, enabled ? EnabledFlag() : DisabledFlag(), RegistryValueKind.Binary);
            bool confirmed = !IsDisabled(key.GetValue(entry.Name) as byte[]) == enabled;
            Log(entry, enabled, confirmed);
            return confirmed;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { Log(entry, enabled, false); return false; }
    }

    private static string ApprovedSubKey(StartupSource source) => source switch
    {
        StartupSource.MachineRun32 => "Run32",
        StartupSource.UserFolder or StartupSource.CommonFolder => "StartupFolder",
        _ => "Run"
    };

    private byte[]? ApprovedValue(StartupSource source, string name)
    {
        try
        {
            using var key = locations.UserRoot.OpenSubKey(locations.ApprovedPath + "\\" + ApprovedSubKey(source));
            return key?.GetValue(name) as byte[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { return null; }
    }

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
