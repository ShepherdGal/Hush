using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Hush;

// 0.9 audit (H1): an elevated Hush must stage everything an elevated helper trusts in an administrator-only folder. These checks use ACL objects in memory
// and fixture folders only; no real update, uninstall or task is started, and the real ProgramData folder is never touched.
internal static class StagingTrustChecks
{
    private sealed class FakeElevation(bool elevated) : IElevation
    {
        public bool IsElevated => elevated;
        public bool IsSignedInUser => true;
        public bool Launch(string exe, string arguments, bool wait, out int exitCode) { exitCode = 0; return false; }
    }

    private static DirectorySecurity Acl(string sddl) { var security = new DirectorySecurity(); security.SetSecurityDescriptorSddlForm(sddl); return security; }

    internal static void Run(string fixture, Action<bool, string> check)
    {
        // ---- The ACL rule, on in-memory descriptors ----
        check(AdminOnlyDirectory.IsAdminOnly(Acl("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")), "An administrator-only ACL (owner Administrators, full control for SYSTEM and Administrators only) is accepted");
        check(AdminOnlyDirectory.IsAdminOnly(Acl("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;BU)")), "Read and execute for Users does not make a folder untrusted");
        check(AdminOnlyDirectory.IsAdminOnly(Acl("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICIIO;FA;;;CO)")), "A CREATOR OWNER rule that only applies to children created later is ignored");
        check(!AdminOnlyDirectory.IsAdminOnly(Acl("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)")), "Full control for Users makes a folder untrusted");
        check(!AdminOnlyDirectory.IsAdminOnly(Acl("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x2;;;AU)")), "Write access for Authenticated Users makes a folder untrusted");
        check(!AdminOnlyDirectory.IsAdminOnly(Acl("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x40;;;WD)")), "Delete-child for Everyone makes a folder untrusted");
        check(!AdminOnlyDirectory.IsAdminOnly(Acl("O:S-1-5-21-1-2-3-1001D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")), "A folder owned by an ordinary user is untrusted even when its rules look fine");

        // ---- Where an elevated Hush stages ----
        string userRoot = UpdateInstaller.StagingRoot;
        string adminRoot = Path.Combine(fixture, "ProgramData", "Hush", "Updates");
        var oldElevation = AdminMode.Elevation;
        try
        {
            AdminMode.Elevation = new FakeElevation(false);
            check(userRoot.StartsWith(CleanupEngine.DataDirectory, StringComparison.OrdinalIgnoreCase) && UpdateInstaller.PrepareStagingRoot() == userRoot, "A normal Hush stages updates in its own data folder");
            UpdateInstaller.AdminRootOverride = adminRoot;
            AdminMode.Elevation = new FakeElevation(true);
            check(UpdateInstaller.StagingRoot == adminRoot, "An elevated Hush stages updates in the administrator-only folder, not in its data folder");

            // A folder somebody else pre-created (here: writable by Users) is never trusted, and the elevated helper refuses a plan inside it.
            Directory.CreateDirectory(adminRoot);
            var open = new DirectoryInfo(adminRoot).GetAccessControl();
            open.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(adminRoot).SetAccessControl(open);
            bool refused = false;
            try { UpdateInstaller.PrepareStagingRoot(); } catch (IOException) { refused = true; }
            check(refused, "An elevated Hush refuses a staging folder that other users can write to");

            string stage = Path.Combine(adminRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            string target = Path.Combine(fixture, "update-target", "Hush.exe"); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "old build"); File.WriteAllText(Path.Combine(stage, "Hush.exe"), "forged build");
            var plan = new UpdatePlan(target, Path.Combine(stage, "Hush.exe"), "00", UpdateInstaller.Hash(target), "9.0.0", Environment.ProcessId, 0);
            string planPath = Path.Combine(stage, "install.json"); File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
            int code = UpdateInstaller.ApplyAsync(planPath).GetAwaiter().GetResult();
            check(code == 1 && File.ReadAllText(target) == "old build", "An elevated update helper refuses a plan from a folder that is not administrator-only and leaves the app untouched");

            // The positive case needs a real elevated process, so it runs only when the tests themselves were started as administrator.
            if (WindowsSystemTools.IsElevated())
            {
                string made = Path.Combine(fixture, "ProgramData2", "Hush", "Updates");
                AdminOnlyDirectory.Ensure(made);
                check(AdminOnlyDirectory.IsAdminOnly(new DirectoryInfo(made).GetAccessControl()), "Ensure creates a folder that passes its own administrator-only check");
            }
        }
        finally { AdminMode.Elevation = oldElevation; UpdateInstaller.AdminRootOverride = null; }
    }
}
