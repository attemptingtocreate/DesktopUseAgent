using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Serialization;

namespace SemanticDesktop.Win32.Vision;

/// <summary>
/// Builds MCP/agent vision payloads: prefer on-disk PNG + optional JPEG thumbnail
/// instead of shipping full pngBase64 to the LLM by default.
/// </summary>
public static class VisionCaptureResponseBuilder
{
    public const int DefaultMaxWidth = 1280;
    public const string DefaultThumbnailFormat = "jpeg";

    public static VisionCapturePayload Build(
        VisionCaptureResult capture,
        int? maxWidth = null,
        string? format = null,
        bool returnBase64 = false)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var dir = Path.Combine(Path.GetTempPath(), "DesktopUseAgent", "vision");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString("N");
        var pngPath = Path.Combine(dir, $"{id}.png");
        File.WriteAllBytes(pngPath, capture.PngBytes);

        string? thumbPath = null;
        string? thumbMime = null;
        int? thumbWidth = null;
        int? thumbHeight = null;
        long? thumbBytes = null;

        var longEdge = maxWidth is > 0 ? maxWidth.Value : DefaultMaxWidth;
        var thumbFormat = string.IsNullOrWhiteSpace(format) ? DefaultThumbnailFormat : format.Trim().ToLowerInvariant();

        try
        {
            using var full = Image.FromStream(new MemoryStream(capture.PngBytes));
            var (tw, th) = FitLongEdge(full.Width, full.Height, longEdge);
            if (tw < full.Width || th < full.Height || string.Equals(thumbFormat, "jpeg", StringComparison.Ordinal) ||
                string.Equals(thumbFormat, "jpg", StringComparison.Ordinal))
            {
                using var thumb = new Bitmap(tw, th);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(full, 0, 0, tw, th);
                }

                if (thumbFormat is "png")
                {
                    thumbPath = Path.Combine(dir, $"{id}.thumb.png");
                    thumb.Save(thumbPath, ImageFormat.Png);
                    thumbMime = "image/png";
                }
                else
                {
                    thumbPath = Path.Combine(dir, $"{id}.thumb.jpg");
                    SaveJpeg(thumb, thumbPath, 82L);
                    thumbMime = "image/jpeg";
                }

                thumbWidth = tw;
                thumbHeight = th;
                thumbBytes = new FileInfo(thumbPath).Length;
            }
        }
        catch
        {
            // Thumbnail is best-effort; full PNG path is still returned.
            thumbPath = null;
            thumbMime = null;
            thumbWidth = null;
            thumbHeight = null;
            thumbBytes = null;
        }

        return new VisionCapturePayload
        {
            MimeType = capture.MimeType,
            Path = pngPath,
            ByteLength = capture.PngBytes.Length,
            Width = capture.Meta.Width,
            Height = capture.Meta.Height,
            ThumbnailPath = thumbPath,
            ThumbnailMimeType = thumbMime,
            ThumbnailByteLength = thumbBytes,
            ThumbnailWidth = thumbWidth,
            ThumbnailHeight = thumbHeight,
            PngBase64 = returnBase64 ? Convert.ToBase64String(capture.PngBytes) : null,
            Meta = capture.Meta
        };
    }

    private static (int Width, int Height) FitLongEdge(int width, int height, int maxLongEdge)
    {
        if (width <= 0 || height <= 0 || maxLongEdge <= 0)
        {
            return (Math.Max(1, width), Math.Max(1, height));
        }

        var longEdge = Math.Max(width, height);
        if (longEdge <= maxLongEdge)
        {
            return (width, height);
        }

        var scale = (double)maxLongEdge / longEdge;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static void SaveJpeg(Image image, string path, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (codec is null)
        {
            image.Save(path, ImageFormat.Jpeg);
            return;
        }

        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        image.Save(path, codec, ep);
    }
}

public sealed class VisionCapturePayload
{
    public required string MimeType { get; init; }
    public required string Path { get; init; }
    public required long ByteLength { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public string? ThumbnailPath { get; init; }
    public string? ThumbnailMimeType { get; init; }
    public long? ThumbnailByteLength { get; init; }
    public int? ThumbnailWidth { get; init; }
    public int? ThumbnailHeight { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PngBase64 { get; init; }
    public required VisionCaptureMeta Meta { get; init; }
}
