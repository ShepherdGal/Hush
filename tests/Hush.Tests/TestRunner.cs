internal static class TestRunner
{
    internal static int Run(string[] args, Action checks)
    {
        // Most layout checks inspect every category card at once, so test windows show the whole list; ReviewGroupChecks switches the groups back on.
        Hush.MainWindow.ReviewShowsAllByDefault = true;
        // No test may ever make a real restore point: every window uses the fake unless a check swaps in its own.
        Hush.MainWindow.RestorePoints = new FakeRestorePoints();
        // Likewise no test window may read the real service list (a background sc.exe still running at exit crashed the runtime once).
        Hush.MainWindow.ServiceSystemFactory = () => new ServiceChecks.FakeServices();
        // And no test window may read or change the real scheduled tasks.
        Hush.MainWindow.TaskSystemFactory = () => new TaskChecks.FakeTasks();
        // ... and its history and log go to a throw-away folder, never into the person's real Hush data folder.
        string taskHistory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Hush.Tests.Tasks." + Guid.NewGuid().ToString("N"), "Windows settings");
        Hush.MainWindow.TaskHistoryDirectory = taskHistory;
        Hush.MainWindow.CloseActionsOnDeactivate = false;
        // The product starts everyone in Simple mode; the layout suites exercise every Settings card and the full scan, so test windows start in Pro mode (ProModeChecks switches it).
        Hush.MainWindow.TestProMode = true;
        // No test window may put an icon next to the clock or write the real "Start with Windows" entry.
        Hush.MainWindow.TrayFactory = (_, _, _) => new TrayChecks.FakeTray();
        Hush.MainWindow.StartupRegistration = new TrayChecks.FakeStartup();
        try
        {
            if (args.Length == 0) checks();
            else if (args is ["--verify-failure-path"]) UiLayoutChecks.Run(forceFailure: true);
            else if (args is ["--verify-buttons"]) UiLayoutChecks.Run(buttonsOnly: true);
            else if (args is ["--verify-navigation"]) UiLayoutChecks.Run(navigationOnly: true);
            else if (args is ["--verify-theme-motion"]) UiLayoutChecks.Run(themeMotionOnly: true);
            else if (args is ["--verify-clean-selection"]) UiLayoutChecks.Run(cleanSelectionOnly: true);
            else if (args is ["--verify-updates"]) UiLayoutChecks.Run(updatesOnly: true);
            else if (args is ["--verify-windows-settings"]) UiLayoutChecks.Run(windowsSettingsOnly: true);
            else if (args is ["--verify-setup"]) UiLayoutChecks.Run(setupOnly: true);
            else if (args is ["--verify-smart-boost"]) UiLayoutChecks.Run(smartBoostOnly: true);
            else if (args is ["--benchmark"]) return PerformanceChecks.Run();
            else if (args is ["--stress-scan"]) return ScaleChecks.Stress(250000);
            else if (args is ["--stress-scan", var count]) return ScaleChecks.Stress(int.Parse(count));
            else if (args is ["--scan-report"]) return PerformanceChecks.ScanReport();
            else if (args is ["--windows-report"]) return PerformanceChecks.WindowsReport();
            else if (args is ["--target-report"]) return PerformanceChecks.TargetReport();
            else if (args is ["--process-report"]) return PerformanceChecks.ProcessReport();
            else if (args is ["--meter-report"]) return PerformanceChecks.MeterReport();
            else if (args is ["--service-report"]) return PerformanceChecks.ServiceReport();
            else if (args is ["--task-report"]) return PerformanceChecks.TaskReport();
            else if (args is ["--ui-perf"]) return UiPerf.Run();
            else if (args is ["--preview-cleaning", var folder]) return UiPerf.PreviewCleaning(folder);
            else if (args is ["--preview-more", var moreFolder]) return UiPerf.PreviewMore(moreFolder);
            else throw new ArgumentException("Unknown test arguments.");
            return 0;
        }
        catch (Exception error)
        {
            // Test failures must remain failures, without escaping to Windows Error Reporting.
            Console.Error.WriteLine("FAIL: Hush verification did not pass.");
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            try { string? top = System.IO.Path.GetDirectoryName(taskHistory); if (top != null && System.IO.Directory.Exists(top)) System.IO.Directory.Delete(top, recursive: true); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
