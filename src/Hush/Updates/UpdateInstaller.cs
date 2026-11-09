using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hush;

internal sealed record UpdatePlan(string Target, string Staged, string NewHash, string OldHash, string Version, int ParentId, long ParentStart);

internal static class UpdateInstaller
{
    // Where updates are staged. A normal Hush stages in the person's own data folder. An elevated Hush runs the update helper elevated, so everything the
    // helper trusts (the staged app, its recorded hash, the plan and the helper copy itself) must sit where only administrators can write: any program the
    // person runs could otherwise swap those files while the helper starts and have its own code copied over Hush and started with administrator rights.
    private static string UserRoot => Path.Combine(CleanupEngine.DataDirectory, "Updates");
    internal static string? AdminRootOverride; // tests point this at a fixture folder
    internal static string AdminRoot => AdminRootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hush", "Updates");
    private static string UpdateRoot => AdminMode.Elevation.IsElevated ? AdminRoot : UserRoot;
    internal static string StagingRoot => UpdateRoot;
    // Creates (if needed) and validates the staging root. For an elevated Hush that means the administrator-only folder; it throws IOException if that
    // folder cannot be created or is not administrator-only (for example because another user pre-created it), and then nothing is staged or run.
    internal static string PrepareStagingRoot()
    {
        if (!AdminMode.Elevation.IsElevated) return UserRoot;
        return AdminOnlyDirectory.Ensure(AdminRoot);
    }
    // The one rollback copy kept next to Hush.exe. Each update replaces it, so old versions never pile up (they used to be one 25 MB file per update).
    internal const string RollbackName = "Hush.previous.exe";
    // The installed copy is Hush.exe; the downloaded portable app is Hush-Portable.exe. The updated file keeps whichever name it has.
    internal static bool IsUpdatableName(string name) => name.Equals("Hush.exe", StringComparison.OrdinalIgnoreCase) || name.Equals(UpdateService.PackagedExecutable, StringComparison.OrdinalIgnoreCase);
    // Only the published single-file exe may replace itself. A development build (apphost + Hush.dll beside it, or a test host) must not.
    // In a single-file app the managed code lives inside the exe, so Hush.dll is not next to it. (Assembly.Location is the wrong tool here:
    // it returns "" for bundled assemblies and the compiler rightly warns about it, IL3000.)
    internal static bool IsPublishedSingleFile => !File.Exists(Path.Combine(AppContext.BaseDirectory, "Hush.dll"));
    internal static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static void Start(string staged, AppRelease release)
    {
        if (release.Version <= UpdateService.CurrentVersion) throw new IOException("The update must be newer than the installed version.");
        string current = Environment.ProcessPath ?? throw new IOException("Application path is unavailable.");
        string parent = Path.GetDirectoryName(current)!;
        if (!IsUpdatableName(Path.GetFileName(current))
            || !CleanupEngine.SafePath(current, parent) || !IsPublishedSingleFile)
            throw new IOException("Updates install from the published portable app. Open Hush-Portable.exe (or the installed Hush.exe) from a writable folder.");
        string directory = Path.GetDirectoryName(staged)!;
        PrepareStagingRoot(); // elevated: the administrator-only folder must still be administrator-only right now
        if (!directory.StartsWith(UpdateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !CleanupEngine.SafePath(staged, directory))
            throw new IOException("Invalid update staging location.");
        UpdateService.VerifyExecutable(staged, release.Version);
        // The download was verified against the release's package hash; the extracted app's own hash was recorded beside it.
        string stagedHash = Hash(staged);
        string recorded = Path.Combine(directory, UpdateService.StagedHashName);
        if (!File.Exists(recorded) || !File.ReadAllText(recorded).Trim().Equals(stagedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("The staged update changed.");
        string probe = Path.Combine(parent, ".hush-write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        using var process = Process.GetCurrentProcess();
        var plan = new UpdatePlan(current, staged, stagedHash, Hash(current), release.Version.ToString(), process.Id, process.StartTime.ToUniversalTime().Ticks);
        string planPath = Path.Combine(directory, "install.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        string helper = Path.Combine(directory, "Hush.Updater.exe");
        File.Copy(current, helper, false);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planPath);
        _ = Process.Start(start) ?? throw new IOException("Could not start the update helper.");
    }
    internal static async Task<int> ApplyAsync(string planPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
        try
        {
            // An elevated helper only trusts a plan that lies inside the administrator-only staging folder (checked again here, in the helper itself).
            if (AdminMode.Elevation.IsElevated) AdminOnlyDirectory.Validate(AdminRoot);
            if (!directory.StartsWith(UpdateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(planPath) != "install.json" || !CleanupEngine.SafePath(planPath, directory)) return 1;
            var plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath)) ?? throw new IOException("Invalid update plan.");
            string parent = Path.GetDirectoryName(plan.Target)!;
            if (plan.Staged != Path.Combine(directory, "Hush.exe") || !CleanupEngine.SafePath(plan.Staged, directory)
                || !IsUpdatableName(Path.GetFileName(plan.Target))
                || !CleanupEngine.SafePath(plan.Target, parent)) throw new IOException("Unsafe update paths.");
            var nextVersion = Version.Parse(plan.Version);
            if (nextVersion <= UpdateService.CurrentVersion) throw new IOException("Refusing an older or identical update.");
            UpdateService.VerifyExecutable(plan.Staged, nextVersion);
            if (!Hash(plan.Staged).Equals(plan.NewHash, StringComparison.OrdinalIgnoreCase) || Hash(plan.Target) != plan.OldHash)
                throw new IOException("An application changed while preparing the update.");
            try
            {
                using var old = Process.GetProcessById(plan.ParentId);
                if (old.StartTime.ToUniversalTime().Ticks != plan.ParentStart) throw new IOException("Update process identity changed.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await old.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { /* Parent exited before the helper started. */ }
            if (Hash(plan.Target) != plan.OldHash) throw new IOException("The installed application changed.");
            string adjacent = Path.Combine(parent, ".hush-update-" + Guid.NewGuid().ToString("N") + ".exe");
            string backup = Path.Combine(parent, RollbackName);
            File.Copy(plan.Staged, adjacent, false);
            if (!Hash(adjacent).Equals(plan.NewHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Copy verification failed.");
            AtomicReplace(adjacent, plan.Target, backup);
            string receipt = Path.Combine(directory, "started.ok");
            try
            {
                var start = new ProcessStartInfo(plan.Target) { UseShellExecute = false, WorkingDirectory = parent };
                start.ArgumentList.Add("--update-complete"); start.ArgumentList.Add(receipt);
                using var updated = Process.Start(start) ?? throw new IOException("The updated app could not start.");
                var watch = Stopwatch.StartNew();
                while (!File.Exists(receipt) && !updated.HasExited && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(150);
                if (!File.Exists(receipt))
                {
                    if (!updated.HasExited) { updated.Kill(); await updated.WaitForExitAsync(); }
                    throw new IOException("The updated app did not become ready; restored the previous version.");
                }
            }
            catch
            {
                File.Copy(backup, plan.Target, true);
                Process.Start(new ProcessStartInfo(plan.Target) { UseShellExecute = false, WorkingDirectory = parent });
                throw;
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "Installed " + plan.Version + ". Previous version: " + backup);
            RemoveLegacyRollbackCopies(parent);
            return 0;
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(directory) && directory.StartsWith(UpdateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), ex.Message); } catch { }
            return 1;
        }
    }
    // Older versions of Hush left one Hush.previous-<id>.exe per update; after a successful update only the single current rollback copy stays.
    internal static int RemoveLegacyRollbackCopies(string folder)
    {
        int removed = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "Hush.previous-*.exe"))
            {
                try { File.Delete(file); removed++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    // Update staging (a 25 MB downloaded copy plus a 25 MB helper copy per attempt) is removed once an update has finished or failed, and
    // after two days for a download that was never installed. A folder whose helper is still running just fails to delete and is retried later.
    // Tidies the person's own staging folder and, for an elevated Hush, the administrator-only one (only after it has proven to be administrator-only).
    internal static void PruneAll()
    {
        PruneStaging(UserRoot);
        if (!AdminMode.Elevation.IsElevated) return;
        try { AdminOnlyDirectory.Validate(AdminRoot); PruneStaging(AdminRoot); }
        catch (IOException) { /* missing or not trustworthy: left alone */ }
    }

    internal static int PruneStaging(string root, DateTime? nowUtc = null)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(root) || !CleanupEngine.SafePath(root, root)) return 0;
            var now = nowUtc ?? DateTime.UtcNow;
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    bool finished = File.Exists(Path.Combine(directory, "started.ok")) || File.Exists(Path.Combine(directory, "result.txt"));
                    bool stale = Directory.GetLastWriteTimeUtc(directory) < now - TimeSpan.FromDays(2);
                    if (!(finished || stale) || !CleanupEngine.SafePath(directory, root)) continue;
                    // Markers go last: if a file is still in use (the helper has not exited yet) the markers stay, so the next pass knows it is finished.
                    foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                        if (Path.GetFileName(file) is not ("started.ok" or "result.txt")) File.Delete(file);
                    Directory.Delete(directory, true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    internal static void AtomicReplace(string source, string target, string backup) => File.Replace(source, target, backup, false);
    internal static void ConfirmStartup(string receipt)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(receipt))!;
        if (Path.GetFileName(receipt) != "started.ok" || !directory.StartsWith(UpdateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !CleanupEngine.SafePath(directory, directory)) return;
        using var stream = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.WriteByte(1); stream.Flush(true);
    }
}
