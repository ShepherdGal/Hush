using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Hush;

// The notification-area icon behind "Minimize to tray" (0.9, user request). Behind an interface so tests never put a real icon next to the clock.
internal interface ITrayIcon : IDisposable
{
    bool Visible { get; set; }
}

// A plain Shell_NotifyIcon icon with a hidden message-only window: no extra library, no timer, no polling (Windows sends a message when the icon is used).
// Left click opens Hush; right click shows a small native menu (Open Hush, Quit Hush). The icon is Hush's own, read from the running exe.
internal sealed class TrayIcon : ITrayIcon
{
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, CallbackMessage = 0x8000 + 41;
    private const int WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_CONTEXTMENU = 0x7B;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, TPM_RIGHTBUTTON = 2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;
    private const int OpenId = 1, QuitId = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Item; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private readonly Action open, quit;
    private readonly string tip;
    private HwndSource? source;
    private IntPtr icon;
    private uint taskbarCreated;
    private bool visible, added;

    internal TrayIcon(string exePath, Action open, Action quit, string tip = "Hush")
    {
        this.open = open; this.quit = quit; this.tip = tip;
        var small = new IntPtr[1];
        try { if (ExtractIconExW(exePath, 0, null, small, 1) > 0) icon = small[0]; } catch { }
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    public bool Visible
    {
        get => visible;
        set { if (visible == value) return; visible = value; if (value) Add(); else Remove(); }
    }

    private NotifyIconData Data() => new() { Size = Marshal.SizeOf<NotifyIconData>(), Window = source!.Handle, Id = 1, Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP, CallbackMessage = CallbackMessage, Icon = icon, Tip = tip, Info = "", InfoTitle = "" };

    private void Add()
    {
        source ??= new HwndSource(new HwndSourceParameters("Hush tray") { ParentWindow = new IntPtr(-3), WindowStyle = 0 }); // message-only window: invisible, no taskbar button
        source.AddHook(WndProc);
        var data = Data();
        added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private void Remove()
    {
        if (source == null) return;
        if (added) { var data = Data(); Shell_NotifyIconW(NIM_DELETE, ref data); added = false; }
        source.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == taskbarCreated && visible) { var data = Data(); added = Shell_NotifyIconW(NIM_ADD, ref data); } // Explorer restarted: put the icon back
        else if ((uint)message == CallbackMessage)
        {
            int mouse = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouse == WM_LBUTTONUP) { handled = true; open(); }
            else if (mouse is WM_RBUTTONUP or WM_CONTEXTMENU) { handled = true; ShowMenu(window); }
        }
        return IntPtr.Zero;
    }

    private void ShowMenu(IntPtr window)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MF_STRING, OpenId, "Open Hush");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, QuitId, "Quit Hush");
            GetCursorPos(out var at);
            SetForegroundWindow(window); // without this the menu would not close when the person clicks elsewhere
            int command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, at.X, at.Y, 0, window, IntPtr.Zero);
            PostMessageW(window, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL: the documented companion of SetForegroundWindow for tray menus
            if (command == OpenId) open(); else if (command == QuitId) quit();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        Visible = false;
        source?.Dispose(); source = null;
        if (icon != IntPtr.Zero) { DestroyIcon(icon); icon = IntPtr.Zero; }
    }
}
