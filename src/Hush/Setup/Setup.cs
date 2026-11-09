using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Hush;

// Where an installed Hush lives. Everything is per user (no administrator rights, nothing machine-wide) and injectable,
// so tests run against temp folders and a HKCU\Software\Hush.TestFixtures\<GUID> registry root, never the real PC.
internal sealed record SetupLocations(string InstallDirectory, string StartMenuDirectory, string DesktopDirectory, string RegistryKey, string DataDirectory, string AdminInstallDirectory = "")
{
    internal static SetupLocations Default => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Hush"),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Hush",
        CleanupEngine.DataDirectory,
        // Administrator mode (0.9) moves the installed copy to a folder only administrators can write to.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hush"));
    internal string AdminExe => Path.Combine(AdminInstallDirectory, "Hush.exe");
    internal string InstalledExe => Path.Combine(InstallDirectory, "Hush.exe");
    internal string StartMenuShortcut => Path.Combine(StartMenuDirectory, "Hush.lnk");
    internal string DesktopShortcut => Path.Combine(DesktopDirectory, "Hush.lnk");
}

internal sealed record SetupResult(bool Success, string Message, bool Deferred = false);

// Install / uninstall. The portable app is the same Hush.exe; "installing" copies it to a per-user folder, adds shortcuts and
// the Settings > Apps entry. Updates keep working because the installed copy is a writable, normally named Hush.exe.
internal static class HushSetup
{
    private const string TempPrefix = ".hush-install-";
    internal static bool SamePath(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
    internal static bool IsInstalledCopy(string? exePath, SetupLocations locations) => exePath != null && (SamePath(exePath, locations.InstalledExe) || IsAdminCopy(exePath, locations));
    // The administrator-mode copy in the admin-only folder (Program Files\Hush).
    internal static bool IsAdminCopy(string? exePath, SetupLocations locations) => exePath != null && locations.AdminInstallDirectory.Length > 0 && SamePath(exePath, locations.AdminExe);
    internal static bool IsInstalled(SetupLocations locations)
    {
        if (!File.Exists(locations.InstalledExe) && !File.Exists(locations.AdminExe)) return false;
        using var key = Registry.CurrentUser.OpenSubKey(locations.RegistryKey);
        return key?.GetValue("UninstallString") is string;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    internal static SetupResult Install(string source, bool desktopShortcut, SetupLocations locations)
    {
        string? temporary = null;
        bool startExisted = File.Exists(locations.StartMenuShortcut), desktopExisted = File.Exists(locations.DesktopShortcut);
        bool registered = false, shortcutsDone = false;
        try
        {
            if (IsInstalledCopy(source, locations)) return new(false, "Hush is already running from its installed location.");
            try { UpdateService.VerifyExecutable(source, UpdateService.CurrentVersion); }
            catch (InvalidDataException) { return new(false, "This file isn't a genuine Hush application, so nothing was installed."); }
            string parent = Path.GetDirectoryName(Path.GetFullPath(locations.InstallDirectory))!;
            Directory.CreateDirectory(locations.InstallDirectory);
            if (!CleanupEngine.SafePath(locations.InstallDirectory, parent)) return new(false, "The install location isn't safe to use, so nothing was installed.");
            temporary = Path.Combine(locations.InstallDirectory, TempPrefix + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(source, temporary, false);
            if (Hash(temporary) != Hash(source)) throw new IOException("The copied file did not verify.");
            // Atomic swap: the previous version is replaced only after the new copy is complete and verified.
            if (File.Exists(locations.InstalledExe)) File.Replace(temporary, locations.InstalledExe, null);
            else File.Move(temporary, locations.InstalledExe);
            temporary = null;
            CreateShortcut(locations.StartMenuShortcut, locations.InstalledExe, locations.InstallDirectory);
            if (desktopShortcut) CreateShortcut(locations.DesktopShortcut, locations.InstalledExe, locations.InstallDirectory);
            shortcutsDone = true;
            Register(locations);
            registered = true;
            return new(true, "Hush is installed. You'll find it in your Start menu.");
        }
        catch (IOException) when (File.Exists(locations.InstalledExe) && IsLocked(locations.InstalledExe))
        { return new(false, "Hush is open from its install folder. Close it and try again."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Undo only what this attempt created; a previously working install is left as it was.
            try { if (!shortcutsDone || !registered) { if (!startExisted) File.Delete(locations.StartMenuShortcut); if (!desktopExisted) File.Delete(locations.DesktopShortcut); } } catch { }
            return new(false, "Hush couldn't be installed: " + ex.Message);
        }
        finally
        {
            try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool IsLocked(string path)
    {
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    // Writes only Hush's own uninstall entry in the current user's hive (the entry Windows lists under Settings > Apps).
    private static void Register(SetupLocations locations) => RegisterFor(locations, locations.InstalledExe, locations.InstallDirectory);

    internal static void RegisterFor(SetupLocations locations, string exe, string directory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(locations.RegistryKey, writable: true);
        key.SetValue("DisplayName", "Hush");
        key.SetValue("DisplayVersion", UpdateService.DisplayVersion);
        key.SetValue("Publisher", "Hush");
        key.SetValue("InstallLocation", directory);
        key.SetValue("DisplayIcon", exe + ",0");
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    // After an in-app update the installed file is newer than the label Windows shows; keep them equal.
    internal static void RefreshRegistration(SetupLocations locations)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(locations.RegistryKey, writable: true);
            if (key == null) return;
            if (key.GetValue("DisplayVersion") is not string shown || shown != UpdateService.DisplayVersion)
            {
                key.SetValue("DisplayVersion", UpdateService.DisplayVersion);
                string exe = File.Exists(locations.AdminExe) && key.GetValue("InstallLocation") is string where && SamePath(where, locations.AdminInstallDirectory) ? locations.AdminExe : locations.InstalledExe;
                key.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    internal static SetupResult Uninstall(SetupLocations locations, bool removeData, string? runningExe, IAdminTaskScheduler? adminScheduler = null)
    {
        try
        {
            // A shortcut is removed only if it really points at this installation.
            foreach (string shortcut in new[] { locations.StartMenuShortcut, locations.DesktopShortcut })
                if (File.Exists(shortcut) && ShortcutTarget(shortcut) is { } target && (SamePath(target, locations.InstalledExe) || SamePath(target, locations.AdminExe))) File.Delete(shortcut);
            // The administrator task and the Program Files copy go too (removing the task needs administrator rights, which the admin copy has).
            if (adminScheduler != null) { try { adminScheduler.Remove(); } catch (IOException) { } }
            Registry.CurrentUser.DeleteSubKeyTree(locations.RegistryKey, throwOnMissingSubKey: false);
            if (removeData) RemoveData(locations.DataDirectory);
            if (IsInstalledCopy(runningExe, locations)) return new(true, "Hush was removed. The last files are deleted when this window closes.", Deferred: true);
            RemoveInstalledFiles(locations.InstallDirectory);
            if (locations.AdminInstallDirectory.Length > 0) RemoveInstalledFiles(locations.AdminInstallDirectory);
            return new(true, "Hush was removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "Hush couldn't be fully removed: " + ex.Message); }
    }

    // Hush's own settings, audit logs and Undo history. Only deleted when the user asks; never a drive root or a linked folder.
    private static void RemoveData(string directory)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string? parent = Path.GetDirectoryName(full);
        if (parent == null || full.Length < 10 || !Directory.Exists(full) || !CleanupEngine.SafePath(full, parent)) return;
        Directory.Delete(full, true);
    }

    // Deletes only the files an install or update creates; anything else in the folder is left, and the folder goes only if empty.
    internal static void RemoveInstalledFiles(string installDirectory)
    {
        if (!Directory.Exists(installDirectory)) return;
        foreach (string pattern in new[] { "Hush.exe", "Hush.previous-*.exe", ".hush-update-*.exe", TempPrefix + "*.exe", ".hush-write-*" })
            foreach (string file in Directory.EnumerateFiles(installDirectory, pattern))
                if (CleanupEngine.SafePath(file, installDirectory)) File.Delete(file);
        if (!Directory.EnumerateFileSystemEntries(installDirectory).Any()) Directory.Delete(installDirectory);
    }

    // The running Hush.exe cannot delete itself, so a copy in a temp folder waits for it to exit, then removes the install folder.
    // which: "all" (uninstall), "user" (the per-user folder after moving to Program Files) or "admin" (the Program Files folder after moving back).
    internal static void StartDeferredRemoval(SetupLocations locations, string? runningExe = null, string which = "all")
    {
        string helperDirectory = Path.Combine(Path.GetTempPath(), "hush-uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDirectory);
        string helper = Path.Combine(helperDirectory, "Hush.exe");
        File.Copy(runningExe != null && File.Exists(runningExe) ? runningExe : locations.InstalledExe, helper, false);
        using var process = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = helperDirectory };
        start.ArgumentList.Add("--finish-uninstall"); start.ArgumentList.Add(process.Id.ToString()); start.ArgumentList.Add(which);
        Process.Start(start)?.Dispose();
    }

    internal static async Task<int> FinishUninstallAsync(string processId, string which = "all")
    {
        try
        {
            var locations = SetupLocations.Default;
            string self = Environment.ProcessPath ?? "";
            string helperDirectory = Path.GetDirectoryName(self) ?? "";
            if (!int.TryParse(processId, out int id) || !Path.GetFileName(helperDirectory).StartsWith("hush-uninstall-", StringComparison.Ordinal)
                || !SamePath(Path.GetDirectoryName(helperDirectory)!, Path.GetTempPath())) return 1;
            try { using var old = Process.GetProcessById(id); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); await old.WaitForExitAsync(timeout.Token); }
            catch (ArgumentException) { /* The window already closed. */ }
            if (which is "all" or "user") RemoveInstalledFiles(locations.InstallDirectory);
            if (which is "all" or "admin" && locations.AdminInstallDirectory.Length > 0) { try { RemoveInstalledFiles(locations.AdminInstallDirectory); } catch (UnauthorizedAccessException) { } }
            // Last step: remove this helper's own temp folder once this process has ended.
            Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
                Arguments = $"/d /c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{helperDirectory}\"" })?.Dispose();
            return 0;
        }
        catch { return 1; }
    }

    internal static void CreateShortcut(string path, string target, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        object? shell = null, shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcuts are unavailable.");
            shell = Activator.CreateInstance(type);
            shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
            dynamic link = shortcut!;
            link.TargetPath = target; link.WorkingDirectory = workingDirectory; link.IconLocation = target + ",0"; link.Description = "Hush";
            link.Save();
        }
        finally
        {
            if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    internal static string? ShortcutTarget(string path)
    {
        object? shell = null, shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            shell = Activator.CreateInstance(type);
            shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
            dynamic link = shortcut!;
            return (string)link.TargetPath;
        }
        catch { return null; }
        finally
        {
            if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
