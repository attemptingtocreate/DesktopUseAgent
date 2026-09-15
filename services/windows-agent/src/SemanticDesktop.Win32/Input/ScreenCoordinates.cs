using SemanticDesktop.Win32.Native;

namespace SemanticDesktop.Win32.Input;

public readonly record struct ScreenPoint(int X, int Y);

public readonly record struct VirtualScreenBounds(int Left, int Top, int Width, int Height);

public static class ScreenCoordinates
{
    /// <summary>
    /// Virtual desktop bounds in physical pixels (multi-monitor aware).
    /// </summary>
    public static VirtualScreenBounds GetVirtualScreen()
    {
        var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0)
        {
            width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            left = 0;
            top = 0;
        }

        return new VirtualScreenBounds(left, top, Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>
    /// Converts physical screen pixels to SendInput absolute normalized coords (0..65535)
    /// across the virtual desktop (DPI + multi-monitor safe).
    /// </summary>
    public static (int Nx, int Ny) ToAbsoluteNormalized(int physicalX, int physicalY, VirtualScreenBounds? bounds = null)
    {
        var screen = bounds ?? GetVirtualScreen();
        var nx = (int)Math.Round((physicalX - screen.Left) * 65535.0 / Math.Max(1, screen.Width - 1));
        var ny = (int)Math.Round((physicalY - screen.Top) * 65535.0 / Math.Max(1, screen.Height - 1));
        return (Clamp(nx, 0, 65535), Clamp(ny, 0, 65535));
    }

    /// <summary>
    /// Optional conversion from logical (DIP) coordinates using an explicit scale factor.
    /// </summary>
    public static ScreenPoint FromLogical(double logicalX, double logicalY, double dpiScale)
    {
        if (dpiScale <= 0)
        {
            dpiScale = 1.0;
        }

        return new ScreenPoint(
            (int)Math.Round(logicalX * dpiScale),
            (int)Math.Round(logicalY * dpiScale));
    }

    public static bool Contains(VirtualScreenBounds screen, int x, int y) =>
        x >= screen.Left && y >= screen.Top &&
        x < screen.Left + screen.Width &&
        y < screen.Top + screen.Height;

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;
}
