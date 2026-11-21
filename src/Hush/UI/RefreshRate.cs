using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace Hush;

// Every animation runs at the monitor's own refresh rate (owner, November 20, 2026: "not 60 fps capped, but according to each Hz of the user's monitor: 165 Hz
// then cap it there"). WPF gives every Timeline a default of 60 frames a second, so a 120 or 165 Hz screen only ever got 60. Hush reads the refresh rate of
// every connected display once at start and makes the fastest one the default for all animations (the composition engine still never draws faster than the
// screen a window is on, so a 60 Hz screen next to a 165 Hz one simply stays at 60). This is the ONE place a frame rate is set (PerformanceRuleChecks).
internal static class RefreshRate
{
    internal const int Fallback = 60, Highest = 500;
    internal static int Applied { get; private set; }

    // Called once from App.OnStartup, before any animation exists (the default of a dependency property can only be overridden once per process).
    internal static void Apply()
    {
        if (Applied != 0) return;
        Applied = Clamp(Fastest());
        if (Applied == Fallback) return;   // WPF's own default already
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = (int?)Applied });
    }

    internal static int Clamp(int hz) => hz is >= 24 and <= Highest ? hz : Fallback;

    // The fastest current refresh rate among the attached displays (1 and 0 mean "the hardware default" to Windows: ignored).
    internal static int Fastest()
    {
        int fastest = 0;
        try
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            for (uint index = 0; EnumDisplayDevices(null, index, ref device, 0); index++, device.Size = Marshal.SizeOf<DisplayDevice>())
            {
                if ((device.StateFlags & 1 /* DISPLAY_DEVICE_ATTACHED_TO_DESKTOP */) == 0) continue;
                var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
                if (EnumDisplaySettings(device.DeviceName, -1 /* ENUM_CURRENT_SETTINGS */, ref mode) && mode.DisplayFrequency > 1)
                    fastest = Math.Max(fastest, mode.DisplayFrequency);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return fastest;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode info);
}
