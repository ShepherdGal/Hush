using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Security.Principal;
using Microsoft.Win32;

namespace Hush;

// Cleanup that Hush never does by deleting files itself: Windows' own tools or APIs do it, so Windows keeps control of what is safe.
// The engine only knows this interface; tests substitute a fake and never start a real tool.
// SelectedByDefault: only the Thumbnails tool, and only when Hush already runs as administrator (so no permission prompt can surprise a default clean).
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
    internal const string WindowsOld = "windows-old", ComponentStore = "component-store", ClipboardHistory = "clipboard-history",
        ComponentDeep = "component-deep", Thumbnails = "thumbnails", DefenderFiles = "defender-files", ClipboardCurrent = "clipboard-current";
    // Disk Cleanup handler names (keys under VolumeCaches). Windows' own handlers decide what is safe to remove.
    internal const string ThumbnailHandler = "Thumbnail Cache", DefenderHandler = "Windows Defender";
    // Windows tools can take many minutes (component cleanup especially); Hush waits this long and then says so instead of reporting a failure.
    internal static int ToolTimeoutMinutes = 45;
    // Disk Cleanup's per-run switch set. Only handlers carrying StateFlags<id> run, so one handler is selected at a time.
    internal const int CleanupSetId = 7351;
    private const string VolumeCaches = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    public IReadOnlyList<SystemToolOffer> Probe(CancellationToken token)
    {
        var offers = new List<SystemToolOffer>();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string systemDrive = (Path.GetPathRoot(windows) ?? @"C:\");
        string previous = Path.Combine(systemDrive, "Windows.old");
        if (Directory.Exists(previous) && !IsLink(previous))
            offers.Add(new(WindowsOld, "WindowsOld", "Previous Windows installation", "Windows.old · removed by Disk Cleanup · you can no longer roll back to the previous Windows version",
                DirectorySize(previous, token)));
        if (Directory.Exists(Path.Combine(windows, "WinSxS")) && Environment.OSVersion.Version.Build >= 22000)
        {
            // Elevated, DISM can tell how much is really reclaimable before the person decides (the folder looks much bigger than it is: most of it is hard links).
            long reclaimable = ComponentStoreReclaimable(token);
            offers.Add(new(ComponentStore, "ComponentStore", "Windows component cleanup", "DISM component cleanup · Windows decides what is removed · installed updates can no longer be uninstalled" + (reclaimable > 0 ? " · DISM estimates about " + Sizes.Format(reclaimable) + " can be reclaimed" : ""), reclaimable));
            offers.Add(new(ComponentDeep, "ComponentReset", "Deep component cleanup", "DISM /ResetBase · updates installed so far can no longer be uninstalled · this cannot be undone", 0));
        }
        if (HandlerExists(ThumbnailHandler))
            offers.Add(new(Thumbnails, "Thumbnails", "Thumbnail cache (Windows' own cleanup)", "Disk Cleanup's Thumbnails step clears the cache Explorer keeps open · Explorer is not closed · previews are rebuilt as needed", 0, SelectedByDefault: IsElevated()));
        if (HandlerExists(DefenderHandler))
            offers.Add(new(DefenderFiles, "Defender", "Microsoft Defender Antivirus files (Windows' own cleanup)", "Disk Cleanup's Defender step removes only non-critical files Microsoft marks as safe · protection history, quarantine and definitions are untouched", 0));
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
                RunElevated("cmd.exe", BuildDiskCleanupArguments("Previous Installations", CleanupSetId));
                string previous = Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\", "Windows.old");
                if (Directory.Exists(previous)) throw new IOException("Disk Cleanup finished but Windows.old is still there (it may have been canceled).");
                break;
            case ComponentStore:
                RunElevated("dism.exe", BuildComponentCleanupArguments());
                break;
            case ClipboardHistory:
                ClearClipboardHistory();
                break;
            case ClipboardCurrent:
                ClearCurrentClipboard();
                break;
            case ComponentDeep:
                RunElevated("dism.exe", BuildResetBaseArguments());
                analysis = null; // the estimate is stale now
                break;
            case Thumbnails:
                RunElevated("cmd.exe", BuildDiskCleanupArguments(ThumbnailHandler, CleanupSetId));
                break;
            case DefenderFiles:
                RunElevated("cmd.exe", BuildDiskCleanupArguments(DefenderHandler, CleanupSetId));
                break;
            default:
                throw new IOException("Unknown cleanup tool.");
        }
    }

    // One elevated cmd line: select only the named handler, run Disk Cleanup silently, then remove the switch again (even if cleanup fails).
    internal static string BuildDiskCleanupArguments(string handler, int id)
    {
        if (handler.IndexOfAny(['"', '&', '|', '^', '%', '\r', '\n']) >= 0) throw new ArgumentException("Unsafe handler name.", nameof(handler));
        string key = $"\"{VolumeCaches}\\{handler}\"";
        return $"/c reg add {key} /v StateFlags{id} /t REG_DWORD /d 2 /f && (cleanmgr.exe /sagerun:{id} & reg delete {key} /v StateFlags{id} /f)";
    }

    // The normal component cleanup keeps every installed update uninstallable; /ResetBase (its own red row) does not.
    internal static string BuildComponentCleanupArguments() => "/Online /Cleanup-Image /StartComponentCleanup";
    // 0.9, user-approved: deeper cleanup that makes every update installed so far permanent. Only ever started from its own Advanced row after consent.
    internal static string BuildResetBaseArguments() => "/Online /Cleanup-Image /StartComponentCleanup /ResetBase";

    private static bool HandlerExists(string handler)
    {
        try { using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\" + handler); return key != null; }
        catch { return false; }
    }

    // ---- DISM /AnalyzeComponentStore: read-only, needs administrator rights, cached for ten minutes because the scan and the clean both probe ----
    private static (long Bytes, DateTime When)? analysis;
    private static long ComponentStoreReclaimable(CancellationToken token)
    {
        if (analysis is { } cached && DateTime.UtcNow - cached.When < TimeSpan.FromMinutes(10)) return cached.Bytes;
        if (!IsElevated()) return 0;
        try
        {
            var start = new ProcessStartInfo("dism.exe", "/Online /Cleanup-Image /AnalyzeComponentStore") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var process = Process.Start(start);
            if (process == null) return 0;
            var output = process.StandardOutput.ReadToEndAsync();
            var clock = Stopwatch.StartNew();
            while (!process.WaitForExit(250))
            {
                if (token.IsCancellationRequested || clock.Elapsed > TimeSpan.FromSeconds(60)) { try { process.Kill(); } catch { } return 0; }
            }
            long bytes = ParseReclaimable(output.GetAwaiter().GetResult());
            analysis = (Math.Max(0, bytes), DateTime.UtcNow);
            return Math.Max(0, bytes);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException) { return 0; }
    }

    // The report lists sizes in a fixed order whatever the language: Explorer-reported, actual, shared with Windows, backups and disabled features,
    // cache and temporary data. What cleanup can reclaim is about the last two together. Returns -1 when the text does not look like that report.
    internal static long ParseReclaimable(string report)
    {
        var sizes = System.Text.RegularExpressions.Regex.Matches(report ?? "", @":\s*([0-9]+(?:[.,][0-9]+)?)\s*(KB|MB|GB|TB)\b");
        if (sizes.Count < 5) return -1;
        static long Bytes(System.Text.RegularExpressions.Match m)
        {
            double value = double.Parse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            double unit = m.Groups[2].Value switch { "KB" => 1024d, "MB" => 1024d * 1024, "GB" => 1024d * 1024 * 1024, _ => 1024d * 1024 * 1024 * 1024 };
            return (long)(value * unit);
        }
        return Bytes(sizes[3]) + Bytes(sizes[4]);
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

    private static void RunElevated(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal })
                ?? throw new IOException("Windows did not start the cleanup tool.");
            // Component cleanup can take many minutes. Past the limit Hush stops waiting (the tool keeps running) and says so, instead of calling it a failure.
            if (!process.WaitForExit(TimeSpan.FromMinutes(ToolTimeoutMinutes))) throw new IOException("Windows is still working on this cleanup. It keeps running in the background; scan again later to see the result.");
            // 3010: DISM succeeded and a restart finishes the job.
            if (process.ExitCode is not (0 or 3010)) throw new IOException($"The Windows cleanup tool reported error {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new IOException("Administrator permission was declined, so nothing was changed."); }
        catch (Win32Exception ex) { throw new IOException("Windows could not start the cleanup tool: " + ex.Message); }
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

    // Read-only size estimate; links are never followed and unreadable folders count as zero.
    internal static long DirectorySize(string path, CancellationToken token)
    {
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
        try
        {
            var lengths = new FileSystemEnumerable<long>(path, (ref FileSystemEntry entry) => entry.IsDirectory ? 0 : entry.Length, options);
            foreach (long length in lengths) { total += length; if (token.IsCancellationRequested) break; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }
}
