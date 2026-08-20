using System.Text;
using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class PdfEmbeddedTextReaderTests
{
    [Fact]
    public void Read_ReturnsMetadataAndFirstPageText()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, "rubbish123.pdf");
        File.WriteAllBytes(path, BuildPdf());

        var embedded = PdfEmbeddedTextReader.Read(path);

        Assert.Equal("Clair de Lune", embedded.Metadata.Title);
        Assert.Equal("Claude Debussy", embedded.Metadata.Author);
        Assert.Equal("Suite bergamasque", embedded.Metadata.Subject);
        Assert.Contains(embedded.PageLines, line => line.Contains("Clair de Lune", StringComparison.Ordinal));
    }

    private static byte[] BuildPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length 49 >>\nstream\nBT /F1 24 Tf 72 720 Td (Clair de Lune) Tj ET\nendstream",
            "<< /Title (Clair de Lune) /Author (Claude Debussy) /Subject (Suite bergamasque) >>"
        };

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n");
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R /Info 6 0 R >>\n");
        builder.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
