using System.IO;
using Hush;

// 0.9 audit, P1: the crash note, long-form roots and the bounded Windows.old measurement. Fixture folders only.
internal static class ReliabilityChecks
{
    internal static void Run(string fixture, Action<bool, string> check)
    {
        // ---- the crash note (T7) ----
        string data = Path.Combine(fixture, "crash-data");
        CrashLog.Write("test", new InvalidOperationException("first problem"), data);
        string log = Path.Combine(data, "Logs", "crash.log");
        string text = File.Exists(log) ? File.ReadAllText(log) : "";
        check(text.Contains("[test]") && text.Contains("InvalidOperationException") && text.Contains("first problem"), "An unhandled error is noted locally with its type, message and place");
        File.WriteAllText(log, new string('x', 300 * 1024));
        CrashLog.Write("test", new Exception("after rotation"), data);
        check(File.Exists(log + ".old") && new FileInfo(log).Length < 20_000 && File.ReadAllText(log).Contains("after rotation"), "The crash note stays small: a big one moves aside and a new one starts");
        bool threw = false;
        try { CrashLog.Write("test", new Exception("x"), "Z:\\no\\such?drive|folder"); } catch { threw = true; }
        check(!threw, "Writing the crash note can never throw (a missing or invalid folder is ignored)");

        // The harmless C++ module unload error at process exit is not noted as a crash (the real exe logged it on every normal close).
        Exception? shutdownNoise = null;
        try { ModuleUninitializer.Unload(); } catch (DllNotFoundException ex) { shutdownNoise = ex; }
        string noiseData = Path.Combine(fixture, "crash-noise");
        CrashLog.Write("unhandled", shutdownNoise, noiseData);
        CrashLog.Write("unhandled", new DllNotFoundException("a real missing library"), noiseData);
        string noiseLog = File.Exists(Path.Combine(noiseData, "Logs", "crash.log")) ? File.ReadAllText(Path.Combine(noiseData, "Logs", "crash.log")) : "";
        check(CrashLog.IsShutdownNoise(shutdownNoise) && noiseLog.Contains("a real missing library") && !noiseLog.Contains("ModuleUninitializer"),
            "The harmless error at process exit is not noted as a crash, while a real missing-library error still is");

        // ---- long-form roots (T9) ----
        check(CleanupEngine.LongPath(@"C:\Windows\Temp\") == @"C:\Windows\Temp\" && CleanupEngine.LongPath(@"C:\NOPE~1\missing") == @"C:\NOPE~1\missing", "A path without short names, or one that does not exist, is left as it is");
        string longFolder = Path.Combine(fixture, "A Rather Long Folder Name For Short Names");
        Directory.CreateDirectory(longFolder);
        string? shortForm = null;
        try
        {
            var type = Type.GetTypeFromProgID("Scripting.FileSystemObject");
            if (type != null) { dynamic fso = Activator.CreateInstance(type)!; shortForm = (string)fso.GetFolder(longFolder).ShortPath; }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        if (shortForm != null && shortForm.Contains('~'))
        {
            check(string.Equals(CleanupEngine.LongPath(shortForm), longFolder, StringComparison.OrdinalIgnoreCase), "A short 8.3 path comes back in its long form");
            string? savedTmp = Environment.GetEnvironmentVariable("TMP"), savedTemp = Environment.GetEnvironmentVariable("TEMP");
            try
            {
                Environment.SetEnvironmentVariable("TMP", shortForm); Environment.SetEnvironmentVariable("TEMP", shortForm);
                var temp = CleanupEngine.DefaultTargets().FirstOrDefault(t => t.Group == "User temp");
                check(temp == null || !temp.Root.Contains('~'), "A temp folder given by its short name becomes a cleanup location by its long name");
            }
            finally { Environment.SetEnvironmentVariable("TMP", savedTmp); Environment.SetEnvironmentVariable("TEMP", savedTemp); }
        }
        else Console.WriteLine("NOTE: 8.3 short names are off on this volume, so the short-name checks were not run here.");

        // ---- Windows.old measurement is bounded (T10) ----
        string tree = Path.Combine(fixture, "measure-tree");
        for (int folder = 0; folder < 4; folder++)
        {
            string dir = Path.Combine(tree, "d" + folder); Directory.CreateDirectory(dir);
            for (int file = 0; file < 400; file++) File.WriteAllText(Path.Combine(dir, $"f{file}.bin"), "12345");
        }
        long full = WindowsSystemTools.DirectorySize(tree, CancellationToken.None, int.MaxValue, out bool allCounted);
        check(allCounted && full == 1600 * 5, "The size of a folder is measured completely when there is time");
        long partial = WindowsSystemTools.DirectorySize(tree, CancellationToken.None, -1, out bool measuredAll);
        check(!measuredAll && partial < full, "A measurement with no time left stops early and says it is incomplete");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        WindowsSystemTools.DirectorySize(tree, cancelled.Token, int.MaxValue, out bool cancelledComplete);
        check(!cancelledComplete, "A cancelled measurement says it is incomplete");
    }
}

// Stands in for the runtime's C++ support type: only its name matters, because CrashLog recognizes the shutdown error by it in the stack trace.
internal static class ModuleUninitializer
{
    internal static void Unload() => throw new DllNotFoundException("Dll was not found.");
}
