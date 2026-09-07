using System.Runtime.InteropServices.WindowsRuntime;
using Flipper.Core.Library;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Flipper.App.Services;

/// <summary>OCR ran, or why it did not.</summary>
public enum OcrOutcome
{
    NotNeeded,
    Success,
    UnavailableEngine,
    UnsupportedLanguage,
    NoReadableText,
    Failed,
    Cancelled
}

public sealed record ScoreExtractionResult(
    ScoreFacts Facts,
    ScoreExtractionOutcome Extraction,
    OcrOutcome Ocr,
    string? Detail = null,
    ExtractionStatus Status = ExtractionStatus.Partial);

public sealed class PdfScoreFactExtractor
{
    private const int OcrPixelWidth = 1600;

    /// <summary>
    /// Configurable budgets: early pages inspected, OCR pages rendered, and the
    /// overall extraction timebox. Defaults cover a cover-page + 2 music pages.
    /// </summary>
    public int MaxEmbeddedPages { get; init; } = 3;
    public int MaxOcrPages { get; init; } = 2;
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(30);

    public static readonly IReadOnlyList<string> PreferredOcrLanguages =
        ["en", "fr", "de", "it", "es"];

    public async Task<ScoreFacts> ExtractAsync(ScoreEntry entry, CancellationToken cancellationToken)
    {
        return (await ExtractWithStatusAsync(entry, cancellationToken)).Facts;
    }

    public async Task<ScoreExtractionResult> ExtractWithStatusAsync(
        ScoreEntry entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = new CancellationTokenSource(TimeBudget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var token = linked.Token;

        var embedded = PdfEmbeddedTextReader.ReadRich(entry.DisplayFullPath, MaxEmbeddedPages);
        if (embedded.Outcome == ScoreExtractionOutcome.Cancelled)
        {
            token.ThrowIfCancellationRequested();
        }

        var ocrOutcome = OcrOutcome.NotNeeded;
        IReadOnlyList<ScoreTextLine> lines = embedded.Lines;
        string? detail = embedded.Detail;
        if (NeedsOcr(entry.DisplayName, embedded))
        {
            var ocr = await ReadOcrLinesAsync(entry.DisplayFullPath, token);
            ocrOutcome = ocr.Outcome;
            detail = ocr.Detail ?? detail;
            if (ocr.Lines.Count > 0)
            {
                // Merge, don't replace: keep useful embedded text, add OCR
                // observations, dedupe identical lines.
                lines = MergeSources(embedded.Lines, ocr.Lines);
            }
        }

        var facts = ScoreFactInference.InferRich(entry.DisplayName, embedded.Metadata, lines);

        // Persist extraction/OCR failure distinctly from a clean Partial:
        // FailedTransient carries backoff so the field can be reconsidered,
        // and a failure is never stored as a successful identification.
        var status = (embedded.Outcome, ocrOutcome) switch
        {
            (ScoreExtractionOutcome.ExtractionFailure, _) => ExtractionStatus.FailedTransient,
            (ScoreExtractionOutcome.Cancelled, _) => ExtractionStatus.FailedTransient,
            (_, OcrOutcome.Failed) => ExtractionStatus.FailedTransient,
            (_, OcrOutcome.Cancelled) => ExtractionStatus.FailedTransient,
            _ => facts.Composer is null || facts.Title is null
                ? ExtractionStatus.Partial
                : ExtractionStatus.Complete,
        };
        return new ScoreExtractionResult(facts, embedded.Outcome, ocrOutcome, detail, status);
    }

    private static bool NeedsOcr(string displayName, ScoreExtraction embedded)
    {
        if (embedded.Outcome is ScoreExtractionOutcome.ExtractionFailure or ScoreExtractionOutcome.Cancelled)
        {
            return true;
        }

        // Trigger on unresolved fields and poor evidence, not a letter count.
        var probe = ScoreFactInference.InferRich(displayName, embedded.Metadata, embedded.Lines);
        if (probe.Title is null || probe.Composer is null)
        {
            return true;
        }

        return !ScoreFactInference.HasUsefulPageText(displayName, embedded.PageLines);
    }

    private static IReadOnlyList<ScoreTextLine> MergeSources(
        IReadOnlyList<ScoreTextLine> embedded,
        IReadOnlyList<ScoreTextLine> ocr)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<ScoreTextLine>(embedded.Count + ocr.Count);
        foreach (var line in embedded.Concat(ocr))
        {
            var key = $"{line.PageNumber}:{(line.Text ?? string.Empty).Trim().ToLowerInvariant()}";
            if (seen.Add(key))
            {
                merged.Add(line);
            }
        }

        return merged
            .OrderBy(line => line.PageNumber)
            .ThenBy(line => line.Source)
            .ThenBy(line => line.Y)
            .ThenBy(line => line.X)
            .ToArray();
    }

    private async Task<(IReadOnlyList<ScoreTextLine> Lines, OcrOutcome Outcome, string? Detail)> ReadOcrLinesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        OcrEngine? engine;
        try
        {
            engine = PickEngine();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ([], OcrOutcome.Failed, ex.Message);
        }

        if (engine is null)
        {
            var wanted = string.Join(",", PreferredOcrLanguages);
            var have = string.Join(",", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag));
            return ([], OcrOutcome.UnavailableEngine, $"no OCR engine (wanted {wanted}; installed: {have})");
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var pageCount = Math.Min(MaxOcrPages, Math.Max(1, PdfBitmapRenderer.GetPageCount(bytes)));
            var lines = new List<ScoreTextLine>();
            for (var page = 0; page < pageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var bitmap = RenderForOcr(bytes, page);
                using var softwareBitmap = ToSoftwareBitmap(bitmap);
                var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
                var width = Math.Max(1, bitmap.Width);
                var height = Math.Max(1, bitmap.Height);
                foreach (var line in result.Lines)
                {
                    var text = line.Text?.Trim() ?? string.Empty;
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    // Windows OCR words carry boxes; fall back to full width.
                    var words = line.Words.ToArray();
                    var left = words.Length > 0 ? words.Min(w => w.BoundingRect.Left) : 0;
                    var top = words.Length > 0 ? words.Min(w => w.BoundingRect.Top) : 0;
                    var right = words.Length > 0 ? words.Max(w => w.BoundingRect.Right) : width;
                    var bottom = words.Length > 0 ? words.Max(w => w.BoundingRect.Bottom) : height;
                    lines.Add(new ScoreTextLine(
                        page + 1, text,
                        left / width, top / height,
                        Math.Max(0, (right - left) / width), Math.Max(0, (bottom - top) / height),
                        null, null, false, ScoreTextSource.Ocr));
                }
            }

            if (lines.Count == 0)
            {
                return ([], OcrOutcome.NoReadableText, "OCR found no text");
            }

            return (lines, OcrOutcome.Success, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ([], OcrOutcome.Failed, ex.Message);
        }
    }

    private static OcrEngine? PickEngine()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is not null)
        {
            return engine;
        }

        // User profile may lack OCR support while a preferred language pack
        // exists; try those before declaring the engine unavailable.
        foreach (var tag in PreferredOcrLanguages)
        {
            try
            {
                engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag));
                if (engine is not null)
                {
                    return engine;
                }
            }
            catch (Exception)
            {
                // Try the next language.
            }
        }

        return null;
    }

    private static SKBitmap RenderForOcr(byte[] bytes, int pageIndex)
    {
        var bitmap = PdfBitmapRenderer.Render(bytes, pageIndex, OcrPixelWidth, useTiling: true);
        var maxDimension = checked((int)OcrEngine.MaxImageDimension);
        var largest = Math.Max(bitmap.Width, bitmap.Height);
        if (largest <= maxDimension)
        {
            return bitmap;
        }

        var scaledWidth = Math.Max(64, bitmap.Width * maxDimension / largest);
        bitmap.Dispose();
        return PdfBitmapRenderer.Render(bytes, pageIndex, scaledWidth, useTiling: true);
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
