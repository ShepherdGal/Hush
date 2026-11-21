using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;

namespace Hush;

// Keeps Hush's memory small. Memory a window has touched stays "in use" until Windows needs it back, so a few seconds after Hush is minimized or
// another program is in front, Hush gives its idle pages back (one full garbage collection, then the working set is released: the pages move to
// Windows' standby list and come back instantly if Hush is used again). Nothing is lost or reloaded from disk. Never runs while Hush is in front.
public partial class MainWindow
{
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);

    private int trimRequest;
    // Tests never trim (they share one process); only the real app window does.
    private bool TrimsMemory => Application.Current?.MainWindow == this;

    private void InitializeMemoryTrim()
    {
        Deactivated += (_, _) => ScheduleMemoryTrim(4000);
        Activated += (_, _) => trimRequest++;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) ScheduleMemoryTrim(1500); else trimRequest++; };
    }

    private async void ScheduleMemoryTrim(int milliseconds)
    {
        if (!TrimsMemory) return;
        int request = ++trimRequest;
        await Task.Delay(milliseconds);
        if (request != trimRequest || busy || (IsActive && WindowState != WindowState.Minimized)) return;
        TrimMemoryNow(releasePages: true);
    }

    // Drops what is no longer needed (old scan results, finished animations, discarded pages). With releasePages the working set is emptied too.
    internal static void TrimMemoryNow(bool releasePages)
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        if (!releasePages) return;
        try { using var self = Process.GetCurrentProcess(); EmptyWorkingSet(self.Handle); } catch { /* purely an optimization */ }
    }

    // After a scan's results are replaced or a clean has finished: collect once so the old list's memory is returned. Not in tests.
    private void ReleaseOldResults()
    {
        if (!TrimsMemory) return;
        Dispatcher.BeginInvoke(() => TrimMemoryNow(releasePages: false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
