using PDFtoImage;
using SkiaSharp;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Flipper.App.Services;

public sealed class PdfPageSource : IDisposable
{
    private static readonly object PdfiumLock = new();
    private readonly byte[] _bytes;
    private bool _disposed;

    public int PageCount { get; }

    public PdfPageSource(string cachePath)
    {
        _bytes = File.ReadAllBytes(cachePath);
        lock (PdfiumLock)
        {
            PageCount = Conversion.GetPageCount(_bytes);
        }
    }

    public BitmapImage? Render(int pageIndex, float dpi)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            return null;
        }

        try
        {
            SKBitmap bitmap;
            lock (PdfiumLock)
            {
                bitmap = Conversion.ToImage(_bytes, pageIndex, options: new RenderOptions
                {
                    Dpi = (int)Math.Round(dpi)
                });
            }

            using (bitmap)
            using (var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100))
            {
                var image = new BitmapImage();
                using var ras = new InMemoryRandomAccessStream();
                using (var output = ras.AsStreamForWrite())
                {
                    encoded.AsStream().CopyTo(output);
                    output.Flush();
                }
                ras.Seek(0);
                image.SetSource(ras);
                return image;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public Task PrefetchAsync(int pageIndex, float dpi)
    {
        return Task.Run(() =>
        {
            if (_disposed || pageIndex < 0 || pageIndex >= PageCount)
            {
                return;
            }

            lock (PdfiumLock)
            {
                try
                {
                    using var bitmap = Conversion.ToImage(_bytes, pageIndex, options: new RenderOptions
                    {
                        Dpi = (int)Math.Round(dpi)
                    });
                }
                catch (Exception)
                {
                }
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
