using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using PDFtoImage;
using SkiaSharp;
using Windows.Storage.Streams;

namespace Flipper.App.Services;

public sealed class PdfPageSource : IDisposable
{
    private static readonly object PdfiumLock = new();
    private const float TitleBandFraction = 0.24f;
    private const byte PaperTone = 240;
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

    public WriteableBitmap? Render(int pageIndex, int pixelWidth)
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
                    Width = Math.Max(64, pixelWidth),
                    WithAspectRatio = true,
                    UseTiling = true
                });
            }

            using (bitmap)
            {
                return ToWriteable(bitmap);
            }
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
            SKBitmap bitmap;
            lock (PdfiumLock)
            {
                bitmap = Conversion.ToImage(bytes, 0, options: new RenderOptions
                {
                    Width = Math.Max(64, pixelWidth),
                    WithAspectRatio = true
                });
            }

            using (bitmap)
            {
                var bandHeight = TitleBandHeight(bitmap.Height);
                SKBitmap? cropped = null;
                if (bandHeight < bitmap.Height && TitleBandHasInk(bitmap, bandHeight))
                {
                    cropped = CropTop(bitmap, bandHeight);
                }

                using (cropped)
                {
                    SavePng(cropped ?? bitmap, pngPath);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            WriteError(ex);
            return false;
        }
    }

    public Task PrefetchAsync(int pageIndex, int pixelWidth)
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
                        Width = Math.Max(64, pixelWidth),
                        WithAspectRatio = true
                    });
                }
                catch (Exception)
                {
                }
            }
        });
    }

    private static int TitleBandHeight(int pageHeight)
    {
        return Math.Clamp((int)Math.Round(pageHeight * TitleBandFraction), 1, Math.Max(1, pageHeight));
    }

    private static bool TitleBandHasInk(SKBitmap bitmap, int bandHeight)
    {
        var width = bitmap.Width;
        var total = width * bandHeight;
        var needed = Math.Max(1, total / 100);
        var ink = 0;
        for (var y = 0; y < bandHeight; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.Red < PaperTone || color.Green < PaperTone || color.Blue < PaperTone)
                {
                    ink++;
                    if (ink >= needed)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static SKBitmap CropTop(SKBitmap source, int height)
    {
        using var subset = new SKBitmap();
        if (!source.ExtractSubset(subset, new SKRectI(0, 0, source.Width, height)))
        {
            throw new InvalidOperationException("Cannot crop preview band");
        }

        return subset.Copy() ?? throw new InvalidOperationException("Cannot copy preview band");
    }

    private static void SavePng(SKBitmap bitmap, string pngPath)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 80);
        Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
        var tmp = pngPath + ".tmp";
        using (var file = File.Create(tmp))
        {
            data.SaveTo(file);
        }

        File.Move(tmp, pngPath, overwrite: true);
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
