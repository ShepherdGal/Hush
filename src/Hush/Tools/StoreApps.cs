using System.Diagnostics;
using System.IO;

namespace Hush;

// Tools > Store apps (owner, November 19, 2026; Pro only): the Store (MSIX) apps installed for this account, through Windows' own package manager. Apps that
// Windows itself needs (system-signed packages, frameworks, resource packs) are never listed. Uninstall removes the app for this account; Reset puts it back to
// how it was when installed (its data is cleared, like Settings > Apps > Advanced options > Reset); End closes its running processes. Each one asks first.
internal sealed record StoreApp(string FullName, string Name, string Publisher, string Version, string InstallPath)
{
    internal string Detail => string.Join(" · ", new[] { Publisher, Version.Length > 0 ? "version " + Version : "" }.Where(part => part.Length > 0));
}

internal interface IStoreAppSystem
{
    IReadOnlyList<StoreApp> List();
    Task<string?> UninstallAsync(StoreApp app);   // null on success, otherwise one plain sentence
    Task<string?> ResetAsync(StoreApp app);
    string? End(StoreApp app);
}

internal sealed class WindowsStoreAppSystem : IStoreAppSystem
{
    public IReadOnlyList<StoreApp> List()
    {
        var manager = new Windows.Management.Deployment.PackageManager();
        var apps = new List<StoreApp>();
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.IsBundle) continue;
                if (package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System) continue;
                string name = package.DisplayName;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) continue;
                var v = package.Id.Version;
                apps.Add(new(package.Id.FullName, name, package.PublisherDisplayName ?? "", $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}", package.InstalledLocation?.Path ?? ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException) { }
        }
        return apps.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<string?> UninstallAsync(StoreApp app)
    {
        try
        {
            var result = await new Windows.Management.Deployment.PackageManager().RemovePackageAsync(app.FullName).AsTask();
            return result.ExtendedErrorCode == null ? null : "Windows could not uninstall it: " + result.ErrorText;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException) { return "Windows could not uninstall it: " + ex.Message; }
    }

    public async Task<string?> ResetAsync(StoreApp app)
    {
        // Windows' own Reset-AppxPackage (the same as Settings > Apps > Advanced options > Reset), run hidden. The package name is checked to be a plain name first.
        if (!System.Text.RegularExpressions.Regex.IsMatch(app.FullName, @"^[A-Za-z0-9._~-]+$")) return "Its package name is unusual, so Hush does not pass it on.";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -Command \"Get-AppxPackage | Where-Object PackageFullName -eq '{app.FullName}' | Reset-AppxPackage -ErrorAction Stop\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        try
        {
            using var process = Process.Start(start);
            if (process == null) return "Windows could not reset it.";
            string error = await process.StandardError.ReadToEndAsync();
            await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 && error.Trim().Length == 0 ? null : "Windows could not reset it." + (error.Trim().Length > 0 ? " " + error.Trim().Split('\n')[0].Trim() : "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { return "Windows could not reset it: " + ex.Message; }
    }

    // Ends only processes whose program file lives in the app's own folder.
    public string? End(StoreApp app)
    {
        if (app.InstallPath.Length < 4) return "Its folder is unknown, so Hush cannot tell which programs are its own.";
        int ended = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? path = WindowsProcessManager.PathOf(process.Id);
                if (path == null || !LeftoverPlaces.IsInside(path, app.InstallPath)) continue;
                try { process.Kill(); ended++; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return ended == 0 ? "It was not running." : null;
    }
}
