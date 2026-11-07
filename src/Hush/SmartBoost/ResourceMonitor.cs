using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Hush;

internal readonly record struct ResourceSnapshot(double CpuPercent, long MemoryUsed, long MemoryTotal, long DiskFree, long DiskTotal, long HushWorkingSet)
{
    public double MemoryPercent => MemoryTotal <= 0 ? 0 : 100.0 * MemoryUsed / MemoryTotal;
    public double DiskUsedPercent => DiskTotal <= 0 ? 0 : 100.0 * (DiskTotal - DiskFree) / DiskTotal;
    // The one-line reading on the Info card: the most pressing thing first, plain words, no advice that is not true.
    public const double Busy = 85, Full = 90;
    public string Headline => DiskUsedPercent >= Full ? "The system drive is almost full."
        : MemoryPercent >= Busy ? "Memory is busy right now."
        : CpuPercent >= Busy ? "The processor is busy right now."
        : "Everything looks light.";
}

// Three cheap readings (Windows' own counters, no performance-counter objects, no background thread): processor use since the previous
// reading, physical memory in use and free space on the system drive. Hush only samples while the indicators are on screen.
internal sealed class ResourceMonitor
{
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private ulong lastIdle, lastBusy;
    private bool primed;

    public ResourceSnapshot Sample()
    {
        double cpu = 0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // Kernel time includes idle time.
            ulong busy = kernel.Value + user.Value - idle.Value;
            if (primed)
            {
                ulong idleDelta = idle.Value - lastIdle, busyDelta = busy - lastBusy, total = idleDelta + busyDelta;
                cpu = total == 0 ? 0 : Math.Clamp(100.0 * busyDelta / total, 0, 100);
            }
            lastIdle = idle.Value; lastBusy = busy; primed = true;
        }
        long used = 0, totalMemory = 0;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory)) { totalMemory = (long)memory.TotalPhys; used = (long)(memory.TotalPhys - memory.AvailPhys); }
        long free = 0, size = 0;
        try
        {
            string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
            var drive = new DriveInfo(root);
            if (drive.IsReady) { free = drive.AvailableFreeSpace; size = drive.TotalSize; }
        }
        catch (IOException) { }
        long own = 0;
        try { using var self = Process.GetCurrentProcess(); own = self.WorkingSet64; } catch { }
        return new(cpu, used, totalMemory, free, size, own);
    }
}
