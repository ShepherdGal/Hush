using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Hush;

// A folder that only administrators can write to (0.9 audit). When Hush runs elevated it must not stage files that it will later run or copy into a place
// any program of the signed-in person can change (%LOCALAPPDATA%): that would turn "the person's own programs" into "administrator code" at the next
// update or uninstall. Elevated work (the update helper and its plan, the uninstall helper) therefore lives under %ProgramData%\Hush, created with an
// ACL that gives only SYSTEM and Administrators any right, and the folder is checked again every time before it is trusted: owner, and no allow-rule
// that lets anyone else write, delete or change permissions. A folder someone else pre-created fails the check and is never used.
internal static class AdminOnlyDirectory
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    // NT SERVICE\TrustedInstaller owns many system folders.
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private const FileSystemRights WriteLike = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static bool Trusted(SecurityIdentifier sid) => sid.Equals(LocalSystem) || sid.Equals(Administrators) || sid.Equals(TrustedInstaller);

    // Pure check on an ACL (so tests need no real folder): trusted owner and no write-like allow-rule for anyone but SYSTEM, Administrators and TrustedInstaller.
    // CREATOR OWNER rules that only apply to children created later are ignored (they name whoever creates a file, which here is an administrator).
    internal static bool IsAdminOnly(FileSystemSecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted(owner)) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & WriteLike) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid) return false;
            if (Trusted(sid)) continue;
            if (sid.Equals(CreatorOwner) && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            return false;
        }
        return true;
    }

    private static DirectorySecurity NewSecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(true, false); // no inherited rules: ProgramData would otherwise let Users create entries
        foreach (var sid in new[] { LocalSystem, Administrators })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    // Throws IOException unless the folder exists, is no link and is administrator-only.
    internal static void Validate(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new IOException("The administrator-only folder does not exist.");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The administrator-only folder must not be a link.");
        bool ok;
        try { ok = IsAdminOnly(info.GetAccessControl()); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException) { ok = false; }
        if (!ok) throw new IOException("The folder " + path + " can be changed by other users, so Hush will not run elevated code from it.");
    }

    // Creates the folder (and its parent) with the administrator-only ACL when missing, then validates the whole chain. Needs an elevated process.
    internal static string Ensure(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.GetDirectoryName(full) ?? throw new IOException("The administrator-only folder needs a parent folder.");
        foreach (string directory in new[] { parent, full })
        {
            if (!Directory.Exists(directory))
            {
                try { new DirectoryInfo(directory).Create(NewSecurity()); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
                { throw new IOException("Hush could not create the administrator-only folder " + directory + ": " + ex.Message); }
            }
            Validate(directory);
        }
        return full;
    }
}
