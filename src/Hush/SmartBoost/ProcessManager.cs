using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Hush;

// One app as the person sees it: every process of the same program in this Windows session, with the memory they use together.
// Members carry each process's id and start time so ending can confirm it is still the same process (an id can be reused by another program).
// NeedsAdministrator: the app runs elevated while Hush does not, so Windows would refuse to end it from here (it is listed, with that note, instead of hidden).
internal sealed record AppProcess(string Name, string Path, long MemoryBytes, IReadOnlyList<(int Pid, DateTime Started)> Members, bool NeedsAdministrator = false)
{
    public int ProcessCount => Members.Count;
}

internal sealed record EndOutcome(int Closed, int Ended, int StillRunning);

internal interface IProcessManager
{
    // Apps with a window the person can see right now. Read-only.
    IReadOnlyList<AppProcess> List();
    // Asks the app to close (so it can offer to save); whatever is still running after a short grace period is ended.
    EndOutcome End(AppProcess app, CancellationToken token);
}

// The real thing. It only ever lists programs that have a visible window in the current user's session, never Windows' own shell and system
// processes, never security software and never Hush itself; it can only end processes it could also list.
internal sealed class WindowsProcessManager(TimeSpan? grace = null) : IProcessManager
{
    private readonly TimeSpan graceWait = grace ?? TimeSpan.FromSeconds(4);
    // Apps seen with a visible window in an earlier snapshot stay listed while they run, so an app that minimizes to the tray does not vanish from the list.
    private readonly HashSet<string> seenWithWindow = new(StringComparer.OrdinalIgnoreCase);

    // Program names (without .exe) that are part of Windows or security software: shown never, ended never.
    internal static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "smss", "system", "idle", "registry", "memory compression",
        "ApplicationFrameHost", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "SearchUI", "LockApp", "LogonUI",
        "fontdrvhost", "ctfmon", "RuntimeBroker", "sihost", "taskhostw", "conhost", "WidgetService", "Widgets", "SecurityHealthSystray", "SecurityHealthService",
        "MsMpEng", "NisSrv", "MpDefenderCoreService", "SgrmBroker", "wlanext", "audiodg", "spoolsv", "dllhost", "backgroundTaskHost", "UserOOBEBroker", "CompPkgSrv"
    };

    public IReadOnlyList<AppProcess> List()
    {
        int self = Environment.ProcessId;
        string? ownPath = Environment.ProcessPath;
        int session = Process.GetCurrentProcess().SessionId;
        var windowed = WindowedProcessIds();
        bool elevated = WindowsSystemTools.IsElevated();
        var groups = new Dictionary<string, (string Name, long Memory, List<(int, DateTime)> Members, bool HasWindow, bool NeedsAdmin)>(StringComparer.OrdinalIgnoreCase);
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id == 0 || process.SessionId != session) continue;
                    string? path = PathOf(process.Id);
                    if (path == null || Protected.Contains(Path.GetFileNameWithoutExtension(path))) continue;
                    if (ownPath != null && path.Equals(ownPath, StringComparison.OrdinalIgnoreCase)) continue;
                    // Read through a limited handle, which Windows also grants for apps running elevated (Process.StartTime would be refused and hide them).
                    var started = StartTimeOf(process.Id);
                    if (started == null) continue;
                    if (!groups.TryGetValue(path, out var group)) group = (DisplayName(path), 0, [], false, false);
                    group.Memory += process.WorkingSet64;
                    group.Members.Add((process.Id, started.Value));
                    group.HasWindow |= windowed.Contains(process.Id);
                    group.NeedsAdmin |= !elevated && !CanOpenToEnd(process.Id);
                    groups[path] = group;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { /* exited or protected: not ours to list */ }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        foreach (var pair in groups.Where(pair => pair.Value.HasWindow)) seenWithWindow.Add(pair.Key);
        seenWithWindow.RemoveWhere(path => !groups.ContainsKey(path)); // no longer running
        return groups.Where(pair => (pair.Value.HasWindow || seenWithWindow.Contains(pair.Key)) && pair.Value.Members.All(member => member.Item1 != self))
            .Select(pair => new AppProcess(pair.Value.Name, pair.Key, pair.Value.Memory, pair.Value.Members.Select(member => (member.Item1, member.Item2)).ToList(), pair.Value.NeedsAdmin))
            .OrderByDescending(app => app.MemoryBytes).ToList();
    }

    public EndOutcome End(AppProcess app, CancellationToken token)
    {
        int self = Environment.ProcessId;
        // Only the exact processes that were listed, and only if each one is still the same program started at the same moment.
        var live = new List<Process>();
        foreach (var (pid, started) in app.Members)
        {
            if (pid == self) continue;
            try
            {
                var process = Process.GetProcessById(pid);
                if (!SameProcess(process, app.Path, started)) { process.Dispose(); continue; }
                live.Add(process);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { /* already gone */ }
        }
        try
        {
            if (live.Count == 0 || Protected.Contains(Path.GetFileNameWithoutExtension(app.Path))) return new(0, 0, 0);
            if (!WindowsSystemTools.IsElevated() && live.Any(process => !CanOpenToEnd(process.Id))) return new(0, 0, live.Count); // Windows would refuse; nothing is attempted
            // First ask politely: every process with a window gets a close request, so the app can ask about unsaved work.
            foreach (var process in live)
            {
                try { if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow(); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
            var deadline = DateTime.UtcNow + graceWait;
            while (DateTime.UtcNow < deadline && live.Any(process => !Exited(process)) && !token.IsCancellationRequested) Thread.Sleep(120);
            int closed = live.Count(Exited);
            int ended = 0, still = 0;
            foreach (var process in live.Where(process => !Exited(process)))
            {
                try { process.Kill(); process.WaitForExit(1500); if (Exited(process)) ended++; else still++; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { if (Exited(process)) ended++; else still++; }
            }
            return new(closed, ended, still);
        }
        finally { foreach (var process in live) process.Dispose(); }
    }

    private static bool Exited(Process process)
    {
        try { return process.HasExited; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return true; }
    }

    // The process must still run the same program and have started when it did (a reused id would be a different program).
    internal static bool SameProcess(Process process, string path, DateTime started)
    {
        try
        {
            return string.Equals(PathOf(process.Id), path, StringComparison.OrdinalIgnoreCase) && StartTimeOf(process.Id) is { } actual && Math.Abs((actual - started).TotalSeconds) < 1.5;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return false; }
    }

    private static string DisplayName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            foreach (string? candidate in new[] { info.FileDescription, info.ProductName })
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    // ----- Windows -----
    private static HashSet<int> WindowedProcessIds()
    {
        var ids = new HashSet<int>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindowTextLength(handle) == 0) return true;
            if ((GetWindowLong(handle, -20) & 0x80) != 0) return true; // tool windows (tray helpers, tooltips) are not apps
            if (GetWindow(handle, 4) != IntPtr.Zero) return true;      // owned windows (dialogs) belong to their owner's app
            if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true; // hidden Store-app windows
            GetWindowThreadProcessId(handle, out int pid);
            if (pid == 0) return true;
            // Store apps (Media Player, Photos, Calculator...) show their window inside one owned by ApplicationFrameHost: the app's own process
            // is the one that owns a child window of that frame, so that is the process listed.
            if (string.Equals(Path.GetFileNameWithoutExtension(PathOf(pid) ?? ""), "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                int host = pid;
                EnumChildWindows(handle, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out int childPid);
                    if (childPid != 0 && childPid != host) ids.Add(childPid);
                    return true;
                }, IntPtr.Zero);
                return true;
            }
            ids.Add(pid);
            return true;
        }, IntPtr.Zero);
        return ids;
    }

    // Start time through a limited-rights handle (allowed for elevated programs too).
    internal static DateTime? StartTimeOf(int pid)
    {
        IntPtr handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try { return GetProcessTimes(handle, out long created, out _, out _, out _) ? DateTime.FromFileTime(created) : null; }
        finally { CloseHandle(handle); }
    }

    // Could this user end the process? Windows refuses PROCESS_TERMINATE on an elevated program unless Hush is elevated too.
    private static bool CanOpenToEnd(int pid)
    {
        IntPtr handle = OpenProcess(0x0001 | 0x1000, false, pid);
        if (handle == IntPtr.Zero) return false;
        CloseHandle(handle);
        return true;
    }

    internal static string? PathOf(int pid)
    {
        IntPtr handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION: works for the person's own programs without elevation
        if (handle == IntPtr.Zero) return null;
        try
        {
            var builder = new StringBuilder(1024); int size = builder.Capacity;
            return QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
