using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VpnRdpTray;

internal sealed class RdpWindowGlow : IDisposable
{
    private const int WindowedExtentDip = 18;
    private const int FullScreenExtentDip = 5;
    private const int ExtendedFrameBounds = 9;
    private const int Popup = unchecked((int)0x80000000);
    private const int Layered = 0x80000, Transparent = 0x20, ToolWindow = 0x80, NoActivate = 0x8000000;
    private const uint KeepSize = 0x0001, KeepZOrder = 0x0004, KeepActivation = 0x0010;
    private const uint PerPixelAlpha = 2;
    private const int HideWindow = 0, ShowNoActivate = 8;
    private static readonly Color Blue = Color.FromArgb(63, 153, 225);
    private static readonly Color FullScreenBlue = Color.FromArgb(20, 136, 255);

    private readonly Form owner;
    private readonly Control content;
    private NativeWindow? overlay;
    private nint sourceDc, bitmap, originalBitmap;
    private Rectangle presentedBounds;
    private Size bitmapSize;
    private int bitmapExtent;
    private Color bitmapColor;
    private bool requested, inset, bitmapInset, updateQueued, shown, disposed;

    internal RdpWindowGlow(Form window, Control content)
    {
        owner = window;
        this.content = content;
        owner.Move += OwnerChanged;
        owner.Resize += OwnerChanged;
        owner.VisibleChanged += OwnerChanged;
        owner.DpiChanged += OwnerChanged;
        owner.HandleCreated += OwnerChanged;
        owner.HandleDestroyed += OwnerHandleDestroyed;
        owner.Disposed += OwnerDisposed;
    }

    internal void Refresh(bool visible, bool fullScreen = false)
    {
        requested = visible;
        inset = fullScreen;
        Synchronize();
    }

    private bool CanShow => requested && !disposed && !owner.Disposing && owner.IsHandleCreated &&
        owner.Visible && owner.WindowState == FormWindowState.Normal;

    private void OwnerChanged(object? sender, EventArgs args)
    {
        if (!CanShow)
        {
            Hide();
            return;
        }
        if (updateQueued)
            return;

        // Apply the final DWM frame position after WinForms finishes the move or resize.
        updateQueued = true;
        try
        {
            owner.BeginInvoke((Action)(() =>
            {
                updateQueued = false;
                Synchronize();
            }));
        }
        catch (InvalidOperationException) { updateQueued = false; }
    }

    private void Synchronize()
    {
        if (!CanShow)
        {
            Hide();
            return;
        }

        var frame = GetFrame();
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            Hide();
            return;
        }

        var bounds = inset ? frame : GetOverlayBounds(frame, owner.DeviceDpi);
        int extent = inset ? FullScreenExtent(owner.DeviceDpi) : frame.Left - bounds.Left;
        if (bounds.Width < 2 * extent || bounds.Height < 2 * extent)
        {
            Hide();
            return;
        }

        var color = inset ? FullScreenBlue : Blue;
        if (SystemInformation.HighContrast)
            color = SystemColors.Highlight;
        bool redraw = bitmapSize != bounds.Size || bitmapExtent != extent || bitmapInset != inset || bitmapColor != color;
        try
        {
            if (overlay is null)
            {
                overlay = new NativeWindow();
                overlay.CreateHandle(new CreateParams
                {
                    Parent = owner.Handle,
                    Style = Popup,
                    ExStyle = Layered | Transparent | ToolWindow | NoActivate
                });
            }

            if ((redraw && !RenderSurface(bounds.Size, extent, color)) || bitmap == 0)
            {
                Hide();
                return;
            }

            if (redraw || presentedBounds.Size != bounds.Size)
            {
                var destination = new NativePoint { X = bounds.X, Y = bounds.Y };
                var size = new NativePoint { X = bounds.Width, Y = bounds.Height };
                var source = new NativePoint();
                var blend = new BlendFunction { ConstantAlpha = 255, AlphaFormat = 1 };
                if (!UpdateLayeredWindow(overlay.Handle, 0, ref destination, ref size, sourceDc, ref source, 0, ref blend, PerPixelAlpha))
                {
                    presentedBounds = Rectangle.Empty;
                    Hide();
                    return;
                }
            }
            else if (presentedBounds.Location != bounds.Location &&
                !SetWindowPos(overlay.Handle, 0, bounds.X, bounds.Y, 0, 0, KeepSize | KeepZOrder | KeepActivation))
            {
                Hide();
                return;
            }

            presentedBounds = bounds;
            if (!shown)
            {
                ShowWindow(overlay.Handle, ShowNoActivate);
                shown = true;
            }
        }
        catch (Win32Exception)
        {
            DestroyWindow();
            Hide();
        }
    }

    private Rectangle GetFrame()
    {
        if (inset)
            return content.IsHandleCreated && content.Visible
                ? content.RectangleToScreen(content.ClientRectangle)
                : Rectangle.Empty;

        nint handle = owner.Handle;
        if (DwmGetWindowAttribute(handle, ExtendedFrameBounds, out var native, Marshal.SizeOf<NativeRect>()) >= 0 &&
            native.Right > native.Left && native.Bottom > native.Top)
            return Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);

        return GetWindowRect(handle, out native)
            ? Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom)
            : Rectangle.Empty;
    }

    internal static Rectangle GetOverlayBounds(Rectangle frame, int dpi)
    {
        int extent = ScaleExtent(WindowedExtentDip, dpi);
        frame.Inflate(extent, extent);
        return frame;
    }

    internal static int FullScreenExtent(int dpi) => ScaleExtent(FullScreenExtentDip, dpi);

    private static int ScaleExtent(int dips, int dpi) => Math.Max(dips, (int)Math.Ceiling(dips * dpi / 96.0));

    internal static int GlowPixel(double distance, int extent, Color color, bool inset = false)
    {
        if (distance <= 0 || distance >= extent)
            return 0;
        double fade = 1 - distance / extent;
        int peakAlpha = inset ? 220 : 115;
        int alpha = (int)Math.Round(peakAlpha * fade * fade * (3 - 2 * fade));
        return (alpha << 24) | (color.R * alpha / 255 << 16) |
            (color.G * alpha / 255 << 8) | (color.B * alpha / 255);
    }

    private bool RenderSurface(Size size, int extent, Color color)
    {
        ReleaseBitmap();
        if ((long)size.Width * size.Height > int.MaxValue / 4)
            return false;

        if (sourceDc == 0)
            sourceDc = CreateCompatibleDC(0);
        if (sourceDc == 0)
            return false;

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = size.Width,
                Height = -size.Height, // Top-down BGRA.
                Planes = 1,
                BitCount = 32
            }
        };
        bitmap = CreateDIBSection(sourceDc, in info, 0, out nint bits, 0, 0);
        if (bitmap == 0 || bits == 0)
        {
            ReleaseBitmap();
            return false;
        }

        originalBitmap = SelectObject(sourceDc, bitmap);
        if (originalBitmap == 0 || originalBitmap == -1)
        {
            ReleaseBitmap();
            return false;
        }

        WriteGlow(bits, size.Width, size.Height, extent, color, inset);
        bitmapSize = size;
        bitmapExtent = extent;
        bitmapColor = color;
        bitmapInset = inset;
        return true;
    }

    internal static void WriteGlow(nint bits, int width, int height, int extent, Color color, bool inset)
    {
        // The center is transparent in both modes. Only the edge rows and columns are painted.
        var sideRow = new int[width];
        var cornerRow = new int[width];
        double Distance(int index) => inset ? index + 0.5 : extent - index - 0.5;
        for (int x = 0; x < extent; x++)
            sideRow[x] = sideRow[width - x - 1] = GlowPixel(Distance(x), extent, color, inset);

        int stride = width * sizeof(int);
        for (int y = extent; y < height - extent; y++)
            Marshal.Copy(sideRow, 0, bits + y * stride, width);

        for (int y = 0; y < extent; y++)
        {
            double dy = Distance(y);
            Array.Fill(cornerRow, GlowPixel(dy, extent, color, inset), extent, width - 2 * extent);
            for (int x = 0; x < extent; x++)
            {
                double dx = Distance(x);
                double distance = inset ? Math.Min(dx, dy) : Math.Sqrt(dx * dx + dy * dy);
                cornerRow[x] = cornerRow[width - x - 1] = GlowPixel(distance, extent, color, inset);
            }
            Marshal.Copy(cornerRow, 0, bits + y * stride, width);
            Marshal.Copy(cornerRow, 0, bits + (height - y - 1) * stride, width);
        }
    }

    private void Hide()
    {
        if (shown && overlay is { Handle: not 0 })
            ShowWindow(overlay.Handle, HideWindow);
        shown = false;
    }

    private void DestroyWindow()
    {
        if (overlay is { Handle: not 0 })
            overlay.DestroyHandle();
        overlay = null;
        presentedBounds = Rectangle.Empty;
        shown = false;
    }

    private void ReleaseBitmap()
    {
        if (originalBitmap != 0 && originalBitmap != -1)
            SelectObject(sourceDc, originalBitmap);
        if (bitmap != 0)
            DeleteObject(bitmap);
        bitmap = originalBitmap = 0;
        bitmapSize = Size.Empty;
    }

    private void OwnerHandleDestroyed(object? sender, EventArgs args)
    {
        updateQueued = false;
        DestroyWindow();
    }

    private void OwnerDisposed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        owner.Move -= OwnerChanged;
        owner.Resize -= OwnerChanged;
        owner.VisibleChanged -= OwnerChanged;
        owner.DpiChanged -= OwnerChanged;
        owner.HandleCreated -= OwnerChanged;
        owner.HandleDestroyed -= OwnerHandleDestroyed;
        owner.Disposed -= OwnerDisposed;
        DestroyWindow();
        ReleaseBitmap();
        if (sourceDc != 0)
        {
            DeleteDC(sourceDc);
            sourceDc = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction { public byte Operation, Flags, ConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, int attribute, out NativeRect value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint handle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(nint handle, nint destinationDc, ref NativePoint destination,
        ref NativePoint size, nint sourceDc, ref NativePoint source, int colorKey, ref BlendFunction blend, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, in BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);
}
