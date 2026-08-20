using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Flipper.Core.Library;

public static class PdfEmbeddedTextReader
{
    public static PdfEmbeddedText Read(string path)
    {
        using var document = PdfDocument.Open(path);
        var metadata = new ScoreMetadata(
            document.Information.Title,
            document.Information.Author,
            document.Information.Subject);
        if (document.NumberOfPages < 1)
        {
            return new PdfEmbeddedText(metadata, []);
        }

        var text = ContentOrderTextExtractor.GetText(document.GetPage(1));
        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToArray();
        return new PdfEmbeddedText(metadata, lines);
    }
}

public sealed record PdfEmbeddedText(ScoreMetadata Metadata, IReadOnlyList<string> PageLines);
