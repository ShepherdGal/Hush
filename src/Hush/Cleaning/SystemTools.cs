using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Security.Principal;
using Microsoft.Win32;

namespace Hush;

// Cleanup that Hush never does by deleting files itself: Windows' own tools or APIs do it, so Windows keeps control of what is safe.
// The engine only knows this interface; tests substitute a fake and never start a real tool.
internal sealed record SystemToolOffer(string ToolId, string CategoryId, string Title, string Detail, long Bytes, long ItemCount = 0);

internal interface ISystemTools
{
    // Read-only: the tools that apply to this PC right now.
    IReadOnlyList<SystemToolOffer> Probe(CancellationToken token);
    // Runs one tool and returns when Windows has finished. Throws IOException when it did not complete.
    void Run(string toolId, CancellationToken token);
}

internal sealed class WindowsSystemTools : ISystemTools
{
    internal const string WindowsOld = "windows-old", ComponentStore = "component-store", ClipboardHistory = "clipboard-history";
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
            offers.Add(new(ComponentStore, "ComponentStore", "Windows component cleanup", "DISM component cleanup · Windows decides what is removed · installed updates can no longer be uninstalled", 0));
        if (ClipboardHistoryEnabled())
            offers.Add(new(ClipboardHistory, "Clipboard", "Windows clipboard history", "Clears the Win+V history · pinned clips are kept", 0));
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

    // Component cleanup only: never /ResetBase, which would also make every installed update permanent.
    internal static string BuildComponentCleanupArguments() => "/Online /Cleanup-Image /StartComponentCleanup";

    private static void RunElevated(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal })
                ?? throw new IOException("Windows did not start the cleanup tool.");
            process.WaitForExit();
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
