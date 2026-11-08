internal static class TestRunner
{
    internal static int Run(string[] args, Action checks)
    {
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
    }
}
