using System.IO;
using Microsoft.Win32;

namespace Hush;

// "Start with Windows" (0.9, user request): one value in the signed-in user's own Run key (HKCU, so no administrator rights and nothing for other
// accounts). Hush starts with --startup, which opens it minimized (in the notification area when Minimize to tray is on). Tests use a fake: no test ever
// writes the real Run key.
internal interface IStartupRegistration
{
    // The command line currently registered for Hush, or null when there is none.
    string? Current();
    void Enable(string exe);
    void Disable();
}

internal sealed class RunKeyStartup : IStartupRegistration
{
    internal const string ValueName = "Hush", Argument = "--startup", KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static string CommandFor(string exe) => "\"" + exe + "\" " + Argument;

    public string? Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string;
    }

    public void Enable(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, CommandFor(exe), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    // Keeps the entry pointing at the copy that is running now (an update, the move to Program Files or a moved portable folder changes the path).
    // Only ever touches Hush's own value. Returns true when it wrote something.
    internal static bool Sync(IStartupRegistration registration, bool wanted, string? exe)
    {
        if (!wanted || string.IsNullOrEmpty(exe)) return false;
        string command = CommandFor(exe);
        if (string.Equals(registration.Current(), command, StringComparison.OrdinalIgnoreCase)) return false;
        registration.Enable(exe);
        return true;
    }
}

// A start from the Run key while administrator mode is on is handed over to the elevated copy through the task, which cannot carry the --startup argument.
// So the first copy leaves a one-line note (the time) in Hush's own data folder and the elevated copy reads it once: only "open quietly" depends on it,
// never anything that changes the PC, so a forged note can do nothing worse than start Hush minimized.
internal static class StartupHint
{
    private static string PathIn(string directory) => Path.Combine(directory, "startup-hint.txt");

    internal static void Leave(string directory)
    {
        try { Directory.CreateDirectory(directory); File.WriteAllText(PathIn(directory), DateTime.UtcNow.Ticks.ToString()); } catch { }
    }

    // True when a note younger than a minute was there; the note is removed either way.
    internal static bool Take(string directory, TimeSpan? maxAge = null)
    {
        string file = PathIn(directory);
        try
        {
            if (!File.Exists(file)) return false;
            string text = File.ReadAllText(file);
            File.Delete(file);
            return long.TryParse(text, out long ticks) && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) is { } age && age >= TimeSpan.Zero && age <= (maxAge ?? TimeSpan.FromMinutes(1));
        }
        catch { return false; }
    }
}
