using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class ScoreExtractionTests
{
    [Fact]
    public void ReadRich_ReturnsLayout_WithFontSizeAndBoxes()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, "layout.pdf");
        File.WriteAllBytes(path, BuildPdf(["Clair de Lune", "Claude Debussy"]));

        var extraction = PdfEmbeddedTextReader.ReadRich(path);

        Assert.Equal(ScoreExtractionOutcome.Success, extraction.Outcome);
        Assert.Equal("Clair de Lune", extraction.Metadata.Title);
        Assert.True(extraction.Lines.Count >= 2);
        var first = extraction.Lines[0];
        Assert.Equal(1, first.PageNumber);
        Assert.InRange(first.Y, 0, 1);
        Assert.InRange(first.X, 0, 1);
        Assert.NotNull(first.FontSize);
        Assert.Equal(ScoreTextSource.Embedded, first.Source);
        // Rich lines still drive inference.
        var facts = ScoreFactInference.InferRich("layout.pdf", extraction.Metadata, extraction.Lines);
        Assert.Equal("Clair de Lune", facts.Title);
    }

    [Fact]
    public void ReadRich_ReadsBeyondPageOne()
    {
        // Cover-only first pages resolve via a later early page. The shared
        // test builder emits a real 2-page PDF: page 2 lines carry PageNumber 2.
        using var root = new TempDir();
        var path = Path.Combine(root.Path, "cover2.pdf");
        File.WriteAllBytes(path, BuildPdf(["Cover"], ["Real Title", "Real Composer"]));

        var one = PdfEmbeddedTextReader.ReadRich(path, maxPages: 1);
        var two = PdfEmbeddedTextReader.ReadRich(path, maxPages: 3);
        Assert.True(two.Lines.Count >= one.Lines.Count);
        Assert.Contains(two.Lines, line => line.PageNumber == 2);
    }

    [Fact]
    public void Inference_SeesPastTenCoverRows()
    {
        // A cover page with many engraving rows must not hide the real
        // title/composer from inference: no hard line cap. Junk rows are
        // distinct publisher boilerplate lines (each passes IsUsefulLine but
        // none agree with the filename or look like a person); the
        // filename-corroborated title + composer must still win over all of
        // them. (Cover rows naming rival composers stay genuinely ambiguous
        // and correctly abstain — that is SelectComposer's job, not the cap's.)
        var ones = new[]
        {
            "one", "two", "three", "four", "five", "six",
            "seven", "eight", "nine", "ten", "eleven", "twelve",
        };
        var junk = ones.Select(w => $"Published edition plate volume {w}").ToArray();
        var facts = ScoreFactInference.Infer(
            "moonlight-sonata.pdf",
            default,
            [.. junk, "Moonlight Sonata", "Composed by Ludwig van Beethoven"]);

        Assert.Equal("Moonlight Sonata", facts.Title);
        Assert.Equal("Ludwig van Beethoven", facts.Composer);
    }

    [Fact]
    public void ReadRich_MissingFile_IsExtractionFailure()
    {
        var extraction = PdfEmbeddedTextReader.ReadRich(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf"));

        Assert.Equal(ScoreExtractionOutcome.ExtractionFailure, extraction.Outcome);
        Assert.Empty(extraction.Lines);
    }

    [Fact]
    public void MergeMultilineTitles_JoinsWrappedHeading()
    {
        var lines = new[]
        {
            new ScoreTextLine(1, "Piano Concerto No.", 0.2, 0.05, 0.6, 0.03, 17.4, "Bold", true, ScoreTextSource.Embedded),
            new ScoreTextLine(1, "2 in C minor", 0.25, 0.09, 0.5, 0.03, 17.4, "Bold", true, ScoreTextSource.Embedded),
            new ScoreTextLine(1, "Sergei Rachmaninoff", 0.3, 0.14, 0.4, 0.02, 11, "Roma", false, ScoreTextSource.Embedded)
        };

        var merged = ScoreFactInference.MergeMultilineTitles(lines);

        Assert.Equal(2, merged.Count);
        Assert.Equal("Piano Concerto No. 2 in C minor", merged[0].Text);
    }

    [Fact]
    public void MergeMultilineTitles_KeepsComposerSeparate()
    {
        var lines = new[]
        {
            new ScoreTextLine(1, "Moonlight Sonata", 0.2, 0.05, 0.6, 0.03, 17.4, "Bold", true, ScoreTextSource.Embedded),
            new ScoreTextLine(1, "Ludwig van Beethoven", 0.3, 0.10, 0.4, 0.02, 11, "Roma", false, ScoreTextSource.Embedded)
        };

        var merged = ScoreFactInference.MergeMultilineTitles(lines);

        Assert.Equal(2, merged.Count);
    }


    [Fact]
    public void MergeMultilineTitles_DoesNotJoinOcrColumnsOrCreditRoles()
    {
        ScoreTextLine[] columns = [
            new(1, "Dawn of a new day", .12, .10, .4, .02, null, null, false, ScoreTextSource.Ocr),
            new(1, "John Williams", .65, .11, .2, .02, null, null, false, ScoreTextSource.Ocr)];
        Assert.Equal(columns, ScoreFactInference.MergeMultilineTitles(columns));
        ScoreTextLine[] credit = [
            columns[0] with { Text = "Song of", X = .2 },
            columns[1] with { Text = "Music by John Williams", X = .2, Y = .14 }];
        Assert.Equal(credit, ScoreFactInference.MergeMultilineTitles(credit));
    }

    [Fact]
    public void ReadRich_SelectsActualPageAndReturnsEmptyBeyondEnd()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, "pages.pdf");
        File.WriteAllBytes(path, BuildPdf(["Cover"], ["Dawn"]));
        var selected = PdfEmbeddedTextReader.ReadRich(path, maxPages: 1, startPage: 2);
        Assert.Single(selected.Lines);
        Assert.Equal(2, selected.Lines[0].PageNumber);
        Assert.Equal("Dawn", selected.Lines[0].Text);
        var beyond = PdfEmbeddedTextReader.ReadRich(path, startPage: 3);
        Assert.Empty(beyond.Lines);
        Assert.Equal(ScoreExtractionOutcome.NoReadableText, beyond.Outcome);
    }

    [Fact]
    public void ReadRich_PropagatesCancellationBeforeOpeningFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PdfEmbeddedTextReader.ReadRich("does-not-exist.pdf", cancellationToken: cancellation.Token));
    }

    private static byte[] BuildPdf(IReadOnlyList<string> firstPage, IReadOnlyList<string>? secondPage = null)
    {
        // Two independent single-page documents would be simpler, but the
        // cover-page regression needs a REAL two-page file: page 2 lines must
        // carry PageNumber 2 through PdfPig. Object layout: 1 catalog,
        // 2 pages, 3 page-one, 4 font, 5 page-one content, 6 info,
        // [7 page-two, 8 page-two content].
        var pageOneStream = PageStream(firstPage);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            secondPage is null
                ? "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"
                : "<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + System.Text.Encoding.ASCII.GetByteCount(pageOneStream) + " >>\nstream\n" + pageOneStream + "\nendstream",
            "<< /Title (Clair de Lune) /Author (Claude Debussy) /Subject (Suite bergamasque) >>"
        };
        if (secondPage is not null)
        {
            var pageTwoStream = PageStream(secondPage);
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 8 0 R >>");
            objects.Add("<< /Length " + System.Text.Encoding.ASCII.GetByteCount(pageTwoStream) + " >>\nstream\n" + pageTwoStream + "\nendstream");
        }

        var builder = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(System.Text.Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = System.Text.Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n");
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R /Info 6 0 R >>\n");
        builder.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string PageStream(IReadOnlyList<string> lines)
    {
        var y = 720;
        var parts = new List<string>();
        foreach (var line in lines)
        {
            parts.Add($"BT /F1 24 Tf 72 {y} Td ({line}) Tj ET");
            y -= 40;
        }

        return string.Join("\n", parts);
    }
}
