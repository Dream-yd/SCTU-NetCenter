using System.Runtime.InteropServices;

namespace NetCenter;

internal static class TaskbarInset
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Rectangle;
        public IntPtr Parameter;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);

    public static double Bottom(double screenBottom, double workBottom, double dpiScale)
    {
        try
        {
            var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
            var hidden = (SHAppBarMessage(4, ref data).ToUInt64() & 1) != 0;
            if (!hidden || SHAppBarMessage(5, ref data) == UIntPtr.Zero) return 0;
            return Calculate(hidden, data.Edge, data.Rectangle.Bottom - data.Rectangle.Top, dpiScale, screenBottom, workBottom);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return 0; }
    }

    public static double Calculate(bool hidden, uint edge, int heightPx, double dpiScale, double screenBottom, double workBottom)
    {
        var existingInset = Math.Max(0, screenBottom - workBottom);
        if (!hidden || edge != 3 || existingInset > 3) return 0;
        var pixels = heightPx is > 3 and <= 200 ? heightPx : 48;
        return Math.Max(0, pixels / (dpiScale > 0 ? dpiScale : 1) - existingInset);
    }
}
