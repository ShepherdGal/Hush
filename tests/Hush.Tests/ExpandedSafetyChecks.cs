using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Hush;

internal static class ExpandedSafetyChecks
{
    internal static void Run(string fixture, Action<bool, string> check)
    {
        string reports = Path.Combine(fixture, "diagnostics"); Directory.CreateDirectory(reports);
        string report = Path.Combine(reports, "old.wer"), personal = Path.Combine(reports, "notes.txt");
        File.WriteAllText(report, "synthetic error report"); File.WriteAllText(personal, "personal fixture");
        foreach (var path in new[] { report, personal }) { File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-40)); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-40)); }
        var engine = new CleanupEngine([new(reports, "Windows diagnostics", CleanupKind.Junk, TimeSpan.FromDays(30), "*.wer")], dataDirectory: Path.Combine(fixture, "audit2"));
        var scan = engine.Scan(null, CancellationToken.None);
        check(scan.Items.Count == 1 && !scan.Items[0].Selected && scan.Items[0].Safety == SafetyLevel.Review, "Junk diagnostics are narrow, aged and opt-in");
        engine.Clean(scan.Items, null, CancellationToken.None);
        check(!File.Exists(report) && File.Exists(personal), "Junk cleanup preserves unrelated files");
        string temp = Path.Combine(reports, "windows-temp.tmp"); File.WriteAllText(temp, "synthetic Windows temp");
        File.SetCreationTimeUtc(temp, DateTime.UtcNow.AddDays(-10)); File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddDays(-10));
        var windows = new CleanupEngine([new(reports, "Windows temp", CleanupKind.Temp, TimeSpan.FromDays(7), "*.tmp", Recursive: false)], dataDirectory: Path.Combine(fixture, "audit3"));
        var windowsScan = windows.Scan(null, CancellationToken.None);
        check(windowsScan.Items.Count == 1 && windowsScan.Items[0].Selected && ReviewCategory.CategoryFor(windowsScan.Items[0]) == "Temp", "Safe Windows temp participates in the normal selected cleanup");
        windows.Clean(windowsScan.Items, null, CancellationToken.None);
        check(!File.Exists(temp) && File.Exists(personal), "Integrated temp cleanup removes only reviewed fixture candidates");
        string links = Path.Combine(fixture, "shortcuts"); Directory.CreateDirectory(links);
        string shortcutPath = Path.Combine(links, "missing.lnk");
        string missing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Hush-missing-fixture-" + Guid.NewGuid().ToString("N"), "app.exe");
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic dynamicShell = shell!; shortcut = dynamicShell.CreateShortcut(shortcutPath); dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = missing; dynamicShortcut.Save();
        }
        finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); if (shell != null) Marshal.FinalReleaseComObject(shell); }
        File.SetCreationTimeUtc(shortcutPath, DateTime.UtcNow.AddDays(-10)); File.SetLastWriteTimeUtc(shortcutPath, DateTime.UtcNow.AddDays(-10));
        var linkEngine = new CleanupEngine([new(links, "Broken shortcuts", CleanupKind.Shortcut, TimeSpan.FromDays(7), "*.lnk")], dataDirectory: Path.Combine(fixture, "audit4"));
        var linkScan = linkEngine.Scan(null, CancellationToken.None);
        bool eligible = CleanupEngine.IsOrphanExecutable(missing);
        check(linkScan.Items.Count == (eligible ? 1 : 0), "Shortcut scan requires both a verified missing app and installation directory");
        if (eligible)
        {
            var item = linkScan.Items.Single();
            check(!item.Selected && item.ShortcutHash?.Length == 64, "Broken shortcuts are opt-in with a content fingerprint");
            string unchanged = Path.Combine(links, "unchanged.lnk"); File.Copy(shortcutPath, unchanged);
            File.SetCreationTimeUtc(unchanged, item.CreatedUtc); File.SetLastWriteTimeUtc(unchanged, item.ModifiedUtc);
            var unchangedItem = linkEngine.Scan(null, CancellationToken.None).Items.Single(candidate => candidate.Path == unchanged);
            var cleanLink = linkEngine.Clean([unchangedItem], null, CancellationToken.None);
            check(cleanLink.Deleted == 1 && !File.Exists(unchanged) && !Directory.Exists(Path.GetDirectoryName(missing)), "Verified shortcut cleanup deletes only the link and never its target or folders");
            byte[] content = File.ReadAllBytes(shortcutPath); content[^1] ^= 1; File.WriteAllBytes(shortcutPath, content);
            File.SetCreationTimeUtc(shortcutPath, item.CreatedUtc); File.SetLastWriteTimeUtc(shortcutPath, item.ModifiedUtc);
            var result = linkEngine.Clean([item], null, CancellationToken.None);
            check(result.Skipped == 1 && File.Exists(shortcutPath), "Changed shortcut contents are rejected even if length and timestamps match");
        }
        string json = JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v9.0.0", body = "Synthetic patch notes", assets = new[] { new { name = UpdateService.AssetName, size = 10, digest = "sha256:" + new string('0', 64), url = $"https://api.github.com/repos/{UpdateService.Repository}/releases/assets/1" } } });
        using var document = JsonDocument.Parse(json);
        var release = UpdateService.ParseRelease(document.RootElement, new Version(1, 0, 0))!;
        check(release.Version == new Version(9, 0, 0) && release.Notes == "Synthetic patch notes", "Updater parses patch notes and newer release metadata");
        check(UpdateService.ParseRelease(document.RootElement, new Version(10, 0, 0)) == null, "Updater never downgrades the app");
        bool rejected = false;
        try { using var invalid = JsonDocument.Parse(json.Replace(UpdateService.Repository, "someone/else")); UpdateService.ParseRelease(invalid.RootElement, new Version(1, 0, 0)); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "Updater rejects assets outside the configured repository");
        rejected = false;
        try { using var invalid = JsonDocument.Parse(json.Replace("sha256:", "sha512:")); UpdateService.ParseRelease(invalid.RootElement, new Version(1, 0, 0)); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "Updater refuses missing or unsupported integrity digests");
        using var handler = new FakeDownload(); using var service = new UpdateService(handler);
        rejected = false;
        try { service.DownloadAsync(release, null, CancellationToken.None, Path.Combine(fixture, "updates")).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "Corrupt update downloads are rejected before replacement or execution");
        check(!handler.ApiAuthorized && !handler.CdnAuthorized, "Update downloads are anonymous: no credentials go to the release API or the asset CDN");
        string original = Path.Combine(fixture, "original.exe"), replacement = Path.Combine(fixture, "replacement.exe"), backup = Path.Combine(fixture, "previous.exe");
        File.WriteAllText(original, "old synthetic bytes"); File.WriteAllText(replacement, "new synthetic bytes");
        UpdateInstaller.AtomicReplace(replacement, original, backup);
        check(File.ReadAllText(original) == "new synthetic bytes" && File.ReadAllText(backup) == "old synthetic bytes", "Atomic updater replacement retains a rollback copy using fixture files only");
    }
    private sealed class FakeDownload : HttpMessageHandler
    {
        internal bool ApiAuthorized, CdnAuthorized;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                ApiAuthorized = request.Headers.Authorization?.Parameter == "synthetic-read-token";
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/synthetic-fixture");
                return Task.FromResult(redirect);
            }
            CdnAuthorized = request.Headers.Authorization != null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("0123456789")) });
        }
    }
}
