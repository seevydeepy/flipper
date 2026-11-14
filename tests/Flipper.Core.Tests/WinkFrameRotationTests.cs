using System.Numerics;
using Flipper.Core.Reader;

namespace Flipper.Core.Tests;

public sealed class WinkFrameRotationTests
{
    [Theory]
    [InlineData(0, 4, 3, "ABCDEFGHIJKL")]
    [InlineData(1, 3, 4, "IEAJFBKGCLHD")]
    [InlineData(2, 4, 3, "LKJIHGFEDCBA")]
    [InlineData(3, 3, 4, "DHLCGKBFJAEI")]
    public void RotatesAnAsymmetricGridClockwise(int quarterTurns, int expectedWidth, int expectedHeight, string expected)
    {
        const int width = 4;
        const int height = 3;
        const string source = "ABCDEFGHIJKL";

        var size = WinkFrameRotation.GetSize(width, height, quarterTurns);
        Assert.Equal((expectedWidth, expectedHeight), size);

        var pixels = new char[size.Width * size.Height];
        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var point = WinkFrameRotation.ToSource(new Vector2(x, y), width, height, quarterTurns);
                pixels[y * size.Width + x] = source[(int)point.Y * width + (int)point.X];
            }
        }

        Assert.Equal(expected, new string(pixels));
    }

    [Theory]
    [InlineData(0, 0.25f, 1.5f)]
    [InlineData(1, 1.5f, 1.75f)]
    [InlineData(2, 2.75f, 0.5f)]
    [InlineData(3, 1.5f, 0.25f)]
    public void MapsFractionalSampleCoordinates(int quarterTurns, float sourceX, float sourceY)
    {
        var source = WinkFrameRotation.ToSource(new Vector2(0.25f, 1.5f), 4, 3, quarterTurns);

        Assert.Equal(new Vector2(sourceX, sourceY), source);
    }
}
