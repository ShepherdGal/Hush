using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Xml.Linq;

namespace Hush;

// Administrator mode (0.9): Hush asks once and, if the person agrees, always runs elevated so system Temp, Prefetch, trace logs and
// Windows Update files are reachable. Everything that touches Windows (elevation, the scheduled task) sits behind these two small
// interfaces; tests use fakes and never register a real task, relaunch or move the real app.
internal interface IElevation
{
    bool IsElevated { get; }
    // False when this process runs as a different Windows account than the person signed in (an admin's password was typed): user-profile
    // locations would then be the admin's profile, so they are left alone.
    bool IsSignedInUser { get; }
    // Starts exe with Windows' own permission prompt. False when the person declined it.
    bool Launch(string exe, string arguments, bool wait, out int exitCode);
}

internal interface IAdminTaskScheduler
{
    bool Exists();
    // Registers (or replaces) the "run with highest privileges" task for the signed-in user. Needs administrator rights.
    void Register(string exe);
    void Remove();
    // Starts the task now (the Start menu path: no permission prompt). False when it could not be started.
    bool Run();
}

internal enum StartAction { Continue, RunTask, Relaunch }

internal static class AdminMode
{
    internal const string TaskName = "Hush (administrator)";
    internal static IElevation Elevation = new WindowsElevation();
    internal static IAdminTaskScheduler Scheduler = new SchtasksScheduler();

    // The folder an administrator task may point at: only places a standard user cannot write to. A task aimed at %LOCALAPPDATA% or a portable
    // folder would let any program the person runs replace the exe and be elevated silently.
    internal static bool IsAdminOnlyFolder(string path, IEnumerable<string>? roots = null)
    {
        roots ??= [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                   Environment.GetFolderPath(Environment.SpecialFolder.Windows)];
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // What a normal start does first. Pure so every case is tested without touching Windows.
    internal static StartAction DecideStart(bool? runAsAdmin, bool elevated, bool adminCopy, bool taskExists)
    {
        if (runAsAdmin != true || elevated) return StartAction.Continue;
        // The installed copy in Program Files starts through its task (no prompt); anything else falls back to Windows' own prompt.
        return adminCopy && taskExists ? StartAction.RunTask : StartAction.Relaunch;
    }

    // The question's wording, shared by the popup and the tests.
    internal const string QuestionTitle = "Let Hush run as administrator?";
    internal const string QuestionBody =
        "System Temp, Prefetch, Windows logs and Windows Update files can only be cleaned as administrator.\n\n" +
        "Hush still shows everything first and asks before it cleans anything. The Never-touch list and the safety checks do not change. " +
        "You can change this at any time in Settings.\n\n" +
        "The installed copy asks Windows for permission once and then starts without a prompt. A portable copy asks each time it starts; " +
        "installing Hush avoids that.";

    internal static void Notify(string message) => Debug.WriteLine(message);
}

internal sealed class WindowsElevation : IElevation
{
    public bool IsElevated
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }

    public bool IsSignedInUser
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                string? session = SessionUserName();
                return session == null || string.Equals(session, identity.Name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }
    }

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [System.Runtime.InteropServices.DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);

    // DOMAIN\user of the person signed in to this Windows session (not of the account this process runs as).
    private static string? SessionUserName()
    {
        const int WTSUserName = 5, WTSDomainName = 7, CurrentSession = -1;
        static string? Query(int kind)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, CurrentSession, kind, out var buffer, out _)) return null;
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer); } finally { WTSFreeMemory(buffer); }
        }
        string? user = Query(WTSUserName), domain = Query(WTSDomainName);
        return string.IsNullOrEmpty(user) ? null : string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
    }

    public bool Launch(string exe, string arguments, bool wait, out int exitCode)
    {
        exitCode = 0;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
            if (process == null) return false;
            if (wait) { process.WaitForExit(); exitCode = process.ExitCode; }
            return true;
        }
        catch (Win32Exception) { return false; } // the person declined Windows' permission prompt
    }
}

// The real task scheduler: schtasks.exe with an XML definition. Registering needs administrator rights; running the task does not.
internal sealed class SchtasksScheduler : IAdminTaskScheduler
{
    private static (int Code, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows did not start the task scheduler.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync(); // both read at once: a full pipe can never block the wait below
        if (!process.WaitForExit(30000)) { try { process.Kill(); } catch { } throw new IOException("The task scheduler did not answer."); }
        return (process.ExitCode, output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
    }

    public bool Exists() { try { return Run("/Query", "/TN", AdminMode.TaskName).Code == 0; } catch { return false; } }

    public void Register(string exe)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(exe));
        if (folder == null || !AdminMode.IsAdminOnlyFolder(folder + Path.DirectorySeparatorChar + "x")) throw new IOException("The administrator task may only point at Hush in an administrator-only folder.");
        using var identity = WindowsIdentity.GetCurrent();
        string xml = TaskXml(exe, identity.User?.Value ?? throw new IOException("Windows did not report the current user."));
        // The task definition is written where only administrators can write: in a folder other programs can change, the file could be swapped between
        // writing it and handing it to the Task Scheduler, which would register a task of their choosing with the highest privileges.
        string file = Path.Combine(AdminOnlyDirectory.Ensure(HushSetup.AdminHelperRoot), "hush-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            var (code, output) = Run("/Create", "/TN", AdminMode.TaskName, "/XML", file, "/F");
            if (code != 0) throw new IOException("Windows did not register the administrator task: " + output.Trim());
        }
        finally { try { File.Delete(file); } catch { } }
    }

    public void Remove()
    {
        if (!Exists()) return;
        var (code, output) = Run("/Delete", "/TN", AdminMode.TaskName, "/F");
        if (code != 0) throw new IOException("Windows did not remove the administrator task: " + output.Trim());
    }

    public bool Run() { try { return Run("/Run", "/TN", AdminMode.TaskName).Code == 0; } catch { return false; } }

    // One on-demand task, no trigger, no time limit, run with the highest privileges for this user only.
    internal static string TaskXml(string exe, string userSid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var document = new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", "Starts Hush with administrator rights, without a permission prompt.")),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "Author"),
                new XElement(ns + "UserId", userSid), new XElement(ns + "LogonType", "InteractiveToken"), new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"), new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(ns + "StopIfGoingOnBatteries", "false"), new XElement(ns + "AllowHardTerminate", "true"), new XElement(ns + "AllowStartOnDemand", "true"),
                new XElement(ns + "Enabled", "true"), new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"), new XElement(ns + "Exec", new XElement(ns + "Command", exe)))));
        return document.Declaration == null ? "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" + document : document.ToString();
    }
}

// Turns the choice into files, shortcuts and the task. The privileged half (copy to the admin-only folder, register the task) runs in the
// elevated helper; the rest (shortcuts, the Settings > Apps entry) stays with the signed-in person.
internal static class AdminModeService
{
    internal const string SetupArgument = "--admin-setup", RemoveArgument = "--admin-off";
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    // Elevated half of turning it on for an installed copy: a verified copy into the admin-only folder, then the task that points at it.
    internal static SetupResult ElevatedSetup(string source, SetupLocations locations, IAdminTaskScheduler scheduler, IEnumerable<string>? adminRoots = null)
    {
        string? temporary = null;
        try
        {
            if (!AdminMode.IsAdminOnlyFolder(Path.Combine(locations.AdminInstallDirectory, "x"), adminRoots)) return new(false, "The administrator location is not an administrator-only folder, so nothing was changed.");
            try { UpdateService.VerifyExecutable(source, UpdateService.CurrentVersion); }
            catch (InvalidDataException) { return new(false, "This file isn't a genuine Hush application, so nothing was changed."); }
            Directory.CreateDirectory(locations.AdminInstallDirectory);
            temporary = Path.Combine(locations.AdminInstallDirectory, ".hush-install-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(source, temporary, false);
            if (Hash(temporary) != Hash(source)) throw new IOException("The copied file did not verify.");
            if (File.Exists(locations.AdminExe)) File.Replace(temporary, locations.AdminExe, null); else File.Move(temporary, locations.AdminExe);
            temporary = null;
            scheduler.Register(locations.AdminExe);
            return new(true, "Hush will start as administrator.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "Hush couldn't be set up to run as administrator: " + ex.Message); }
        finally { try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    // Signed-in half after the helper succeeded: shortcuts and the Settings > Apps entry now point at the admin copy.
    internal static void RepointInstalled(SetupLocations locations)
    {
        foreach (string shortcut in new[] { locations.StartMenuShortcut, locations.DesktopShortcut })
            if (File.Exists(shortcut) && HushSetup.ShortcutTarget(shortcut) is { } target && (HushSetup.SamePath(target, locations.InstalledExe) || HushSetup.SamePath(target, locations.AdminExe)))
                HushSetup.CreateShortcut(shortcut, locations.AdminExe, locations.AdminInstallDirectory);
        HushSetup.RegisterFor(locations, locations.AdminExe, locations.AdminInstallDirectory);
    }

    // Elevated half of turning it off: the task goes, and the exe moves back to the per-user folder so updates and shortcuts work without administrator rights.
    internal static SetupResult Disable(SetupLocations locations, IAdminTaskScheduler scheduler)
    {
        try
        {
            scheduler.Remove();
            if (File.Exists(locations.AdminExe))
            {
                Directory.CreateDirectory(locations.InstallDirectory);
                string temporary = Path.Combine(locations.InstallDirectory, ".hush-install-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(locations.AdminExe, temporary, false);
                if (File.Exists(locations.InstalledExe)) File.Replace(temporary, locations.InstalledExe, null); else File.Move(temporary, locations.InstalledExe);
                foreach (string shortcut in new[] { locations.StartMenuShortcut, locations.DesktopShortcut })
                    if (File.Exists(shortcut) && HushSetup.ShortcutTarget(shortcut) is { } target && HushSetup.SamePath(target, locations.AdminExe))
                        HushSetup.CreateShortcut(shortcut, locations.InstalledExe, locations.InstallDirectory);
                HushSetup.RegisterFor(locations, locations.InstalledExe, locations.InstallDirectory);
            }
            return new(true, "Hush no longer runs as administrator.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "Hush couldn't fully turn administrator mode off: " + ex.Message); }
    }

    // Removes the leftover admin-folder files (never the running exe itself; the deferred helper deletes that after exit).
    internal static void RemoveAdminFiles(SetupLocations locations) => HushSetup.RemoveInstalledFiles(locations.AdminInstallDirectory);
}
