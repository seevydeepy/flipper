using System.Text.Json;
using Flipper.Core.Reader;
using static Flipper.Core.Reader.EyeState;

namespace Flipper.Core.Tests;

public sealed class WinkProbabilityClassifierTests
{
    private static readonly WinkCalibrationProfile Profile = new()
    {
        CameraId = "camera-under-test",
        CreatedUtc = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero),
        LeftOpen = 0.9,
        LeftClosed = 0.2,
        RightOpen = 0.8,
        RightClosed = 0.1,
        ClosedThreshold = 0.25,
        LeftWinkOtherEyeMinimum = 0.45,
        LeftWinkMinimumDifference = 0.3,
        RightWinkOtherEyeMinimum = 0.3,
        RightWinkMinimumDifference = 0.2
    };

    [Theory]
    [InlineData(0.95f, 0.97f, Open, Open)]
    [InlineData(0.05f, 0.04f, Closed, Closed)]
    [InlineData(0.47f, 0.19f, Open, Closed)]
    [InlineData(0.29f, 0.48f, Closed, Open)]
    [InlineData(0.47f, 0.24f, Unknown, Closed)]
    [InlineData(0.44f, 0.48f, Unknown, Unknown)]
    [InlineData(0.26f, 0.16f, Closed, Closed)]
    [InlineData(0.88f, 0.44f, Open, Unknown)]
    public void Classify_NormalisesEachEyeAndUsesDirectionSpecificSeparation(
        float left, float right, EyeState expectedLeft, EyeState expectedRight)
    {
        Assert.Equal((expectedLeft, expectedRight), WinkProbabilityClassifier.Classify(Profile, left, right));
    }

    [Theory]
    [InlineData(null, 0.8f)]
    [InlineData(0.9f, null)]
    [InlineData(float.NaN, 0.8f)]
    [InlineData(0.9f, float.PositiveInfinity)]
    [InlineData(-0.01f, 0.8f)]
    public void Classify_InvalidProbabilityInvalidatesBothEyes(float? left, float? right)
    {
        Assert.Equal((Unknown, Unknown), WinkProbabilityClassifier.Classify(Profile, left, right));
    }

    [Theory]
    [InlineData(0.662f, 0.31f, Open, Unknown)]
    [InlineData(0.41f, 0.562f, Unknown, Open)]
    public void Classify_InsufficientSeparationCannotConfirmUnilateralClosure(
        float left, float right, EyeState expectedLeft, EyeState expectedRight)
    {
        var strict = Profile with
        {
            ClosedThreshold = 0.45,
            LeftWinkMinimumDifference = 0.6,
            RightWinkMinimumDifference = 0.6
        };
        Assert.Equal((expectedLeft, expectedRight), WinkProbabilityClassifier.Classify(strict, left, right));

        var detector = new WinkPageTurnDetector();
        for (var time = 0; time <= 450; time += 50)
        {
            var (leftOpen, rightOpen) = time is 250 or 300 ? (left, right) : (0.9f, 0.8f);
            var (leftEye, rightEye) = WinkProbabilityClassifier.Classify(strict, leftOpen, rightOpen);
            Assert.Equal(0, detector.Observe(leftEye, rightEye, TimeSpan.FromMilliseconds(time)));
        }
    }

    [Fact]
    public void Profile_RejectsInvalidFieldsAndDoesNotSerialiseItsValidationFlag()
    {
        var json = JsonSerializer.Serialize(Profile);
        Assert.DoesNotContain(nameof(WinkCalibrationProfile.IsValid), json, StringComparison.Ordinal);
        Assert.Equal(Profile, JsonSerializer.Deserialize<WinkCalibrationProfile>(json));
        Assert.False((Profile with { Version = 2 }).IsValid);
        Assert.False((Profile with { CameraId = " " }).IsValid);
        Assert.False((Profile with { LeftOpen = 0.3 }).IsValid);
        Assert.False((Profile with { RightClosed = double.NaN }).IsValid);
        Assert.False((Profile with { RightWinkMinimumDifference = double.PositiveInfinity }).IsValid);
        Assert.Equal((Unknown, Unknown), WinkProbabilityClassifier.Classify(Profile with { Version = 2 }, 0.9f, 0.8f));
    }

    [Fact]
    public void Classify_RapidSuccessiveWinksYieldTwoTurnsThroughRealDetector()
    {
        var detector = new WinkPageTurnDetector();
        var turns = new List<(int TimeMs, int Direction)>();
        for (var time = 0; time <= 800; time += 50)
        {
            var (leftProbability, rightProbability) = time switch
            {
                250 or 300 => (0.47f, 0.19f),
                600 or 650 => (0.29f, 0.48f),
                _ => (0.9f, 0.8f)
            };
            var (leftEye, rightEye) = WinkProbabilityClassifier.Classify(Profile, leftProbability, rightProbability);
            var direction = detector.Observe(leftEye, rightEye, TimeSpan.FromMilliseconds(time));
            if (direction != 0)
            {
                turns.Add((time, direction));
            }
        }

        Assert.Equal([(450, 1), (800, -1)], turns);
    }
}
