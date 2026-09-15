using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Win32.Input;
using SemanticDesktop.Win32.Native;

namespace SemanticDesktop.Win32.Vision;

/// <summary>
/// Local GDI/BitBlt capture provider. Analysis/LLM providers can wrap or replace this
/// via <see cref="IVisionCaptureProvider"/> without hardcoding a vision model.
/// </summary>
public sealed class GdiVisionCaptureProvider : IVisionCaptureProvider
{
    public const string DefaultVisionReason = "semantic_interfaces_failed";

    public string Id => "gdi-local";

    public VisionCaptureResult CaptureScreen(int? monitorIndex, string? visionReason)
    {
        var screen = ScreenCoordinates.GetVirtualScreen();
        if (monitorIndex is int idx)
        {
            var monitors = EnumerateMonitors();
            if (idx < 0 || idx >= monitors.Count)
            {
                throw new ArgumentException($"{ErrorCodes.InvalidArgument}: monitor index {idx} out of range (0..{monitors.Count - 1}).");
            }

            var m = monitors[idx];
            return CaptureRect(m.Left, m.Top, m.Width, m.Height, idx, null, visionReason);
        }

        return CaptureRect(screen.Left, screen.Top, screen.Width, screen.Height, null, null, visionReason);
    }

    public VisionCaptureResult CaptureWindow(IntPtr hwnd, string? windowId, string? visionReason)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            throw new ArgumentException($"{ErrorCodes.NotFound}: window handle is invalid.");
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            throw new InvalidOperationException($"{ErrorCodes.VisionFailed}: GetWindowRect failed.");
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var monitor = MonitorIndexFromRect(rect);
        var scale = GetScaleForWindow(hwnd);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdcDest = g.GetHdc();
            try
            {
                // Prefer PrintWindow for occluded/composited windows; fall back to BitBlt.
                if (!NativeMethods.PrintWindow(hwnd, hdcDest, NativeMethods.PW_RENDERFULLCONTENT))
                {
                    var hdcSrc = NativeMethods.GetDC(hwnd);
                    if (hdcSrc == IntPtr.Zero)
                    {
                        throw new InvalidOperationException($"{ErrorCodes.VisionFailed}: GetDC failed.");
                    }

                    try
                    {
                        if (!NativeMethods.BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, NativeMethods.SRCCOPY))
                        {
                            throw new InvalidOperationException($"{ErrorCodes.VisionFailed}: BitBlt failed.");
                        }
                    }
                    finally
                    {
                        NativeMethods.ReleaseDC(hwnd, hdcSrc);
                    }
                }
            }
            finally
            {
                g.ReleaseHdc(hdcDest);
            }
        }

        return ToResult(bitmap, monitor, windowId, scale, rect.Left, rect.Top, visionReason);
    }

    public VisionCaptureResult CaptureRegion(int x, int y, int width, int height, string? visionReason)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: width and height must be positive.");
        }

        if (width > 10000 || height > 10000)
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: capture region too large.");
        }

        var rect = new NativeMethods.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        var monitor = MonitorIndexFromRect(rect);
        return CaptureRect(x, y, width, height, monitor, null, visionReason);
    }

    private VisionCaptureResult CaptureRect(
        int left,
        int top,
        int width,
        int height,
        int? monitor,
        string? windowId,
        string? visionReason)
    {
        var hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
        {
            throw new InvalidOperationException($"{ErrorCodes.VisionFailed}: GetDC(desktop) failed.");
        }

        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr old = IntPtr.Zero;
        try
        {
            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, width, height);
            if (hdcMem == IntPtr.Zero || hBitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException($"{ErrorCodes.VisionFailed}: CreateCompatibleBitmap failed.");
            }

            old = NativeMethods.SelectObject(hdcMem, hBitmap);
            if (!NativeMethods.BitBlt(hdcMem, 0, 0, width, height, hdcScreen, left, top, NativeMethods.SRCCOPY))
            {
                throw new InvalidOperationException($"{ErrorCodes.VisionFailed}: BitBlt failed.");
            }

            using var bitmap = Image.FromHbitmap(hBitmap);
            var scale = monitor is int mIdx ? GetScaleForMonitorIndex(mIdx) : 1.0;
            return ToResult(bitmap, monitor, windowId, scale, left, top, visionReason);
        }
        finally
        {
            if (old != IntPtr.Zero && hdcMem != IntPtr.Zero)
            {
                NativeMethods.SelectObject(hdcMem, old);
            }

            if (hBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hBitmap);
            }

            if (hdcMem != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(hdcMem);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private VisionCaptureResult ToResult(
        Image bitmap,
        int? monitor,
        string? windowId,
        double scale,
        int left,
        int top,
        string? visionReason)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return new VisionCaptureResult
        {
            PngBytes = ms.ToArray(),
            Meta = new VisionCaptureMeta
            {
                Monitor = monitor,
                WindowId = windowId,
                Scale = scale,
                Width = bitmap.Width,
                Height = bitmap.Height,
                Timestamp = DateTimeOffset.UtcNow,
                VisionReason = string.IsNullOrWhiteSpace(visionReason) ? DefaultVisionReason : visionReason,
                Provider = Id,
                Left = left,
                Top = top
            }
        };
    }

    private static double GetScaleForWindow(IntPtr hwnd)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return GetScaleForMonitor(monitor);
    }

    private static double GetScaleForMonitorIndex(int index)
    {
        var monitors = EnumerateMonitors();
        if (index < 0 || index >= monitors.Count)
        {
            return 1.0;
        }

        return GetScaleForMonitor(monitors[index].Handle);
    }

    private static double GetScaleForMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            return 1.0;
        }

        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (DllNotFoundException)
        {
            // older platforms
        }

        return 1.0;
    }

    private static int? MonitorIndexFromRect(NativeMethods.RECT rect)
    {
        var monitor = NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitors = EnumerateMonitors();
        for (var i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].Handle == monitor)
            {
                return i;
            }
        }

        return null;
    }

    private static List<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        NativeMethods.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (hMonitor, _, _, _) =>
            {
                var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    list.Add(new MonitorInfo(
                        hMonitor,
                        info.rcMonitor.Left,
                        info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top));
                }

                return true;
            },
            IntPtr.Zero);
        return list;
    }

    private readonly record struct MonitorInfo(IntPtr Handle, int Left, int Top, int Width, int Height);
}
