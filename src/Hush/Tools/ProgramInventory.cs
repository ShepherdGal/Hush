using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Hush;

// Tools > Programs (owner, November 19, 2026; Pro only): the installed programs Windows knows about (the same list as Settings > Apps for desktop programs).
// Reading is the registry's Uninstall keys only, read when the card is opened. Uninstalling runs the program's OWN uninstaller (Windows' permission
// prompt appears when it needs one); Hush never removes a program's files by itself while its uninstaller exists. What it leaves behind is found and
// removed afterwards by LeftoverFinder / LeftoverRemover, with a backup first.
internal sealed record InstalledProgram(
    string Id,                 // the registry path of its Uninstall entry, e.g. HKLM\SOFTWARE\...\Uninstall\Foo
    string KeyName,
    string Name,
    string Publisher,
    string Version,
    long Bytes,
    DateTime? Installed,
    string? InstallLocation,
    string? UninstallCommand,
    bool AllUsers,
    bool UninstallerMissing,
    string? DisplayIcon = null)   // the program's own icon ("path" or "path,index"), shown in Tools > Programs
{
    internal string Detail => string.Join(" · ", new[] { Publisher, Version.Length > 0 ? "version " + Version : "", Bytes > 0 ? FormatBytes(Bytes) : "", Installed is { } date ? "installed " + date.ToString("d MMM yyyy") : "" }.Where(part => part.Length > 0));
    internal static string FormatBytes(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}

internal sealed record UninstallRun(bool Started, string? Problem);

internal interface IProgramSystem
{
    IReadOnlyList<InstalledProgram> List();
    // Runs the program's own uninstaller and returns when that process has ended. Many uninstallers hand over to a copy of themselves and return at once,
    // so the caller also checks StillInstalled and lets the person say when the uninstaller window is finished.
    Task<UninstallRun> RunUninstallerAsync(InstalledProgram program, CancellationToken token);
    bool StillInstalled(InstalledProgram program);
}

internal sealed class RegistryProgramSystem(IReadOnlyList<string>? uninstallKeys = null) : IProgramSystem
{
    internal const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    // The three places desktop programs register: all users (64- and 32-bit) and this account.
    internal static readonly string[] DefaultKeys = [@"HKLM\" + UninstallPath, @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"HKCU\" + UninstallPath];
    private readonly IReadOnlyList<string> keys = uninstallKeys ?? DefaultKeys;

    public IReadOnlyList<InstalledProgram> List()
    {
        var found = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in keys)
        {
            using var parent = RegistryPaths.Open(root);
            if (parent == null) continue;
            foreach (string name in SafeNames(parent))
            {
                try
                {
                    using var key = parent.OpenSubKey(name);
                    if (key == null || Read(key, root + "\\" + name, name, root.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)) is not { } program) continue;
                    // The same program can be listed twice (both views, or a per-user and an all-users entry with the same name and version): show it once.
                    if (seen.Add(program.Name + "\u0001" + program.Version)) found.Add(program);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        return found;
    }

    private static string[] SafeNames(RegistryKey key) { try { return key.GetSubKeyNames(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; } }

    internal static InstalledProgram? Read(RegistryKey key, string id, string keyName, bool allUsers)
    {
        string name = (key.GetValue("DisplayName") as string ?? "").Trim();
        if (name.Length == 0) return null;
        // Windows' own components, updates of another program and Hush itself are never offered.
        if (key.GetValue("SystemComponent") is int system && system == 1) return null;
        if (key.GetValue("ParentKeyName") is string parentName && parentName.Length > 0) return null;
        if (key.GetValue("ReleaseType") is string release && release.Contains("Update", StringComparison.OrdinalIgnoreCase) | release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) return null;
        if (keyName.Equals("Hush", StringComparison.OrdinalIgnoreCase)) return null;
        string? command = (key.GetValue("UninstallString") as string)?.Trim();
        string? location = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(location)) location = null;
        long bytes = key.GetValue("EstimatedSize") is int kilobytes && kilobytes > 0 ? kilobytes * 1024L : 0;
        DateTime? installed = key.GetValue("InstallDate") is string date && DateTime.TryParseExact(date, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;
        bool missing = string.IsNullOrEmpty(command) || (UninstallCommand.Split(command) is { } split && !UninstallCommand.IsMsiExec(split.Exe) && Path.IsPathFullyQualified(split.Exe) && !File.Exists(split.Exe));
        return new InstalledProgram(id, keyName, name, (key.GetValue("Publisher") as string ?? "").Trim(), (key.GetValue("DisplayVersion") as string ?? "").Trim(), bytes, installed, location, command, allUsers, missing, (key.GetValue("DisplayIcon") as string)?.Trim());
    }

    public bool StillInstalled(InstalledProgram program) { using var key = RegistryPaths.Open(program.Id); return key != null; }

    public async Task<UninstallRun> RunUninstallerAsync(InstalledProgram program, CancellationToken token)
    {
        if (program.UninstallCommand is not { Length: > 0 } command || UninstallCommand.Split(command) is not { } split) return new(false, "It has no uninstaller.");
        var (exe, arguments) = UninstallCommand.ForUninstall(split.Exe, split.Arguments);
        var start = new ProcessStartInfo(exe, arguments) { UseShellExecute = true };
        if (Path.IsPathFullyQualified(exe) && Path.GetDirectoryName(exe) is { } folder && Directory.Exists(folder)) start.WorkingDirectory = folder;
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return new(false, "Windows' permission prompt was declined, so nothing was uninstalled."); }
        catch (Win32Exception ex) { return new(false, "Its uninstaller could not start: " + ex.Message); }
        if (process == null) return new(true, null);
        using (process)
        {
            try { await process.WaitForExitAsync(token); } catch (OperationCanceledException) { }
        }
        return new(true, null);
    }
}

// Reading an uninstall command line the way Windows' own Apps page runs it.
internal static class UninstallCommand
{
    internal static (string Exe, string Arguments)? Split(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return null;
        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            return end < 0 ? (command.Trim('"'), "") : (command[1..end], command[(end + 1)..].Trim());
        }
        // Unquoted: the program ends at the first ".exe" followed by a space or the end (paths with spaces are often left unquoted).
        int at = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            int after = at + 4;
            if (after == command.Length || command[after] == ' ') return (command[..after], command[after..].Trim());
            at = command.IndexOf(".exe", after, StringComparison.OrdinalIgnoreCase);
        }
        int space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
    }

    internal static bool IsMsiExec(string exe) => Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(exe).Equals("msiexec", StringComparison.OrdinalIgnoreCase);

    // Windows Installer entries often say "/I{product}" (open the setup), which Settings > Apps turns into a removal: Hush does the same.
    internal static (string Exe, string Arguments) ForUninstall(string exe, string arguments)
    {
        if (!IsMsiExec(exe)) return (exe, arguments);
        string fixedArguments = System.Text.RegularExpressions.Regex.Replace(arguments, @"(^|\s)/[Ii]\s*(\{[0-9A-Fa-f-]{36}\})", "$1/X$2");
        return ("msiexec.exe", fixedArguments);
    }
}

// "HKLM\..." / "HKCU\..." paths, always in the 64-bit registry view (the 32-bit view is reached through its WOW6432Node path).
internal static class RegistryPaths
{
    internal static RegistryKey? Open(string path, bool writable = false)
    {
        if (Split(path) is not { } split) return null;
        try
        {
            using var hive = RegistryKey.OpenBaseKey(split.Hive, RegistryView.Registry64);
            return split.SubKey.Length == 0 ? null : hive.OpenSubKey(split.SubKey, writable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    internal static (RegistryHive Hive, string SubKey)? Split(string path)
    {
        int slash = path.IndexOf('\\');
        string head = slash < 0 ? path : path[..slash], rest = slash < 0 ? "" : path[(slash + 1)..].Trim('\\');
        RegistryHive? hive = head.ToUpperInvariant() switch { "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine, "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser, _ => null };
        return hive is { } value ? (value, rest) : null;
    }

    internal static bool Exists(string path) { using var key = Open(path); return key != null; }
}
