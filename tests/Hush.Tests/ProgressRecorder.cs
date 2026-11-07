using Hush;

internal sealed class ProgressRecorder(Action<ScanProgress>? callback = null) : IProgress<ScanProgress>
{
    internal List<ScanProgress> Reports { get; } = [];
    public void Report(ScanProgress value) { Reports.Add(value); callback?.Invoke(value); }
}
