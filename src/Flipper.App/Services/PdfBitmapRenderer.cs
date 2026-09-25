using System.Drawing;
using PDFtoImage;
using SkiaSharp;

namespace Flipper.App.Services;

internal static class PdfBitmapRenderer
{
    private static readonly object Gate = new();
    private static readonly SKColor Paper = new(0xF4, 0xF3, 0xEF);

    public static int GetPageCount(byte[] bytes)
    {
        lock (Gate)
        {
            return Conversion.GetPageCount(bytes);
        }
    }

    public static SizeF GetPageSize(byte[] bytes, int pageIndex)
    {
        lock (Gate)
        {
            var size = Conversion.GetPageSize(bytes, pageIndex);
            return new SizeF(size.Width, size.Height);
        }
    }

    public static SKBitmap Render(
        byte[] bytes,
        int pageIndex,
        int pixelWidth,
        bool useTiling = false,
        RectangleF? bounds = null,
        bool paperBackground = true,
        CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Conversion.ToImage(bytes, pageIndex, options: new RenderOptions
            {
                Width = Math.Max(64, pixelWidth),
                WithAspectRatio = true,
                UseTiling = useTiling,
                BackgroundColor = paperBackground ? Paper : SKColors.White,
                Bounds = bounds,
                DpiRelativeToBounds = bounds.HasValue
            });
        }
    }
}
