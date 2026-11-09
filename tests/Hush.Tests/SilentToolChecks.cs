using System.IO;
using Hush;

// 0.9 (user direction): Hush uses NO Windows cleanup tool (no Disk Cleanup, no DISM): Windows' tools often removed nothing. The previous Windows installation is
// removed by Hush itself. Only fixture folders are ever removed here; the real Windows.old is never touched by a test.
internal static class SilentToolChecks
{
    internal static void Run(string fixture, Action<bool, string> check)
    {
        string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Hush"));
        var code = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories).Where(path => !path.Contains(@"\obj\") && !path.Contains(@"\bin\")).Select(File.ReadAllText).ToList();
        check(!code.Any(text => text.Contains("cleanmgr") || text.Contains("dism.exe") || text.Contains("/sagerun") || text.Contains("StartComponentCleanup")),
            "No Windows cleanup tool (Disk Cleanup, DISM) is used anywhere in Hush");
        check(!ReviewCategory.Definitions.Any(d => d.Id is "ComponentStore" or "ComponentReset") && !ReviewCategory.Definitions.Any(d => d.Description.Contains("Disk Cleanup") || d.Description.Contains("DISM /") || d.Description.Contains("DISM component")),
            "No Review category is run by a Windows cleanup tool, so nothing says \"By Windows\" any more");
        check(WindowsOldRemoval.ExpectedPath().EndsWith(@"\Windows.old", StringComparison.OrdinalIgnoreCase) && Path.GetPathRoot(WindowsOldRemoval.ExpectedPath()) == Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            "The one folder Hush may remove as the previous Windows installation is Windows.old at the root of the system drive");

        // ---- the deletion itself, on a fixture ----
        string root = Path.Combine(fixture, "windows-old-fixture", "Windows.old");
        string outside = Path.Combine(fixture, "windows-old-outside");
        Directory.CreateDirectory(Path.Combine(root, "Users", "Old", "Documents")); Directory.CreateDirectory(Path.Combine(root, "Windows", "System32")); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(root, "Users", "Old", "Documents", "note.txt"), "x");
        string locked = Path.Combine(root, "Windows", "System32", "readonly.dll"); File.WriteAllText(locked, "x"); File.SetAttributes(locked, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(root, "root-file.bin"), "x");
        string survivor = Path.Combine(outside, "must-survive.txt"); File.WriteAllText(survivor, "keep");
        string junction = Path.Combine(root, "Users", "Old", "link-to-outside");
        using (var command = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { Arguments = $"/c mklink /J \"{junction}\" \"{outside}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
        { command!.WaitForExit(); check(command.ExitCode == 0, "Junction fixture created"); }
        var owned = new List<string>();
        var result = WindowsOldRemoval.RemoveTree(root, CancellationToken.None, (path, directory) => owned.Add(path));
        check(!Directory.Exists(root) && result.Failed == 0 && result.Removed >= 8, $"The whole tree is removed, read-only files included ({result.Removed} items)");
        check(File.Exists(survivor) && Directory.Exists(outside), "A junction inside is removed as a link: its target and everything outside the folder survive");
        check(owned.Count == 0, "Ownership is taken only for an entry Windows refused to delete (none here)");

        // ---- unsafe roots and cancellation ----
        bool rejected = false;
        // Never aimed at a real drive: a fixture folder that is not named Windows.old (and so, by the same rule, a drive root) is refused before anything is touched.
        string notOld = Path.Combine(fixture, "windows-old-wrongname", "Other"); Directory.CreateDirectory(notOld); File.WriteAllText(Path.Combine(notOld, "keep.txt"), "x");
        try { WindowsOldRemoval.RemoveTree(notOld, CancellationToken.None, (_, _) => { }); } catch (IOException) { rejected = true; }
        check(rejected && File.Exists(Path.Combine(notOld, "keep.txt")) && Directory.Exists(outside), "Only a folder named exactly Windows.old can be removed: any other folder (a drive root included) is refused and untouched");
        string linkRoot = Path.Combine(fixture, "windows-old-link");
        using (var command = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { Arguments = $"/c mklink /J \"{linkRoot}\" \"{outside}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
            command!.WaitForExit();
        bool linkRejected = false;
        try { WindowsOldRemoval.RemoveTree(linkRoot, CancellationToken.None, (_, _) => { }); } catch (IOException) { linkRejected = true; }
        check(linkRejected && File.Exists(survivor), "A root that is a link is refused and its target is untouched");
        Directory.Delete(linkRoot);
        Directory.CreateDirectory(Path.Combine(root, "a")); File.WriteAllText(Path.Combine(root, "a", "f.txt"), "x");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool canceled = false;
        try { WindowsOldRemoval.RemoveTree(root, cancel.Token, (_, _) => { }); } catch (OperationCanceledException) { canceled = true; }
        check(canceled && File.Exists(Path.Combine(root, "a", "f.txt")), "A canceled removal stops before deleting");
        // Without administrator rights the real removal refuses before touching anything.
        if (!WindowsSystemTools.IsElevated())
        {
            bool needsAdmin = false;
            try { WindowsOldRemoval.RemoveWindowsOld(CancellationToken.None); } catch (IOException ex) { needsAdmin = ex.Message.Contains("administrator"); }
            check(needsAdmin, "Without administrator mode the removal refuses and changes nothing");
        }
    }
}
