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
    ExtractionStatus Status = ExtractionStatus.Partial,
    ScoreFactInference.InferenceDecision? Decision = null,
    bool JevEvaluated = false);

public sealed class PdfScoreFactExtractor
{
    private const int OcrPixelWidth = 1600;
    private static readonly HttpClient JevHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly JevScoreSelector Jev = new(JevHttp);

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

        var embedded = new ScoreExtraction(default, [], ScoreExtractionOutcome.NoReadableText);
        var decision = ScoreFactInference.InferRichWithEvidence(entry.DisplayName, default, []);
        IReadOnlyList<ScoreTextLine> allLines = [];
        var jevEvaluated = false;
        var ocrOutcome = OcrOutcome.NotNeeded;
        try
        {
            // Inspect one page at a time. An identified title is sufficient;
            // an absent composer must not turn every score into an OCR job.
            for (var page = 1; page <= MaxEmbeddedPages; page++)
            {
                var next = PdfEmbeddedTextReader.ReadRich(entry.DisplayFullPath, 1, token, startPage: page);
                embedded = new ScoreExtraction(next.Metadata,
                    embedded.Lines.Concat(next.Lines).ToArray(), next.Outcome, next.Detail);
                token.ThrowIfCancellationRequested();
                decision = ScoreFactInference.InferRichWithEvidence(entry.DisplayName, embedded.Metadata, embedded.Lines);
                allLines = embedded.Lines;
                if (decision.TitleVerified || next.Outcome == ScoreExtractionOutcome.ExtractionFailure) break;
            }

            string? detail = embedded.Detail;
            if (!decision.TitleVerified && NeedsOcr(embedded))
            {
                var ocr = await ReadOcrLinesAsync(entry, embedded, token);
                ocrOutcome = ocr.Outcome;
                detail = ocr.Detail ?? detail;
                if (ocr.Lines.Count > 0)
                {
                    allLines = MergeSources(embedded.Lines, ocr.Lines);
                    decision = ScoreFactInference.InferRichWithEvidence(entry.DisplayName, embedded.Metadata, allLines);
                }
            }

            token.ThrowIfCancellationRequested();
            try
            {
                var apiKey = JevApiKeyStore.Load();
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    var candidates = ScoreFactInference.ProposeRichCandidates(
                        entry.DisplayName, embedded.Metadata, allLines);
                    var selected = await Jev.SelectAsync(apiKey, entry.DisplayName,
                        embedded.Metadata, candidates, token);
                    jevEvaluated = candidates.Titles.Count > 0 || candidates.Composers.Count > 0;
                    var title = selected.Title;
                    var composer = selected.Composer;
                    if (string.Equals(title, composer, StringComparison.OrdinalIgnoreCase)) composer = null;
                    decision = decision with
                    {
                        Facts = new ScoreFacts
                        {
                            Title = title ?? ScoreFactInference.CleanFileName(entry.DisplayName),
                            Composer = composer,
                            Subtitle = title is not null && string.Equals(title, decision.Facts.Title,
                                StringComparison.OrdinalIgnoreCase) ? decision.Facts.Subtitle : null
                        },
                        TitleVerified = title is not null,
                        TitleEvidence = title is null ? "Jev abstained" : $"Jev {selected.Model} selected printed title",
                        ComposerEvidence = composer is null ? "Jev abstained" : $"Jev {selected.Model} selected composer",
                        TitleRunnerUp = null,
                        TitleProbability = selected.TitleProbability,
                        ComposerProbability = selected.ComposerProbability
                    };
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // A Jev timeout leaves the existing local decision intact.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed optional service leaves the existing local decision intact.
            }
            token.ThrowIfCancellationRequested();
            var facts = IdentifiedFacts(decision);
            var status = !decision.TitleVerified
                && (embedded.Outcome == ScoreExtractionOutcome.ExtractionFailure || ocrOutcome == OcrOutcome.Failed)
                ? ExtractionStatus.FailedTransient
                : facts.Title is null || facts.Composer is null ? ExtractionStatus.Partial : ExtractionStatus.Complete;
            return new ScoreExtractionResult(facts, embedded.Outcome, ocrOutcome, detail, status, decision, jevEvaluated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            return new ScoreExtractionResult(IdentifiedFacts(decision), ScoreExtractionOutcome.Cancelled,
                OcrOutcome.Cancelled, "Extraction time budget exceeded", ExtractionStatus.FailedTransient, decision);
        }
    }

    private static ScoreFacts IdentifiedFacts(ScoreFactInference.InferenceDecision decision)
    {
        return new ScoreFacts
        {
            Title = decision.TitleVerified ? decision.Facts.Title : null,
            Composer = decision.Facts.Composer,
            Subtitle = decision.Facts.Subtitle
        };
    }

    private static bool NeedsOcr(ScoreExtraction embedded) =>
        embedded.Outcome == ScoreExtractionOutcome.ExtractionFailure
        || embedded.Lines.Sum(line => line.Text.Count(char.IsLetter)) < 20;

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
        ScoreEntry entry,
        ScoreExtraction embedded,
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
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(entry.DisplayFullPath);
            var pageCount = Math.Min(MaxOcrPages, Math.Max(1, PdfBitmapRenderer.GetPageCount(bytes)));
            var lines = new List<ScoreTextLine>();
            for (var page = 0; page < pageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var bitmap = RenderForOcr(bytes, page, cancellationToken);
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

                var decision = ScoreFactInference.InferRichWithEvidence(entry.DisplayName, embedded.Metadata,
                    MergeSources(embedded.Lines, lines));
                if (decision.TitleVerified) break;
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

    private static SKBitmap RenderForOcr(byte[] bytes, int pageIndex, CancellationToken cancellationToken)
    {
        var bitmap = PdfBitmapRenderer.Render(bytes, pageIndex, OcrPixelWidth, useTiling: true,
            cancellationToken: cancellationToken);
        var maxDimension = checked((int)OcrEngine.MaxImageDimension);
        var largest = Math.Max(bitmap.Width, bitmap.Height);
        if (largest <= maxDimension)
        {
            return bitmap;
        }

        var scaledWidth = Math.Max(64, bitmap.Width * maxDimension / largest);
        bitmap.Dispose();
        return PdfBitmapRenderer.Render(bytes, pageIndex, scaledWidth, useTiling: true,
            cancellationToken: cancellationToken);
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
