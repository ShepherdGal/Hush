using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Hush;

// A System Restore point before big Windows changes (0.9). Hush only ever CREATES one (through Windows' System Restore API); it never deletes restore points.
// Everything sits behind IRestorePoints so tests use a fake and never make a real restore point on this PC.
internal sealed record RestorePointStatus(bool ProtectionOn, bool RecentPoint, bool CanCreate);

internal interface IRestorePoints
{
    // Read-only: is System Protection on for C:, was a point made in the last 24 hours (Windows allows one a day), may this copy create one (administrator).
    RestorePointStatus Probe();
    // Makes the point. Null on success, otherwise one plain sentence about why not. Never throws.
    string? Create(string description);
    // Turns System Protection on for C:. Null on success, otherwise the reason. Needs administrator rights.
    string? EnableProtection();
}

internal sealed class WindowsRestorePoints : IRestorePoints
{
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

    public RestorePointStatus Probe()
    {
        bool on = false, recent = false;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(SystemRestoreKey);
            bool disabled = key?.GetValue("DisableSR") is int off && off == 1;
            // While System Protection is on, Windows keeps RPSessionInterval at 1; it is 0 (or missing) when it is off.
            on = !disabled && key?.GetValue("RPSessionInterval") is int interval && interval > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        recent = on && NewestPointWithin(TimeSpan.FromHours(24));
        return new(on, recent, WindowsSystemTools.IsElevated());
    }

    // Reads the restore points' creation times through WMI's scripting object (no extra package needed). Any failure counts as "none recent".
    private static bool NewestPointWithin(TimeSpan span)
    {
        object? locator = null, service = null, results = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type == null) return false;
            locator = Activator.CreateInstance(type);
            service = type.InvokeMember("ConnectServer", System.Reflection.BindingFlags.InvokeMethod, null, locator, [".", @"root\default"]);
            if (service == null) return false;
            results = service.GetType().InvokeMember("ExecQuery", System.Reflection.BindingFlags.InvokeMethod, null, service, ["SELECT CreationTime FROM SystemRestore"]);
            if (results is not System.Collections.IEnumerable rows) return false;
            foreach (object item in rows)
            {
                try
                {
                    string? stamp = item.GetType().InvokeMember("CreationTime", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                    if (stamp is { Length: >= 14 } && DateTime.TryParseExact(stamp[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var made)
                        && DateTime.Now - made < span) return true;
                }
                finally { Release(item); }
            }
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or InvalidCastException or MissingMemberException or NullReferenceException) { }
        finally { Release(results); Release(service); Release(locator); }
        return false;
    }

    // The WMI scripting objects are COM objects: let go of them now instead of waiting for the garbage collector.
    private static void Release(object? com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); }
        catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException) { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct RestorePointInfo
    {
        public int EventType, PointType; public long SequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ManagerStatus { public int Status; public long SequenceNumber; }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, EntryPoint = "SRSetRestorePointW")]
    private static extern bool SetRestorePoint(ref RestorePointInfo info, out ManagerStatus status);

    public string? Create(string description)
    {
        try
        {
            if (!WindowsSystemTools.IsElevated()) return "creating a restore point needs administrator rights";
            // BEGIN_SYSTEM_CHANGE (100) with MODIFY_SETTINGS (12), then END_SYSTEM_CHANGE (101) with the sequence number Windows returned.
            var begin = new RestorePointInfo { EventType = 100, PointType = 12, Description = description.Length > 255 ? description[..255] : description };
            if (!SetRestorePoint(ref begin, out var status) || status.Status != 0) return "Windows did not create the restore point (System Protection may be off)";
            var end = new RestorePointInfo { EventType = 101, PointType = 12, SequenceNumber = status.SequenceNumber, Description = begin.Description };
            SetRestorePoint(ref end, out _);
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or SEHException) { return "System Restore is not available on this PC"; }
    }

    public string? EnableProtection()
    {
        object? locator = null, service = null, restoreClass = null;
        try
        {
            if (!WindowsSystemTools.IsElevated()) return "turning on System Protection needs administrator rights";
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type == null) return "Windows management is not available";
            locator = Activator.CreateInstance(type);
            service = type.InvokeMember("ConnectServer", System.Reflection.BindingFlags.InvokeMethod, null, locator, [".", @"root\default"]);
            if (service == null) return "Windows management is not available";
            restoreClass = service.GetType().InvokeMember("Get", System.Reflection.BindingFlags.InvokeMethod, null, service, ["SystemRestore"]);
            if (restoreClass == null) return "Windows management is not available";
            restoreClass.GetType().InvokeMember("Enable", System.Reflection.BindingFlags.InvokeMethod, null, restoreClass, [@"C:\"]);
            return Probe().ProtectionOn ? null : "Windows did not turn System Protection on";
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or InvalidCastException or MissingMemberException or NullReferenceException)
        { return "Windows refused to turn System Protection on"; }
        finally { Release(restoreClass); Release(service); Release(locator); }
    }
}
