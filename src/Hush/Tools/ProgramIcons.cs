using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hush;

// Tools > Programs shows each program's own icon (owner, November 19, 2026: "programs to be uninstalled should have icons, more life to it"). The icon is the one
// the program registered for Settings > Apps (its Uninstall entry's DisplayIcon: "C:\path\app.exe,0" or an .ico file); it is read on a worker thread after the
// list is on screen, one small 32 px picture per program, frozen. A program without a readable icon gets a letter tile in a soft color picked from its name
// (owner's pick, November 20, 2026), so each one still looks different. Reading an icon never runs the program.
internal static class ProgramIcons
{
    // Test seam: the test runner can replace the reader (the product reads the real files).
    internal static Func<string, ImageSource?> Loader = Load;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);

    internal static ImageSource? Load(string displayIcon)
    {
        string text = displayIcon.Trim();
        int index = 0, comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out int parsed)) { index = parsed; text = text[..comma]; }
        text = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(text) || !File.Exists(text)) return null;
        var large = new IntPtr[1];
        try
        {
            if (ExtractIconEx(text, index, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or IOException or UnauthorizedAccessException or ExternalException) { return null; }
        finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); }
    }

    // Soft tile colors, all in the blue-to-violet range: never the risk colors (green, amber) and never anything near the one Hush red.
    private static readonly Brush[] Tiles = new[] { "#5B8DEF", "#7C6CF2", "#3FA9D9", "#8A7FD8", "#4F8FC7", "#6C7FE4", "#5A9FD6", "#9A86E8" }
        .Select(hex => { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return (Brush)brush; }).ToArray();

    // The same name always gets the same color (a stable hash, not string.GetHashCode, which changes per run).
    internal static Brush TileFor(string name)
    {
        uint hash = 2166136261;
        foreach (char letter in name.ToUpperInvariant()) hash = (hash ^ letter) * 16777619;
        return Tiles[hash % (uint)Tiles.Length];
    }
}
