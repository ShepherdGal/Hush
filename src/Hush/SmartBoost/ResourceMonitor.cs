using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Hush;

internal readonly record struct ResourceSnapshot(double CpuPercent, long MemoryUsed, long MemoryTotal, long DiskFree, long DiskTotal, long HushWorkingSet)
{
    public double MemoryPercent => MemoryTotal <= 0 ? 0 : 100.0 * MemoryUsed / MemoryTotal;
    public double DiskUsedPercent => DiskTotal <= 0 ? 0 : 100.0 * (DiskTotal - DiskFree) / DiskTotal;
    // Where each Info bar turns amber (middle) and red (bad). Below the first number a bar is green (good).
    public const double CpuAmber = 60, CpuRed = 85, MemoryAmber = 70, MemoryRed = 85, DiskAmber = 75, DiskRed = 90, TempAmber = 50, TempRed = 100;
    public static MeterLevel LevelOf(double percent, double amberAt, double redAt) => percent >= redAt ? MeterLevel.Red : percent >= amberAt ? MeterLevel.Amber : MeterLevel.Green;

    // The one line under "Info": what the situation is and, when it is not good, what to do about it. The most pressing thing is named first (red before amber;
    // drive, memory, processor, temporary files). tempPercent is how full the Temporary files bar is (0-100), or a negative number while it is not read yet.
    // Plain words, no promise of speed: closing programs and cleaning free memory and space, nothing more.
    public InfoReading Assess(double tempPercent)
    {
        var temp = tempPercent < 0 ? MeterLevel.Green : LevelOf(tempPercent, TempAmber, TempRed);
        var disk = LevelOf(DiskUsedPercent, DiskAmber, DiskRed);
        var memory = LevelOf(MemoryPercent, MemoryAmber, MemoryRed);
        var cpu = LevelOf(CpuPercent, CpuAmber, CpuRed);
        foreach (var level in new[] { MeterLevel.Red, MeterLevel.Amber })
        {
            if (disk == level) return new(level, level == MeterLevel.Red ? "The system drive is almost full. Clean files or uninstall big programs." : "The system drive is getting full. A cleanup soon would help.");
            if (memory == level) return new(level, level == MeterLevel.Red ? "Memory is almost full. Close programs you aren't using, or restart." : "Memory use is rising. Closing unused programs or Boost helps.");
            if (cpu == level) return new(level, level == MeterLevel.Red ? "The processor is overloaded. If it stays so, close heavy programs." : "The processor is working hard. Close programs you don't need.");
            if (temp == level) return new(level, level == MeterLevel.Red ? "Temporary files have piled up. Press Boost to clean them." : "Temporary files are building up. Boost can clean them.");
        }
        return new(MeterLevel.Green, "Everything looks good. Nothing needs your attention.");
    }
}

internal enum MeterLevel { Green, Amber, Red }

internal readonly record struct InfoReading(MeterLevel Level, string Text);

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
