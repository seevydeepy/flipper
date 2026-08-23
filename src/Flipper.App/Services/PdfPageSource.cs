using System.Drawing;
using System.Runtime.InteropServices.WindowsRuntime;
using Flipper.Core.Reader;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Storage.Streams;

namespace Flipper.App.Services;

public sealed class PdfPageSource : IDisposable
{
    private readonly byte[] _bytes;
    private readonly Dictionary<int, RectangleF?> _ink = new();
    private readonly object _inkGate = new();
    private bool _disposed;

    public int PageCount { get; }

    public PdfPageSource(string cachePath)
    {
        _bytes = File.ReadAllBytes(cachePath);
        PageCount = PdfBitmapRenderer.GetPageCount(_bytes);
    }

    public WriteableBitmap? Render(int pageIndex, int pixelWidth, bool cropToInk = false)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            return null;
        }

        try
        {
            var bounds = cropToInk ? InkBounds(pageIndex) : null;
            using var bitmap = PdfBitmapRenderer.Render(_bytes, pageIndex, pixelWidth, useTiling: true, bounds);
            return ToWriteable(bitmap);
        }
        catch (Exception ex)
        {
            WriteError(ex);
            return null;
        }
    }

    public static bool TrySavePreview(string pdfPath, string pngPath, int pixelWidth)
    {
        try
        {
            var bytes = File.ReadAllBytes(pdfPath);
            using (var bitmap = PdfBitmapRenderer.Render(bytes, 0, pixelWidth))
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 80))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
                var tmp = pngPath + ".tmp";
                using (var file = File.Create(tmp))
                {
                    data.SaveTo(file);
                }

                File.Move(tmp, pngPath, overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            WriteError(ex);
            return false;
        }
    }

    public Task PrefetchAsync(int pageIndex, int pixelWidth, bool cropToInk = false)
    {
        return Task.Run(() =>
        {
            if (_disposed || pageIndex < 0 || pageIndex >= PageCount)
            {
                return;
            }

            try
            {
                var bounds = cropToInk ? InkBounds(pageIndex) : null;
                using var bitmap = PdfBitmapRenderer.Render(_bytes, pageIndex, pixelWidth, bounds: bounds);
            }
            catch (Exception)
            {
            }
        });
    }

    private RectangleF? InkBounds(int pageIndex)
    {
        lock (_inkGate)
        {
            if (_ink.TryGetValue(pageIndex, out var cached))
            {
                return cached;
            }

            RectangleF? bounds = null;
            try
            {
                var page = PdfBitmapRenderer.GetPageSize(_bytes, pageIndex);
                using var preview = PdfBitmapRenderer.Render(_bytes, pageIndex, 400);
                var found = InkCrop.FromPixels(preview.Bytes, preview.Width, preview.Height, preview.BytesPerPixel);
                if (found is { } ink && InkCrop.WorthCropping(ink))
                {
                    var padded = InkCrop.Pad(ink);
                    bounds = new RectangleF(
                        padded.Left * page.Width,
                        padded.Top * page.Height,
                        padded.Width * page.Width,
                        padded.Height * page.Height);
                }
            }
            catch (Exception)
            {
                bounds = null;
            }

            _ink[pageIndex] = bounds;
            return bounds;
        }
    }

    private static WriteableBitmap ToWriteable(SKBitmap source)
    {
        using var converted = source.ColorType == SKColorType.Bgra8888
            ? null
            : source.Copy(SKColorType.Bgra8888);
        var pixels = converted ?? source;
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            var bytes = pixels.Bytes;
            stream.Write(bytes, 0, bytes.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    private static void WriteError(Exception ex)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Flipper",
                "last-error.txt");
            File.WriteAllText(path, DateTime.Now.ToString("O") + Environment.NewLine + ex);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
