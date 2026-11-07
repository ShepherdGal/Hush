using System.Runtime.InteropServices;

namespace Hush;

internal static class ShortcutProbe
{
    // Read shortcut metadata only. Never resolve, launch, repair, or visit its target.
    internal static string? MissingApplication(string path)
    {
        object? shell = null, shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            shell = Activator.CreateInstance(type);
            shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
            if (shortcut == null) return null;
            dynamic link = shortcut;
            string target = (string)link.TargetPath;
            string arguments = (string)link.Arguments;
            return string.IsNullOrWhiteSpace(arguments) && CleanupEngine.IsOrphanExecutable(target) ? target : null;
        }
        catch { return null; }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
