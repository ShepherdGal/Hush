using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hush;

internal sealed record AppRelease(Version Version, string Notes, Uri AssetUri, long Bytes, string Sha256);
internal interface IUpdateClient : IDisposable
{
    Task<AppRelease?> CheckAsync(CancellationToken cancellation);
    Task<string> DownloadAsync(AppRelease release, IProgress<double>? progress, CancellationToken cancellation, string? stagingRoot = null);
}

internal sealed class UpdateService : IUpdateClient
{
    internal const string Repository = "ShepherdGal/Hush";
    // GitHub does not accept .exe uploads, so releases carry zips. Updates read the portable zip, which holds Hush-Portable.exe.
    internal const string AssetName = "Hush-Portable.zip";
    internal const string PackagedExecutable = "Hush-Portable.exe";
    internal const string StagedHashName = "Hush.exe.sha256";
    private const long MaxBytes = 268435456;
    internal static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version is { } version ? new(version.Major, version.Minor, version.Build) : new(0, 7, 0);
    /// <summary>Short user-facing form: 0.7 rather than 0.7.0.</summary>
    internal static string Display(Version version) => version.Build > 0 ? version.ToString(3) : version.ToString(2);
    internal static string DisplayVersion => Display(CurrentVersion);
    private readonly HttpClient client;
    internal UpdateService(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Hush/" + CurrentVersion);
    }
    // Updates come from the app's public release page, read anonymously: no account, sign-in or token of any kind.
    public async Task<AppRelease?> CheckAsync(CancellationToken cancellation)
    {
        using var response = await SendAsync(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), false, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidDataException("No update has been published yet. Try again later.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidDataException("Updates can't be checked right now. Try again in a little while.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2_000_000) throw new InvalidDataException("Release information is too large.");
        using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int count;
        while ((count = await input.ReadAsync(chunk, cancellation)) != 0)
        {
            if (buffer.Length + count > 2_000_000) throw new InvalidDataException("Release information is too large.");
            buffer.Write(chunk, 0, count);
        }
        using var data = JsonDocument.Parse(buffer.ToArray());
        if (data.RootElement.GetProperty("draft").GetBoolean() || data.RootElement.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("No stable release is available.");
        return ParseRelease(data.RootElement, CurrentVersion);
    }
    internal static AppRelease? ParseRelease(JsonElement data, Version current)
    {
        if (data.GetProperty("draft").GetBoolean() || data.GetProperty("prerelease").GetBoolean()) return null;
        string tag = data.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) throw new InvalidDataException("Release version is invalid.");
        if (version <= current) return null;
        var asset = data.GetProperty("assets").EnumerateArray().SingleOrDefault(item => item.GetProperty("name").GetString() == AssetName);
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("This release has no Windows portable package.");
        long size = asset.GetProperty("size").GetInt64();
        string digest = asset.GetProperty("digest").GetString() ?? "";
        if (size is <= 0 or > MaxBytes || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71
            || !digest[7..].All(Uri.IsHexDigit)) throw new InvalidDataException("Release integrity information is missing or invalid.");
        var uri = new Uri(asset.GetProperty("url").GetString() ?? "");
        ValidateSource(uri);
        string notes = data.TryGetProperty("body", out var body) ? body.GetString() ?? "No patch notes supplied." : "No patch notes supplied.";
        return new(new(version.Major, version.Minor, Math.Max(0, version.Build)), notes.Length > 20000 ? notes[..20000] : notes, uri, size, digest[7..]);
    }
    public async Task<string> DownloadAsync(AppRelease release, IProgress<double>? progress, CancellationToken cancellation, string? stagingRoot = null)
    {
        ValidateSource(release.AssetUri);
        if (release.Bytes is <= 0 or > MaxBytes || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Release integrity information is missing or invalid.");
        // Elevated: the administrator-only staging folder (created and checked here); otherwise the person's own data folder.
        string directory = Path.Combine(stagingRoot ?? UpdateInstaller.PrepareStagingRoot(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!CleanupEngine.SafePath(directory, directory)) throw new IOException("Update staging location is unsafe.");
        string staged = Path.Combine(directory, "Hush.exe");
        string package = Path.Combine(directory, AssetName);
        try
        {
            using var response = await SendAsync(release.AssetUri, true, cancellation);
            response.EnsureSuccessStatusCode();
            using (var output = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var input = await response.Content.ReadAsStreamAsync(cancellation))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[65536]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    total += count;
                    if (total > release.Bytes) throw new InvalidDataException("Download exceeds its declared size.");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    progress?.Report((double)total / release.Bytes);
                }
                if (total != release.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Download verification failed; the current app was left unchanged.");
                output.Flush(true);
            }
            // The package hash is verified; now take out exactly one file, by its exact name, with a size cap, never extracting paths.
            using (var archive = ZipFile.OpenRead(package))
            {
                var entry = archive.Entries.SingleOrDefault(item => item.FullName == PackagedExecutable)
                    ?? throw new InvalidDataException("The update package does not contain Hush.");
                if (entry.Length is <= 0 or > MaxBytes) throw new InvalidDataException("The update package has an unexpected size.");
                using var from = entry.Open();
                using var to = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[65536]; long written = 0; int count;
                while ((count = await from.ReadAsync(buffer, cancellation)) > 0)
                {
                    written += count;
                    if (written > entry.Length) throw new InvalidDataException("The update package is inconsistent.");
                    await to.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
                if (written != entry.Length) throw new InvalidDataException("The update package is inconsistent.");
                to.Flush(true);
            }
            VerifyExecutable(staged, release.Version);
            // Remember the verified hash of the extracted app so the installer can confirm nothing changed it before the swap.
            File.WriteAllText(Path.Combine(directory, StagedHashName), UpdateInstaller.Hash(staged));
            File.Delete(package);
            return staged;
        }
        catch
        {
            // Only this request's freshly created staging files can be removed.
            foreach (string leftover in new[] { staged, package })
            {
                try { if (CleanupEngine.SafePath(leftover, directory)) File.Delete(leftover); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }
    private static void ValidateSource(Uri uri)
    {
        if (uri.Scheme != "https" || uri.Host != "api.github.com" || !uri.AbsolutePath.StartsWith($"/repos/{Repository}/releases/assets/", StringComparison.Ordinal)
            || !long.TryParse(uri.Segments[^1], out _) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("The update is not from the configured GitHub release channel.");
    }
    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool binary, CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            bool api = uri.Host == "api.github.com";
            if (uri.Scheme != "https" || (!api && uri.Host != "github.com" && uri.Host != "release-assets.githubusercontent.com" && uri.Host != "objects.githubusercontent.com"))
                throw new InvalidDataException("Unexpected update download host.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            { uri = next.IsAbsoluteUri ? next : new Uri(uri, next); response.Dispose(); continue; }
            return response;
        }
        throw new IOException("Too many update redirects.");
    }
    internal static void VerifyExecutable(string path, Version expected)
    {
        using var file = File.OpenRead(path); using var reader = new BinaryReader(file);
        if (file.Length < 256 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Update is not a Windows application.");
        file.Position = 0x3C; int offset = reader.ReadInt32();
        if (offset < 0 || offset > file.Length - 6) throw new InvalidDataException("Invalid update header.");
        file.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664) throw new InvalidDataException("Update is not a Windows x64 application.");
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName != "Hush" || !Version.TryParse(info.FileVersion, out var version)
            || new Version(version.Major, version.Minor, version.Build) != expected) throw new InvalidDataException("Update application identity or version does not match.");
    }
    public void Dispose() => client.Dispose();
}
