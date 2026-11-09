using System.IO;

namespace Hush;

// A short local note of every error nobody handled (0.9 audit: a failure on a background thread used to vanish or close the app without a trace). Written under
// %LOCALAPPDATA%\Hush\Logs\crash.log, kept small (the older half moves to crash.log.old), never throws, and holds only what .NET reports about the error:
// no settings, no file contents. Nothing is ever sent anywhere.
internal static class CrashLog
{
    private const long MaxBytes = 256 * 1024;
    private const int MaxText = 8000;

    internal static void Write(string source, Exception? exception, string? dataDirectory = null)
    {
        try
        {
            string directory = Path.Combine(dataDirectory ?? CleanupEngine.DataDirectory, "Logs");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "crash.log");
            if (File.Exists(file) && new FileInfo(file).Length > MaxBytes) File.Move(file, file + ".old", true);
            string detail = exception?.ToString() ?? "(no exception object)";
            if (detail.Length > MaxText) detail = detail[..MaxText] + " …";
            File.AppendAllText(file, $"{DateTime.UtcNow:O}  [{source}] Hush {UpdateService.DisplayVersion}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* the note is a courtesy: logging must never cause a second failure */ }
    }

    // Hooks for errors nobody caught: other threads (the process is about to end) and tasks nobody awaited (observed, so they do not end the process).
    internal static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Write("unhandled", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Write("task", args.Exception); args.SetObserved(); };
    }
}
