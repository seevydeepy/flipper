using Flipper.Core.Library;
using SkiaSharp;

namespace Flipper.App.Services;

/// <summary>Small, in-memory page renders for the optional Jev image check. Call from a worker.</summary>
internal static class PdfScorePageImages
{
    public static async Task<IReadOnlyList<JevScoreSelector.PageImage>> ReadAsync(
        string path, Func<bool> permitted, CancellationToken cancellationToken)
    {
        if (!permitted()) return [];
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var count = Math.Min(JevScoreSelector.MaxImagePages, PdfBitmapRenderer.GetPageCount(bytes));
        var images = new List<JevScoreSelector.PageImage>();
        for (var page = 0; page < count; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!permitted()) return [];
            var size = PdfBitmapRenderer.GetPageSize(bytes, page);
            if (!float.IsFinite(size.Width) || !float.IsFinite(size.Height)
                || size.Width <= 0 || size.Height <= 0)
                throw new InvalidDataException("Invalid PDF page size.");

            var width = (int)Math.Floor(JevScoreSelector.MaxImageDimension
                * (double)size.Width / Math.Max(size.Width, size.Height));
            // The shared renderer has a 64-pixel minimum width. Reject an extreme
            // aspect ratio before it could allocate an unbounded native bitmap.
            if (width < 64) throw new InvalidDataException("PDF page is too narrow to render safely.");
            using var rendered = PdfBitmapRenderer.Render(bytes, page, width,
                paperBackground: false, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var scale = Math.Min(1d, JevScoreSelector.MaxImageDimension
                / (double)Math.Max(rendered.Width, rendered.Height));
            var scaledWidth = Math.Max(1, (int)Math.Floor(rendered.Width * scale));
            var scaledHeight = Math.Max(1, (int)Math.Floor(rendered.Height * scale));
            using var padded = new SKBitmap(Math.Max(JevScoreSelector.MinImageDimension, scaledWidth),
                Math.Max(JevScoreSelector.MinImageDimension, scaledHeight));
            using (var canvas = new SKCanvas(padded))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(rendered, SKRect.Create(
                    (padded.Width - scaledWidth) / 2f, (padded.Height - scaledHeight) / 2f,
                    scaledWidth, scaledHeight), new SKSamplingOptions(SKFilterMode.Linear));
            }
            using var encoded = padded.Encode(SKEncodedImageFormat.Jpeg, 90);
            if (encoded is null || encoded.Size > JevScoreSelector.MaxImageBytes)
                throw new InvalidDataException("Page image exceeds the upload budget.");
            images.Add(new(page + 1, encoded.ToArray(), padded.Width, padded.Height));
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Recheck after native rendering in case the user switched image uploads off.
        return permitted() ? images : [];
    }
}
