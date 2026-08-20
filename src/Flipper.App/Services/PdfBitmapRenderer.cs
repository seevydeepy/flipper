using PDFtoImage;
using SkiaSharp;

namespace Flipper.App.Services;

internal static class PdfBitmapRenderer
{
    private static readonly object Gate = new();

    public static int GetPageCount(byte[] bytes)
    {
        lock (Gate)
        {
            return Conversion.GetPageCount(bytes);
        }
    }

    public static SKBitmap Render(byte[] bytes, int pageIndex, int pixelWidth, bool useTiling = false)
    {
        lock (Gate)
        {
            return Conversion.ToImage(bytes, pageIndex, options: new RenderOptions
            {
                Width = Math.Max(64, pixelWidth),
                WithAspectRatio = true,
                UseTiling = useTiling
            });
        }
    }
}
