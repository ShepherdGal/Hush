using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hush;

internal sealed record UpdatePlan(string Target, string Staged, string NewHash, string OldHash, string Version, int ParentId, long ParentStart);

internal static class UpdateInstaller
{
    private static string UpdateRoot => Path.Combine(CleanupEngine.DataDirectory, "Updates");
    internal static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static void Start(string staged, AppRelease release)
    {
        if (release.Version <= UpdateService.CurrentVersion) throw new IOException("The update must be newer than the installed version.");
        string current = Environment.ProcessPath ?? throw new IOException("Application path is unavailable.");
        string parent = Path.GetDirectoryName(current)!;
        if (!Path.GetFileName(current).Equals("Hush.exe", StringComparison.OrdinalIgnoreCase)
            || !CleanupEngine.SafePath(current, parent) || typeof(App).Assembly.Location.Length != 0)
            throw new IOException("Updates install from the published portable app. Open its Hush.exe in a writable folder.");
        string directory = Path.GetDirectoryName(staged)!;
        if (!directory.StartsWith(UpdateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !CleanupEngine.SafePath(staged, directory))
            throw new IOException("Invalid update staging location.");
        UpdateService.VerifyExecutable(staged, release.Version);
        if (!Hash(staged).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("The staged update changed.");
        string probe = Path.Combine(parent, ".hush-write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        using var process = Process.GetCurrentProcess();
        var plan = new UpdatePlan(current, staged, release.Sha256, Hash(current), release.Version.ToString(), process.Id, process.StartTime.ToUniversalTime().Ticks);
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
            if (!directory.StartsWith(UpdateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(planPath) != "install.json" || !CleanupEngine.SafePath(planPath, directory)) return 1;
            var plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath)) ?? throw new IOException("Invalid update plan.");
            string parent = Path.GetDirectoryName(plan.Target)!;
            if (plan.Staged != Path.Combine(directory, "Hush.exe") || !CleanupEngine.SafePath(plan.Staged, directory)
                || !Path.GetFileName(plan.Target).Equals("Hush.exe", StringComparison.OrdinalIgnoreCase)
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
            string backup = Path.Combine(parent, "Hush.previous-" + Guid.NewGuid().ToString("N") + ".exe");
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
            return 0;
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(directory) && directory.StartsWith(UpdateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), ex.Message); } catch { }
            return 1;
        }
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
