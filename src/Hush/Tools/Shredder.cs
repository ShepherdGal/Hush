using System.IO;
using System.Security.Cryptography;

namespace Hush;

// Tools > Shredder (owner, November 19, 2026; Pro only). Shred: every byte of each chosen file is overwritten with random data, the file is renamed to a random
// name, shortened to nothing and deleted, so ordinary recovery tools find nothing. Wipe free space: a drive's free space is filled with zeros in a temporary
// file that deletes itself, so the remains of files deleted earlier are overwritten. Honest limit, said in the card: on SSDs and flash drives the drive itself
// moves data around (wear levelling, TRIM), so no program can promise every old copy is overwritten; on a hard disk one pass is enough.
internal sealed record ShredReport(int Files, int Failed, long Bytes, IReadOnlyList<string> Problems);

internal static class Shredder
{
    private const int Chunk = 1 << 20;

    // Folders and roots that are never shredded, whatever was picked: drive roots, Windows, Program Files and the profile's own top folders.
    internal static string? Refusal(string path, IReadOnlyList<string> never)
    {
        string full = LeftoverPlaces.Normalize(path);
        if (Path.GetPathRoot(full) is { } root && LeftoverPlaces.Normalize(root).Equals(full, StringComparison.OrdinalIgnoreCase)) return "a whole drive";
        foreach (string folder in never)
        {
            string normal = LeftoverPlaces.Normalize(folder);
            if (normal.Equals(full, StringComparison.OrdinalIgnoreCase) || LeftoverPlaces.IsInside(normal, full)) return "a folder Windows or your profile needs";
        }
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && (LeftoverPlaces.IsInside(full, windows) || LeftoverPlaces.Normalize(windows).Equals(full, StringComparison.OrdinalIgnoreCase))) return "part of Windows";
        return null;
    }

    internal static IReadOnlyList<string> DefaultNever()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), profile, Path.GetDirectoryName(profile) ?? profile, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(profile, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) }
            .Where(p => p.Length > 0).ToList();
    }

    internal static ShredReport Shred(IReadOnlyList<string> paths, IReadOnlyList<string> never, IProgress<double>? progress, CancellationToken token)
    {
        var problems = new List<string>();
        var files = new List<string>();
        var folders = new List<string>();
        foreach (string path in paths)
        {
            if (Refusal(path, never) is { } why) { problems.Add($"{path}: left alone ({why})"); continue; }
            if (File.Exists(path)) files.Add(path);
            else if (Directory.Exists(path))
            {
                folders.Add(path);
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                files.AddRange(Directory.EnumerateFiles(path, "*", options));
            }
        }
        long total = Math.Max(1, files.Sum(file => { try { return new FileInfo(file).Length; } catch (IOException) { return 0L; } })), done = 0;
        int shredded = 0, failed = 0;
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            try { done += ShredFile(file, written => progress?.Report((done + written) / (double)total), token); shredded++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; problems.Add($"{file}: {ex.Message}"); }
        }
        // Folders: their now-empty sub-folders, deepest first; a link inside is removed as a link, never followed.
        foreach (string folder in folders)
        {
            try { RemoveEmpty(new DirectoryInfo(folder)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{folder}: {ex.Message}"); }
        }
        progress?.Report(1);
        return new(shredded, failed, done, problems);
    }

    private static void RemoveEmpty(DirectoryInfo directory)
    {
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) { if (child is DirectoryInfo link) link.Delete(false); else child.Delete(); }
            else if (child is DirectoryInfo sub) RemoveEmpty(sub);
        }
        directory.Delete(false);
    }

    private static long ShredFile(string file, Action<long> written, CancellationToken token)
    {
        var info = new FileInfo(file);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { info.Delete(); return 0; } // a link: remove the link, never what it points at
        info.Attributes = FileAttributes.Normal;
        long length = info.Length;
        var buffer = new byte[(int)Math.Min(Chunk, Math.Max(1, length))];
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            for (long position = 0; position < length; position += buffer.Length)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buffer.Length, length - position);
                RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                stream.Write(buffer, 0, count);
                written(position + count);
            }
            stream.Flush(true);
            stream.SetLength(0);
        }
        string renamed = Path.Combine(info.DirectoryName!, Path.GetRandomFileName());
        File.Move(file, renamed);
        File.Delete(renamed);
        return length;
    }

    // Fills the free space of the drive that holds `folder` with zeros, then lets the file delete itself. limit: tests only (stop after this many bytes).
    internal static long WipeFreeSpace(string folder, IProgress<double>? progress, CancellationToken token, long? limit = null)
    {
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "Hush wipe " + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
        long target = Math.Min(limit ?? long.MaxValue, drive.AvailableFreeSpace), written = 0;
        var zeros = new byte[Chunk * 4];
        try
        {
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.DeleteOnClose);
            while (written < target)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(zeros.Length, target - written);
                try { stream.Write(zeros, 0, count); }
                catch (IOException) { break; } // the drive is full: that is the goal
                written += count;
                progress?.Report(written / (double)Math.Max(1, target));
            }
            try { stream.Flush(true); } catch (IOException) { }
        }
        finally { try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } }
        progress?.Report(1);
        return written;
    }

    // Where the wipe file goes: the drive's own root folder when Hush can write there, otherwise the person's temp folder on that drive.
    internal static string WipeFolder(string driveRoot)
    {
        string temp = Path.GetTempPath();
        if (string.Equals(Path.GetPathRoot(temp), driveRoot, StringComparison.OrdinalIgnoreCase)) return temp;
        return Path.Combine(driveRoot, "Hush wipe");
    }
}
