using System.IO;
using System.Security.Cryptography;

// The lock on docs/locked/* (owner instruction, November 9, 2026): parked work (today the Mica look) must not be touched by accident. Every file in a locked folder
// is compared with CHECKSUMS.sha256; a changed, added or missing file fails with a message that says what to do. Only the owner unlocks a folder; after that
// `pwsh scripts/Lock-Archive.ps1 <folder>` writes the new checksums.
internal static class LockedArchiveChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string testsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string locked = Path.GetFullPath(Path.Combine(testsDirectory, "..", "..", "docs", "locked"));
        var folders = Directory.Exists(locked) ? Directory.GetDirectories(locked) : [];
        check(folders.Length >= 1, "The locked archive exists (docs/locked/mica-look): parked work is kept, not deleted");
        foreach (string folder in folders)
        {
            string name = Path.GetFileName(folder), sums = Path.Combine(folder, "CHECKSUMS.sha256");
            if (!File.Exists(sums)) { check(false, $"LOCKED ARCHIVE {name}: CHECKSUMS.sha256 is missing. Only the owner may unlock it; then run scripts/Lock-Archive.ps1"); continue; }
            var expected = File.ReadAllLines(sums).Where(line => line.Length > 66).ToDictionary(line => line[66..], line => line[..64]);
            var actual = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != "CHECKSUMS.sha256")
                .ToDictionary(path => Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/'), path => { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); });
            var problems = new List<string>();
            problems.AddRange(expected.Keys.Except(actual.Keys).Select(file => "missing: " + file));
            problems.AddRange(actual.Keys.Except(expected.Keys).Select(file => "added: " + file));
            problems.AddRange(expected.Keys.Intersect(actual.Keys).Where(file => expected[file] != actual[file]).Select(file => "changed: " + file));
            check(problems.Count == 0 && expected.Count > 0,
                $"LOCKED ARCHIVE {name} was touched ({string.Join(", ", problems)}). It is locked by the owner: do not change it unless the owner says so in plain words; undo the change.");
        }
    }
}
