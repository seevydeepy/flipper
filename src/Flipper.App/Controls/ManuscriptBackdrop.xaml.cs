using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Flipper.App.Controls;

public sealed partial class ManuscriptBackdrop : UserControl
{
    private readonly DispatcherTimer _paintTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private int _lastWidth;
    private int _lastHeight;

    public ManuscriptBackdrop()
    {
        InitializeComponent();
        _paintTimer.Tick += (_, _) =>
        {
            _paintTimer.Stop();
            Paint();
        };
        SizeChanged += (_, _) =>
        {
            _paintTimer.Stop();
            _paintTimer.Start();
        };
        Loaded += (_, _) => _paintTimer.Start();
        ActualThemeChanged += (_, _) =>
        {
            _lastWidth = 0;
            _lastHeight = 0;
            Paint();
        };
        Unloaded += (_, _) => _paintTimer.Stop();
    }

    private void Paint()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Clamp((int)Math.Round(ActualWidth * scale), 0, 4096);
        var height = Math.Clamp((int)Math.Round(ActualHeight * scale), 0, 4096);
        if (width < 8 || height < 8 || (width == _lastWidth && height == _lastHeight && PaperImage.Source is not null))
        {
            return;
        }

        var pixels = new byte[width * height * 4];
        FillPaper(pixels, width, height);
        DrawStaves(pixels, width, height, scale);

        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        PaperImage.Source = bitmap;
        _lastWidth = width;
        _lastHeight = height;
    }

    private static void FillPaper(byte[] pixels, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var ny = y / (float)height;
            for (var x = 0; x < width; x++)
            {
                var nx = x / (float)width;
                var lamp = Math.Max(0f, 1f - MathF.Sqrt(nx * nx + ny * ny) * 1.15f);
                var dx = nx * 2f - 1f;
                var dy = ny * 2f - 1f;
                var vignette = 1f - 0.14f * Math.Min(1f, (dx * dx + dy * dy) * 0.55f);
                var grain = ((Hash(x, y) & 15) - 7) * 0.7f;

                var r = (243 + lamp * 10 + grain) * vignette;
                var g = (232 + lamp * 6 + grain) * vignette;
                var b = (214 + lamp * 2 + grain * 0.8f) * vignette;
                SetPixel(pixels, width, x, y, Clamp(b), Clamp(g), Clamp(r));
            }
        }
    }

    private static void DrawStaves(byte[] pixels, int width, int height, double scale)
    {
        var lineGap = Math.Max(8, (int)Math.Round(11 * scale));
        var staffGap = Math.Max(28, (int)Math.Round(46 * scale));
        var startY = Math.Max(16, (int)Math.Round(30 * scale));
        var staffHeight = lineGap * 4;
        var left = Math.Max(8, (int)Math.Round(18 * scale));
        var right = width - Math.Max(8, (int)Math.Round(18 * scale));

        for (var top = startY; top + staffHeight < height - 12; top += staffHeight + staffGap)
        {
            for (var line = 0; line < 5; line++)
            {
                DrawHorizontal(pixels, width, height, left, right, top + line * lineGap, 148, 168, 182, 78);
            }
        }
    }

    private static void DrawHorizontal(byte[] pixels, int width, int height, int x1, int x2, int y, byte r, byte g, byte b, byte alpha)
    {
        if (y < 0 || y >= height)
        {
            return;
        }

        var start = Math.Clamp(x1, 0, width - 1);
        var end = Math.Clamp(x2, 0, width - 1);
        for (var x = start; x <= end; x++)
        {
            Blend(pixels, width, x, y, r, g, b, alpha);
        }
    }

    private static void Blend(byte[] pixels, int width, int x, int y, byte r, byte g, byte b, byte alpha)
    {
        var i = (y * width + x) * 4;
        var t = alpha / 255f;
        pixels[i] = Clamp(pixels[i] * (1 - t) + b * t);
        pixels[i + 1] = Clamp(pixels[i + 1] * (1 - t) + g * t);
        pixels[i + 2] = Clamp(pixels[i + 2] * (1 - t) + r * t);
    }

    private static void SetPixel(byte[] pixels, int width, int x, int y, byte b, byte g, byte r)
    {
        var i = (y * width + x) * 4;
        pixels[i] = b;
        pixels[i + 1] = g;
        pixels[i + 2] = r;
        pixels[i + 3] = 255;
    }

    private static byte Clamp(float value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static uint Hash(int x, int y)
    {
        unchecked
        {
            var n = (uint)(x * 374761393 + y * 668265263);
            n = (n ^ (n >> 13)) * 1274126177u;
            return n ^ (n >> 16);
        }
    }
}
