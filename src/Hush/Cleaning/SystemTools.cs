using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Security.Principal;
using Microsoft.Win32;

namespace Hush;

// Cleanup that is not plain file deletion in a listed folder: the previous Windows installation (removed by Hush itself, see WindowsOldRemoval) and the
// clipboard (cleared through the clipboard API). 0.9, user direction: Hush uses NO Windows cleanup tool (no Disk Cleanup, no DISM), because they often
// removed nothing. The engine only knows this interface; tests substitute a fake and never remove a real folder or clear a real clipboard.
internal sealed record SystemToolOffer(string ToolId, string CategoryId, string Title, string Detail, long Bytes, long ItemCount = 0, bool SelectedByDefault = false);

internal interface ISystemTools
{
    // Read-only: the tools that apply to this PC right now.
    IReadOnlyList<SystemToolOffer> Probe(CancellationToken token);
    // Runs one tool and returns when Windows has finished. Throws IOException when it did not complete.
    void Run(string toolId, CancellationToken token);
}

internal sealed class WindowsSystemTools : ISystemTools
{
    internal const string WindowsOld = "windows-old", ClipboardHistory = "clipboard-history", ClipboardCurrent = "clipboard-current";

    public IReadOnlyList<SystemToolOffer> Probe(CancellationToken token)
    {
        var offers = new List<SystemToolOffer>();
        string previous = WindowsOldRemoval.ExpectedPath();
        if (Directory.Exists(previous) && !IsLink(previous))
        {
            var (size, complete) = MeasuredSize(previous, token);
            offers.Add(new(WindowsOld, "WindowsOld", "Previous Windows installation", "Windows.old · removed by Hush itself, folder by folder · includes the old Users folder · you can no longer roll back to the previous Windows version"
                + (complete ? "" : " · size is an estimate (measuring stopped early)") + (IsElevated() ? "" : " · needs administrator mode"), size));
        }
        if (ClipboardHistoryEnabled())
            offers.Add(new(ClipboardHistory, "Clipboard", "Windows clipboard history", "Clears the Win+V history · pinned clips are kept", 0));
        else if (ClipboardHasContent())
            offers.Add(new(ClipboardCurrent, "Clipboard", "Current clipboard", "Clears what you last copied · clipboard history is off", 0));
        return offers;
    }

    public void Run(string toolId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (toolId)
        {
            case WindowsOld:
                try { WindowsOldRemoval.RemoveWindowsOld(token); }
                finally { ForgetSizeCache(); } // the remembered size no longer describes the folder
                break;
            case ClipboardHistory:
                ClearClipboardHistory();
                break;
            case ClipboardCurrent:
                ClearCurrentClipboard();
                break;
            default:
                throw new IOException("Unknown cleanup tool.");
        }
    }

    // Presence only (CountClipboardFormats); the content is never read.
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int CountClipboardFormats();
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    private static bool ClipboardHasContent() { try { return CountClipboardFormats() > 0; } catch { return false; } }
    private static void ClearCurrentClipboard()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try { if (EmptyClipboard()) return; } finally { CloseClipboard(); }
                break;
            }
            Thread.Sleep(80); // another program has it open for a moment
        }
        throw new IOException("Windows did not clear the clipboard (another program was using it).");
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Windows records whether Win+V history is on here; it is the documented user setting, read-only for us.
    private static bool ClipboardHistoryEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Clipboard"); return key?.GetValue("EnableClipboardHistory") is int value && value == 1; }
        catch { return false; }
    }

    private static void ClearClipboardHistory()
    {
        try
        {
            if (!Windows.ApplicationModel.DataTransfer.Clipboard.ClearHistory())
                throw new IOException("Windows did not clear the clipboard history. Use Settings > System > Clipboard > Clear.");
        }
        catch (Exception ex) when (ex is not IOException) { throw new IOException("Windows could not clear the clipboard history: " + ex.Message); }
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch { return true; }
    }

    // Windows.old can hold tens of gigabytes in hundreds of thousands of files. Measuring it is limited in time, and the answer is kept for a minute: a scan, and
    // the fresh probe right before a clean, used to walk the whole tree each time, and a walk that outlived its step went on in the background (0.9 audit).
    internal static int SizeBudgetMilliseconds = 15000, SizeCacheMilliseconds = 60000;
    private static (string Path, long Bytes, bool Complete, long Tick)? sizeCache;
    private static readonly object sizeLock = new();

    private static (long Bytes, bool Complete) MeasuredSize(string path, CancellationToken token)
    {
        lock (sizeLock)
        {
            if (sizeCache is { } known && string.Equals(known.Path, path, StringComparison.OrdinalIgnoreCase) && Environment.TickCount64 - known.Tick < SizeCacheMilliseconds)
                return (known.Bytes, known.Complete);
        }
        long bytes = DirectorySize(path, token, SizeBudgetMilliseconds, out bool complete);
        if (!token.IsCancellationRequested) lock (sizeLock) sizeCache = (path, bytes, complete, Environment.TickCount64);
        return (bytes, complete);
    }

    // Read-only size estimate; links are never followed and unreadable folders count as zero. Stops (complete = false) when the token is cancelled or the time budget is used up.
    internal static long DirectorySize(string path, CancellationToken token) => DirectorySize(path, token, int.MaxValue, out _);

    internal static long DirectorySize(string path, CancellationToken token, int budgetMilliseconds, out bool complete)
    {
        long total = 0;
        complete = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
        try
        {
            var lengths = new FileSystemEnumerable<long>(path, (ref FileSystemEntry entry) => entry.IsDirectory ? 0 : entry.Length, options);
            int count = 0;
            foreach (long length in lengths)
            {
                total += length;
                if (token.IsCancellationRequested) { complete = false; break; }
                if ((++count & 255) == 0 && clock.ElapsedMilliseconds > budgetMilliseconds) { complete = false; break; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    // Tests: forget the remembered size.
    internal static void ForgetSizeCache() { lock (sizeLock) sizeCache = null; }
}
