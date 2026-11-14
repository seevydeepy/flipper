using System.Numerics;

namespace Flipper.Core.Reader;

public static class WinkFrameRotation
{
    public static (int Width, int Height) GetSize(int width, int height, int quarterTurns) => quarterTurns switch
    {
        0 or 2 => (width, height),
        1 or 3 => (height, width),
        _ => throw new ArgumentOutOfRangeException(nameof(quarterTurns))
    };

    // Map a sample in the clockwise-rotated image back to the original frame.
    public static Vector2 ToSource(Vector2 point, int width, int height, int quarterTurns) => quarterTurns switch
    {
        0 => point,
        1 => new(point.Y, height - 1 - point.X),
        2 => new(width - 1 - point.X, height - 1 - point.Y),
        3 => new(width - 1 - point.Y, point.X),
        _ => throw new ArgumentOutOfRangeException(nameof(quarterTurns))
    };
}
