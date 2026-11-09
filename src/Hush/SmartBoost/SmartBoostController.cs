namespace Hush;

internal sealed record JunkReading(long Bytes, long Files, DateTime WhenUtc)
{
    public string Describe => Files == 0 ? "No safe junk found" : $"{Sizes.Format(Bytes)} in {Files:N0} files";
}

// The logic behind Quick Boost, junk monitoring and Automatic Boost, with no window in it so it can be tested on fixtures.
// Only Safe categories are ever measured or cleaned here; Review and Advanced items always need the person's own review.
internal sealed class SmartBoostController(CleanupEngine engine)
{
    internal static bool IsSafeTarget(CleanupTarget target) =>
        ReviewCategory.SafetyOf(ReviewCategory.CategoryFor(target.Kind, target.Group, target.Category)) == SafetyLevel.Safe;

    // Read-only and light: counts Safe junk without keeping a single item, at background CPU and disk priority.
    public JunkReading Measure(CancellationToken token)
    {
        var result = engine.Scan(null, token, new(IsSafeTarget, CountOnly: true, ReportIntervalMs: 1000, Background: true));
        return new(result.TotalBytes, result.FileCount, DateTime.UtcNow);
    }

    public static bool ThresholdReached(JunkReading reading, BoostSettings settings) =>
        reading.Files > 0 && reading.Bytes >= (long)settings.ThresholdMb * 1024 * 1024;

    // Automatic Boost: scan the Safe categories and clean exactly what that scan found. Every delete is re-verified by the engine and logged.
    // Returns null when nothing was found or the scan was stopped (a partial scan is never cleaned).
    // progress (optional): 0..1 of the files cleaned so far, for the Boost bar.
    public CleanupResult? RunAutomaticBoost(CancellationToken token, IProgress<double>? progress = null)
    {
        using var background = LowResourceMode.BackgroundScope();
        var scan = engine.Scan(null, token, new(IsSafeTarget, Background: true));
        if (scan.Stopped) return null;
        var items = scan.Items.Where(item => item.Selected && item.Safety == SafetyLevel.Safe).ToList();
        if (items.Count == 0) return null;
        return engine.Clean(items, progress == null ? null : new Progress<int>(done => progress.Report(Math.Min(1.0, done / (double)items.Count))), token);
    }
}
