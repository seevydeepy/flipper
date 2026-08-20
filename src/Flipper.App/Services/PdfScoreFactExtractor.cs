using System.Runtime.InteropServices.WindowsRuntime;
using Flipper.Core.Library;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Flipper.App.Services;

public sealed class PdfScoreFactExtractor
{
    private const int OcrPixelWidth = 1600;

    public async Task<ScoreFacts> ExtractAsync(ScoreEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var embedded = ReadEmbedded(entry.DisplayFullPath);
        var lines = embedded.PageLines;
        if (!ScoreFactInference.HasUsefulPageText(entry.DisplayName, lines))
        {
            var ocrLines = await ReadOcrLinesAsync(entry.DisplayFullPath, cancellationToken);
            if (ocrLines.Count > 0)
            {
                lines = ocrLines;
            }
        }

        return ScoreFactInference.Infer(entry.DisplayName, embedded.Metadata, lines);
    }

    private static PdfEmbeddedText ReadEmbedded(string path)
    {
        try
        {
            return PdfEmbeddedTextReader.Read(path);
        }
        catch (Exception)
        {
            return new PdfEmbeddedText(default, []);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadOcrLinesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                return [];
            }

            var bytes = File.ReadAllBytes(path);
            using var bitmap = RenderForOcr(bytes);
            using var softwareBitmap = ToSoftwareBitmap(bitmap);
            var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
            return result.Lines
                .Select(line => line.Text?.Trim() ?? string.Empty)
                .Where(line => line.Length > 0)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static SKBitmap RenderForOcr(byte[] bytes)
    {
        var bitmap = PdfBitmapRenderer.Render(bytes, 0, OcrPixelWidth, useTiling: true);
        var maxDimension = checked((int)OcrEngine.MaxImageDimension);
        var largest = Math.Max(bitmap.Width, bitmap.Height);
        if (largest <= maxDimension)
        {
            return bitmap;
        }

        var scaledWidth = Math.Max(64, bitmap.Width * maxDimension / largest);
        bitmap.Dispose();
        return PdfBitmapRenderer.Render(bytes, 0, scaledWidth, useTiling: true);
    }

    private static SoftwareBitmap ToSoftwareBitmap(SKBitmap source)
    {
        using var converted = source.ColorType == SKColorType.Bgra8888
            ? null
            : source.Copy(SKColorType.Bgra8888);
        var pixels = converted ?? source;
        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            pixels.Width,
            pixels.Height,
            BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(pixels.Bytes.AsBuffer());
        return bitmap;
    }
}
