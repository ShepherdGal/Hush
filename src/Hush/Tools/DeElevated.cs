using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Hush;

// Runs one program as the signed-in person WITHOUT administrator rights, from an elevated Hush (owner, November 20, 2026: updating Codex CLI said "the
// requested operation is not permitted": Windows' app installer refuses to change a program installed for the person's own account from an administrator
// process, error 0x8A15007D). It borrows the desktop shell's own token (the person's normal rights), so no prompt appears; the output goes to a file in the
// person's temp folder and is read back. Used only for that retry; nothing else in Hush lowers its rights.
internal static class DeElevated
{
    internal static async Task<(int Code, string Output)> RunAsync(string exe, string arguments, CancellationToken token, TimeSpan limit)
    {
        string output = Path.Combine(Path.GetTempPath(), "hush-as-user-" + Guid.NewGuid().ToString("N") + ".txt");
        string command = $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /d /s /c \"\"{exe}\" {arguments} > \"{output}\" 2>&1\"";
        IntPtr process = Start(command);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (WaitForSingleObject(process, 200) == 0x102 /* WAIT_TIMEOUT */)
            {
                if (token.IsCancellationRequested || clock.Elapsed > limit) { TerminateProcess(process, 1); return (int.MinValue, ""); }
                await Task.Delay(50, CancellationToken.None);
            }
            GetExitCodeProcess(process, out uint code);
            string text = File.Exists(output) ? await File.ReadAllTextAsync(output, CancellationToken.None) : "";
            return (unchecked((int)code), text);
        }
        finally
        {
            CloseHandle(process);
            try { File.Delete(output); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static IntPtr Start(string command)
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) throw new IOException("The desktop is not running, so Hush cannot run this as you.");
        GetWindowThreadProcessId(shell, out uint pid);
        IntPtr shellProcess = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (shellProcess == IntPtr.Zero) throw new Win32Exception();
        IntPtr shellToken = IntPtr.Zero, primary = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(shellProcess, 0x0002 /* TOKEN_DUPLICATE */, out shellToken)) throw new Win32Exception();
            const uint access = 0x0001 | 0x0002 | 0x0008 | 0x0080 | 0x0100;   // assign primary, duplicate, query, adjust default, adjust session id
            if (!DuplicateTokenEx(shellToken, access, IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out primary)) throw new Win32Exception();
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessWithTokenW(primary, 0, null, command, 0x08000000 /* CREATE_NO_WINDOW */, IntPtr.Zero, null, ref startup, out ProcessInformation info))
                throw new Win32Exception();
            CloseHandle(info.hThread);
            return info.hProcess;
        }
        finally
        {
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
            CloseHandle(shellProcess);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? application, string commandLine, uint creationFlags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
