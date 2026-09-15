namespace SemanticDesktop.Win32.Vision;

public interface IVisionCaptureProvider
{
    string Id { get; }

    VisionCaptureResult CaptureScreen(int? monitorIndex, string? visionReason);

    VisionCaptureResult CaptureWindow(IntPtr hwnd, string? windowId, string? visionReason);

    VisionCaptureResult CaptureRegion(int x, int y, int width, int height, string? visionReason);
}

public sealed class VisionCaptureResult
{
    public required byte[] PngBytes { get; init; }
    public string MimeType { get; init; } = "image/png";
    public required VisionCaptureMeta Meta { get; init; }
}

public sealed class VisionCaptureMeta
{
    public int? Monitor { get; init; }
    public string? WindowId { get; init; }
    public double Scale { get; init; } = 1.0;
    public int Width { get; init; }
    public int Height { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? VisionReason { get; init; }
    public required string Provider { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
}
