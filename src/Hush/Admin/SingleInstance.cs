using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Hush;

// Only one Hush at a time (0.9): a named mutex per user session. A second start does not open a window; it asks the running Hush to come
// to the front and exits. The mutex is readable across elevation levels (explicit access for this user), and the wake-up message is allowed
// through the integrity barrier on the running window, so a normal start can reach an elevated Hush and the other way round.
internal sealed class SingleInstance : IDisposable
{
    internal const string ActivateMessageName = "Hush.Activate.v1";
    private SafeWaitHandle? handle;
    private SingleInstance(SafeWaitHandle handle) { this.handle = handle; }

    internal static string NameFor(string kind = "Hush")
    {
        using var identity = WindowsIdentity.GetCurrent();
        return $@"Local\{kind}-{identity.User?.Value ?? "user"}";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexW(ref SecurityAttributes attributes, bool initialOwner, string name);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision, out IntPtr descriptor, IntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public bool InheritHandle; }

    // Null when another Hush (any elevation level) already holds the name.
    internal static SingleInstance? TryAcquire(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        // This user and administrators may open it; nobody else.
        string sddl = $"D:(A;;GA;;;{identity.User?.Value})(A;;GA;;;BA)(A;;GA;;;SY)";
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out descriptor, IntPtr.Zero)) return new(new SafeWaitHandle(IntPtr.Zero, false)); // cannot decide: do not block starting
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor, InheritHandle = false };
            var created = CreateMutexW(ref attributes, false, name);
            int error = Marshal.GetLastWin32Error();
            if (created.IsInvalid) return error == 5 ? null : new(created); // access denied: it exists and is not ours to open
            if (error == 183) { created.Dispose(); return null; } // ERROR_ALREADY_EXISTS
            return new(created);
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    public void Dispose() { handle?.Dispose(); handle = null; }

    // ----- Waking the running window -----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, int action, IntPtr change);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    private static readonly IntPtr Broadcast = new(0xFFFF);

    // Called by the second start: wake every Hush window (only ours register this message).
    internal static void AskRunningToShow()
    {
        AllowSetForegroundWindow(-1); // ASFRW_ANY: the running window may take the foreground
        PostMessageW(Broadcast, RegisterWindowMessageW(ActivateMessageName), IntPtr.Zero, IntPtr.Zero);
    }

    // Called by the running window once it has a handle: brings it to the front when the message arrives.
    internal static void ListenForActivation(Window window, Action? onActivate = null)
    {
        uint message = RegisterWindowMessageW(ActivateMessageName);
        var helper = new WindowInteropHelper(window);
        void Attach()
        {
            ChangeWindowMessageFilterEx(helper.Handle, message, 1, IntPtr.Zero); // MSGFLT_ALLOW: a differently elevated Hush may wake us
            HwndSource.FromHwnd(helper.Handle)?.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if ((uint)msg != message) return IntPtr.Zero;
                handled = true;
                BringToFront(window);
                onActivate?.Invoke();
                return IntPtr.Zero;
            });
        }
        if (helper.Handle != IntPtr.Zero) Attach(); else window.SourceInitialized += (_, _) => Attach();
    }

    internal static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        window.Topmost = true; window.Topmost = false; // the usual nudge past the foreground lock
        window.Focus();
    }
}
