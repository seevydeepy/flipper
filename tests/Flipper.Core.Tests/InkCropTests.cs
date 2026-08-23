using Flipper.Core.Reader;

namespace Flipper.Core.Tests;

public sealed class InkCropTests
{
    [Fact]
    public void FromPixels_FindsDarkRectOnWhite()
    {
        var pixels = White(10, 10);
        FillRect(pixels, 10, 2, 3, 6, 7, 0, 0, 0);

        var bounds = InkCrop.FromPixels(pixels, 10, 10, 4);

        Assert.NotNull(bounds);
        Assert.Equal(0.2f, bounds.Value.Left, 3);
        Assert.Equal(0.3f, bounds.Value.Top, 3);
        Assert.Equal(0.5f, bounds.Value.Width, 3);
        Assert.Equal(0.5f, bounds.Value.Height, 3);
    }

    [Fact]
    public void FromPixels_WhitePage_ReturnsNull()
    {
        Assert.Null(InkCrop.FromPixels(White(8, 8), 8, 8, 4));
    }

    [Fact]
    public void Pad_ExpandsAndClampsToPage()
    {
        var padded = InkCrop.Pad(new InkBounds(0.01f, 0.10f, 0.50f, 0.50f), 0.05f);

        Assert.Equal(0f, padded.Left, 3);
        Assert.Equal(0.05f, padded.Top, 3);
        Assert.Equal(0.56f, padded.Width, 3);
        Assert.Equal(0.60f, padded.Height, 3);
    }

    [Fact]
    public void WorthCropping_IgnoresNearFullPage()
    {
        Assert.False(InkCrop.WorthCropping(new InkBounds(0.01f, 0.01f, 0.98f, 0.98f)));
        Assert.True(InkCrop.WorthCropping(new InkBounds(0.08f, 0.10f, 0.84f, 0.80f)));
    }

    private static byte[] White(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        return pixels;
    }

    private static void FillRect(byte[] pixels, int width, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var i = ((y * width) + x) * 4;
                pixels[i] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = 255;
            }
        }
    }
}
