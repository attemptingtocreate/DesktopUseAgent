using SemanticDesktop.Core.Errors;
using SemanticDesktop.Win32.Vision;

namespace SemanticDesktop.Win32.Tests;

public class VisionCaptureTests
{
    [Fact]
    public void CaptureRegion_Rejects_NonPositive_Size()
    {
        var vision = new GdiVisionCaptureProvider();
        var ex = Assert.Throws<ArgumentException>(() => vision.CaptureRegion(0, 0, 0, 10, null));
        Assert.Contains(ErrorCodes.InvalidArgument, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureRegion_Produces_Png_And_Metadata()
    {
        var vision = new GdiVisionCaptureProvider();
        var result = vision.CaptureRegion(0, 0, 32, 24, "uia_failed");
        Assert.True(result.PngBytes.Length > 32);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(32, result.Meta.Width);
        Assert.Equal(24, result.Meta.Height);
        Assert.Equal("uia_failed", result.Meta.VisionReason);
        Assert.Equal("gdi-local", result.Meta.Provider);
        Assert.True(result.PngBytes[0] == 0x89 && result.PngBytes[1] == 0x50); // PNG magic
    }

    [Fact]
    public void CaptureScreen_Works_For_Primary_Virtual_Desktop()
    {
        var vision = new GdiVisionCaptureProvider();
        var result = vision.CaptureScreen(null, null);
        Assert.True(result.Meta.Width > 0);
        Assert.True(result.Meta.Height > 0);
        Assert.Equal(GdiVisionCaptureProvider.DefaultVisionReason, result.Meta.VisionReason);
        Assert.True(result.PngBytes.Length > 100);
    }

    [Fact]
    public void CaptureWindow_Rejects_Invalid_Hwnd()
    {
        var vision = new GdiVisionCaptureProvider();
        Assert.Throws<ArgumentException>(() => vision.CaptureWindow(IntPtr.Zero, "win_0", null));
    }
}
