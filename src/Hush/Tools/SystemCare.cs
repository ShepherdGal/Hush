using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hush;

// Phase C system care tools (owner, November 19, 2026; Pro only): duplicate and large files, software updates through Windows' own package manager (winget),
// a read-only disk check, and a registry size measurement. Each is behind a small interface so tests use fakes: nothing real is deleted, installed,
// checked or saved by a test.

// ----- Duplicate and large files -----

internal sealed record FoundFile(string Path, long Bytes, DateTime Modified);
internal sealed record DuplicateSet(string Hash, long Bytes, IReadOnlyList<FoundFile> Files)
{
    internal long Wasted => Bytes * (Files.Count - 1);
}
internal sealed record FileSearchResult(IReadOnlyList<DuplicateSet> Duplicates, IReadOnlyList<FoundFile> Large, int FilesSeen, bool Stopped);

internal static class FileFinder
{
    internal const long DefaultMinimumDuplicate = 1L << 20, DefaultLargeFile = 500L << 20;
    internal const int LargestShown = 100;

    // Folders never walked: Windows, Program Files, ProgramData, system folders at a drive root and Hush's own data. Links are never followed.
    internal static bool Skipped(string folder)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (name.StartsWith('$') || name is "System Volume Information" or "Recovery" or "Config.Msi" or "MSOCache" or "PerfLogs") return true;
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), CleanupEngine.DataDirectory })
            if (root.Length > 0 && (LeftoverPlaces.Normalize(root).Equals(LeftoverPlaces.Normalize(folder), StringComparison.OrdinalIgnoreCase) || LeftoverPlaces.IsInside(folder, root))) return true;
        return false;
    }

    // One pass: every file's size; then files sharing a size are compared by the hash of their first 64 KB, then by a full hash.
    internal static FileSearchResult Search(IReadOnlyList<string> folders, long minimumDuplicate, long largeFile, IProgress<string>? progress, CancellationToken token)
    {
        var bySize = new Dictionary<long, List<FoundFile>>();
        var large = new List<FoundFile>();
        int seen = 0;
        bool stopped = false;
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var pending = new Stack<string>(folders.Where(Directory.Exists).Select(LeftoverPlaces.Normalize).Distinct(StringComparer.OrdinalIgnoreCase));
        while (pending.Count > 0)
        {
            if (token.IsCancellationRequested) { stopped = true; break; }
            string folder = pending.Pop();
            if (Skipped(folder) && !folders.Any(chosen => LeftoverPlaces.Normalize(chosen).Equals(folder, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var entries = new FileSystemEnumerable<(string Path, bool Directory, long Length, DateTime Modified)>(folder,
                    (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory, entry.Length, entry.LastWriteTimeUtc.LocalDateTime), options);
                foreach (var entry in entries)
                {
                    if (entry.Directory) { pending.Push(entry.Path); continue; }
                    seen++;
                    if ((seen & 1023) == 0) progress?.Report($"Looking at files… {seen:N0}");
                    var file = new FoundFile(entry.Path, entry.Length, entry.Modified);
                    if (entry.Length >= largeFile) large.Add(file);
                    if (entry.Length < minimumDuplicate) continue;
                    if (!bySize.TryGetValue(entry.Length, out var list)) bySize[entry.Length] = list = [];
                    list.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var sets = new List<DuplicateSet>();
        var candidates = bySize.Values.Where(list => list.Count > 1).ToList();
        int done = 0;
        foreach (var group in candidates)
        {
            if (token.IsCancellationRequested) { stopped = true; break; }
            progress?.Report($"Comparing files of the same size… {++done:N0} of {candidates.Count:N0}");
            foreach (var quick in group.GroupBy(file => Hash(file.Path, 64 * 1024)).Where(g => g.Key != null && g.Count() > 1))
                foreach (var full in quick.GroupBy(file => Hash(file.Path, long.MaxValue)).Where(g => g.Key != null && g.Count() > 1))
                    sets.Add(new(full.Key!, group[0].Bytes, full.OrderBy(file => file.Modified).ToList()));
        }
        return new(sets.OrderByDescending(set => set.Wasted).ToList(), large.OrderByDescending(file => file.Bytes).Take(LargestShown).ToList(), seen, stopped);
    }

    private static string? Hash(string path, long limit)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var buffer = new byte[1 << 16];
            long left = limit;
            int read;
            while (left > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0) { sha.TransformBlock(buffer, 0, read, null, 0); left -= read; }
            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Sends files to the Recycle Bin through the shell (the owner's choice: a mistake can be undone). Returns how many are gone from their place.
    internal static (int Moved, List<string> Problems) Recycle(IReadOnlyList<string> files, Func<string, bool>? recycle = null)
    {
        var problems = new List<string>();
        int moved = 0;
        foreach (string file in files)
        {
            if (!File.Exists(file)) { problems.Add(file + ": already gone"); continue; }
            if ((recycle ?? RecycleOne)(file) && !File.Exists(file)) moved++;
            else problems.Add(file + ": Windows could not move it to the Recycle Bin");
        }
        return (moved, problems);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr Window; public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags; public bool Aborted; public IntPtr Mappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref ShFileOp operation);

    private static bool RecycleOne(string file)
    {
        // FO_DELETE with FOF_ALLOWUNDO (Recycle Bin), FOF_NOCONFIRMATION, FOF_SILENT, FOF_NOERRORUI, FOF_WANTNUKEWARNING off.
        var op = new ShFileOp { Func = 3, From = file + "\0\0", Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400 };
        return SHFileOperation(ref op) == 0 && !op.Aborted;
    }
}

// ----- Software updates (winget) -----

internal sealed record AppUpdate(string Name, string Id, string Version, string Available, string Source);

internal interface ISoftwareUpdater
{
    bool Available { get; }
    Task<IReadOnlyList<AppUpdate>> ListAsync(CancellationToken token);
    Task<(bool Ok, string Message)> UpdateAsync(AppUpdate update, CancellationToken token);
}

internal sealed class WingetUpdater : ISoftwareUpdater
{
    private static string? Exe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe") is var path && File.Exists(path) ? path : null;
    public bool Available => Exe != null;

    public async Task<IReadOnlyList<AppUpdate>> ListAsync(CancellationToken token)
    {
        var (code, output) = await Run("upgrade --include-unknown --accept-source-agreements --disable-interactivity", token, TimeSpan.FromMinutes(3));
        if (code == int.MinValue) throw new IOException("Windows' app installer did not answer in time.");
        return ParseTable(output);
    }

    public async Task<(bool Ok, string Message)> UpdateAsync(AppUpdate update, CancellationToken token)
    {
        if (!Regex.IsMatch(update.Id, @"^[A-Za-z0-9._+\-]{2,128}$")) return (false, "Its id is unusual, so Hush does not pass it on.");
        string arguments = $"upgrade --id \"{update.Id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
        var (code, output) = await Runner(arguments, token, TimeSpan.FromMinutes(20));
        // A program installed for the persons own account (Codex CLI, a portable one) cannot be changed by an administrator process: Windows app installer
        // says 0x8A15007D. Hush then runs the same update as the person, with normal rights (owner, November 20, 2026).
        if (code == AdminContextRefused && IsElevated())
        {
            try { (code, output) = await UserRunner(Exe ?? throw new IOException("Windows app installer is not on this PC."), arguments, token, TimeSpan.FromMinutes(20)); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return (false, "Not updated: it is installed for your account only, and Hush could not run the update as you (" + ex.Message + ")."); }
        }
        if (code == 0) return (true, $"Updated to {update.Available}.");
        string last = output.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 3 && !line.All(c => "-\\|/ █▒".Contains(c))) ?? "";
        return (false, code == int.MinValue ? "It took too long and was stopped." : "Not updated" + (last.Length > 0 ? ": " + last : $" (code {code})."));
    }

    // winget prints a table: a header (Name, Id, Version, Available, Source), a line of dashes, then one row per app, then a summary line.
    internal static List<AppUpdate> ParseTable(string output)
    {
        var lines = output.Replace("\r", "\n").Split('\n').Where(line => line.Trim().Length > 0).ToList();
        int dash = lines.FindIndex(line => Regex.IsMatch(line.Trim(), @"^-{10,}$"));
        if (dash < 1) return [];
        string header = lines[dash - 1];
        int id = header.IndexOf(" Id", StringComparison.Ordinal) + 1, version = header.IndexOf(" Version", StringComparison.Ordinal) + 1,
            available = header.IndexOf(" Available", StringComparison.Ordinal) + 1, source = header.IndexOf(" Source", StringComparison.Ordinal) + 1;
        if (id <= 0 || version <= id || available <= version) return [];
        string Cut(string line, int from, int to) => from >= line.Length ? "" : line[from..Math.Min(line.Length, to <= 0 ? line.Length : to)].Trim();
        var updates = new List<AppUpdate>();
        foreach (string line in lines.Skip(dash + 1))
        {
            if (line.Length < available || Regex.IsMatch(line, @"^\d+ (upgrades|upgrade|package)", RegexOptions.IgnoreCase)) break;
            string appId = Cut(line, id, version);
            if (appId.Length == 0 || appId.Contains(' ')) continue;
            updates.Add(new(Cut(line, 0, id), appId, Cut(line, version, available), Cut(line, available, source > 0 ? source : line.Length), source > 0 ? Cut(line, source, line.Length) : ""));
        }
        return updates;
    }

    // Windows app installer: "not permitted from an administrator context on packages installed within the user scope".
    internal const int AdminContextRefused = unchecked((int)0x8A15007D);
    // Test seams: how winget runs normally, as the signed-in person, and whether Hush is elevated.
    internal static Func<string, CancellationToken, TimeSpan, Task<(int Code, string Output)>> Runner = Run;
    internal static Func<string, string, CancellationToken, TimeSpan, Task<(int Code, string Output)>> UserRunner = DeElevated.RunAsync;
    internal static Func<bool> IsElevated = WindowsSystemTools.IsElevated;

    private static async Task<(int Code, string Output)> Run(string arguments, CancellationToken token, TimeSpan limit)
    {
        string exe = Exe ?? throw new IOException("Windows' app installer is not on this PC.");
        var start = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        using var process = Process.Start(start) ?? throw new IOException("Windows' app installer could not start.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } if (token.IsCancellationRequested) throw; return (int.MinValue, ""); }
        return (process.ExitCode, await output + await error);
    }
}

// ----- Disk check (read-only) and drive health -----

internal sealed record DiskCheckResult(bool Ran, bool Problems, string Summary, string Details);
internal sealed record DriveHealth(string Name, string Media, string Health);

internal interface IDiskCheck
{
    Task<DiskCheckResult> CheckAsync(string drive, CancellationToken token);
    Task<string?> ScheduleRepairAsync(string drive);   // null on success
    Task<IReadOnlyList<DriveHealth>> HealthAsync();
}

internal sealed class WindowsDiskCheck : IDiskCheck
{
    private static string System32(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
    private static bool PlainDrive(string drive) => Regex.IsMatch(drive, @"^[A-Za-z]:\\?$");

    // chkdsk without switches only reads: it never changes the drive.
    public async Task<DiskCheckResult> CheckAsync(string drive, CancellationToken token)
    {
        if (!PlainDrive(drive)) return new(false, false, "That is not a drive letter.", "");
        var (code, output) = await Hidden(System32("chkdsk.exe"), drive.TrimEnd('\\'), token, TimeSpan.FromMinutes(30));
        if (code == int.MinValue) return new(false, false, "The check took too long and was stopped.", output);
        if (output.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) || output.Contains("administrator", StringComparison.OrdinalIgnoreCase) && code != 0)
            return new(false, false, "The disk check needs administrator mode.", output);
        bool fine = code == 0 && !output.Contains("found problems", StringComparison.OrdinalIgnoreCase) && !output.Contains("errors found", StringComparison.OrdinalIgnoreCase);
        string summary = fine ? "Windows found no problems on this drive." : "Windows found problems on this drive. A repair at the next restart can fix them.";
        return new(true, !fine, summary, output);
    }

    // Marks the drive so Windows checks and repairs it while starting (before anything uses it). Needs administrator mode.
    public async Task<string?> ScheduleRepairAsync(string drive)
    {
        if (!PlainDrive(drive)) return "That is not a drive letter.";
        var (code, output) = await Hidden(System32("fsutil.exe"), "dirty set " + drive.TrimEnd('\\'), CancellationToken.None, TimeSpan.FromMinutes(1));
        return code == 0 ? null : "Windows did not schedule the repair" + (output.Trim().Length > 0 ? ": " + output.Trim().Split('\n')[0].Trim() : ".");
    }

    // What Windows' storage service reports about each physical disk (Healthy / Warning / Unhealthy), read through PowerShell's storage cmdlets.
    public async Task<IReadOnlyList<DriveHealth>> HealthAsync()
    {
        var (code, output) = await Hidden(System32(@"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -Command \"Get-PhysicalDisk | ForEach-Object { $_.FriendlyName + '|' + $_.MediaType + '|' + $_.HealthStatus }\"", CancellationToken.None, TimeSpan.FromSeconds(30));
        if (code != 0) return [];
        return output.Split('\n').Select(line => line.Trim().Split('|')).Where(parts => parts.Length == 3 && parts[0].Length > 0).Select(parts => new DriveHealth(parts[0], parts[1], parts[2])).ToList();
    }

    internal static async Task<(int Code, string Output)> Hidden(string exe, string arguments, CancellationToken token, TimeSpan limit)
    {
        var start = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        using var process = Process.Start(start);
        if (process == null) return (-1, "");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } return (int.MinValue, ""); }
        return (process.ExitCode, await output + await error);
    }
}

// ----- Registry size (owner's choice: measure only, never swap the files) -----

internal sealed record HiveSize(string Name, long Current, long Compact)
{
    internal long Saving => Math.Max(0, Current - Compact);
}

internal interface IRegistryCompactor
{
    Task<IReadOnlyList<HiveSize>> MeasureAsync(CancellationToken token);
}

// Windows' reg.exe saves a fresh, compact copy of a hive (administrator only); its size against the hive file in use is what a compaction could save.
// The copy goes to a temporary file that is deleted at once; nothing is ever swapped.
internal sealed class RegistryCompactor(string workFolder) : IRegistryCompactor
{
    public async Task<IReadOnlyList<HiveSize>> MeasureAsync(CancellationToken token)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "config");
        var hives = new (string Name, string Key, string File)[] { ("This account", "HKCU", Path.Combine(profile, "NTUSER.DAT")), ("Installed software (all users)", @"HKLM\SOFTWARE", Path.Combine(config, "SOFTWARE")), ("System", @"HKLM\SYSTEM", Path.Combine(config, "SYSTEM")) };
        Directory.CreateDirectory(workFolder);
        var sizes = new List<HiveSize>();
        foreach (var hive in hives)
        {
            token.ThrowIfCancellationRequested();
            long current;
            try { current = new FileInfo(hive.File).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            string copy = Path.Combine(workFolder, "measure-" + Guid.NewGuid().ToString("N")[..8] + ".hiv");
            try
            {
                int code = await Task.Run(() => RegistryBackupService.RegTool("save", $"\"{hive.Key}\" \"{copy}\" /y"), token);
                if (code == 0 && File.Exists(copy)) sizes.Add(new(hive.Name, current, new FileInfo(copy).Length));
            }
            finally { try { File.Delete(copy); } catch (IOException) { } }
        }
        return sizes;
    }
}
