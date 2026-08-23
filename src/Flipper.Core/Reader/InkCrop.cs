namespace Flipper.Core.Reader;

public readonly record struct InkBounds(float Left, float Top, float Width, float Height)
{
    public float Right => Left + Width;
    public float Bottom => Top + Height;
}

public static class InkCrop
{
    public const byte DefaultThreshold = 245;
    public const float DefaultPad = 0.03f;

    public static InkBounds? FromPixels(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int bytesPerPixel,
        byte threshold = DefaultThreshold)
    {
        if (width <= 0 || height <= 0 || bytesPerPixel < 3)
        {
            return null;
        }

        var stride = width * bytesPerPixel;
        if (pixels.Length < stride * height)
        {
            return null;
        }

        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + (x * bytesPerPixel);
                if (pixels[i] >= threshold && pixels[i + 1] >= threshold && pixels[i + 2] >= threshold)
                {
                    continue;
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return null;
        }

        return new InkBounds(
            minX / (float)width,
            minY / (float)height,
            (maxX - minX + 1) / (float)width,
            (maxY - minY + 1) / (float)height);
    }

    public static InkBounds Pad(InkBounds bounds, float pad = DefaultPad)
    {
        var left = Math.Clamp(bounds.Left - pad, 0f, 1f);
        var top = Math.Clamp(bounds.Top - pad, 0f, 1f);
        var right = Math.Clamp(bounds.Right + pad, 0f, 1f);
        var bottom = Math.Clamp(bounds.Bottom + pad, 0f, 1f);
        return new InkBounds(left, top, Math.Max(0f, right - left), Math.Max(0f, bottom - top));
    }

    public static bool WorthCropping(InkBounds bounds)
    {
        return bounds.Width < 0.97f || bounds.Height < 0.97f;
    }
}
