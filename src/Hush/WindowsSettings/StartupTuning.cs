using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Hush;

// Settings > Startup (owner, November 14, 2026: "hidden files or apps that open together when Windows starts ... find them and stop them"). One card for
// everything that starts with Windows, read only when the card is opened, in five sources:
//   - Run keys and Startup folders (this account and all users): Task Manager's own StartupApproved flag (StartupAppsService), never the entry itself;
//   - Store (MSIX) apps' own startup tasks (Windows Terminal, Phone Link...): the same per-user State value Task Manager switches;
//   - scheduled tasks that run at sign-in or at start-up, outside \Microsoft\ (the Scheduled tasks card's own catalog stays there): disabled, never deleted;
//   - services of other programs that start with Windows (Automatic, program file outside the Windows folder): set to Manual (owner's choice), so the
//     program can still start them when it needs them;
//   - shown but locked (a wrong change there can stop Windows from starting or is owned by a policy): Explorer Run policies, RunOnce, a changed
//     Winlogon Shell/Userinit, AppInit_DLLs.
// Like every system change in Hush: only what Scan offers right now can change, Windows' own and security software never, every original is saved to
// startup-changed.json BEFORE the first change, each result is read back and reported exactly, Undo restores the originals, each change is logged.
internal enum StartupWhere { Registry, Folder, StoreApp, Task, Service, Locked }

internal sealed record StartupCandidate(string Id, string Title, string Command, StartupWhere Where, bool AllUsers, bool StartsNow, bool FileMissing,
    string LockedNote = "")
{
    internal bool Locked => Where == StartupWhere.Locked;
    // Run/folder entries of this account and Store apps work without administrator rights; everything else needs them.
    internal bool NeedsAdministrator => Where is StartupWhere.Task or StartupWhere.Service || AllUsers && Where != StartupWhere.StoreApp;
    internal bool ChangedByHush { get; init; }
    internal string Topic => Where switch
    {
        StartupWhere.Registry => "Startup apps in the registry",
        StartupWhere.Folder => "Startup folder shortcuts",
        StartupWhere.StoreApp => "Store apps that start at sign-in",
        StartupWhere.Task => "Tasks that run at sign-in",
        StartupWhere.Service => "Services of other programs",
        _ => "Set by Windows or a policy"
    };
    internal string Place => Where switch
    {
        StartupWhere.Registry => AllUsers ? "Registry · all users" : "Registry · this account",
        StartupWhere.Folder => AllUsers ? "Startup folder · all users" : "Startup folder · this account",
        StartupWhere.StoreApp => "Store app",
        StartupWhere.Task => "Scheduled task",
        StartupWhere.Service => "Service",
        _ => LockedNote
    };
    internal string Status => Locked ? "Shown only · " + LockedNote
        : (FileMissing ? "Its program file is missing · " : "")
          + (ChangedByHush ? "Turned off by Hush" : StartsNow ? "Starts with Windows" : Where == StartupWhere.Service ? "Starts only when needed" : "Turned off")
          + " · " + Place;
}

internal sealed record StartupReport(IReadOnlyList<string> Changed, IReadOnlyList<string> Failed)
{
    internal string Text(bool on)
    {
        string verb = on ? "start with Windows again" : "no longer start with Windows";
        string text = Changed.Count == 0 ? "Nothing was changed." : $"{Changed.Count} item{(Changed.Count == 1 ? "" : "s")} {verb}, checked: {string.Join(", ", Changed)}.";
        if (Failed.Count > 0) text += $"\n\nCould not be changed: {string.Join("; ", Failed)}.";
        return text;
    }
}

// Where the locked sources are read from (tests point these at a fixture key).
// PackagedApps: where Windows keeps each Store (MSIX) app's startup-task switch for this account (checked on the owner's PC, November 14, 2026).
internal sealed record StartupLockedPlaces(RegistryKey UserRoot, RegistryKey MachineRoot, string PoliciesRun, string RunOnce, string Winlogon, string Windows, string PackagedApps)
{
    internal static StartupLockedPlaces Default => new(Registry.CurrentUser, Registry.LocalMachine,
        @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData");
}

internal sealed class StartupTuning(StartupAppsService apps, ITaskSystem tasks, IServiceSystem services, string directory, StartupLockedPlaces? lockedPlaces = null)
{
    private const int Automatic = 2, Manual = 3;
    private readonly StartupLockedPlaces locked = lockedPlaces ?? StartupLockedPlaces.Default;
    private readonly string folder = Path.GetFullPath(directory);
    private string HistoryPath => Path.Combine(folder, "startup-changed.json");
    private static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
    // Never listed and never changed, whoever made it: Windows Security's own tray, Hush's own Start-with-Windows entry, anything that names security software.
    private static readonly Regex SecurityName = new(@"antivirus|defender|firewall|security|malware|endpoint|SecurityHealth", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static readonly string[] TopicOrder = ["Startup apps in the registry", "Startup folder shortcuts", "Store apps that start at sign-in", "Tasks that run at sign-in", "Services of other programs", "Set by Windows or a policy"];
    // Services that look like another program's but that the program cannot do without (Office does not open while Click-to-Run is stopped).
    private static readonly HashSet<string> NeverOffer = new(StringComparer.OrdinalIgnoreCase) { "ClickToRunSvc" };
    // A Store app's startup task: 0 off (the app's default), 1 turned off by the person, 2 on; 3 and 4 are set by a policy (shown, locked).
    private const int StoreOff = 0, StoreOffByUser = 1, StoreOn = 2;

    internal bool CanChangeAll => tasks.IsAdministrator;
    internal bool HasChanges { get { try { return Load()?.Items.Count > 0; } catch { return false; } } }
    internal int ChangedCount { get { try { return Load()?.Items.Count ?? 0; } catch { return 0; } } }

    private static bool Protected(string name, string command) =>
        SecurityName.IsMatch(name) || SecurityName.IsMatch(command) || name.Equals(RunKeyStartup.ValueName, StringComparison.OrdinalIgnoreCase);

    private static bool UnderWindows(string? path) => path != null && path.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase);

    private bool Missing(string command)
    {
        string? file = ServiceTuning.ExecutablePath(command);
        return file != null && !file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(file) && !services.FileExists(file);
    }

    // Everything that starts with Windows from the five sources. Read-only; works without administrator rights (some tasks show only as administrator).
    internal IReadOnlyList<StartupCandidate> Scan()
    {
        var result = new List<StartupCandidate>();
        var history = SafeLoad();
        bool Changed(string id) => history?.Items.Any(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) == true;

        foreach (var entry in apps.List())
        {
            if (Protected(entry.Name, entry.Command)) continue;
            bool folderEntry = entry.Source is StartupSource.UserFolder or StartupSource.CommonFolder;
            string id = "app|" + entry.Id;
            string title = folderEntry ? Path.GetFileNameWithoutExtension(entry.Name) : entry.Name;
            result.Add(new(id, title, entry.Command, folderEntry ? StartupWhere.Folder : StartupWhere.Registry, StartupAppsService.IsMachine(entry.Source), entry.Enabled,
                !folderEntry && Missing(entry.Command)) { ChangedByHush = Changed(id) });
        }

        var storeTasks = StoreTasks().ToList();
        foreach (var (key, package, task, state) in storeTasks)
        {
            string id = "store|" + package + "\\" + task;
            string title = StoreTitle(package, task, storeTasks.Count(other => other.Package == package) > 1);
            if (state is 3 or 4) { result.Add(new("locked|store|" + package + "\\" + task, title, package, StartupWhere.Locked, false, state == 4, false, "Set by a policy (Store app)")); continue; }
            if (state is not (StoreOff or StoreOffByUser or StoreOn)) continue;
            result.Add(new(id, title, package, StartupWhere.StoreApp, false, state == StoreOn, false) { ChangedByHush = Changed(id) });
        }

        foreach (var task in tasks.List())
        {
            if (!(task.AtLogon || task.AtBoot) || !IsOwnTask(task.Path) || Protected(TaskName(task.Path), task.Command)) continue;
            string id = "task|" + task.Path;
            bool changed = Changed(id);
            if (!task.Enabled && !changed) continue; // disabled by someone else: not ours to offer
            result.Add(new(id, TaskName(task.Path), task.Command, StartupWhere.Task, true, task.Enabled, Missing(Environment.ExpandEnvironmentVariables(task.Command))) { ChangedByHush = changed });
        }

        foreach (var info in services.List())
        {
            if (!IsOtherProgramsService(info)) continue;
            string id = "service|" + info.Name;
            bool changed = Changed(id);
            if (info.Start != Automatic && !(changed && info.Start == Manual)) continue;
            string title = info.Display.Length == 0 || info.Display.StartsWith('@') ? info.Name : info.Display;
            result.Add(new(id, title, info.ImagePath, StartupWhere.Service, true, info.Start == Automatic, Missing(info.ImagePath)) { ChangedByHush = changed });
        }

        result.AddRange(LockedEntries());
        return result;
    }

    // A task outside Windows' own folder that the Scheduled tasks card does not already own (its catalog), never Hush's own and never a protected folder.
    private static bool IsOwnTask(string path) =>
        !path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) && !TaskTuning.IsNeverTouch(path) && TaskTuning.CatalogEntry(path) == null;

    private static string TaskName(string path) => path[(path.LastIndexOf('\\') + 1)..];

    // A service of another program: a program service (no driver), set to start by itself, its file outside the Windows folder, not in the Services card's
    // catalog (that card owns it), never one Hush must not touch and never security software.
    private static bool IsOtherProgramsService(ServiceInfo info)
    {
        string? file = ServiceTuning.ExecutablePath(info.ImagePath);
        return ServiceTuning.IsProgramService(info.Type) && file != null && !UnderWindows(file) && !ServiceTuning.NeverTouch.Contains(info.Name) && !NeverOffer.Contains(info.Name)
            && !ServiceTuning.Catalog.Any(item => item.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase)) && !Protected(info.Name + " " + info.Display, info.ImagePath);
    }

    // Every Store app startup task of this account: (key path, package family, task id, State). Read-only.
    private IEnumerable<(string Key, string Package, string Task, int State)> StoreTasks()
    {
        var found = new List<(string, string, string, int)>();
        try
        {
            using var root = locked.UserRoot.OpenSubKey(locked.PackagedApps);
            if (root == null) return found;
            foreach (string package in root.GetSubKeyNames())
            {
                using var packageKey = root.OpenSubKey(package);
                if (packageKey == null) continue;
                foreach (string task in packageKey.GetSubKeyNames())
                {
                    using var taskKey = packageKey.OpenSubKey(task);
                    if (taskKey?.GetValue("State") is int state && !Protected(package + " " + task, "")) found.Add((locked.PackagedApps + "\\" + package + "\\" + task, package, task, state));
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return found;
    }

    // "ContosoLtd.Sketchpad_fnn82j28hfe8t" -> "Sketchpad", "Microsoft.WindowsTerminal_8wekyb3d8bbwe" -> "Windows Terminal": the app's name; only an app with more
    // than one startup task adds the task's own name ("Canon Inkjet Smart Connect · CanonPRINT").
    internal static string StoreTitle(string package, string task, bool several)
    {
        string name = package.Split('_')[0];
        name = name.Contains('.') ? name[(name.IndexOf('.') + 1)..] : name;
        name = Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])", " ");
        return several ? $"{name} · {task}" : name;
    }

    private int? StoreState(string package, string task)
    {
        try
        {
            using var key = locked.UserRoot.OpenSubKey(locked.PackagedApps + "\\" + package + "\\" + task);
            return key?.GetValue("State") as int?;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { return null; }
    }

    private string? SetStoreState(string package, string task, int state, int expected)
    {
        try
        {
            using var key = locked.UserRoot.OpenSubKey(locked.PackagedApps + "\\" + package + "\\" + task, writable: true);
            if (key?.GetValue("State") is not int) return "the app's startup task is gone";
            key.SetValue("State", state, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return "Windows refused the change"; }
        return StoreState(package, task) == expected ? null : "did not stay changed";
    }

    // Shown, never changed: a policy owns it, it runs only once, or a wrong change could stop Windows from starting.
    private IEnumerable<StartupCandidate> LockedEntries()
    {
        var found = new List<StartupCandidate>();
        void Values(RegistryKey root, string path, string note, bool allUsers)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key == null) return;
                foreach (string name in key.GetValueNames())
                    if (name.Length > 0 && key.GetValue(name) is string command && !Protected(name, command))
                        found.Add(new("locked|" + note + "|" + name, name, command, StartupWhere.Locked, allUsers, true, false, note));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
        Values(locked.UserRoot, locked.PoliciesRun, "Set by a policy (this account)", false);
        Values(locked.MachineRoot, locked.PoliciesRun, "Set by a policy (all users)", true);
        Values(locked.UserRoot, locked.RunOnce, "Runs once at the next sign-in", false);
        Values(locked.MachineRoot, locked.RunOnce, "Runs once at the next start (all users)", true);
        try
        {
            using var winlogon = locked.MachineRoot.OpenSubKey(locked.Winlogon);
            if (winlogon?.GetValue("Shell") is string shell && !shell.Trim().Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                found.Add(new("locked|winlogon|Shell", "Windows shell", shell, StartupWhere.Locked, true, true, false, "Changed Windows shell · a wrong change stops Windows from starting"));
            if (winlogon?.GetValue("Userinit") is string userinit && !Regex.IsMatch(userinit.Trim(), @"^([A-Za-z]:\\Windows\\system32\\)?userinit\.exe,?$", RegexOptions.IgnoreCase))
                found.Add(new("locked|winlogon|Userinit", "Sign-in program (Userinit)", userinit, StartupWhere.Locked, true, true, false, "Changed sign-in program · a wrong change stops Windows from starting"));
            using var windows = locked.MachineRoot.OpenSubKey(locked.Windows);
            if (windows?.GetValue("LoadAppInit_DLLs") is int load && load != 0 && windows.GetValue("AppInit_DLLs") is string dlls && dlls.Trim().Length > 0)
                found.Add(new("locked|appinit|AppInit_DLLs", "Libraries loaded into every program (AppInit_DLLs)", dlls, StartupWhere.Locked, true, true, false, "Loaded into every program · change it only with the program that set it"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return found;
    }

    // ----- changing -----

    // Stops the named items starting with Windows (off = true) or lets them start again (off = false). Only ids Scan offers right now, never a locked one.
    internal StartupReport Change(IReadOnlyCollection<string> ids, bool off)
    {
        var offered = Scan().Where(item => !item.Locked).ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var chosen = ids.Distinct(StringComparer.OrdinalIgnoreCase).Where(offered.ContainsKey).Select(id => offered[id]).Where(item => item.StartsNow == off).ToList();
        var changed = new List<string>(); var failed = new List<string>();
        if (chosen.Count == 0) return new(changed, failed);
        if (chosen.Any(item => item.NeedsAdministrator) && !CanChangeAll)
            throw new IOException("Administrator rights are needed for all-users entries, tasks and services. Nothing was changed.");
        var history = Load() ?? new History(1, []);
        if (off)
        {
            // The ORIGINAL of each item is saved BEFORE the first change (an item already in the history keeps its first original).
            foreach (var item in chosen)
                if (!history.Items.Any(saved => saved.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))) history.Items.Add(new(item.Id, Original(item)));
            Save(history);
        }
        foreach (var item in chosen)
        {
            string? error = off ? TurnOff(item) : TurnOn(item, history.Items.FirstOrDefault(saved => saved.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)));
            if (error != null)
            {
                failed.Add($"{item.Title} ({error})");
                if (off && !item.ChangedByHush) history.Items.RemoveAll(saved => saved.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)); // nothing changed: nothing to put back
                continue;
            }
            changed.Add(item.Title);
            if (!off) history.Items.RemoveAll(saved => saved.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
            Log(off ? "off" : "on", item);
        }
        if (history.Items.Count == 0) { try { File.Delete(HistoryPath); } catch (IOException) { } } else Save(history);
        return new(changed, failed);
    }

    // Undo: every item Hush stopped starts with Windows again, exactly as it was (only = null: all of them).
    internal StartupReport Undo(IReadOnlyCollection<string>? only = null)
    {
        var history = Load() ?? throw new IOException("Hush has not changed anything that starts with Windows, so there is nothing to put back.");
        var ids = history.Items.Select(item => item.Id).Where(id => only == null || only.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        return Change(ids, off: false);
    }

    private string Original(StartupCandidate item) => Parse(item.Id) switch
    {
        ("app", StartupEntry entry) => apps.ReadFlag(entry.Source, entry.Name) is { } flag ? Convert.ToBase64String(flag) : "",
        ("task", _) => "enabled",
        ("store", (string package, string task)) => (StoreState(package, task) ?? StoreOn).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ("service", string name) => services.Read(name)?.Delayed == true ? "automatic-delayed" : "automatic",
        _ => ""
    };

    private string? TurnOff(StartupCandidate item)
    {
        switch (Parse(item.Id))
        {
            case ("app", StartupEntry entry):
                try { apps.WriteFlag(entry.Source, entry.Name, StartupAppsService.DisabledFlag()); }
                catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return "Windows refused the change"; }
                return apps.List().FirstOrDefault(now => now.Id == entry.Id)?.Enabled == false ? null : "did not stay off";
            case ("task", string path):
                if (!tasks.TrySetEnabled(path, false, out string taskError)) return taskError;
                return tasks.Read(path) is { Enabled: false } ? null : "did not stay off";
            case ("store", (string package, string task)):
                return SetStoreState(package, task, StoreOffByUser, StoreOffByUser); // what Task Manager writes when you turn it off
            case ("service", string name):
                if (!services.TrySetStart(name, Manual, false, out string serviceError)) return serviceError;
                return services.Read(name) is { Start: Manual } ? null : "did not stay changed";
            default: return "unknown item";
        }
    }

    private string? TurnOn(StartupCandidate item, Saved? saved)
    {
        switch (Parse(item.Id))
        {
            case ("app", StartupEntry entry):
                // Back to exactly what was there (no flag, or the old bytes); without a saved original (turned off elsewhere) Task Manager's "on" flag.
                byte[]? value = saved == null ? StartupAppsService.EnabledFlag() : saved.Original.Length == 0 ? null : FromBase64(saved.Original);
                if (value != null && StartupAppsService.IsDisabled(value)) value = StartupAppsService.EnabledFlag();
                try { apps.WriteFlag(entry.Source, entry.Name, value); }
                catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return "Windows refused the change"; }
                return apps.List().FirstOrDefault(now => now.Id == entry.Id)?.Enabled == true ? null : "is still off";
            case ("store", (string package, string task)):
                return SetStoreState(package, task, StoreOn, StoreOn);
            case ("task", string path):
                if (saved == null) return "Hush did not turn it off";
                if (!tasks.TrySetEnabled(path, true, out string taskError)) return taskError;
                return tasks.Read(path) is { Enabled: true } ? null : "is still off";
            case ("service", string name):
                if (saved == null) return "Hush did not change it";
                bool delayed = saved.Original == "automatic-delayed";
                if (!services.TrySetStart(name, Automatic, delayed, out string serviceError)) return serviceError;
                return services.Read(name) is { Start: Automatic } ? null : "did not return to its old setting";
            default: return "unknown item";
        }
    }

    private static byte[]? FromBase64(string text) { try { return Convert.FromBase64String(text); } catch (FormatException) { return null; } }

    // "app|<source>|<name>" -> the listed Run/folder entry; "task|<path>"; "service|<name>".
    private (string Kind, object? Target) Parse(string id)
    {
        if (id.StartsWith("app|", StringComparison.Ordinal)) return ("app", apps.List().FirstOrDefault(entry => "app|" + entry.Id == id));
        if (id.StartsWith("task|", StringComparison.Ordinal)) return ("task", id[5..]);
        if (id.StartsWith("service|", StringComparison.Ordinal)) return ("service", id[8..]);
        if (id.StartsWith("store|", StringComparison.Ordinal) && id[6..].Split('\\') is [var package, var task] && package.Length > 0 && task.Length > 0) return ("store", (package, task));
        return ("", null);
    }

    // ----- history (allow-listed on load: an elevated Undo must never be aimed at anything Hush could not have changed) -----
    private sealed record Saved(string Id, string Original);
    private sealed record History(int Schema, List<Saved> Items);

    private History? SafeLoad() { try { return Load(); } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { return null; } }

    private History? Load()
    {
        if (!File.Exists(HistoryPath)) return null;
        if (!CleanupEngine.SafePath(HistoryPath, folder) || new FileInfo(HistoryPath).Length > 65536) throw new InvalidDataException("The startup history is unsafe or damaged. Nothing was changed.");
        var history = JsonSerializer.Deserialize<History>(File.ReadAllText(HistoryPath)) ?? throw new InvalidDataException("The startup history is damaged.");
        if (history.Schema != 1 || history.Items == null) throw new InvalidDataException("The startup history is damaged.");
        history.Items.RemoveAll(saved => saved == null || string.IsNullOrWhiteSpace(saved.Id) || saved.Original == null || !Allowed(saved));
        return history;
    }

    // Structural allow-list: a Run/folder flag (only ever written under StartupApproved, value name = the entry's own name), a task outside \Microsoft\ that
    // is not protected and not Hush's own (original: enabled), or another program's service that Hush may touch (original: automatic). Nothing else.
    private bool Allowed(Saved saved)
    {
        if (saved.Id.StartsWith("app|", StringComparison.Ordinal)) return saved.Original.Length == 0 || FromBase64(saved.Original) is { Length: <= 64 };
        if (saved.Id.StartsWith("task|", StringComparison.Ordinal)) return saved.Original == "enabled" && saved.Id.Length > 6 && saved.Id[5] == '\\' && IsOwnTask(saved.Id[5..]);
        if (saved.Id.StartsWith("store|", StringComparison.Ordinal))
            return saved.Original is "0" or "1" or "2" && saved.Id[6..].Split('\\') is [var package, var task] && package.Length > 0 && task.Length > 0 && !package.Contains("..") && StoreState(package, task) is not null;
        if (saved.Id.StartsWith("service|", StringComparison.Ordinal))
            return saved.Original is "automatic" or "automatic-delayed" && services.Read(saved.Id[8..]) is { } info && IsOtherProgramsService(info);
        return false;
    }

    private void Save(History history)
    {
        Directory.CreateDirectory(folder);
        if (!CleanupEngine.SafePath(folder, folder)) throw new IOException("The startup history location is unsafe.");
        string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, history); file.Flush(true); }
            File.Move(temporary, HistoryPath, overwrite: true);
        }
        finally { if (File.Exists(temporary) && CleanupEngine.SafePath(temporary, folder)) File.Delete(temporary); }
    }

    private void Log(string action, StartupCandidate item)
    {
        try
        {
            string logs = Path.Combine(folder, "..", "Logs");
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "startup-changes.jsonl"), JsonSerializer.Serialize(new { utc = DateTime.UtcNow, action, id = item.Id, title = item.Title, command = item.Command }) + Environment.NewLine);
        }
        catch { /* the log is a courtesy; the saved history is what Undo relies on */ }
    }
}
