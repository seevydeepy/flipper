using System.Text.Json.Serialization;

namespace Flipper.Core.Reader;

public sealed record WinkCalibrationProfile
{
    public int Version { get; init; } = 1;
    public string CameraId { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public double LeftOpen { get; init; }
    public double LeftClosed { get; init; }
    public double RightOpen { get; init; }
    public double RightClosed { get; init; }
    public double ClosedThreshold { get; init; }
    public double LeftWinkOtherEyeMinimum { get; init; }
    public double LeftWinkMinimumDifference { get; init; }
    public double RightWinkOtherEyeMinimum { get; init; }
    public double RightWinkMinimumDifference { get; init; }

    [JsonIgnore]
    public bool IsValid =>
        Version == 1 && !string.IsNullOrWhiteSpace(CameraId)
        && IsProbability(LeftOpen) && IsProbability(LeftClosed) && LeftOpen - LeftClosed >= 0.15
        && IsProbability(RightOpen) && IsProbability(RightClosed) && RightOpen - RightClosed >= 0.15
        && IsProbability(ClosedThreshold)
        && IsProbability(LeftWinkOtherEyeMinimum) && IsProbability(LeftWinkMinimumDifference)
        && IsProbability(RightWinkOtherEyeMinimum) && IsProbability(RightWinkMinimumDifference);

    internal static bool IsProbability(double value) => value is >= 0 and <= 1;
}
