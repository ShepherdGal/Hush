using System.IO;
using Microsoft.Win32;
using Hush;

// Pro registry traces and issues (owner, November 14, 2026): every rule runs against a fixture key (HKCU\Software\Hush.TestFixtures\<GUID>\...), never the
// real registry. Checks what is found and what is not, that a clean removes exactly that (and keeps the neighbours), writes a .reg backup first, and that a
// changed or forged item is left alone.
internal static class RegistryTraceChecks
{
    internal static void Run(string fixture, Action<bool, string> check)
    {
        string prefix = @"Software\Hush.TestFixtures\" + Guid.NewGuid().ToString("N") + @"\";
        void Set(string path, string name, object value, RegistryValueKind kind = RegistryValueKind.Unknown)
        {
            using var key = Registry.CurrentUser.CreateSubKey(prefix + path);
            if (kind == RegistryValueKind.Unknown) key.SetValue(name, value); else key.SetValue(name, value, kind);
        }
        object? Get(string path, string name) { using var key = Registry.CurrentUser.OpenSubKey(prefix + path); return key?.GetValue(name); }
        bool KeyExists(string path) { using var key = Registry.CurrentUser.OpenSubKey(prefix + path); return key != null; }
        const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";
        string gone = @"C:\Program Files\Hush Test Gone " + Guid.NewGuid().ToString("N")[..8];
        string existing = Environment.ProcessPath!;
        try
        {
            // Privacy lists.
            Set(Explorer + "RunMRU", "a", "cmd\\1"); Set(Explorer + "RunMRU", "MRUList", "a");
            Set(Explorer + @"ComDlg32\OpenSavePidlMRU\txt", "0", new byte[] { 1, 2, 3 }); Set(Explorer + @"ComDlg32\OpenSavePidlMRU", "MRUListEx", new byte[] { 0, 0, 0, 0 });
            Set(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "LastKey", @"Computer\HKEY_CURRENT_USER\Software");
            Set(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "View", new byte[] { 9 });        // a setting, not history: stays
            Set(@"Software\7-Zip\Compression", "ArcHistory", new byte[] { 1, 0 }); Set(@"Software\7-Zip\Compression", "Level", 5); // only the history goes
            // Issues: certainly gone vs still there.
            const string Mui = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
            Set(Mui, gone + @"\gone.exe.FriendlyAppName", "Gone App");
            Set(Mui, existing + ".FriendlyAppName", "This test");
            Set(@"AppEvents\Schemes\Apps\.Default\Gone\.Current", "", gone + @"\ding.wav");
            Set(@"AppEvents\Schemes\Apps\.Default\Windows\.Current", "", @"C:\Windows\Media\Windows Ding.wav"); // under Windows: never
            Set(@"AppEvents\Schemes\Apps\.Default\Relative\.Current", "", "ding.wav");                          // not a full path: cannot tell
            string goneClass = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}", liveClass = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            Set(@"Software\Classes\CLSID\" + goneClass + @"\InprocServer32", "", gone + @"\gone.dll");
            Set(@"Software\Classes\CLSID\" + goneClass, "", "Gone class");
            Set(@"Software\Classes\CLSID\" + liveClass + @"\InprocServer32", "", existing);
            Set(@"Software\Classes\.hushgone", "", "HushTest.Gone.Type");
            Set(@"Software\Classes\.hushlive", "", "HushTest.Live.Type"); Set(@"Software\Classes\HushTest.Live.Type", "", "Live");
            Set(@"Software\Classes\.hushmore", "", "HushTest.Gone.Type"); Set(@"Software\Classes\.hushmore", "Content Type", "text/x"); // holds more: left alone

            var traces = new RegistryTraces(Registry.CurrentUser, prefix);
            var found = new List<CleanupItem>();
            traces.Scan(found, CancellationToken.None);
            CleanupItem? By(string category, string contains) => found.FirstOrDefault(item => item.CategoryId == category && item.Path.Contains(contains, StringComparison.OrdinalIgnoreCase));
            check(By("ExplorerHistory", "RunMRU") is { ItemCount: 2 } && By("ExplorerHistory", "OpenSavePidlMRU") is { ItemCount: 2 } && By("ExplorerHistory", "Regedit") is { ItemCount: 1 }
                && By("AppRecentLists", "7-Zip") is { ItemCount: 1 } && found.All(item => item.Kind == CleanupKind.Registry && !item.Selected && item.Safety == SafetyLevel.Advanced),
                "History lists are found (only their history values: Regedit's settings and 7-Zip's compression level are not counted), Advanced and opt-in");
            check(By("RegMuiCache", "gone.exe") != null && !found.Any(item => item.CategoryId == "RegMuiCache" && item.Path.Contains(existing, StringComparison.OrdinalIgnoreCase)),
                "The program-name cache lists a removed program, not one that exists");
            check(By("RegSounds", @"\Gone\") != null && By("RegSounds", @"\Windows\") == null && By("RegSounds", @"\Relative\") == null,
                "A sound whose file is gone is listed; Windows' own sounds and paths Hush cannot be sure about are not");
            check(By("RegComClasses", goneClass) != null && By("RegComClasses", liveClass) == null, "A COM class whose file and folder are gone is listed; one whose file exists is not");
            check(By("RegFileTypes", ".hushgone") != null && By("RegFileTypes", ".hushlive") == null && By("RegFileTypes", ".hushmore") == null,
                "A file type whose program type exists nowhere is listed; one that is still registered, or holds more than that, is not");

            // Cleaning through the engine: exactly these go, a .reg backup is written first for each, the neighbours stay.
            string data = Path.Combine(fixture, "registry-traces");
            var engine = new CleanupEngine([], includeRegistry: true, dataDirectory: data) { Traces = traces };
            var chosen = found.ToList();
            var forged = new CleanupItem { Path = @"HKCU\Software\Elsewhere", Root = RegistryTraces.RootPrefix + "run-mru", Group = "x", Kind = CleanupKind.Registry, CategoryId = "ExplorerHistory",
                RegistrySnapshot = "x", RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System" };
            var unknownRule = new CleanupItem { Path = @"HKCU\x", Root = RegistryTraces.RootPrefix + "no-such-rule", Group = "x", Kind = CleanupKind.Registry, RegistrySnapshot = "x", RegistryKeyPath = "x" };
            // One item changes after the scan: it must be left alone.
            Set(@"Software\WinRAR\ArcHistory", "0", @"C:\a.rar");
            var winrar = new List<CleanupItem>(); new RegistryTraces(Registry.CurrentUser, prefix).Scan(winrar, CancellationToken.None);
            var changed = winrar.Single(item => item.Path.Contains("WinRAR"));
            Set(@"Software\WinRAR\ArcHistory", "1", @"C:\b.rar");
            chosen.AddRange([forged, unknownRule, changed]);
            var result = engine.Clean(chosen, null, CancellationToken.None);
            check(result.Deleted == found.Count && result.Skipped == 3 && forged.Outcome.StartsWith("Skipped") && unknownRule.Outcome.StartsWith("Skipped") && changed.Outcome.StartsWith("Skipped")
                && Get(@"Software\WinRAR\ArcHistory", "1") != null,
                $"Every finding is removed; a forged place, an unknown rule and a list that changed since the scan are left alone ({result.Deleted} deleted, {result.Skipped} skipped)");
            check(Get(Explorer + "RunMRU", "a") == null && KeyExists(Explorer + "RunMRU") && !KeyExists(Explorer + @"ComDlg32\OpenSavePidlMRU\txt")
                && Get(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "LastKey") == null && Get(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "View") != null
                && Get(@"Software\7-Zip\Compression", "ArcHistory") == null && Get(@"Software\7-Zip\Compression", "Level") != null,
                "Lists are emptied (the keys stay), and only the history values go: settings next to them stay");
            check(Get(Mui, gone + @"\gone.exe.FriendlyAppName") == null && Get(Mui, existing + ".FriendlyAppName") != null
                && Get(@"AppEvents\Schemes\Apps\.Default\Gone\.Current", "") == null && Get(@"AppEvents\Schemes\Apps\.Default\Windows\.Current", "") != null
                && !KeyExists(@"Software\Classes\CLSID\" + goneClass) && KeyExists(@"Software\Classes\CLSID\" + liveClass)
                && !KeyExists(@"Software\Classes\.hushgone") && KeyExists(@"Software\Classes\.hushlive") && KeyExists(@"Software\Classes\.hushmore"),
                "Issues: exactly the findings go; everything that still works stays");
            var backups = Directory.GetFiles(Path.Combine(data, "Registry backups"), "*.reg");
            check(backups.Length == found.Count && backups.All(file => File.ReadAllText(file, System.Text.Encoding.Unicode).StartsWith("Windows Registry Editor Version 5.00"))
                && backups.Any(file => File.ReadAllText(file, System.Text.Encoding.Unicode).Contains(goneClass) && File.ReadAllText(file, System.Text.Encoding.Unicode).Contains("InprocServer32")),
                "A .reg backup is written for every removal (a whole COM class with its sub-keys), so a double-click restores it");
            // The rules themselves: only the current user's registry, never a policy or system place.
            check(RegistryTraces.All.All(rule => !rule.KeyPath.Contains("Policies", StringComparison.OrdinalIgnoreCase) && !rule.KeyPath.StartsWith("SYSTEM", StringComparison.OrdinalIgnoreCase))
                && RegistryTraces.All.Select(rule => rule.Id).Distinct().Count() == RegistryTraces.All.Count()
                && RegistryTraces.All.All(rule => ReviewCategory.SafetyOf(rule.Category) == SafetyLevel.Advanced),
                "Every rule is in the current user's registry, never a policy or system key, has a unique id and is Advanced");
        }
        finally { try { Registry.CurrentUser.DeleteSubKeyTree(prefix.TrimEnd('\\'), false); } catch { } }
    }
}
