using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hush;
using Microsoft.Win32;

// Installer and uninstaller, exercised only against temp folders and a HKCU\Software\Hush.TestFixtures\<GUID> registry root.
// Nothing here ever installs Hush on, or removes it from, this PC.
internal static class SetupChecks
{
    internal static void Run(string fixture)
    {
        string guid = Guid.NewGuid().ToString("N");
        string registryRoot = @"Software\Hush.TestFixtures\" + guid;
        string root = Path.Combine(fixture, "setup");
        string source = Path.Combine(AppContext.BaseDirectory, "Hush.exe");
        SetupLocations Locations(string name) => new(Path.Combine(root, name, "Programs", "Hush"), Path.Combine(root, name, "StartMenu"),
            Path.Combine(root, name, "Desktop"), registryRoot + @"\" + name, Path.Combine(root, name, "AppData", "Hush"));
        try
        {
            EngineChecks(source, Locations);
            WindowChecks(source, Locations);
            Console.WriteLine("PASS: Installer and uninstaller: verified copy, shortcuts, Settings > Apps entry, upgrade over an existing install, locked and fake files, safe uninstall that keeps unrelated files, optional data removal and both setup windows (temp folders and a fixture registry key only)");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryRoot, throwOnMissingSubKey: false);
        }
    }

    private static void EngineChecks(string source, Func<string, SetupLocations> locate)
    {
        var loc = locate("main");
        Require(!HushSetup.IsInstalled(loc), "Nothing is installed before setup runs");
        var result = HushSetup.Install(source, true, loc);
        Require(result.Success, "Install succeeds from a genuine Hush.exe: " + result.Message);
        Require(File.Exists(loc.InstalledExe) && Hash(loc.InstalledExe) == Hash(source), "The installed file is a byte-for-byte verified copy");
        Require(HushSetup.SamePath(HushSetup.ShortcutTarget(loc.StartMenuShortcut)!, loc.InstalledExe)
            && HushSetup.SamePath(HushSetup.ShortcutTarget(loc.DesktopShortcut)!, loc.InstalledExe), "Start menu and desktop shortcuts point at the installed app");
        using (var key = Registry.CurrentUser.OpenSubKey(loc.RegistryKey))
            Require(key != null && (string?)key.GetValue("DisplayName") == "Hush" && (string?)key.GetValue("DisplayVersion") == UpdateService.DisplayVersion
                && (string?)key.GetValue("UninstallString") == $"\"{loc.InstalledExe}\" --uninstall" && (string?)key.GetValue("InstallLocation") == loc.InstallDirectory
                && (int)key.GetValue("NoModify")! == 1, "The Settings > Apps entry carries name, version, install folder and a quiet uninstall command");
        Require(HushSetup.IsInstalled(loc) && HushSetup.IsInstalledCopy(loc.InstalledExe, loc) && !HushSetup.IsInstalledCopy(source, loc), "An installed copy is recognized only by its own path");

        // Upgrade over an existing install: still exactly one program file, no temp leftovers, shortcuts intact.
        Require(HushSetup.Install(source, false, loc).Success && Directory.EnumerateFiles(loc.InstallDirectory).Count() == 1
            && File.Exists(loc.StartMenuShortcut), "Installing over an existing copy replaces it cleanly and leaves no temp files");

        // A copy that is open (locked) cannot be replaced: report it, keep the old file, leave nothing behind.
        string before = Hash(loc.InstalledExe);
        using (new FileStream(loc.InstalledExe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var locked = HushSetup.Install(source, false, loc);
            Require(!locked.Success && locked.Message.Contains("Close it and try again"), "An open installed copy is reported plainly, not overwritten");
        }
        Require(Hash(loc.InstalledExe) == before && Directory.EnumerateFiles(loc.InstallDirectory).Count() == 1, "A failed attempt leaves the working install and no temp files");
        Require(!HushSetup.Install(loc.InstalledExe, false, loc).Success, "Setup refuses to install a copy onto itself");

        // A file that merely carries the name is rejected before anything is created.
        var fake = locate("fake");
        string fakeSource = Path.Combine(Path.GetDirectoryName(loc.InstallDirectory)!, "fake-source", "Hush.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeSource)!);
        File.WriteAllBytes(fakeSource, RandomNumberGenerator.GetBytes(4096));
        var rejected = HushSetup.Install(fakeSource, true, fake);
        Require(!rejected.Success && !Directory.Exists(fake.InstallDirectory) && !File.Exists(fake.StartMenuShortcut) && !HushSetup.IsInstalled(fake),
            "A fake Hush.exe is rejected before any folder, shortcut or registry entry is created");

        // Uninstall: keeps unrelated files, a shortcut that points elsewhere, and (by default) the user's settings and history.
        Directory.CreateDirectory(loc.DataDirectory);
        string history = Path.Combine(loc.DataDirectory, "windows-settings.json"); File.WriteAllText(history, "{}");
        string unrelated = Path.Combine(loc.InstallDirectory, "keep-me.txt"); File.WriteAllText(unrelated, "mine");
        File.Delete(loc.DesktopShortcut);
        HushSetup.CreateShortcut(loc.DesktopShortcut, source, Path.GetDirectoryName(source)!);
        var removed = HushSetup.Uninstall(loc, removeData: false, runningExe: null);
        Require(removed.Success && !File.Exists(loc.InstalledExe) && !File.Exists(loc.StartMenuShortcut) && !HushSetup.IsInstalled(loc)
            && Registry.CurrentUser.OpenSubKey(loc.RegistryKey) == null, "Uninstall removes the program, its Start menu shortcut and the Settings > Apps entry");
        Require(File.Exists(unrelated) && Directory.Exists(loc.InstallDirectory), "Unrelated files in the install folder are never deleted, so the folder stays");
        Require(File.Exists(loc.DesktopShortcut), "A shortcut that does not point at this installation is left alone");
        Require(File.Exists(history), "Settings, history and logs are kept unless the user asks to delete them");

        // Optional data removal, and a deferred removal while the installed copy itself is running.
        var second = locate("second");
        Require(HushSetup.Install(source, false, second).Success, "A second fixture install works");
        Directory.CreateDirectory(second.DataDirectory); File.WriteAllText(Path.Combine(second.DataDirectory, "settings.json"), "{}");
        var running = HushSetup.Uninstall(second, removeData: false, runningExe: second.InstalledExe);
        Require(running.Success && running.Deferred && File.Exists(second.InstalledExe) && !File.Exists(second.StartMenuShortcut),
            "Removing from the running copy defers only the program file and removes everything else at once");
        HushSetup.RemoveInstalledFiles(second.InstallDirectory);
        Require(!File.Exists(second.InstalledExe) && !Directory.Exists(second.InstallDirectory) && Directory.Exists(second.DataDirectory), "The deferred step deletes the program file and the now-empty folder");
        var third = locate("third");
        Require(HushSetup.Install(source, true, third).Success, "A third fixture install works");
        Directory.CreateDirectory(third.DataDirectory); File.WriteAllText(Path.Combine(third.DataDirectory, "settings.json"), "{}");
        Require(HushSetup.Uninstall(third, removeData: true, runningExe: null).Success && !Directory.Exists(third.DataDirectory) && !Directory.Exists(third.InstallDirectory)
            && !File.Exists(third.DesktopShortcut), "Deleting settings, history and logs happens only when asked and removes nothing else");

        // The Settings > Apps version label follows an in-app update.
        var refresh = locate("refresh");
        Require(HushSetup.Install(source, false, refresh).Success, "A fourth fixture install works");
        var announced = new List<string>(); var realAnnouncer = HushSetup.ShellIconRefresher; HushSetup.ShellIconRefresher = paths => announced.AddRange(paths);   // never tell the real shell
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(refresh.RegistryKey, writable: true)) key!.SetValue("DisplayVersion", "0.1");
            HushSetup.RefreshRegistration(refresh);
            using (var key = Registry.CurrentUser.OpenSubKey(refresh.RegistryKey))
                Require((string?)key!.GetValue("DisplayVersion") == UpdateService.DisplayVersion, "After an update the Settings > Apps version label is refreshed");

            // The icon refreshes at once after an update (owner bug: the new icon only showed after uninstalling and reinstalling): the first start of a changed
            // program file saves the shortcut again, repeats the Apps icon and announces the change to the shell; a start with an unchanged file does nothing.
            Require(announced.Contains(refresh.InstalledExe) && announced.Contains(refresh.StartMenuShortcut) && HushSetup.ShortcutTarget(refresh.StartMenuShortcut) is { } t && HushSetup.SamePath(t, refresh.InstalledExe),
                "The first start of an updated program announces its changed icons (the program and its shortcut) and keeps the shortcut pointing at it");
            int first = announced.Count; HushSetup.RefreshRegistration(refresh);
            Require(announced.Count == first, "A start with an unchanged program file announces nothing");
            using (var key = Registry.CurrentUser.OpenSubKey(refresh.RegistryKey, writable: true)) key!.SetValue("IconStamp", "an older program file");
            HushSetup.RefreshRegistration(refresh);
            Require(announced.Count > first, "A different program file (the next update) announces its icons again");
        }
        finally { HushSetup.ShellIconRefresher = realAnnouncer; }
        HushSetup.Uninstall(refresh, false, null);
    }

    private static void WindowChecks(string source, Func<string, SetupLocations> locate)
    {
        var loc = locate("window");
        string? opened = null;
        var install = new SetupWindow(SetupMode.Install, loc, sourceExe: source, openInstalled: path => opened = path, runPortable: () => { });
        install.ShowActivated = false; install.ShowInTaskbar = false;
        try
        {
            install.Show();
            Pump(120);
            Require(((TextBlock)install.FindName("Heading")).Text == "Install Hush" && ((TextBlock)install.FindName("Details")).Text.Contains(loc.InstallDirectory)
                && ((TextBlock)install.FindName("Details")).Text.Contains("administrator"), "Setup says what it will do and that it needs no administrator rights");
            Require(((Button)install.FindName("PrimaryButton")).Content.ToString() == "Install" && ((Button)install.FindName("SecondaryButton")).Content.ToString() == "Run without installing"
                && ((CheckBox)install.FindName("OptionBox")).IsChecked == true && !HushSetup.IsInstalled(loc), "Setup offers Install or running portable, and changes nothing yet");
            Capture(install, "setup-install");
            Click(install, "PrimaryButton");
            UntilTrue(() => ((TextBlock)install.FindName("Heading")).Text == "Hush is installed");
            Require(HushSetup.IsInstalled(loc) && File.Exists(loc.DesktopShortcut) && ((Button)install.FindName("PrimaryButton")).Content.ToString() == "Open Hush",
                "Install completes from the window and offers to open Hush");
            Capture(install, "setup-installed");
            Click(install, "PrimaryButton");
            UntilTrue(() => !install.IsVisible);
            Require(opened == loc.InstalledExe, "Open Hush starts the installed copy, not the downloaded file");
        }
        finally { if (install.IsVisible) install.Close(); }

        string unrelatedData = Path.Combine(loc.DataDirectory, "settings.json");
        Directory.CreateDirectory(loc.DataDirectory); File.WriteAllText(unrelatedData, "{}");
        var uninstall = new SetupWindow(SetupMode.Uninstall, loc);
        uninstall.ShowActivated = false; uninstall.ShowInTaskbar = false;
        try
        {
            uninstall.Show();
            Pump(120);
            Require(((TextBlock)uninstall.FindName("Heading")).Text == "Remove Hush?" && ((CheckBox)uninstall.FindName("OptionBox")).IsChecked == false
                && ((TextBlock)uninstall.FindName("OptionHint")).Text.Contains("Undo"), "Uninstall asks first, keeps settings by default and warns that deleting them also removes Undo history");
            Capture(uninstall, "setup-uninstall");
            Click(uninstall, "PrimaryButton");
            UntilTrue(() => ((TextBlock)uninstall.FindName("Heading")).Text == "Hush was removed");
            Require(!HushSetup.IsInstalled(loc) && !Directory.Exists(loc.InstallDirectory) && File.Exists(unrelatedData), "Uninstall from the window removes Hush and keeps the user's data");
            Capture(uninstall, "setup-removed");
            Click(uninstall, "SecondaryButton");
            UntilTrue(() => !uninstall.IsVisible);
        }
        finally { if (uninstall.IsVisible) uninstall.Close(); }
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is not string directory) return;
        Directory.CreateDirectory(directory);
        var root = (FrameworkElement)window.Content; root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false; timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static void UntilTrue(Func<bool> done)
    {
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) => { if (done() || clock.ElapsedMilliseconds > 6000) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Require(done(), "Setup window step did not finish within six seconds");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
