using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Hush;

// Tools > Registry (owner, November 19, 2026; Pro only): full registry backups and registry search.
//
// Full backup: this account's whole registry (HKCU) and, when Hush is administrator, the all-users software part (HKLM\SOFTWARE), exported by Windows' own
// reg.exe (hidden, the same format Registry Editor writes) and packed into one zip. Made before each registry clean (on by default) and with "Back up now".
// The newest five are kept. Restore imports the copy back: values it holds are put back; keys created after the backup stay (an import never deletes), and
// a restart is advised. Elevated backups live in the administrator-only %ProgramData%\Hush folder, so an elevated restore never reads a file another program
// could have written.
internal sealed record RegistryBackupPart(string Name, string Key);
internal sealed record RegistryBackup(string File, DateTime When, long Bytes, string Label, IReadOnlyList<string> Parts);

internal sealed class RegistryBackupService(string folder, IReadOnlyList<RegistryBackupPart> parts, Func<string, string, int>? regTool = null)
{
    internal const int Keep = 5;
    private readonly Func<string, string, int> reg = regTool ?? RegTool;

    internal static IReadOnlyList<RegistryBackupPart> DefaultParts(bool elevated) => elevated
        ? [new("This account", "HKCU"), new("All users (software)", @"HKLM\SOFTWARE")]
        : [new("This account", "HKCU")];

    internal RegistryBackup Create(string label)
    {
        Directory.CreateDirectory(folder);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string work = Path.Combine(folder, stamp + ".partial");
        Directory.CreateDirectory(work);
        try
        {
            var names = new List<string>();
            for (int i = 0; i < parts.Count; i++)
            {
                string file = Path.Combine(work, $"{i}.reg");
                if (reg("export", $"\"{parts[i].Key}\" \"{file}\" /y") != 0 || !File.Exists(file)) throw new IOException($"Windows could not export {parts[i].Name.ToLowerInvariant()}.");
                names.Add(parts[i].Name);
            }
            File.WriteAllText(Path.Combine(work, "backup.json"), JsonSerializer.Serialize(new { label, when = DateTime.Now, parts = parts.Select(p => new { p.Name, p.Key }) }));
            string zip = Path.Combine(folder, $"{stamp} registry.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(work, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
            Prune();
            return new(zip, DateTime.Now, new FileInfo(zip).Length, label, names);
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    internal IReadOnlyList<RegistryBackup> List()
    {
        if (!Directory.Exists(folder)) return [];
        var list = new List<RegistryBackup>();
        foreach (string zip in Directory.EnumerateFiles(folder, "* registry.zip"))
        {
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                var entry = archive.GetEntry("backup.json");
                if (entry == null) continue;
                using var reader = new StreamReader(entry.Open());
                using var json = JsonDocument.Parse(reader.ReadToEnd());
                string label = json.RootElement.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                DateTime when = json.RootElement.TryGetProperty("when", out var w) && w.TryGetDateTime(out var date) ? date : File.GetLastWriteTime(zip);
                var names = json.RootElement.TryGetProperty("parts", out var p) ? p.EnumerateArray().Select(part => part.GetProperty("Name").GetString() ?? "").ToList() : [];
                list.Add(new(zip, when, new FileInfo(zip).Length, label, names));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return list.OrderByDescending(backup => backup.When).ToList();
    }

    // Imports every part of the backup back. Returns null on success, otherwise one plain sentence.
    internal string? Restore(string zip)
    {
        string full = LeftoverPlaces.Normalize(zip);
        if (!LeftoverPlaces.IsInside(full, folder) || !full.EndsWith(" registry.zip", StringComparison.OrdinalIgnoreCase)) return "That is not one of Hush's registry backups.";
        string work = Path.Combine(folder, "restore-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ZipFile.ExtractToDirectory(full, work);
            var failed = new List<string>();
            foreach (string file in Directory.EnumerateFiles(work, "*.reg").Order(StringComparer.Ordinal))
                if (reg("import", $"\"{file}\"") != 0) failed.Add(Path.GetFileNameWithoutExtension(file));
            return failed.Count == 0 ? null : "Windows could not put back every part (some keys may be protected or need administrator mode). What could be imported was imported.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return "The backup could not be read: " + ex.Message; }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    internal void Delete(string zip)
    {
        string full = LeftoverPlaces.Normalize(zip);
        if (!LeftoverPlaces.IsInside(full, folder)) throw new IOException("That is not one of Hush's registry backups.");
        File.Delete(full);
    }

    private void Prune() { foreach (var old in List().Skip(Keep)) try { File.Delete(old.File); } catch (IOException) { } }

    // reg.exe exports and imports exactly what Registry Editor does; it runs hidden and is never used for cleaning.
    internal static int RegTool(string verb, string arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"), verb + " " + arguments)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start);
        if (process == null) return -1;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(600_000);
        if (!exited) { try { process.Kill(); } catch (InvalidOperationException) { } return -1; }
        return process.ExitCode;
    }
}

// Registry search: finds a text in key names, value names and text data under this account's registry and the all-users software part. Bounded (2,000 finds,
// 90 seconds, cancelable) so it can never run away. Deleting a find exports it first (Unicode .reg backup in "Registry backups"), then removes it; finds in
// places Windows needs (policies, Winlogon, security, cryptography, servicing) are shown but can never be deleted, and all-users finds need administrator mode.
internal enum RegistryMatch { KeyName, ValueName, ValueData }

internal sealed record RegistryFind(string Key, string? Value, RegistryMatch Match, string Preview)
{
    internal string Path => Value == null ? Key : Key + " → " + (Value.Length == 0 ? "(Default)" : Value);
    internal bool Protected => RegistrySearch.IsProtected(Key);
    internal bool AllUsers => Key.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);
}

internal static class RegistrySearch
{
    internal const int MaxFinds = 2000;
    internal static readonly string[] DefaultRoots = ["HKCU", @"HKLM\SOFTWARE"];
    private static readonly string[] ProtectedParts = [@"\Policies", @"\Windows NT\CurrentVersion\Winlogon", @"\Windows Defender", @"\Cryptography", @"\Component Based Servicing", @"\SystemCertificates",
        @"\Windows\CurrentVersion\Group Policy", @"\Windows NT\CurrentVersion\Image File Execution Options", @"\Microsoft\Windows\CurrentVersion\Setup", @"\Microsoft\Windows NT\CurrentVersion\ProfileList"];

    // Never deletable: a hive or its first level (HKCU\Software itself), and anything under the parts Windows needs.
    internal static bool IsProtected(string key)
    {
        if (key.Count(c => c == '\\') < 2) return true;
        return ProtectedParts.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    internal static List<RegistryFind> Search(string text, IReadOnlyList<string> roots, bool keys, bool values, bool data, CancellationToken token, TimeSpan? limit = null)
    {
        var finds = new List<RegistryFind>();
        if (text.Trim().Length < 3) return finds;
        var clock = Stopwatch.StartNew();
        var budget = limit ?? TimeSpan.FromSeconds(90);
        bool Done() => finds.Count >= MaxFinds || clock.Elapsed > budget || token.IsCancellationRequested;
        void Walk(RegistryKey key, string path, int depth)
        {
            if (Done() || depth > 40) return;
            if (values || data)
            {
                string[] names;
                try { names = key.GetValueNames(); } catch (Exception ex) when (Ignorable(ex)) { names = []; }
                foreach (string name in names)
                {
                    if (Done()) return;
                    if (values && name.Contains(text, StringComparison.OrdinalIgnoreCase)) { finds.Add(new(path, name, RegistryMatch.ValueName, Preview(key, name))); continue; }
                    if (!data) continue;
                    string preview = Preview(key, name);
                    if (preview.Contains(text, StringComparison.OrdinalIgnoreCase)) finds.Add(new(path, name, RegistryMatch.ValueData, preview));
                }
            }
            string[] subs;
            try { subs = key.GetSubKeyNames(); } catch (Exception ex) when (Ignorable(ex)) { return; }
            foreach (string sub in subs)
            {
                if (Done()) return;
                string child = path + "\\" + sub;
                if (keys && sub.Contains(text, StringComparison.OrdinalIgnoreCase)) finds.Add(new(child, null, RegistryMatch.KeyName, ""));
                try { using var next = key.OpenSubKey(sub); if (next != null) Walk(next, child, depth + 1); }
                catch (Exception ex) when (Ignorable(ex)) { }
            }
        }
        foreach (string root in roots)
        {
            using var key = RegistryPaths.Open(root) ?? (RegistryPaths.Split(root) is { SubKey: "" } split ? RegistryKey.OpenBaseKey(split.Hive, RegistryView.Registry64) : null);
            if (key != null) Walk(key, root, 0);
        }
        return finds;
    }

    private static string Preview(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
            {
                string s => s.Length > 200 ? s[..200] + "…" : s,
                string[] many => string.Join(" · ", many).Length > 200 ? string.Join(" · ", many)[..200] + "…" : string.Join(" · ", many),
                int number => number.ToString(),
                long number => number.ToString(),
                byte[] bytes => $"{bytes.Length} bytes",
                _ => ""
            };
        }
        catch (Exception ex) when (Ignorable(ex)) { return ""; }
    }

    // Exports each find, then deletes it (a key with everything under it, or one value). Returns the counts and problems.
    internal static (int Deleted, List<string> Problems) Delete(IReadOnlyList<RegistryFind> finds, string backupFolder, bool elevated, Func<string, string, int>? regTool = null)
    {
        var reg = regTool ?? RegistryBackupService.RegTool;
        var problems = new List<string>();
        int deleted = 0;
        Directory.CreateDirectory(backupFolder);
        foreach (var find in finds)
        {
            if (find.Protected) { problems.Add(find.Path + ": Windows needs it; left alone"); continue; }
            if (find.AllUsers && !elevated) { problems.Add(find.Path + ": needs administrator mode"); continue; }
            try
            {
                string backup = System.IO.Path.Combine(backupFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-search-{Guid.NewGuid():N}.reg");
                if (find.Value == null)
                {
                    if (reg("export", $"\"{find.Key}\" \"{backup}\" /y /reg:64") != 0) throw new IOException("it could not be exported first, so it was left alone");
                    int slash = find.Key.LastIndexOf('\\');
                    using var parent = RegistryPaths.Open(find.Key[..slash], writable: true) ?? throw new IOException("its parent key is gone");
                    parent.DeleteSubKeyTree(find.Key[(slash + 1)..], throwOnMissingSubKey: false);
                }
                else
                {
                    using var key = RegistryPaths.Open(find.Key, writable: true) ?? throw new IOException("its key is gone");
                    File.WriteAllText(backup, ValueExport(key, find.Key, find.Value), Encoding.Unicode);
                    key.DeleteValue(find.Value, throwOnMissingValue: false);
                }
                deleted++;
            }
            catch (Exception ex) when (Ignorable(ex)) { problems.Add(find.Path + ": " + ex.Message); }
        }
        return (deleted, problems);
    }

    // One value as .reg text (double-clicking the backup puts it back).
    private static string ValueExport(RegistryKey key, string keyPath, string name)
    {
        string hive = keyPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
        string sub = keyPath[(keyPath.IndexOf('\\') + 1)..];
        string label = name.Length == 0 ? "@" : "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string Hex(byte[] bytes) => string.Join(',', bytes.Select(b => b.ToString("x2")));
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        string encoded = key.GetValueKind(name) switch
        {
            RegistryValueKind.String when value is string s && s.IndexOfAny(['\r', '\n']) < 0 => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            RegistryValueKind.String => "hex(1):" + Hex(Encoding.Unicode.GetBytes((string)value! + "\0")),
            RegistryValueKind.ExpandString => "hex(2):" + Hex(Encoding.Unicode.GetBytes((string)value! + "\0")),
            RegistryValueKind.MultiString => "hex(7):" + Hex(Encoding.Unicode.GetBytes(string.Join('\0', (string[])value!) + "\0\0")),
            RegistryValueKind.DWord => "dword:" + unchecked((uint)(int)value!).ToString("x8"),
            RegistryValueKind.QWord => "hex(b):" + Hex(BitConverter.GetBytes((long)value!)),
            RegistryValueKind.Binary => "hex:" + Hex((byte[])value!),
            _ => "hex(0):" + Hex(value as byte[] ?? [])
        };
        return $"Windows Registry Editor Version 5.00\r\n\r\n[{hive}\\{sub}]\r\n{label}={encoded}\r\n";
    }

    private static bool Ignorable(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException;
}
