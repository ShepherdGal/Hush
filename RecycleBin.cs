using System.IO;
using System.Runtime.InteropServices;

namespace Hush;

internal readonly record struct RecycleBinInfo(long Bytes, long Items);

// The Recycle Bin is only touched through the documented shell API; tests substitute a fake.
internal interface IRecycleBin
{
    RecycleBinInfo Query();
    void Empty();
}

internal sealed class ShellRecycleBin : IRecycleBin
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct QueryInfo { public uint Size; public long Bytes; public long Items; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? root, ref QueryInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr window, string? root, uint flags);
    private const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;

    public RecycleBinInfo Query()
    {
        var info = new QueryInfo { Size = (uint)Marshal.SizeOf<QueryInfo>() };
        return SHQueryRecycleBinW(null, ref info) < 0 ? default : new(info.Bytes, info.Items);
    }

    public void Empty()
    {
        int result = SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        // Windows reports an error for an already-empty bin; that is a success for us.
        if (result < 0 && Query().Items != 0) throw new IOException($"Windows could not empty the Recycle Bin (0x{result:X8}).");
    }
}
