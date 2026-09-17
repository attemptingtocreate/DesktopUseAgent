using System.Diagnostics;
using SemanticDesktop.Core.Errors;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace SemanticDesktop.Win32.Vision;

public sealed class OcrLineResult
{
    public required string Text { get; init; }
    public double? Confidence { get; init; }
    public OcrBounds? Bounds { get; init; }
}

public sealed class OcrBounds
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public sealed class OcrResult
{
    public required IReadOnlyList<OcrLineResult> Lines { get; init; }
    public required string Text { get; init; }
    public required long DurationMs { get; init; }
    public required string Provider { get; init; }
    public string? Language { get; init; }
}

/// <summary>Windows.Media.Ocr wrapper. Throws clearly when WinRT OCR is unavailable.</summary>
public static class WindowsOcrService
{
    public const string ProviderId = "windows.media.ocr";

    public static bool IsAvailable()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages() is not null;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<OcrResult> RecognizePngAsync(byte[] pngBytes, string? languageTag, CancellationToken cancellationToken)
    {
        if (pngBytes is null || pngBytes.Length == 0)
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: image bytes are required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.StartNew();
        var engine = CreateEngine(languageTag);

        using var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(pngBytes);
        await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        writer.DetachStream();
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied).AsTask(cancellationToken).ConfigureAwait(false);

        using (bitmap)
        {
            var ocr = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            var lines = new List<OcrLineResult>();
            foreach (var line in ocr.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = line.Text ?? "";
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                OcrBounds? bounds = null;
                try
                {
                    var words = line.Words;
                    if (words is { Count: > 0 })
                    {
                        var rects = words.Select(w => w.BoundingRect).ToList();
                        var left = rects.Min(r => r.X);
                        var top = rects.Min(r => r.Y);
                        var right = rects.Max(r => r.X + r.Width);
                        var bottom = rects.Max(r => r.Y + r.Height);
                        bounds = new OcrBounds
                        {
                            X = left,
                            Y = top,
                            Width = Math.Max(0, right - left),
                            Height = Math.Max(0, bottom - top)
                        };
                    }
                }
                catch
                {
                    // bounds optional
                }

                lines.Add(new OcrLineResult
                {
                    Text = text,
                    Confidence = null,
                    Bounds = bounds
                });
            }

            var fullText = string.IsNullOrWhiteSpace(ocr.Text)
                ? string.Join(Environment.NewLine, lines.Select(l => l.Text))
                : ocr.Text;

            return new OcrResult
            {
                Lines = lines,
                Text = fullText,
                DurationMs = started.ElapsedMilliseconds,
                Provider = ProviderId,
                Language = engine.RecognizerLanguage?.LanguageTag
            };
        }
    }

    private static OcrEngine CreateEngine(string? languageTag)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(languageTag))
            {
                var lang = new Windows.Globalization.Language(languageTag.Trim());
                var tagged = OcrEngine.TryCreateFromLanguage(lang);
                if (tagged is not null)
                {
                    return tagged;
                }
            }

            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                throw new InvalidOperationException(
                    $"{ErrorCodes.Unsupported}: Windows.Media.Ocr is unavailable (no OCR language packs or WinRT OCR not supported on this host).");
            }

            return engine;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{ErrorCodes.Unsupported}: Windows.Media.Ocr is unavailable: {ex.Message}", ex);
        }
    }
}
