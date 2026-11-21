using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Hush;

// One app as the person sees it: every process of the same program in this Windows session, with the memory they use together.
// Members carry each process's id and start time so ending can confirm it is still the same process (an id can be reused by another program).
// NeedsAdministrator: the app runs elevated while Hush does not, so Windows would refuse to end it from here (it is listed, with that note, instead of hidden).
internal sealed record AppProcess(string Name, string Path, long MemoryBytes, IReadOnlyList<(int Pid, DateTime Started)> Members, bool NeedsAdministrator = false,
    ProcessKind Kind = ProcessKind.Window, string Reason = "")
{
    public int ProcessCount => Members.Count;
}

// Window: an app with a window open. LeftBehind: helper processes whose app is gone (owner, November 14, 2026: "when I exit some desktop apps,
// some of the app are still left in the Task Manager"). NoWindow: a program of the person's that runs without any window (often a tray app; listed, never
// ended in bulk).
internal enum ProcessKind { Window, LeftBehind, NoWindow }

internal sealed record EndOutcome(int Closed, int Ended, int StillRunning);

internal interface IProcessManager
{
    // Apps with a window the person can see right now. Read-only.
    IReadOnlyList<AppProcess> List();
    // Programs of this session without any window: what closed apps left behind, and the rest. Read-only; only read when the Process card is opened.
    IReadOnlyList<AppProcess> ListBackground() => [];
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

    // Windows' own helpers that may still be offered, but only as "left behind" and only while that is still true when they are ended:
    // a console host whose program has exited, and the Store-app frame host while no Store app window is open (Windows starts both again when needed).
    // Security software is never listed, whoever made it (the same words the Scheduled tasks card refuses).
    private static readonly System.Text.RegularExpressions.Regex SecurityPath = new(@"antivirus|defender|firewall|security|malware|endpoint", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static readonly HashSet<string> LeftBehindSystemHelpers = new(StringComparer.OrdinalIgnoreCase) { "conhost", "ApplicationFrameHost" };

    public IReadOnlyList<AppProcess> ListBackground()
    {
        int self = Environment.ProcessId;
        string? ownPath = Environment.ProcessPath;
        int session = Process.GetCurrentProcess().SessionId;
        var windowed = WindowedProcessIds();
        var framePids = VisibleWindowOwners();
        var parents = ParentIds();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        bool elevated = WindowsSystemTools.IsElevated();
        var found = new List<(int Pid, string Path, DateTime Started, long Memory, string? Reason)>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id <= 4 || process.Id == self || process.SessionId != session || windowed.Contains(process.Id)) continue;
                    string? path = PathOf(process.Id);
                    if (path == null || ownPath != null && path.Equals(ownPath, StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileNameWithoutExtension(path);
                    var started = StartTimeOf(process.Id);
                    if (started == null) continue;
                    string? reason;
                    if (LeftBehindSystemHelpers.Contains(name))
                    {
                        // Only Windows' own copy, and only while it is really idle.
                        if (!path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                        reason = LeftBehindReason(process.Id, name, started.Value, parents, framePids);
                        if (reason == null) continue;
                    }
                    // Windows itself and security software: never.
                    else if (Protected.Contains(name) || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) || SecurityPath.IsMatch(path)) continue;
                    else reason = LeftBehindReason(process.Id, name, started.Value, parents, framePids);
                    found.Add((process.Id, path, started.Value, process.WorkingSet64, reason));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { /* exited or protected: not ours to list */ }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        // A helper started by a leftover is a leftover too (the children of a WebView2 host whose app closed): a few passes settle every chain.
        var leftover = found.Where(item => item.Reason != null).ToDictionary(item => item.Pid, item => item.Reason!);
        for (int pass = 0; pass < 4; pass++)
            for (int index = 0; index < found.Count; index++)
            {
                var item = found[index];
                if (item.Reason != null || !parents.TryGetValue(item.Pid, out int parent) || !leftover.TryGetValue(parent, out var parentReason)) continue;
                if (CommandLineOf(item.Pid)?.Contains(" --type=", StringComparison.Ordinal) != true) continue;
                found[index] = item with { Reason = parentReason };
                leftover[item.Pid] = parentReason;
            }
        var groups = new Dictionary<string, (string Name, long Memory, List<(int, DateTime)> Members, bool NeedsAdmin, bool AllLeft, string Reason)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in found)
        {
            if (!groups.TryGetValue(item.Path, out var group)) group = (DisplayName(item.Path), 0, [], false, true, "");
            group.Memory += item.Memory;
            group.Members.Add((item.Pid, item.Started));
            group.NeedsAdmin |= !elevated && !CanOpenToEnd(item.Pid);
            group.AllLeft &= item.Reason != null;
            if (item.Reason != null && group.Reason.Length == 0) group.Reason = item.Reason;
            groups[item.Path] = group;
        }
        // An app with any window open is in the main list; what is left runs without a window. A group is "left behind" only when every one of its
        // processes is (one living main process means the app is still running, perhaps in the tray).
        // A program that has a window in the main list is not repeated here (its windowless helpers belong to the open app).
        var openPaths = List().Select(app => app.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return groups.Where(pair => !openPaths.Contains(pair.Key))
            .Select(pair => new AppProcess(pair.Value.Name, pair.Key, pair.Value.Memory, pair.Value.Members.Select(member => (member.Item1, member.Item2)).ToList(), pair.Value.NeedsAdmin,
                pair.Value.AllLeft ? ProcessKind.LeftBehind : ProcessKind.NoWindow, pair.Value.AllLeft ? pair.Value.Reason : "No window open · still running"))
            .OrderBy(app => app.Kind).ThenByDescending(app => app.MemoryBytes).ToList();
    }

    // Why a windowless process is a leftover, or null when it is not one. Leftovers are processes whose starter is gone and that are useless on their own:
    // a Chromium/Electron/WebView2 helper (its command line has --type=) or WebView2 host, Office's AI helper (ai.exe), a console host; or the Store-app frame
    // host with no Store window open. A program whose starter is merely gone (a launcher that exits, as many tray apps use) is NOT a leftover.
    internal static string? LeftBehindReason(int pid, string name, DateTime started, IReadOnlyDictionary<int, int> parents, IReadOnlySet<int> framePids)
    {
        if (name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            return framePids.Contains(pid) ? null : "No Store app is open · Windows starts it again when one opens";
        if (!ParentGone(pid, started, parents)) return null;
        if (name.Equals("conhost", StringComparison.OrdinalIgnoreCase)) return "Its console program has closed";
        if (name.Equals("ai", StringComparison.OrdinalIgnoreCase)) return "Office's AI helper · the Office app that started it has closed";
        if (name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) return "Web view of an app that has closed";
        return CommandLineOf(pid) is { } line && line.Contains(" --type=", StringComparison.Ordinal) ? "Its app has closed · these helper processes stayed behind" : null;
    }

    // True when the process that started this one no longer runs (its id is free, or now belongs to a process that started later).
    private static bool ParentGone(int pid, DateTime started, IReadOnlyDictionary<int, int> parents)
    {
        if (!parents.TryGetValue(pid, out int parent) || parent <= 4) return false; // started by the system: not a leftover
        if (!parents.ContainsKey(parent)) return true;
        return StartTimeOf(parent) is not { } parentStarted || parentStarted > started;
    }

    // Is this listed leftover still a leftover right now? Checked again just before ending one of Windows' own helpers.
    private static bool StillLeftBehind(int pid, string path, DateTime started)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (!LeftBehindSystemHelpers.Contains(name)) return true;
        return LeftBehindReason(pid, name, started, ParentIds(), VisibleWindowOwners()) != null;
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
                if (!SameProcess(process, app.Path, started) || app.Kind == ProcessKind.LeftBehind && !StillLeftBehind(pid, app.Path, started)) { process.Dispose(); continue; }
                live.Add(process);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { /* already gone */ }
        }
        try
        {
            string program = Path.GetFileNameWithoutExtension(app.Path);
            if (live.Count == 0 || Protected.Contains(program) && !(app.Kind == ProcessKind.LeftBehind && LeftBehindSystemHelpers.Contains(program))) return new(0, 0, 0);
            if (!WindowsSystemTools.IsElevated() && live.Any(process => !CanOpenToEnd(process.Id))) return new(0, 0, live.Count); // Windows would refuse; nothing is attempted
            // First ask politely: every process with a window gets a close request, so the app can ask about unsaved work.
            foreach (var process in live)
            {
                try { if (HasWindow(process)) process.CloseMainWindow(); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
            // Nothing to wait for when no process has a window (leftovers and background programs cannot be asked to close).
            var deadline = live.Any(HasWindow) ? DateTime.UtcNow + graceWait : DateTime.UtcNow;
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

    private static bool HasWindow(Process process)
    {
        try { return process.MainWindowHandle != IntPtr.Zero; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return false; }
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

    // Every process that owns a visible top-level window (the Store-app frame host owns the frames of open Store apps).
    private static HashSet<int> VisibleWindowOwners()
    {
        var ids = new HashSet<int>();
        EnumWindows((handle, _) =>
        {
            if (IsWindowVisible(handle) && GetWindowTextLength(handle) > 0 && !(DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
            { GetWindowThreadProcessId(handle, out int pid); if (pid != 0) ids.Add(pid); }
            return true;
        }, IntPtr.Zero);
        return ids;
    }

    // Process id -> the id of the process that started it, for every process, from one Toolhelp snapshot.
    internal static Dictionary<int, int> ParentIds()
    {
        var parents = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(0x2, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return parents;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry)) parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally { CloseHandle(snapshot); }
        return parents;
    }

    // The command line, through a limited-rights handle (no memory reading). Null when Windows does not give it.
    internal static string? CommandLineOf(int pid)
    {
        IntPtr handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out int length);
            if (length <= 0 || length > 1 << 20) return null;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, 60, buffer, length, out _) != 0) return null;
                var text = Marshal.PtrToStructure<UnicodeString>(buffer);
                return text.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public IntPtr DefaultHeapId; public uint ModuleId, Threads, ParentProcessId; public int PriorityClassBase; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returned);

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
