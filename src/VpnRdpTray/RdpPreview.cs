using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VpnRdpTray;

internal static class RdpPreview
{
    private const uint ClientOnly = 0x00000001;
    private const uint RenderFullContent = 0x00000002;
    private const uint PrintMessage = 0x0317;
    private const int PrintClientAndChildren = 0x00000004 | 0x00000008 | 0x00000010;
    private const uint AbortIfHung = 0x0002, ErrorOnExit = 0x0020;
    private const long MaxSourcePixels = 16_777_216;
    private const int MaxPreviewDimension = 1440;

    internal static Bitmap? Capture(nint handle, Size client, Size maximum)
    {
        if (handle == 0 || client.Width <= 0 || client.Height <= 0 ||
            (long)client.Width * client.Height > MaxSourcePixels || maximum.Width <= 0 || maximum.Height <= 0)
            return null;

        try
        {
            using var source = new Bitmap(client.Width, client.Height, PixelFormat.Format32bppRgb);
            if (!(PrintClient(handle, source) && !IsBlank(source)) &&
                !(PrintChildren(handle, source) && !IsBlank(source)))
                return null;

            double scale = Math.Min(1, Math.Min(
                Math.Min(maximum.Width, MaxPreviewDimension) / (double)client.Width,
                Math.Min(maximum.Height, MaxPreviewDimension) / (double)client.Height));
            if (scale == 1)
                return new Bitmap(source);

            int width = Math.Max(1, (int)Math.Floor(client.Width * scale));
            int height = Math.Max(1, (int)Math.Floor(client.Height * scale));
            var preview = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            try
            {
                using var graphics = Graphics.FromImage(preview);
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(source, 0, 0, width, height);
                return preview;
            }
            catch
            {
                preview.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static bool PrintClient(nint handle, Bitmap target)
    {
        using var graphics = Graphics.FromImage(target);
        var dc = graphics.GetHdc();
        try
        {
            // Print the control itself to an off-screen bitmap. Screen pixels could contain other windows.
            return PrintWindow(handle, dc, ClientOnly | RenderFullContent) || PrintWindow(handle, dc, ClientOnly);
        }
        finally { graphics.ReleaseHdc(dc); }
    }

    private static bool PrintChildren(nint handle, Bitmap target)
    {
        using var graphics = Graphics.FromImage(target);
        var dc = graphics.GetHdc();
        try
        {
            // Include only the control subtree and allow it to paint while its owner is minimized.
            return SendMessageTimeoutW(handle, PrintMessage, dc, PrintClientAndChildren,
                AbortIfHung | ErrorOnExit, 250, out _) != 0;
        }
        finally { graphics.ReleaseHdc(dc); }
    }

    private static bool IsBlank(Bitmap frame)
    {
        // A minimized compositor can report success but leave the target empty.
        const int samples = 20;
        for (int y = 0; y < samples; y++)
            for (int x = 0; x < samples; x++)
                if ((frame.GetPixel((frame.Width - 1) * x / (samples - 1),
                    (frame.Height - 1) * y / (samples - 1)).ToArgb() & 0x00ffffff) != 0)
                    return false;
        return true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint deviceContext, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
}
