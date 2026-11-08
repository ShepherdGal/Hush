using System.IO;
using Hush;

// Terms of use, downloads and leftovers: the wording is kept in step across the app and the docs, the README leads with easy downloads,
// the build writes one clear set of files, and the updater leaves only one rollback copy and cleans its staging. Fixtures only.
internal static class DistributionChecks
{
    internal static void Run(string fixture, Action<bool, string> check)
    {
        string testsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string repository = Path.GetFullPath(Path.Combine(testsDirectory, "..", ".."));
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([repository, .. parts])).Replace("\r\n", "\n");

        // Terms of use.
        string terms = Read("docs", "TERMS.md");
        check(Terms.Body.Split("\n\n").All(paragraph => terms.Contains(paragraph)), "docs/TERMS.md carries exactly the text the app shows");
        check(Terms.Body.Contains("own risk") && Terms.Body.Contains("without warranty") && Terms.Body.Contains("not liable") && Terms.Body.Contains("cannot be undone")
            && Terms.Notice.Contains("own risk") && Terms.Notice.Contains("isn't responsible") && Terms.Version.Length > 0, "The terms say use at your own risk, no warranty and no liability");
        string readme = Read("README.md");
        check(readme.Contains("## Use at your own risk") && readme.Contains("docs/TERMS.md") && readme.Contains("Terms of use"), "The README states the terms and links to them");

        // MIT license.
        string license = Read("LICENSE");
        check(license.StartsWith("MIT License\n\nCopyright (c) 2026 ShepherdGal") && license.Contains("Permission is hereby granted, free of charge") && license.Contains("THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND")
            && license.Contains("IN NO EVENT SHALL THE") && license.Contains("The above copyright notice and this permission notice shall be included"), "LICENSE is the standard MIT License text with the copyright line");
        check(readme.Contains("## License") && readme.Contains("[MIT License](LICENSE)") && terms.Contains("MIT License") && Read("scripts", "Build.ps1").Contains("'LICENSE'")
            && Read("src", "Hush", "Hush.csproj").Contains("<Copyright>Copyright (c) 2026 ShepherdGal. MIT License.</Copyright>"), "The README, terms, zip and file properties all point to the MIT license");

        // Popup buttons: the action first (left, and first in tab order), the exit second (right).
        string mainXaml = Read("src", "Hush", "MainWindow.xaml"), setupXaml = Read("src", "Hush", "Setup", "SetupWindow.xaml");
        foreach (var (action, exit) in new[] { ("ConfirmDeleteButton", "CancelDeleteButton"), ("ConfirmWindowsSettingsButton", "CancelWindowsSettingsButton"), ("ConsentConfirmButton", "ConsentCancelButton"), ("CheckUpdateButton", "CloseAboutButton") })
        {
            string[] lines = mainXaml.Split('\n');
            int a = Array.FindIndex(lines, l => l.Contains($"x:Name=\"{action}\"")), e = Array.FindIndex(lines, l => l.Contains($"x:Name=\"{exit}\""));
            check(a >= 0 && e > a && (lines[e].Contains("Grid.Column=\"2\"") || lines[e].Contains("Grid.Column=\"3\"")), $"In the popup, {action} comes before {exit} in the markup and {exit} sits in the right-hand column");
        }
        check(!setupXaml.Contains("x:Name=\"PrimaryButton\" Grid.Column") && setupXaml.Contains("x:Name=\"SecondaryButton\" Grid.Column=\"2\""), "In Setup the action (Install / Open Hush) is on the left and Run without installing / Close on the right");

        // Easy downloads.
        check(readme.Contains("releases/latest/download/Hush.exe") && readme.Contains("releases/latest/download/Hush-Setup.exe") && readme.Contains("releases/latest/download/Hush-windows-x64.zip")
            && readme.IndexOf("## Download", StringComparison.Ordinal) < readme.IndexOf("## Project layout", StringComparison.Ordinal) && readme.Contains("Portable") || readme.Contains("portable"),
            "The README leads with a Download table: portable, installer and zip, each a direct latest-release link");
        string releasing = Read("docs", "RELEASING.md");
        check(releasing.Contains("**Which file?**") && releasing.Contains("Hush.exe#") && releasing.Contains("scripts/Tidy.ps1"), "The release process has a Which file? template, asset labels and the tidy step");
        string build = Read("scripts", "Build.ps1");
        check(build.Contains("WHICH FILE.txt") && build.Contains("Join-Path $artifacts 'Hush.exe'") && build.Contains("Hush-Setup.exe") && !build.Contains(@"artifacts\Hush\Hush.exe"),
            "The build writes one flat set of files (portable, installer, zip, WHICH FILE.txt) and no nested copy");
        string tidy = Read("scripts", "Tidy.ps1");
        check(tidy.Contains("-Apply") && tidy.Contains("a program is running from here") && !tidy.Contains("Remove-Item -LiteralPath (Join-Path $root 'artifacts')") && !tidy.Contains("Backups' -Recurse"),
            "Tidy never touches artifacts or Backups and skips folders with a running program");

        // Updater leftovers.
        check(UpdateInstaller.RollbackName == "Hush.previous.exe", "The rollback copy has one fixed name");
        string app = Path.Combine(fixture, "rollback-app"); Directory.CreateDirectory(app);
        string current = Path.Combine(app, "Hush.exe"), backup = Path.Combine(app, UpdateInstaller.RollbackName);
        foreach (string version in new[] { "one", "two", "three" })
        {
            string next = Path.Combine(app, ".next-" + version + ".exe"); File.WriteAllText(next, "synthetic " + version);
            if (!File.Exists(current)) File.WriteAllText(current, "synthetic zero");
            UpdateInstaller.AtomicReplace(next, current, backup);
        }
        check(Directory.GetFiles(app).Length == 2 && File.ReadAllText(current) == "synthetic three" && File.ReadAllText(backup) == "synthetic two",
            "Three updates in a row leave the app and exactly one rollback copy (the previous version), not one copy per update");
        File.WriteAllText(Path.Combine(app, "Hush.previous-aaaa.exe"), "old"); File.WriteAllText(Path.Combine(app, "Hush.previous-bbbb.exe"), "old"); File.WriteAllText(Path.Combine(app, "notes.txt"), "mine");
        check(UpdateInstaller.RemoveLegacyRollbackCopies(app) == 2 && File.Exists(backup) && File.Exists(current) && File.Exists(Path.Combine(app, "notes.txt")) && Directory.GetFiles(app, "Hush.previous-*").Length == 0,
            "Legacy per-update rollback files are removed and nothing else is");

        string staging = Path.Combine(fixture, "update-staging"); Directory.CreateDirectory(staging);
        string finished = Path.Combine(staging, "finished"), running = Path.Combine(staging, "downloading"), stale = Path.Combine(staging, "stale"), locked = Path.Combine(staging, "locked");
        foreach (string directory in new[] { finished, running, stale, locked }) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "Hush.exe"), "synthetic staged copy"); }
        File.WriteAllText(Path.Combine(finished, "result.txt"), "Installed"); File.WriteAllText(Path.Combine(locked, "started.ok"), "1");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        int removed;
        using (new FileStream(Path.Combine(locked, "Hush.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            removed = UpdateInstaller.PruneStaging(staging);
        check(removed == 2 && !Directory.Exists(finished) && !Directory.Exists(stale) && Directory.Exists(running) && Directory.Exists(locked),
            "Finished and stale update staging is removed; a download in progress and a folder whose helper is still running are kept");
        check(UpdateInstaller.PruneStaging(staging) == 1 && !Directory.Exists(locked) && Directory.Exists(running), "A folder that was in use is removed on the next pass once it is free");
        check(UpdateInstaller.PruneStaging(Path.Combine(fixture, "no-such-staging")) == 0, "Pruning a missing staging folder does nothing");
    }
}
