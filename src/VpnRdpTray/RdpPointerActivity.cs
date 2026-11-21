using System.Runtime.InteropServices;

namespace VpnRdpTray;

internal sealed class RdpPointerActivity
{
    internal const int MouseMove = 0x0200;
    private (Point Point, Size Desktop)? anchor;
    private bool nudged;

    internal void Observe(nint host, Size client, Size desktop, in Message message)
    {
        if (message.Msg != MouseMove || !BelongsToControl(host, message.HWnd))
            return;
        if (message.WParam != 0)
        {
            Reset();
            return;
        }

        long coordinates = message.LParam.ToInt64();
        var point = new NativePoint { X = unchecked((short)coordinates), Y = unchecked((short)(coordinates >> 16)) };
        if (message.HWnd != host && (!ClientToScreen(message.HWnd, ref point) || !ScreenToClient(host, ref point)))
        {
            Reset();
            return;
        }

        anchor = Project(new Point(point.X, point.Y), client, desktop) is { } projected ? (projected, desktop) : null;
        nudged = false;
    }

    internal void Reset() => anchor = null;

    private static Point? Project(Point point, Size client, Size desktop)
    {
        if (desktop.Width <= 0 || desktop.Height <= 0 || !new Rectangle(Point.Empty, client).Contains(point))
            return null;
        return new Point((int)((long)point.X * desktop.Width / client.Width),
            (int)((long)point.Y * desktop.Height / client.Height));
    }

    internal void Move(Size desktop, Func<Point, bool> send)
    {
        if (anchor is not { } sample)
            return;
        if (sample.Desktop != desktop)
        {
            Reset();
            return;
        }

        // Stay near the last user position and away from desktop edges.
        var point = sample.Point;
        if (point.X < 2 || point.Y < 2 || point.X >= desktop.Width - 2 || point.Y >= desktop.Height - 2)
            return;
        if (send(new Point(nudged ? point.X : point.X + 1, point.Y)))
            nudged = !nudged;
    }

    private static bool BelongsToControl(nint host, nint window) =>
        host != 0 && window != 0 && (host == window || IsChild(host, window));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref NativePoint point);
}
