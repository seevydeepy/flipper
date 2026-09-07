using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Flipper.Core.Library;

/// <summary>Where the characters on a line came from.</summary>
public enum ScoreTextSource
{
    Embedded,
    Ocr
}

/// <summary>
/// One observed text line: page number, normalised bounding box (0..1 across
/// the page's media box, origin top-left), font info when embedded, and source.
/// Coordinates are normalised per page so embedded and OCR boxes compare.
/// </summary>
public sealed record ScoreTextLine(
    int PageNumber,
    string Text,
    double X,
    double Y,
    double Width,
    double Height,
    double? FontSize,
    string? FontName,
    bool Bold,
    ScoreTextSource Source);

/// <summary>
/// How an extraction attempt ended. A failure is data, never persisted as a
/// successful identification.
/// </summary>
public enum ScoreExtractionOutcome
{
    Success,
    NoReadableText,
    ExtractionFailure,
    OcrUnavailable,
    OcrFailed,
    Cancelled
}

public sealed record ScoreExtraction(
    ScoreMetadata Metadata,
    IReadOnlyList<ScoreTextLine> Lines,
    ScoreExtractionOutcome Outcome,
    string? Detail = null)
{
    /// <summary>Plain strings for the inference pipeline (back-compat).</summary>
    public IReadOnlyList<string> PageLines => Lines.Select(line => line.Text).ToArray();
}

public static class PdfEmbeddedTextReader
{
    /// <summary>
    /// Read embedded text with layout: up to <paramref name="maxPages"/> early
    /// pages (default 3), lines grouped by baseline with per-line font size,
    /// bold flag, and normalised bounding boxes.
    /// </summary>
    public static PdfEmbeddedText Read(string path)
    {
        return ReadRich(path, maxPages: 1).AsLegacy();
    }

    public static ScoreExtraction ReadRich(string path, int maxPages = 3)
    {
        try
        {
            using var document = PdfDocument.Open(path);
            var metadata = new ScoreMetadata(
                document.Information.Title,
                document.Information.Author,
                document.Information.Subject);
            if (document.NumberOfPages < 1)
            {
                return new ScoreExtraction(metadata, [], ScoreExtractionOutcome.NoReadableText);
            }

            var lines = new List<ScoreTextLine>();
            var pages = Math.Min(Math.Max(1, maxPages), document.NumberOfPages);
            for (var pageNumber = 1; pageNumber <= pages; pageNumber++)
            {
                var page = document.GetPage(pageNumber);
                lines.AddRange(ReadPageLines(page, ScoreTextSource.Embedded));
            }

            if (lines.Count == 0)
            {
                return new ScoreExtraction(metadata, [], ScoreExtractionOutcome.NoReadableText);
            }

            return new ScoreExtraction(metadata, lines, ScoreExtractionOutcome.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ScoreExtraction(default, [], ScoreExtractionOutcome.ExtractionFailure, ex.Message);
        }
    }

    internal static IReadOnlyList<ScoreTextLine> ReadPageLines(Page page, ScoreTextSource source)
    {
        // Content-order text for the raw strings, then word boxes for geometry.
        // Words sharing a baseline band form a line; the line box covers them.
        var words = page.GetWords().ToArray();
        if (words.Length == 0)
        {
            var fallback = ContentOrderTextExtractor.GetText(page)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Length > 0)
                .Select(line => new ScoreTextLine(
                    page.Number, line, 0, 0, 0, 0, null, null, false, source))
                .ToArray();
            return fallback;
        }

        var width = Math.Max(1, page.Width);
        var height = Math.Max(1, page.Height);
        var groups = new List<List<Word>> { new() { words[0] } };
        foreach (var word in words.Skip(1))
        {
            var current = groups[^1];
            var band = current.Select(w => w.BoundingBox.Centroid.Y).Average();
            var tolerance = Math.Max(2, current.SelectMany(w => w.Letters).Select(l => l.FontSize).DefaultIfEmpty(12).Average() * 0.4);
            if (Math.Abs(word.BoundingBox.Centroid.Y - band) <= tolerance)
            {
                current.Add(word);
            }
            else
            {
                groups.Add(new List<Word> { word });
            }
        }

        return groups
            .Select(group =>
            {
                var ordered = group.OrderBy(w => w.BoundingBox.Left).ToArray();
                var text = string.Join(" ", ordered.Select(w => w.Text)).Trim();
                var left = ordered.Min(w => w.BoundingBox.Left);
                var right = ordered.Max(w => w.BoundingBox.Right);
                // PdfPig Y grows upward; normalise to top-left origin.
                var bottom = ordered.Min(w => w.BoundingBox.Bottom);
                var top = ordered.Max(w => w.BoundingBox.Top);
                var sizes = ordered.SelectMany(w => w.Letters).Select(l => l.FontSize).ToArray();
                var fonts = ordered.SelectMany(w => w.Letters).Select(l => l.FontName).ToArray();
                var bold = fonts.Any(f => f.Contains("bold", StringComparison.OrdinalIgnoreCase));
                return new ScoreTextLine(
                    page.Number,
                    text,
                    left / width,
                    1 - (top / height),
                    (right - left) / width,
                    (top - bottom) / height,
                    sizes.Length > 0 ? sizes.Average() : null,
                    fonts.GroupBy(f => f).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key,
                    bold,
                    source);
            })
            .Where(line => line.Text.Length > 0)
            .OrderBy(line => line.Y)
            .ThenBy(line => line.X)
            .ToArray();
    }
}

public sealed record PdfEmbeddedText(ScoreMetadata Metadata, IReadOnlyList<string> PageLines);

internal static class PdfEmbeddedTextCompat
{
    internal static PdfEmbeddedText AsLegacy(this ScoreExtraction extraction)
    {
        return new PdfEmbeddedText(extraction.Metadata, extraction.PageLines);
    }
}
