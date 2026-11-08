using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hush;

// Settings > Interface size: Smart, 75%, 100% or 120%. Scales the whole window content together instead of making people resize the window.
public partial class MainWindow
{
    private double uiScale = 1;
    private bool uiSizeLoading;
    internal double UiScaleValue => uiScale;

    private void InitializeUiSize()
    {
        prefs.UiSize = UiScale.Normalize(prefs.UiSize);
        // Smart sizing belongs to the real app window; windows hosted by the test runner stay at 100% unless a size was chosen on purpose.
        SourceInitialized += (_, _) =>
        {
            if (Application.Current?.MainWindow == this || prefs.UiSize != UiScale.Smart) ApplyUiScale(resizeWindow: true);
            else RefreshUiSizeChoices();
        };
        RefreshUiSizeChoices();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    // The work area (in DIPs) of the monitor this window is on, so a small second screen is sized for itself; the primary screen's is the fallback.
    private Rect CurrentWorkArea()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(MonitorFromWindow(handle, 2 /* nearest */), ref info))
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    return new Rect(info.Work.Left / dpi.DpiScaleX, info.Work.Top / dpi.DpiScaleY,
                        (info.Work.Right - info.Work.Left) / dpi.DpiScaleX, (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
                }
            }
        }
        catch { /* fall back to the primary screen */ }
        return SystemParameters.WorkArea;
    }

    // Applies the saved choice. Layout is built at 100%; this multiplies the content, sets the smallest allowed window size and (when asked) the window size.
    internal void ApplyUiScale(bool resizeWindow)
    {
        var work = CurrentWorkArea();
        double scale = UiScale.Resolve(prefs.UiSize, work);
        double previous = uiScale;
        uiScale = scale;
        WindowSurface.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        var min = UiScale.MinimumSize(scale, work);
        MinWidth = min.Width; MinHeight = min.Height;
        if (resizeWindow)
        {
            var size = UiScale.WindowSize(scale, work);
            if (IsVisible && !double.IsNaN(Left))
            {
                // Keep the window's centre where it was, then pull it back inside the screen.
                double centreX = Left + ActualWidth / 2, centreY = Top + ActualHeight / 2;
                Width = size.Width; Height = size.Height;
                Left = Math.Clamp(centreX - size.Width / 2, work.Left, Math.Max(work.Left, work.Right - size.Width));
                Top = Math.Clamp(centreY - size.Height / 2, work.Top, Math.Max(work.Top, work.Bottom - size.Height));
            }
            else
            {
                // Not shown yet: WPF may already have centred the design-size window, so centre the new size explicitly (never partly off-screen).
                Width = size.Width; Height = size.Height;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = work.Left + (work.Width - size.Width) / 2;
                Top = work.Top + (work.Height - size.Height) / 2;
            }
        }
        else if (previous != scale)
        {
            Width = Math.Max(Width, min.Width); Height = Math.Max(Height, min.Height);
        }
        RefreshUiSizeChoices();
    }

    private void RefreshUiSizeChoices()
    {
        if (UiSizeSummary == null) return;
        var work = CurrentWorkArea();
        double resolved = UiScale.Resolve(prefs.UiSize, work);
        double requested = UiScale.Requested(prefs.UiSize, work);
        uiSizeLoading = true;
        try
        {
            foreach (var chip in new[] { UiSizeSmart, UiSize75, UiSize100, UiSize120 }) chip.IsChecked = (string)chip.Tag == prefs.UiSize;
        }
        finally { uiSizeLoading = false; }
        // In a window that was never shown (and so never scaled) report what the choice would give on this screen.
        double shown = IsLoaded || uiScale != 1 ? uiScale : resolved;
        UiSizeSummary.Text = UiScale.Label(prefs.UiSize, shown);
        UiSizeNote.Text = resolved < requested - 0.001
            ? $"{requested * 100:0}% would not fit this screen, so Hush is showing {resolved * 100:0}%, the largest size that does."
            : prefs.UiSize == UiScale.Smart
                ? "Smart picks a size that fits this screen and keeps text easy to read."
                : "The window grows or shrinks with this choice. You can still drag its edges.";
    }

    private void UiSize_Click(object sender, RoutedEventArgs e)
    {
        if (uiSizeLoading || sender is not RadioButton { Tag: string choice }) return;
        prefs.UiSize = UiScale.Normalize(choice);
        SavePreferences();
        ApplyUiScale(resizeWindow: true);
    }
}
