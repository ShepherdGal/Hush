using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Hush;

// "Previous Windows installation" removed by Hush itself (0.9, user direction: no Windows cleanup tool). Disk Cleanup's handler often removes nothing, so Hush
// deletes C:\Windows.old directly. Its files belong to TrustedInstaller, so an administrator must first take ownership: Hush does that in-process (the
// documented SeTakeOwnership/SeRestore privileges, then owner = Administrators and a Full Control entry for them), only on an entry it is about to delete.
// Rules: only the folder named Windows.old at the root of the system drive; never a link (a junction or symlink inside is removed as a link, its target is
// never entered); administrator mode required; cancellable; nothing outside that folder is touched; the result says exactly how much was removed and how
// much Windows still protects or is using. Tests use a fixture folder and a no-op ownership step.
internal static class WindowsOldRemoval
{
    internal sealed record Result(int Removed, int Failed, IReadOnlyList<string> Examples);

    // The one folder this may ever remove.
    internal static string ExpectedPath()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Combine(Path.GetPathRoot(windows) ?? @"C:\", "Windows.old");
    }

    // Removes Windows.old. Throws IOException when it is not possible or not complete (the message is shown to the person).
    internal static void RemoveWindowsOld(CancellationToken token)
    {
        if (!WindowsSystemTools.IsElevated()) throw new IOException("Removing the previous Windows installation needs administrator mode (Settings > Administrator).");
        string root = ExpectedPath();
        if (!Directory.Exists(root)) return; // already gone
        EnablePrivilege("SeTakeOwnershipPrivilege"); EnablePrivilege("SeRestorePrivilege"); EnablePrivilege("SeBackupPrivilege");
        var result = RemoveTree(root, token, TakeOwnership);
        if (Directory.Exists(root) && result.Failed > 0)
            throw new IOException($"Windows.old was only partly removed: {result.Removed:N0} items removed, {result.Failed:N0} could not be removed because Windows still protects or uses them (for example {string.Join(", ", result.Examples)}). Restart the PC and clean again to finish.");
        if (Directory.Exists(root)) throw new IOException($"Windows.old was only partly removed ({result.Removed:N0} items removed); the folder itself is still there.");
    }

    // The deletion itself. takeOwnership(path, isDirectory) is called only after a plain delete was refused. Links are removed as links and never entered.
    internal static Result RemoveTree(string root, CancellationToken token, Action<string, bool> takeOwnership)
    {
        string full = Path.GetFullPath(root);
        if (full.Length <= 3 || !Path.GetFileName(full.TrimEnd('\\', '/')).Equals("Windows.old", StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("The folder is unsafe to remove.");
        int removed = 0, failed = 0; var examples = new List<string>();
        void Fail(string path) { failed++; if (examples.Count < 3) examples.Add(Path.GetFileName(path)); }

        bool TryDelete(string path, bool directory, bool isLink)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (directory) Directory.Delete(path, false);
                    else { try { File.SetAttributes(path, FileAttributes.Normal); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } File.Delete(path); }
                    return true;
                }
                catch (FileNotFoundException) { return true; }
                catch (DirectoryNotFoundException) { return true; }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    if (attempt == 1 || isLink) return false;
                    try { takeOwnership(path, directory); } catch (Exception inner) when (inner is UnauthorizedAccessException or IOException or InvalidOperationException) { return false; }
                }
            }
            return false;
        }

        void Walk(string directory)
        {
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList(); }
            catch (UnauthorizedAccessException)
            {
                try { takeOwnership(directory, true); entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { Fail(directory); return; }
            }
            catch (IOException) { Fail(directory); return; }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                bool link = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                if (isDirectory && !link) Walk(entry.FullName);
                if (TryDelete(entry.FullName, isDirectory, link)) removed++; else Fail(entry.FullName);
            }
        }

        Walk(full);
        token.ThrowIfCancellationRequested();
        if (TryDelete(full, true, false)) removed++; else Fail(full);
        return new(removed, failed, examples);
    }

    // Owner = Administrators, then Full Control for them. Windows applies the new entry to the children that inherit, so a folder's contents usually follow.
    private static void TakeOwnership(string path, bool directory)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (directory)
        {
            var info = new DirectoryInfo(path);
            var owner = new DirectorySecurity(); owner.SetOwner(admins); info.SetAccessControl(owner);
            var access = info.GetAccessControl();
            access.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(access);
        }
        else
        {
            var info = new FileInfo(path);
            var owner = new FileSecurity(); owner.SetOwner(admins); info.SetAccessControl(owner);
            var access = info.GetAccessControl();
            access.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(access);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct TokenPrivileges { public int Count; public long Luid; public int Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    // Switches a privilege the administrator already holds from "present" to "enabled" for this process (it grants nothing new).
    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x20 | 0x08, out var token)) return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out long luid)) return;
            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
            AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }
}
