using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Hush;

// Settings > Scheduled tasks (0.9, user request: "also clean unneeded tasks"): the sibling of Settings > Services. Finds Windows scheduled tasks that most home
// PCs do not need (telemetry and diagnostics jobs, app updaters) and tasks left behind by removed programs (their program file no longer exists), and lets the
// person turn them off. "Off" means Disabled in Task Scheduler: the task stays, it just no longer runs, and Undo enables it again. Nothing is ever deleted.
// Rules, kept like every other system change in Hush: a curated list only (whatever the UI sends, nothing outside it can change), nothing under
// \Microsoft\Windows\ that is not an exact catalog entry, no security / update / recovery / system task, every original state is saved to disk BEFORE the first
// change, the result is read back and reported exactly, Undo puts every original back, and each change is logged. Everything that touches Windows sits behind
// ITaskSystem, so tests never read for changing or change a real task.
internal sealed record TaskInfo(string Path, bool Enabled, string Command, bool AtLogon, bool AtBoot);

internal interface ITaskSystem
{
    bool IsAdministrator { get; }
    // Every scheduled task this process can read. Read-only; works without administrator rights (then some tasks cannot be seen).
    IReadOnlyList<TaskInfo> List();
    // One task by its path ("\Folder\Name"); null when it does not exist or cannot be read.
    TaskInfo? Read(string path);
    // False only when the file is certainly missing (an absent or removable drive, or a folder this process may not look into, counts as "cannot tell").
    bool FileExists(string path);
    bool TrySetEnabled(string path, bool enabled, out string error);
}

internal enum TaskLevel { Recommended, Somewhat, LeftBehind }

// One task Hush offers. Hush would always disable it (reversible with Undo).
internal sealed record TaskCandidate(string Path, string Title, string Why, TaskLevel Level, string Topic, bool AtLogon, bool AtBoot)
{
    // The folder and name as Task Scheduler shows them, plus when it runs if that was cheap to read.
    internal string Status => (AtLogon ? "Runs at sign-in · " : AtBoot ? "Runs when Windows starts · " : "") + Path;
}

internal sealed record TaskReport(IReadOnlyList<string> Changed, IReadOnlyList<string> Failed)
{
    internal string Text(bool undo)
    {
        string verb = undo ? "turned back on" : "turned off";
        string text = Changed.Count == 0 ? "Nothing was changed." : $"{Changed.Count} scheduled task{(Changed.Count == 1 ? "" : "s")} {verb} and checked: {string.Join(", ", Changed)}.";
        if (Failed.Count > 0) text += $"\n\nCould not be changed: {string.Join("; ", Failed)}.";
        return text;
    }
}

internal sealed class TaskTuning(ITaskSystem system, string directory)
{
    // Topic names differ from the Services and Windows-choice topics on purpose: the Settings page remembers which headings are open by name.
    internal const string TelemetryTopic = "Telemetry and diagnostic tasks";
    internal const string ExtrasTopic = "Phone, maps, family and games";
    internal const string UpdatersTopic = "App updater tasks";
    internal const string LeftBehindTopic = "Tasks left behind by removed programs";
    private static readonly string[] TopicOrder = [TelemetryTopic, ExtrasTopic, UpdatersTopic, LeftBehindTopic];
    internal static int TopicRank(string topic)
    {
        int index = Array.IndexOf(TopicOrder, topic);
        return index >= 0 ? index : topic == ListGrouping.Other ? 998 : 997;
    }

    private const string Win = @"\Microsoft\Windows\";
    private const string Rare = "Rarely needed";

    // The curated list. A pattern is an exact task path, or a prefix ending in * for tasks whose name ends in a random id (the * never crosses a folder).
    // Under \Microsoft\Windows\ only exact paths are allowed (a test checks it). Level: Recommended = rarely used on a home PC; Somewhat = read first.
    internal static readonly (string Pattern, string Title, string Why, TaskLevel Level, string Topic)[] Catalog =
    [
        (Win + @"Application Experience\Microsoft Compatibility Appraiser", "Compatibility Appraiser", "Collects data about your programs and devices and sends it to Microsoft to check upgrade compatibility.", TaskLevel.Recommended, TelemetryTopic),
        (Win + @"Application Experience\ProgramDataUpdater", "Program data collector", "Collects data about installed programs for Microsoft's Customer Experience Improvement Program.", TaskLevel.Recommended, TelemetryTopic),
        (Win + @"Application Experience\StartupAppTask", "Startup app scan", "Measures the programs that start with Windows. Task Manager may stop showing their startup impact.", TaskLevel.Somewhat, TelemetryTopic),
        (Win + @"Customer Experience Improvement Program\Consolidator", "Customer Experience Improvement Program", "Collects and sends usage data for Microsoft's Customer Experience Improvement Program.", TaskLevel.Recommended, TelemetryTopic),
        (Win + @"Customer Experience Improvement Program\UsbCeip", "USB usage reports", "Collects data about your USB devices for Microsoft's improvement program.", TaskLevel.Recommended, TelemetryTopic),
        (Win + @"Customer Experience Improvement Program\KernelCeipTask", "Kernel usage reports", "Collects system usage data for Microsoft's improvement program.", TaskLevel.Recommended, TelemetryTopic),
        (Win + @"DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", "Disk diagnostic data collector", "Collects disk diagnostic information and sends it to Microsoft.", TaskLevel.Recommended, TelemetryTopic),
        (Win + @"Windows Error Reporting\QueueReporting", "Queued error reports", "Sends queued crash and error reports to Microsoft. Reports are no longer sent in the background.", TaskLevel.Somewhat, TelemetryTopic),
        (Win + @"Feedback\Siuf\DmClient", "Feedback surveys", "Downloads and sends Windows feedback surveys. Not needed if you do not answer them.", TaskLevel.Recommended, TelemetryTopic),
        (Win + @"Feedback\Siuf\DmClientOnScenarioDownload", "Feedback survey downloads", "Downloads Windows feedback survey scenarios. Not needed if you do not answer them.", TaskLevel.Recommended, TelemetryTopic),
        (Win + @"Autochk\Proxy", "Disk check data collector", "Collects data from the disk check at start-up for Microsoft's improvement program.", TaskLevel.Recommended, TelemetryTopic),
        (Win + @"NetTrace\GatherNetworkInfo", "Network information collector", "Gathers network details for diagnostics. Troubleshooters still collect them when you run one.", TaskLevel.Somewhat, TelemetryTopic),
        (Win + @"Device Information\Device", "Device information collector", "Collects information about this device and sends it to Microsoft.", TaskLevel.Recommended, TelemetryTopic),
        (@"\NvTmRep_*", "NVIDIA crash and telemetry reports", "Sends crash and usage reports from the NVIDIA driver. The driver works without it.", TaskLevel.Recommended, TelemetryTopic),
        (@"\NvTmMon_*", "NVIDIA telemetry monitor", "Watches the NVIDIA driver for its usage reports. The driver works without it.", TaskLevel.Recommended, TelemetryTopic),
        (Win + @"Maps\MapsToastTask", "Maps notifications", "Shows notifications from the Maps app. Not needed if you do not use offline maps.", TaskLevel.Recommended, ExtrasTopic),
        (Win + @"Maps\MapsUpdateTask", "Offline maps update", "Keeps offline maps up to date for the Maps app. Not needed if you do not use offline maps.", TaskLevel.Recommended, ExtrasTopic),
        (@"\Microsoft\XblGameSave\XblGameSaveTask", "Xbox game save sync", "Syncs Xbox game saves in the background. Not needed if you do not play Xbox games on this PC.", TaskLevel.Recommended, ExtrasTopic),
        (Win + @"Shell\FamilySafetyMonitor", "Family Safety monitor", "Needed only if you use Microsoft Family Safety on this PC.", TaskLevel.Somewhat, ExtrasTopic),
        (Win + @"Shell\FamilySafetyRefreshTask", "Family Safety refresh", "Needed only if you use Microsoft Family Safety on this PC.", TaskLevel.Somewhat, ExtrasTopic),
        (Win + @"Work Folders\Work Folders Logon Synchronization", "Work Folders sync at sign-in", "Syncs Work Folders from a work server. Not used on a home PC.", TaskLevel.Recommended, ExtrasTopic),
        (@"\GoogleUpdateTaskMachineCore*", "Google Update (daily)", "Checks for Chrome and Google app updates in the background. Chrome still updates when you open it.", TaskLevel.Somewhat, UpdatersTopic),
        (@"\GoogleUpdateTaskMachineUA*", "Google Update (hourly)", "Checks for Chrome and Google app updates in the background. Chrome still updates when you open it.", TaskLevel.Somewhat, UpdatersTopic),
        (@"\MicrosoftEdgeUpdateTaskMachineCore*", "Microsoft Edge Update (daily)", "Checks for Edge updates in the background. Edge still updates when you open it.", TaskLevel.Somewhat, UpdatersTopic),
        (@"\MicrosoftEdgeUpdateTaskMachineUA*", "Microsoft Edge Update (hourly)", "Checks for Edge updates in the background. Edge still updates when you open it.", TaskLevel.Somewhat, UpdatersTopic),
        (@"\MicrosoftEdgeUpdateBrowserUpdateTask*", "Microsoft Edge browser update", "Updates Edge in the background. Edge still updates when you open it.", TaskLevel.Somewhat, UpdatersTopic),
        (@"\Adobe Acrobat Update Task", "Adobe Acrobat Update", "Checks for Adobe Reader and Acrobat updates in the background. They can still check when you open them.", TaskLevel.Somewhat, UpdatersTopic),
        (@"\Adobe Flash Player Updater", "Adobe Flash Player Updater", "Flash Player ended in 2020, so this updater has nothing left to update.", TaskLevel.Recommended, UpdatersTopic),
        (@"\DropboxUpdateTaskMachineCore*", "Dropbox Update (daily)", "Checks for Dropbox updates in the background. Dropbox still updates itself while it runs.", TaskLevel.Somewhat, UpdatersTopic),
        (@"\DropboxUpdateTaskMachineUA*", "Dropbox Update (hourly)", "Checks for Dropbox updates in the background. Dropbox still updates itself while it runs.", TaskLevel.Somewhat, UpdatersTopic),
        (@"\BraveSoftwareUpdateTask*", "Brave Update", "Checks for Brave updates in the background. Brave still updates when you open it.", TaskLevel.Somewhat, UpdatersTopic),
        (@"\Mozilla\Firefox Background Update *", "Firefox background update", "Updates Firefox while it is closed. Firefox still updates when you open it.", TaskLevel.Somewhat, UpdatersTopic),
        (@"\OneDrive Standalone Update Task-*", "OneDrive update", "Checks for OneDrive updates in the background. OneDrive still updates itself while it runs.", TaskLevel.Somewhat, UpdatersTopic),
    ];

    // Folders (and everything under them) that are never offered and never changed, also not on Undo. Windows Update's own tasks belong to the separate
    // Windows Update switch. In general nothing under \Microsoft\Windows\ is touched unless it is an exact catalog entry.
    internal static readonly string[] NeverTouchFolders =
    [
        Win + "UpdateOrchestrator", Win + "WindowsUpdate", Win + "WaaSMedic", Win + "Windows Defender", Win + "SystemRestore", Win + "Servicing", Win + "Registry",
        Win + "Time Synchronization", Win + "TaskScheduler", Win + "BitLocker", Win + "Defrag", Win + "DeviceDirectoryClient", Win + "Plug and Play",
        Win + "Power Efficiency Diagnostics", Win + "WDI", Win + "Wininet",
    ];
    private static readonly string[] NeverTouchWords = ["Antivirus", "Defender", "Firewall", "Security"];

    internal static bool IsNeverTouch(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (path.Equals(@"\" + AdminMode.TaskName, StringComparison.OrdinalIgnoreCase)) return true; // Hush's own administrator task
        if (path.StartsWith(Win + "Security", StringComparison.OrdinalIgnoreCase)) return true;
        if (NeverTouchWords.Any(word => path.Contains(word, StringComparison.OrdinalIgnoreCase))) return true;
        return NeverTouchFolders.Any(folder => path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase));
    }

    // The catalog entry a task path matches, or null. A prefix pattern only matches inside its own folder.
    internal static (string Pattern, string Title, string Why, TaskLevel Level, string Topic)? CatalogEntry(string path)
    {
        if (IsNeverTouch(path)) return null;
        foreach (var entry in Catalog)
        {
            if (!entry.Pattern.EndsWith('*')) { if (path.Equals(entry.Pattern, StringComparison.OrdinalIgnoreCase)) return entry; continue; }
            string prefix = entry.Pattern[..^1];
            if (path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.IndexOf('\\', prefix.Length) < 0) return entry;
        }
        return null;
    }

    private sealed record SavedTask(string Path, bool Enabled);
    private sealed record History(int Schema, List<SavedTask> Tasks);
    private readonly string folder = System.IO.Path.GetFullPath(directory);
    private string HistoryPath => System.IO.Path.Combine(folder, "tasks-changed.json");

    internal bool CanChange => system.IsAdministrator;
    internal bool HasChanges { get { try { return Load()?.Tasks.Count > 0; } catch { return false; } } }
    internal int ChangedCount { get { try { return Load()?.Tasks.Count ?? 0; } catch { return 0; } } }

    // What Hush would offer right now. Read-only: one list read, nothing changed or saved. Only tasks that exist and are enabled are offered.
    internal IReadOnlyList<TaskCandidate> Scan()
    {
        var result = new List<TaskCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in system.List())
        {
            if (!task.Enabled || IsNeverTouch(task.Path) || !seen.Add(task.Path)) continue;
            if (CatalogEntry(task.Path) is { } entry) { result.Add(new(task.Path, entry.Title, entry.Why, entry.Level, entry.Topic, task.AtLogon, task.AtBoot)); continue; }
            if (MissingProgram(task) is not { } file) continue;
            result.Add(new(task.Path, NameOf(task.Path), $"Left behind by a program that was removed: its file ({System.IO.Path.GetFileName(file)}) no longer exists, so this task can only fail when it runs.",
                TaskLevel.LeftBehind, LeftBehindTopic, task.AtLogon, task.AtBoot));
        }
        return result.OrderBy(c => TopicRank(c.Topic)).ThenBy(c => c.Level).ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string NameOf(string path) { int slash = path.LastIndexOf('\\'); return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path; }

    // Machine-wide variables only: a per-user one (%LOCALAPPDATA%, %APPDATA%, %USERPROFILE%, %TEMP%...) would expand to whoever runs Hush, not to the task's user,
    // and could make a perfectly installed program look missing.
    private static readonly HashSet<string> MachineVariables = new(StringComparer.OrdinalIgnoreCase)
    { "SystemRoot", "windir", "SystemDrive", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "ALLUSERSPROFILE" };

    // The program a leftover task would run when that file is certainly gone: a task outside \Microsoft\Windows\, not on the never-touch list, not a catalog
    // entry, whose first program is a fully qualified local .exe outside the Windows folder. Null when it is not a leftover.
    private string? MissingProgram(TaskInfo task)
    {
        if (task.Path.StartsWith(Win, StringComparison.OrdinalIgnoreCase) || IsNeverTouch(task.Path) || CatalogEntry(task.Path) != null) return null;
        if (Regex.Matches(task.Command, "%([^%]+)%").Any(match => !MachineVariables.Contains(match.Groups[1].Value))) return null;
        string? file = ServiceTuning.ExecutablePath(task.Command);
        return file != null && ServiceTuning.IsOutsideWindows(file) && !system.FileExists(file) ? file : null;
    }

    internal TaskReport Apply(IReadOnlyCollection<string> paths)
    {
        if (!system.IsAdministrator) throw new IOException("Administrator rights are needed to change scheduled tasks. Nothing was changed.");
        // Only what Scan offers right now can be changed, whatever arrives: an unknown or protected path is ignored.
        var offered = Scan().ToDictionary(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase);
        var chosen = paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(offered.ContainsKey).Select(path => offered[path]).ToList();
        var changed = new List<string>(); var failed = new List<string>();
        if (chosen.Count == 0) return new(changed, failed);
        var history = Load() ?? new History(1, []);
        var savedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in chosen)
            if (!history.Tasks.Any(saved => saved.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase)))
            {
                history.Tasks.Add(new(candidate.Path, true)); // the ORIGINAL (only enabled tasks are offered), kept until it is put back
                savedNow.Add(candidate.Path);
            }
        Save(history); // saved BEFORE the first change
        foreach (var candidate in chosen)
        {
            if (!system.TrySetEnabled(candidate.Path, false, out string error)) { failed.Add($"{candidate.Title} ({error})"); Forget(history, savedNow, candidate.Path); continue; }
            var after = system.Read(candidate.Path);
            if (after == null || after.Enabled) { failed.Add($"{candidate.Title} did not stay turned off"); Forget(history, savedNow, candidate.Path); continue; }
            changed.Add(candidate.Title);
            Log("disabled", candidate.Path, candidate.Title);
        }
        if (history.Tasks.Count == 0) { try { File.Delete(HistoryPath); } catch (IOException) { } } else Save(history);
        return new(changed, failed);
    }

    private static void Forget(History history, HashSet<string> savedNow, string path)
    {
        if (savedNow.Contains(path)) history.Tasks.RemoveAll(saved => saved.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    internal TaskReport Undo()
    {
        if (!system.IsAdministrator) throw new IOException("Administrator rights are needed to turn scheduled tasks back on. Nothing was changed.");
        var dropped = new List<string>();
        var history = Load(dropped) ?? throw new IOException("Hush has not changed any scheduled task, so there is nothing to put back.");
        var changed = new List<string>(); var failed = new List<string>();
        foreach (string path in dropped) failed.Add($"{path} (left as it is: Hush could not confirm that it changed this task)");
        foreach (var saved in history.Tasks.ToList())
        {
            var now = system.Read(saved.Path);
            if (now == null) { history.Tasks.Remove(saved); continue; } // the task no longer exists
            string title = CatalogEntry(saved.Path)?.Title ?? NameOf(saved.Path);
            if (now.Enabled != saved.Enabled)
            {
                if (!system.TrySetEnabled(saved.Path, saved.Enabled, out string error)) { failed.Add($"{title} ({error})"); continue; }
                var after = system.Read(saved.Path);
                if (after == null || after.Enabled != saved.Enabled) { failed.Add($"{title} did not return to its old setting"); continue; }
                Log("enabled", saved.Path, title);
            }
            changed.Add(title);
            history.Tasks.Remove(saved);
        }
        if (history.Tasks.Count == 0) { try { File.Delete(HistoryPath); } catch (IOException) { } } else Save(history);
        return new(changed, failed);
    }

    // rejected (optional) receives the paths of entries that were dropped because Hush cannot confirm it could ever have changed them.
    private History? Load(List<string>? rejected = null)
    {
        if (!File.Exists(HistoryPath)) return null;
        if (!CleanupEngine.SafePath(HistoryPath, folder) || new FileInfo(HistoryPath).Length > 65536) throw new InvalidDataException("The scheduled tasks history is unsafe or damaged. Nothing was changed.");
        var history = JsonSerializer.Deserialize<History>(File.ReadAllText(HistoryPath)) ?? throw new InvalidDataException("The scheduled tasks history is damaged.");
        if (history.Schema != 1 || history.Tasks == null) throw new InvalidDataException("The scheduled tasks history is damaged.");
        // The history sits in a folder the person's own programs can write to, and Undo runs with administrator rights, so the file must never be able to aim
        // Hush at another task. Hush only ever saves "was enabled", and only for a catalog task or a leftover whose program file is, right now, still missing.
        history.Tasks.RemoveAll(saved => saved == null || string.IsNullOrWhiteSpace(saved.Path) || IsNeverTouch(saved.Path) || !saved.Enabled);
        foreach (var saved in history.Tasks.ToList())
        {
            if (CatalogEntry(saved.Path) != null || IsVerifiedLeftover(saved.Path)) continue;
            history.Tasks.Remove(saved);
            rejected?.Add(saved.Path);
        }
        return history;
    }

    // The same test Scan uses for leftovers, minus the enabled state (Hush has already disabled it).
    private bool IsVerifiedLeftover(string path) => system.Read(path) is { } task && MissingProgram(task) != null;

    private void Save(History history)
    {
        Directory.CreateDirectory(folder);
        if (!CleanupEngine.SafePath(folder, folder)) throw new IOException("The scheduled tasks history location is unsafe.");
        string temporary = System.IO.Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, history); file.Flush(true); }
            File.Move(temporary, HistoryPath, overwrite: true);
        }
        finally { if (File.Exists(temporary) && CleanupEngine.SafePath(temporary, folder)) File.Delete(temporary); }
    }

    private void Log(string action, string path, string title)
    {
        try
        {
            string logs = System.IO.Path.Combine(folder, "..", "Logs");
            Directory.CreateDirectory(logs);
            File.AppendAllText(System.IO.Path.Combine(logs, "tasks.jsonl"), JsonSerializer.Serialize(new { utc = DateTime.UtcNow, action, task = path, title }) + Environment.NewLine);
        }
        catch { /* the log is a courtesy; the saved history is what Undo relies on */ }
    }
}

// The real Windows. Tasks are read from their definition files under %SystemRoot%\System32\Tasks (read-only; files Hush may not read are skipped, and without
// administrator rights the top folder cannot be listed, so tasks there only show as administrator). Changes go through schtasks.exe (Windows' own tool) and
// are read back from the same file. Task files are never edited or deleted by Hush. Never used by tests to change anything.
internal sealed class RealTaskSystem : ITaskSystem
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
    private const long MaxFileBytes = 1 << 20;
    public bool IsAdministrator => WindowsSystemTools.IsElevated();

    public IReadOnlyList<TaskInfo> List()
    {
        var result = new List<TaskInfo>();
        if (!Walk(Root, 0, result))
            foreach (string known in new[] { "Microsoft", "Mozilla" }) Walk(Path.Combine(Root, known), 1, result); // the top folder needs administrator rights to list
        return result;
    }

    // False when the folder could not be listed.
    private static bool Walk(string directory, int depth, List<TaskInfo> result)
    {
        if (depth > 16) return true;
        FileSystemInfo[] entries;
        try { entries = new DirectoryInfo(directory).GetFileSystemInfos(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return false; }
        foreach (var entry in entries)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never follow a link out of the Tasks folder
            if (entry is DirectoryInfo) { Walk(entry.FullName, depth + 1, result); continue; }
            if (Parse(entry.FullName) is { } task) result.Add(task);
        }
        return true;
    }

    public TaskInfo? Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('\\') || path.Contains("..") || path.Contains('/')) return null;
        string file = Path.GetFullPath(Path.Combine(Root, path.TrimStart('\\')));
        return file.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase) ? Parse(file) : null;
    }

    private static readonly XmlReaderSettings XmlSettings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };

    // One task definition: <Settings><Enabled> (true when absent), the first <Exec><Command>, and whether a sign-in or start-up trigger exists.
    private static TaskInfo? Parse(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileBytes) return null;
            using var reader = XmlReader.Create(stream, XmlSettings);
            var task = XDocument.Load(reader).Root;
            if (task == null || task.Name.LocalName != "Task") return null;
            XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(element => element.Name.LocalName == name);
            string enabled = Child(Child(task, "Settings"), "Enabled")?.Value.Trim() ?? "true";
            string command = Child(task, "Actions")?.Elements().Where(element => element.Name.LocalName == "Exec").Select(exec => Child(exec, "Command")?.Value.Trim()).FirstOrDefault() ?? "";
            var triggers = Child(task, "Triggers")?.Elements().Select(element => element.Name.LocalName).ToList() ?? [];
            string path = "\\" + Path.GetRelativePath(Root, file);
            return new(path, !enabled.Equals("false", StringComparison.OrdinalIgnoreCase), command, triggers.Contains("LogonTrigger"), triggers.Contains("BootTrigger"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or System.Security.SecurityException or InvalidOperationException) { return null; }
    }

    // "Certainly missing" only: a removable or absent drive, or a folder this process may not look into, counts as "cannot tell" (true).
    public bool FileExists(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (root == null) return true;
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return true;
            if (File.Exists(path)) return true;
            // File.Exists also says false when access is denied, so walk up to the nearest folder that can be listed and look for the next part of the path.
            string child = Path.GetFullPath(path);
            string? parent = Path.GetDirectoryName(child);
            while (parent != null)
            {
                if (Directory.Exists(parent))
                {
                    try { return Directory.EnumerateFileSystemEntries(parent).Any(entry => entry.Equals(child, StringComparison.OrdinalIgnoreCase)); }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return true; }
                }
                child = parent; parent = Path.GetDirectoryName(parent);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return true; }
    }

    public bool TrySetEnabled(string path, bool enabled, out string error)
    {
        error = "";
        if (Read(path) == null) { error = "the task could not be found"; return false; }
        try
        {
            var (code, output) = RealServiceSystem.Run("schtasks.exe", "/Change", "/TN", path, enabled ? "/ENABLE" : "/DISABLE");
            if (code != 0) error = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { Length: > 0 and < 200 } line ? line : "Windows refused the change";
            return code == 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { error = ex.Message; return false; }
    }
}
