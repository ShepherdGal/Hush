using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hush;

// What a memory release did, measured from Windows' own physical-memory figure before and after (it can fill up again as programs run).
internal sealed record MemoryBoostResult(int ProgramsTrimmed, long UsedBefore, long UsedAfter)
{
    public long Released => Math.Max(0, UsedBefore - UsedAfter);
}

internal interface IMemoryBooster
{
    // Asks Windows to release idle memory held by the person's running programs. Never closes a program.
    MemoryBoostResult Release(CancellationToken token);
}

// "Boost" memory release, the same technique Microsoft PC Manager uses: EmptyWorkingSet asks Windows to move a program's idle pages to
// the page file / standby list. Nothing is closed or killed, no service or setting changes, and programs bring back what they need
// (so they may pause for a moment). Only programs of the current user in the current Windows session are touched; Windows' own
// processes, security software and Hush itself are skipped. Tests pass a filter so only the test process is ever trimmed.
internal sealed class WindowsMemoryBooster(Func<Process, bool>? only = null) : IMemoryBooster
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(SafeProcessHandle process);
    private const uint SetQuota = 0x0100, QueryLimitedInformation = 0x1000;
    // Never trimmed, whoever owns them: the shell's compositor and logon, security software and the system itself.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "dwm", "fontdrvhost",
        "MsMpEng", "MpDefenderCoreService", "NisSrv", "SecurityHealthService", "SecurityHealthSystray", "MsSense", "Hush"
    };

    public MemoryBoostResult Release(CancellationToken token)
    {
        var monitor = new ResourceMonitor();
        long before = monitor.Sample().MemoryUsed;
        int trimmed = 0;
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (token.IsCancellationRequested) break;
                if (process.Id <= 4 || process.SessionId != session || Protected.Contains(process.ProcessName)) continue;
                if (only != null && !only(process)) continue;
                using var handle = OpenProcess(SetQuota | QueryLimitedInformation, false, process.Id);
                if (!handle.IsInvalid && EmptyWorkingSet(handle)) trimmed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            finally { process.Dispose(); }
        }
        // Give Windows a moment to settle before reading the figure again.
        Thread.Sleep(250);
        return new(trimmed, before, monitor.Sample().MemoryUsed);
    }
}
