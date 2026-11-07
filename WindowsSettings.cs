using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace Hush;

internal sealed record WindowsAdjustment(string Id, string Title, string Description, string Key, string ValueName,
    int Value, bool Advanced = false, bool Machine = false, bool EnterpriseOnly = false);

internal sealed record PolicyValue(bool Exists, int Number = 0, bool IsDword = true)
{
    internal static readonly PolicyValue Missing = new(false);
    internal bool Is(int value) => Exists && IsDword && Number == value;
}

internal sealed record WindowsEnvironment(int Build, string Edition, bool Administrator, bool Managed, string Owner)
{
    internal bool Supported => Build is >= 22000 and <= 26200 && (Enterprise || Edition is "Professional" or "ProfessionalN"
        or "ProfessionalEducation" or "ProfessionalEducationN" or "ProfessionalWorkstation" or "ProfessionalWorkstationN");
    internal bool Enterprise => Edition is "Enterprise" or "EnterpriseN" or "EnterpriseS" or "EnterpriseSN"
        or "Education" or "EducationN" or "IoTEnterprise" or "IoTEnterpriseS";
}

internal interface IWindowsPolicyStore
{
    WindowsEnvironment Environment { get; }
    PolicyValue Read(WindowsAdjustment setting, bool machineOverride = false);
    void Write(WindowsAdjustment setting, PolicyValue value);
}

internal sealed record AdjustmentState(WindowsAdjustment Definition, PolicyValue Current, bool Enabled, bool CanEdit, string Status);
internal sealed record WindowsSettingsState(IReadOnlyList<AdjustmentState> Settings, string Summary, bool CanUndo, string UndoHint);
internal sealed record AdjustmentChange(string Id, PolicyValue Expected, PolicyValue Target);
internal sealed record AdjustmentPlan(IReadOnlyList<AdjustmentChange> Changes, bool Undo);

internal sealed class WindowsSettingsService
{
    // Fixed catalog only. Never accept registry paths or executable commands from UI/history.
    internal static readonly IReadOnlyList<WindowsAdjustment> Catalog = Array.AsReadOnly(new[]
    {
        new WindowsAdjustment("diagnostics", "Limit optional diagnostic data",
            "Limits Windows diagnostics to required data for this user. Required data still sends; other apps have their own controls.",
            @"Software\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1),
        new WindowsAdjustment("tailored", "Stop personalized offers from diagnostic data",
            "Stops Windows using diagnostic data to personalize tips and offers for this user. Suggestions can still appear.",
            @"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1),
        new WindowsAdjustment("suggestions", "Hide suggestions in Windows Settings",
            "Turns off suggested content in Windows Settings for this user. Requires Enterprise, Education or IoT Enterprise.",
            @"Software\Policies\Microsoft\Windows\CloudContent", "DisableWindowsSpotlightOnSettings", 1, EnterpriseOnly: true),
        new WindowsAdjustment("location", "Turn off location for this user",
            "Prevents apps using the Windows location feature for this user. Maps and location-based features may stop working; websites can still estimate location.",
            @"Software\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1, Advanced: true),
        new WindowsAdjustment("widgets", "Turn off Windows Widgets",
            "Disables the Widgets board and its taskbar content for every user. Does not uninstall Widgets. Requires running Hush as administrator.",
            @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, Advanced: true, Machine: true)
    });

    internal const string NotConfigured = "Not configured by Hush";
    private sealed record SavedValue(string Id, PolicyValue Original);
    private sealed record History(int Schema, string Owner, bool Pending, List<SavedValue> Values);
    private readonly IWindowsPolicyStore store;
    private readonly string directory, historyPath;
    internal WindowsSettingsService(IWindowsPolicyStore store, string directory)
    {
        this.store = store;
        this.directory = Path.GetFullPath(directory);
        historyPath = Path.Combine(this.directory, "windows-settings.json");
    }
    internal static WindowsSettingsService Create() => new(new RegistryPolicyStore(), Path.Combine(CleanupEngine.DataDirectory, "Windows settings"));
    private static WindowsAdjustment Find(string id) => Catalog.SingleOrDefault(item => item.Id == id) ?? throw new InvalidDataException("Unknown Windows setting.");

    internal WindowsSettingsState Read()
    {
        using var gate = Lock();
        var environment = store.Environment;
        var history = Load(environment);
        var rows = Catalog.Select(setting => ReadRow(setting, environment, history)).ToArray();
        string summary = !environment.Supported ? "Policy controls require Windows 11 Pro, Enterprise or Education (21H2–25H2). This Windows edition/build is read-only."
            : environment.Managed ? "A work or school management connection was detected. Windows policies are read-only."
            : "Choose changes, then review before applying. Windows may require signing out or restarting to use policy changes.";
        bool canUndo = history.Values.Count > 0 && history.Values.All(saved => CanRestore(Find(saved.Id), saved, environment));
        string undoHint = history.Values.Count == 0 ? "Undo restores the values saved before Hush's changes."
            : history.Pending ? "An interrupted operation needs review. Use Undo changes to restore tracked values."
            : !canUndo ? "Undo is blocked by a changed value, management policy or administrator requirement. Refresh after resolving it."
            : "Undo restores all changes tracked by Hush, including an originally absent policy. It does not force Windows defaults.";
        return new(rows, summary, canUndo, undoHint);
    }

    private AdjustmentState ReadRow(WindowsAdjustment setting, WindowsEnvironment environment, History history)
    {
        var current = store.Read(setting);
        var saved = history.Values.SingleOrDefault(value => value.Id == setting.Id);
        bool enabled = current.Is(setting.Value) || setting.Id == "diagnostics" && current.Is(0);
        string? reason = Restriction(setting, environment);
        if (reason == null && history.Pending) reason = "Recovery needed · use Undo changes";
        if (reason == null && saved != null && current != new PolicyValue(true, setting.Value)) reason = "Changed outside Hush · read-only";
        if (reason == null && saved == null && current.Exists) reason = "Already configured outside Hush · read-only";
        if (reason == null && !setting.Machine && store.Read(setting, machineOverride: true).Exists) reason = "Device policy exists · read-only";
        return new(setting, current, enabled, reason == null, reason ?? (saved == null ? NotConfigured : "Applied by Hush · can be undone"));
    }
    private static string? Restriction(WindowsAdjustment setting, WindowsEnvironment environment)
        => !environment.Supported ? "Unsupported Windows edition/build"
        : environment.Managed ? "Managed device · read-only"
        : setting.EnterpriseOnly && !environment.Enterprise ? "Enterprise or Education required"
        : setting.Machine && !environment.Administrator ? "Administrator required · all users"
        : null;
    private bool CanRestore(WindowsAdjustment setting, SavedValue saved, WindowsEnvironment environment)
    {
        var current = store.Read(setting);
        return !environment.Managed && (!setting.Machine || environment.Administrator)
            && (current == new PolicyValue(true, setting.Value) || current == saved.Original)
            && (setting.Machine || !store.Read(setting, machineOverride: true).Exists);
    }

    internal AdjustmentPlan Prepare(IReadOnlyList<AdjustmentState> displayed, IReadOnlyDictionary<string, bool> requested)
    {
        var fresh = Read();
        var changes = new List<AdjustmentChange>();
        foreach (var request in requested)
        {
            var row = fresh.Settings.Single(item => item.Definition.Id == request.Key);
            var before = displayed.Single(item => item.Definition.Id == request.Key);
            if (request.Value == before.Enabled) continue;
            if (!row.CanEdit || row.Current != before.Current || !before.CanEdit) throw new IOException("A Windows setting changed or is unavailable. Refresh and review again.");
            changes.Add(new(request.Key, row.Current, request.Value ? new(true, row.Definition.Value) : PolicyValue.Missing));
        }
        return new(changes.AsReadOnly(), false);
    }

    internal AdjustmentPlan PrepareUndo()
    {
        using var gate = Lock();
        var environment = store.Environment;
        var history = Load(environment);
        if (history.Values.Count == 0) throw new IOException("There are no tracked changes to undo.");
        if (history.Values.Any(saved => !CanRestore(Find(saved.Id), saved, environment)))
            throw new IOException("A tracked value changed, is managed, or requires administrator access. Nothing was restored.");
        return new(history.Values.Select(saved => new AdjustmentChange(saved.Id, store.Read(Find(saved.Id)), saved.Original)).ToArray(), true);
    }

    // Called only after the dedicated confirmation. Revalidates the entire plan before any write.
    internal void Execute(AdjustmentPlan plan)
    {
        using var gate = Lock();
        var environment = store.Environment;
        var history = Load(environment);
        if (plan.Changes.Count == 0 || plan.Changes.Count > Catalog.Count || plan.Changes.Select(change => change.Id).Distinct().Count() != plan.Changes.Count)
            throw new IOException("No valid Windows changes were confirmed.");
        if (plan.Undo && plan.Changes.Count != history.Values.Count) throw new IOException("Undo history changed. Review again.");
        foreach (var change in plan.Changes) Validate(change, plan.Undo, environment, history);
        var tracked = history.Values.ToList();
        foreach (var change in plan.Changes)
            if (!tracked.Any(saved => saved.Id == change.Id)) tracked.Add(new(change.Id, change.Expected));
        var pending = history with { Pending = true, Values = tracked };
        Save(pending); // Flush original values before the first registry mutation.
        var attempted = new List<AdjustmentChange>();
        try
        {
            foreach (var change in plan.Changes)
            {
                // A policy/permission/value may have changed while the confirmation was open.
                Validate(change, plan.Undo, store.Environment, history);
                if (change.Expected == change.Target) continue;
                attempted.Add(change); // Include a write that throws after changing the value.
                store.Write(Find(change.Id), change.Target);
                if (store.Read(Find(change.Id)) != change.Target) throw new IOException("Windows did not retain a confirmed setting.");
            }
            foreach (var change in plan.Changes)
                if (change.Target == PolicyValue.Missing) tracked.RemoveAll(saved => saved.Id == change.Id);
            Save(pending with { Pending = false, Values = tracked });
        }
        catch (Exception failure)
        {
            foreach (var change in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    // Do not overwrite an unrelated edit made during a failed operation.
                    if (store.Read(Find(change.Id)) == change.Target) store.Write(Find(change.Id), change.Expected);
                }
                catch { /* Durable originals remain for explicit recovery. */ }
            }
            throw new IOException("Windows changes could not be completed. Saved history was retained. Refresh and review Undo changes; another tool's changes will not be overwritten.", failure);
        }
    }

    private void Validate(AdjustmentChange change, bool undo, WindowsEnvironment environment, History history)
    {
        var setting = Find(change.Id);
        var saved = history.Values.SingleOrDefault(value => value.Id == change.Id);
        if (store.Read(setting) != change.Expected) throw new IOException("A Windows value changed after review. Nothing further was changed.");
        if (undo)
        {
            if (saved == null || change.Target != saved.Original || !CanRestore(setting, saved, environment)) throw new IOException("Undo is no longer safe. Refresh and review again.");
        }
        else
        {
            var row = ReadRow(setting, environment, history);
            if (!row.CanEdit || change.Target != (row.Enabled ? PolicyValue.Missing : new PolicyValue(true, setting.Value)))
                throw new IOException("The confirmed Windows change is no longer available.");
        }
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(directory);
        if (!CleanupEngine.SafePath(directory, directory)) throw new IOException("Windows settings history location is unsafe.");
        string path = Path.Combine(directory, "operation.lock");
        if (File.Exists(path) && !CleanupEngine.SafePath(path, directory)) throw new IOException("Windows settings history is unsafe.");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private History Load(WindowsEnvironment environment)
    {
        if (!File.Exists(historyPath)) return new(1, environment.Owner, false, []);
        if (!CleanupEngine.SafePath(historyPath, directory) || new FileInfo(historyPath).Length > 32768) throw new InvalidDataException("Windows settings history is unsafe or damaged. No settings were changed.");
        var history = JsonSerializer.Deserialize<History>(File.ReadAllText(historyPath)) ?? throw new InvalidDataException("Windows settings history is damaged.");
        if (history.Schema != 1 || history.Owner != environment.Owner || history.Values == null || history.Values.Count > Catalog.Count
            || history.Values.Select(saved => saved.Id).Distinct().Count() != history.Values.Count
            || history.Values.Any(saved => !Catalog.Any(setting => setting.Id == saved.Id) || saved.Original != PolicyValue.Missing))
            throw new InvalidDataException("Windows settings history does not match this account or catalog. No settings were changed.");
        return history;
    }
    private void Save(History history)
    {
        if (File.Exists(historyPath) && !CleanupEngine.SafePath(historyPath, directory)) throw new IOException("Unsafe Windows settings history.");
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, history); file.Flush(true); }
            File.Move(temporary, historyPath, overwrite: true);
        }
        finally { if (File.Exists(temporary) && CleanupEngine.SafePath(temporary, directory)) File.Delete(temporary); }
    }
}

internal sealed class RegistryPolicyStore : IWindowsPolicyStore
{
    public WindowsEnvironment Environment
    {
        get
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var version = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            int build = int.TryParse(version?.GetValue("CurrentBuildNumber") as string, out int number) ? number : 0;
            using var identity = WindowsIdentity.GetCurrent();
            bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            bool managed = IsManaged(root);
            return new(build, version?.GetValue("EditionID") as string ?? "", admin, managed, identity.User?.Value ?? "unknown");
        }
    }
    private static bool IsManaged(RegistryKey machine)
    {
        int result = NetGetJoinInformation(null, out IntPtr name, out int status);
        try { if (result != 0 || status is not (1 or 2)) return true; }
        finally { if (name != IntPtr.Zero) NetApiBufferFree(name); }
        int joined = NetGetAadJoinInformation(null, out IntPtr info);
        try { if (joined != 0 || info != IntPtr.Zero) return true; }
        finally { if (info != IntPtr.Zero) NetFreeAadJoinInformation(info); }
        using var enrollments = machine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
        if (enrollments != null)
            foreach (string child in enrollments.GetSubKeyNames())
            { using var enrollment = enrollments.OpenSubKey(child); if (enrollment?.GetValue("EnrollmentState") is int state && state == 1) return true; }
        return false;
    }
    public PolicyValue Read(WindowsAdjustment setting, bool machineOverride = false)
    {
        using var root = RegistryKey.OpenBaseKey(setting.Machine || machineOverride ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(setting.Key);
        return ReadValue(key, setting.ValueName);
    }
    public void Write(WindowsAdjustment setting, PolicyValue value)
    {
        if (!WindowsSettingsService.Catalog.Contains(setting)) throw new InvalidOperationException("Unknown Windows policy.");
        using var root = RegistryKey.OpenBaseKey(setting.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        if (value.Exists)
        { using var key = root.CreateSubKey(setting.Key, writable: true); WriteValue(key, setting.ValueName, value); }
        else
        { using var key = root.OpenSubKey(setting.Key, writable: true); if (key != null) WriteValue(key, setting.ValueName, value); }
    }
    internal static PolicyValue ReadValue(RegistryKey? key, string name)
    {
        if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return PolicyValue.Missing;
        return key.GetValueKind(name) == RegistryValueKind.DWord && key.GetValue(name) is int number ? new(true, number) : new(true, IsDword: false);
    }
    internal static void WriteValue(RegistryKey key, string name, PolicyValue value)
    {
        if (value.Exists)
        { if (!value.IsDword) throw new InvalidDataException("Only DWORD policies are supported."); key.SetValue(name, value.Number, RegistryValueKind.DWord); }
        else key.DeleteValue(name, throwOnMissingValue: false);
        key.Flush(); // Never remove a registry key/tree or any neighboring value.
    }
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);
    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetGetAadJoinInformation(string? tenant, out IntPtr info);
    [DllImport("netapi32.dll")] private static extern void NetFreeAadJoinInformation(IntPtr info);
}

internal sealed class AdjustmentChoice(AdjustmentState state) : INotifyPropertyChanged
{
    internal AdjustmentState Baseline { get; } = state;
    public string Id => Baseline.Definition.Id;
    public string Title => Baseline.Definition.Title;
    public string Description => Baseline.Definition.Description;
    public string Status => Baseline.Status;
    // The default "Not configured by Hush" line is noise on every row; only show a status when it tells the user something.
    public bool HasNotice => Baseline.Status != WindowsSettingsService.NotConfigured;
    public bool CanEdit => Baseline.CanEdit;
    public bool Advanced => Baseline.Definition.Advanced;
    private bool selected = state.Enabled;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
