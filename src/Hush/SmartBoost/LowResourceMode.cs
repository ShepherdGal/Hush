using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hush;

// Makes Hush itself lighter. It only changes this process: its CPU priority, Windows' efficiency mode (EcoQoS) for it, and how often
// it redraws and reports. No other program, service or setting is touched.
internal static class LowResourceMode
{
    [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(IntPtr process, int information, ref PowerThrottling state, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);
    private const int ProcessPowerThrottlingInformation = 4, BackgroundBegin = 0x00010000, BackgroundEnd = 0x00020000;

    public static bool IsActive { get; private set; }
    // Progress reports during a scan: 40 ms normally, 250 ms when saving resources.
    public static int ReportIntervalMs => IsActive ? 250 : 40;

    public static void Apply(bool on)
    {
        IsActive = on;
        ScanActivity.FrameInterval = TimeSpan.FromMilliseconds(on ? 100 : 33);
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = on ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
        }
        catch { }
        try
        {
            var state = new PowerThrottling { Version = 1, ControlMask = 1, StateMask = on ? 1u : 0u };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingInformation, ref state, Marshal.SizeOf<PowerThrottling>());
        }
        catch { }
    }

    // Lowers CPU and disk priority of the calling thread until disposed (a background scan must not compete with what the person is doing).
    public static IDisposable BackgroundScope()
    {
        bool began = SetThreadPriority(GetCurrentThread(), BackgroundBegin);
        return new Scope(began);
    }
    private sealed class Scope(bool began) : IDisposable
    {
        public void Dispose() { if (began) SetThreadPriority(GetCurrentThread(), BackgroundEnd); }
    }
}
