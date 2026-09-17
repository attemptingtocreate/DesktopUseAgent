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
    public void ResponseBuilder_Writes_Path_And_Omits_Base64_By_Default()
    {
        var vision = new GdiVisionCaptureProvider();
        var result = vision.CaptureRegion(0, 0, 64, 48, "compact");
        var payload = VisionCaptureResponseBuilder.Build(result, maxWidth: 32, format: "jpeg", returnBase64: false);

        Assert.True(File.Exists(payload.Path));
        Assert.Null(payload.PngBase64);
        Assert.Equal(result.PngBytes.Length, payload.ByteLength);
        Assert.NotNull(payload.ThumbnailPath);
        Assert.True(File.Exists(payload.ThumbnailPath));
        Assert.Equal("image/jpeg", payload.ThumbnailMimeType);
        Assert.True(payload.ThumbnailWidth <= 32);
        Assert.True(payload.ThumbnailHeight <= 32);
    }

    [Fact]
    public void ResponseBuilder_Includes_Base64_When_Requested()
    {
        var vision = new GdiVisionCaptureProvider();
        var result = vision.CaptureRegion(0, 0, 16, 16, "inline");
        var payload = VisionCaptureResponseBuilder.Build(result, returnBase64: true);
        Assert.False(string.IsNullOrWhiteSpace(payload.PngBase64));
        Assert.Equal(Convert.ToBase64String(result.PngBytes), payload.PngBase64);
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
