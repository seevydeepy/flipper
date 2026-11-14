using Flipper.Core.Reader;
using static Flipper.Core.Reader.WinkCalibrationFailure;
using static Flipper.Core.Reader.WinkCalibrationGesture;

namespace Flipper.Core.Tests;

public sealed class WinkCalibrationTests
{
    private const string CameraId = "camera-under-test";
    private const float Open = 0.9f;
    private const float Closed = 0.1f;

    [Fact]
    public void Fit_LearnsPartialOtherEyeClosureAndPreservesBlinks()
    {
        var result = Fit();

        Assert.Equal(None, result.Failure);
        var profile = Assert.IsType<WinkCalibrationProfile>(result.Profile);
        Assert.True(profile.IsValid);
        Assert.Equal(CameraId, profile.CameraId);

        Assert.True(WinkCalibration.Matches(profile, Trial(RightWink, 8,
            (0.43f, 0.12f), (0.45f, 0.11f), (0.44f, 0.1f))));
        Assert.True(WinkCalibration.Matches(profile, Trial(LeftWink, 1,
            (0.14f, 0.44f), (0.16f, 0.45f), (0.12f, 0.47f))));
        Assert.True(WinkCalibration.Matches(profile, Trial(Blink, 4,
            (0.41f, Closed), (Closed, Closed), (Closed, Closed), (Closed, 0.4f))));
        Assert.True(WinkCalibration.Matches(profile, Trial(Neither, 1)));
    }

    [Fact]
    public void Fit_DerivesAnchorsFromMediansAndAdjacentClosurePairs()
    {
        var baseline = Baseline();
        baseline[0] = baseline[0] with { LeftOpenProbability = 0.25f, RightOpenProbability = 0.3f };
        WinkCalibrationTrial[] training =
        [
            Trial(RightWink, 0, (0.46f, 0.16f), (0.46f, 0.16f)),
            Trial(RightWink, 3, (0.46f, 0.16f), (0.46f, 0.16f)),
            Trial(LeftWink, 0, (0.18f, 0.47f), (0.18f, 0.47f)),
            Trial(LeftWink, 3, (0.18f, 0.47f), (0.18f, 0.47f)),
            Trial(Blink, 0, (0.05f, 0.02f), (0.15f, 0.14f), (0.18f, 0.16f)),
            Trial(Blink, 3, (0.2f, 0.17f), (0.22f, 0.19f)),
            new(Neither, baseline, 0)
        ];

        var profile = Assert.IsType<WinkCalibrationProfile>(WinkCalibration.Fit(CameraId, baseline, training).Profile);

        Assert.Equal(0.9, profile.LeftOpen, 5);
        Assert.Equal(0.9, profile.RightOpen, 5);
        Assert.Equal(0.155, profile.LeftClosed, 5);
        Assert.Equal(0.13, profile.RightClosed, 5);
    }

    [Fact]
    public void Fit_RequiresSufficientBaselineAndTrainingLabels()
    {
        Assert.Equal(InsufficientSamples, WinkCalibration.Fit(CameraId, Baseline()[..19], TrainingTrials()).Failure);
        Assert.Equal(InsufficientSamples, WinkCalibration.Fit(CameraId, Baseline(), TrainingTrials()[..^1]).Failure);
    }

    [Fact]
    public void Fit_RejectsSmallBlinkToOpenRange()
    {
        var training = TrainingTrials();
        training[4] = Trial(Blink, 2, (0.83f, Closed), (0.83f, Closed));
        training[5] = Trial(Blink, 3, (0.85f, Closed), (0.85f, Closed));

        var result = Fit(training);

        Assert.Equal(InsufficientEyeRange, result.Failure);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void Fit_CannotSeparateLabelledWinkFromIdenticalBlinkTrace()
    {
        var training = TrainingTrials();
        training[4] = training[0] with { Actual = Blink };

        var result = Fit(training);

        Assert.Equal(NoSeparation, result.Failure);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void Fit_RejectsMixedOrientationUnorderedTimesAndLowTrackingCoverage()
    {
        var training = TrainingTrials();
        var samples = training[0].Samples.ToArray();
        training[0] = training[0] with { Samples = samples };
        var original = samples[5];

        samples[5] = original with { RotationQuarterTurns = 1 };
        Assert.Equal(UnstableTracking, Fit(training).Failure);

        samples[5] = original with { TimeMs = samples[4].TimeMs };
        Assert.Equal(UnstableTracking, Fit(training).Failure);

        samples[5] = original with { TimeMs = double.NaN };
        Assert.Equal(UnstableTracking, Fit(training).Failure);

        samples[5] = original;
        for (var index = 0; index < samples.Length / 5; index++)
        {
            samples[index] = samples[index] with { IsTracked = false };
        }

        Assert.Equal(UnstableTracking, Fit(training).Failure);
    }

    [Fact]
    public void Matches_RequiresOneCorrectTurnAfterWarmupAndNoOtherTurns()
    {
        var profile = Assert.IsType<WinkCalibrationProfile>(Fit().Profile);
        var fresh = Trial(RightWink, 8, (0.43f, 0.12f), (0.45f, 0.11f));
        var duplicate = fresh.Samples.ToArray();
        duplicate[45] = duplicate[45] with { LeftOpenProbability = 0.45f, RightOpenProbability = 0.11f };
        duplicate[46] = duplicate[46] with { LeftOpenProbability = 0.44f, RightOpenProbability = 0.1f };

        Assert.True(WinkCalibration.Matches(profile, fresh));
        Assert.False(WinkCalibration.Matches(profile, fresh with { Actual = LeftWink }));
        Assert.False(WinkCalibration.Matches(profile, fresh with { Actual = Blink }));
        Assert.False(WinkCalibration.Matches(profile, fresh with { WarmupMs = 2000 }));
        Assert.False(WinkCalibration.Matches(profile, fresh with { Samples = duplicate }));
        Assert.False(WinkCalibration.Matches(profile, Trial(RightWink, 8)));
    }

    [Fact]
    public void Matches_DoesNotLinkClosureEvidenceAcrossLostTrackingOrLongGap()
    {
        var profile = Assert.IsType<WinkCalibrationProfile>(Fit().Profile);
        var fresh = Trial(RightWink, 0, (0.43f, 0.12f), (0.45f, 0.11f), (0.44f, 0.1f));
        var lostTracking = fresh.Samples.ToArray();
        lostTracking[24] = lostTracking[24] with { IsTracked = false };
        var gap = fresh.Samples.Select((sample, index) => index < 24
            ? sample
            : sample with { TimeMs = sample.TimeMs + 250 }).ToArray();

        Assert.True(WinkCalibration.Matches(profile, fresh));
        Assert.False(WinkCalibration.Matches(profile, fresh with { Samples = lostTracking }));
        Assert.False(WinkCalibration.Matches(profile, fresh with { Samples = gap }));
    }

    [Fact]
    public void Matches_RejectsAWarmupWinkConfirmedAfterThePrompt()
    {
        var profile = Assert.IsType<WinkCalibrationProfile>(Fit().Profile);
        var leadIn = Trial(RightWink, -6, (0.45f, 0.11f), (0.44f, 0.1f));

        // Closure at 850 and 900 ms, reopening at 950 ms, confirmation at 1050 ms.
        Assert.True(WinkCalibration.Matches(profile, leadIn with { WarmupMs = 950 }));
        Assert.False(WinkCalibration.Matches(profile, leadIn));
    }

    [Fact]
    public void Matches_RequiresPostPromptBlinkEvidenceInBothEyes()
    {
        var profile = Assert.IsType<WinkCalibrationProfile>(Fit().Profile);
        var blink = Trial(Blink, 0, (Closed, Closed), (Closed, Closed));
        var hidden = blink.Samples.ToArray();
        hidden[23] = hidden[23] with { IsTracked = false };
        hidden[24] = hidden[24] with { IsTracked = false };
        var staggered = Trial(Blink, 1, (Closed, 0.4f), (0.4f, Closed));

        Assert.True(WinkCalibration.Matches(profile, blink));
        Assert.True(WinkCalibration.Matches(profile, staggered));
        Assert.False(WinkCalibration.Matches(profile, Trial(Blink, 0)));
        Assert.False(WinkCalibration.Matches(profile, blink with { Samples = hidden }));
        Assert.False(WinkCalibration.Matches(profile, Trial(Blink, -6, (Closed, Closed), (Closed, Closed))));
    }

    [Fact]
    public void Matches_RejectsABlinkWithoutStableReopeningAtTheEnd()
    {
        var profile = Assert.IsType<WinkCalibrationProfile>(Fit().Profile);
        var blink = Trial(Blink, 0, (Closed, Closed), (Closed, Closed));
        var frames = blink.Samples.ToArray();
        var finalTrackingLoss = frames[..28];
        finalTrackingLoss[^1] = finalTrackingLoss[^1] with { IsTracked = false };

        Assert.False(WinkCalibration.Matches(profile, blink with { Samples = frames[..25] }));
        Assert.False(WinkCalibration.Matches(profile, blink with { Samples = frames[..27] }));
        Assert.False(WinkCalibration.Matches(profile, blink with { Samples = finalTrackingLoss }));
        Assert.True(WinkCalibration.Matches(profile, blink with { Samples = frames[..28] }));
    }

    [Fact]
    public void FitRepeated_ToleratesAnIncidentalBlinkWithoutChangingStrictMatching()
    {
        var incidental = Trial(RightWink, 4, (Closed, Closed), (Closed, Closed));
        WinkCalibrationTrial[] training = [.. RepeatedTrials(), incidental];

        var result = WinkCalibration.FitRepeated(CameraId, Baseline(), training);

        Assert.Equal(None, result.Failure);
        var profile = Assert.IsType<WinkCalibrationProfile>(result.Profile);
        Assert.True(WinkCalibration.IsIncidentalBlink(profile, incidental));
        Assert.False(WinkCalibration.IsIncidentalBlink(profile, incidental with { Actual = Blink }));
        Assert.False(WinkCalibration.Matches(profile, incidental));
        Assert.Equal(NoSeparation, Fit(training).Failure);

        var assessment = Assert.IsType<WinkCalibrationAssessment>(result.Assessment);
        Assert.Equal(new WinkCalibrationMatchCount(4, 4, 4), assessment.RightWinks);
        Assert.Equal(new WinkCalibrationMatchCount(4, 4, 4), assessment.Blinks);
        Assert.Equal(13, assessment.SampleCount);
        Assert.Equal(100, assessment.MatchPercentage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void FitRepeated_IncidentalBlinksCannotFillWinkQuota(int genuineWinks)
    {
        var training = RepeatedTrials();
        for (var index = genuineWinks; index < 4; index++)
        {
            training[index] = Trial(RightWink, index, (Closed, Closed), (Closed, Closed));
        }

        var result = WinkCalibration.FitRepeated(CameraId, Baseline(), training);

        Assert.Equal(NoSeparation, result.Failure);
        Assert.Null(result.Profile);
        var assessment = Assert.IsType<WinkCalibrationAssessment>(result.Assessment);
        Assert.Equal(new WinkCalibrationMatchCount(genuineWinks, genuineWinks, 4), assessment.RightWinks);
        Assert.Equal(100, assessment.MatchPercentage);
        Assert.False(assessment.HasRequiredSamples);
    }

    [Fact]
    public void FitRepeated_RejectsWrongAndMissedWinksWithoutHardBlinkEvidence()
    {
        var training = RepeatedTrials();
        var profile = Assert.IsType<WinkCalibrationProfile>(WinkCalibration.FitRepeated(CameraId, Baseline(), training).Profile);
        var wrongEye = Trial(RightWink, 3, (Closed, 0.46f), (0.1f, 0.48f));
        var missed = Trial(RightWink, 3, (0.66f, 0.66f), (0.64f, 0.65f));

        foreach (var trial in new[] { wrongEye, missed })
        {
            Assert.False(WinkCalibration.IsIncidentalBlink(profile, trial));
            Assert.Equal(NoSeparation, WinkCalibration.FitRepeated(CameraId, Baseline(), [.. training, trial]).Failure);
        }
    }

    [Fact]
    public void FitRepeated_ReportsFailedTrialsWithoutAcceptingTheDiagnosticCandidate()
    {
        var coincidentalBlink = Trial(RightWink, 4, (Closed, Closed), (Closed, Closed));
        var missed = Trial(RightWink, 5, (0.65f, 0.65f), (0.65f, 0.65f));
        WinkCalibrationTrial[] trials = [.. RepeatedTrials(), coincidentalBlink, missed];

        var result = WinkCalibration.FitRepeated(CameraId, Baseline(), trials);

        Assert.Equal(NoSeparation, result.Failure);
        Assert.Null(result.Profile);
        var assessment = Assert.IsType<WinkCalibrationAssessment>(result.Assessment);
        Assert.True(assessment.Candidate.IsValid);
        Assert.True(assessment.HasRequiredSamples);
        Assert.Equal(new WinkCalibrationMatchCount(4, 5, 4), assessment.RightWinks);
        Assert.Equal(new WinkCalibrationMatchCount(4, 4, 4), assessment.LeftWinks);
        Assert.Equal(new WinkCalibrationMatchCount(4, 4, 4), assessment.Blinks);
        Assert.Equal(new WinkCalibrationMatchCount(1, 1, 1), assessment.OpenEyes);
        Assert.Equal(13, assessment.MatchedCount);
        Assert.Equal(14, assessment.SampleCount);
        Assert.Equal(92.8571, assessment.MatchPercentage, precision: 4);

        var latest = Assert.IsType<WinkCalibrationAssessment>(
            WinkCalibration.AssessRepeated(assessment.Candidate, trials[..^1]));
        Assert.Equal(100, latest.MatchPercentage);
        Assert.Equal(13, latest.SampleCount);
        Assert.Null(WinkCalibration.AssessRepeated(assessment.Candidate, [missed with { Samples = [] }]));
        Assert.Null(WinkCalibration.AssessRepeated(assessment.Candidate with { CameraId = "" }, trials));
    }

    [Fact]
    public void IsIncidentalBlink_RequiresPostPromptClosureAndNoDetectedTurn()
    {
        var profile = Assert.IsType<WinkCalibrationProfile>(Fit().Profile);
        var beforePrompt = Trial(RightWink, -6, (Closed, Closed), (Closed, Closed));
        var staggered = Trial(RightWink, 0, (Closed, 0.4f), (0.4f, Closed));
        var blink = Trial(RightWink, 0, (Closed, Closed), (Closed, Closed));
        var samples = blink.Samples.ToArray();
        samples[39] = new(1950, 0.45f, Closed);
        samples[40] = new(2000, 0.44f, 0.11f);
        var withWink = blink with { Samples = samples };

        Assert.False(WinkCalibration.IsIncidentalBlink(profile, beforePrompt));
        Assert.True(WinkCalibration.Matches(profile, staggered with { Actual = Blink }));
        Assert.False(WinkCalibration.IsIncidentalBlink(profile, staggered));
        Assert.True(WinkCalibration.Matches(profile, withWink));
        Assert.False(WinkCalibration.IsIncidentalBlink(profile, withWink));
    }

    private static WinkCalibrationResult Fit(WinkCalibrationTrial[]? training = null) =>
        WinkCalibration.Fit(CameraId, Baseline(), training ?? TrainingTrials());

    private static WinkCalibrationTrial[] TrainingTrials() =>
    [
        Trial(RightWink, 0, (0.46f, Closed), (0.43f, 0.11f), (0.46f, 0.13f)),
        Trial(RightWink, 3, (0.44f, 0.12f), (0.46f, 0.13f), (0.45f, Closed)),
        Trial(LeftWink, 0, (Closed, 0.46f), (0.12f, 0.44f), (0.13f, 0.45f)),
        Trial(LeftWink, 3, (0.11f, 0.44f), (0.13f, 0.46f), (Closed, 0.45f)),
        Trial(Blink, 2, (Closed, Closed), (0.11f, 0.12f), (Closed, Closed)),
        Trial(Blink, 3, (Open, Closed), (Closed, Closed), (Closed, Closed), (Closed, Open)),
        new(Neither, Baseline(), 0)
    ];

    private static WinkCalibrationTrial[] RepeatedTrials() =>
    [
        .. Enumerable.Range(0, 4).Select(offset => Trial(RightWink, offset, (0.45f, Closed), (0.44f, 0.11f))),
        .. Enumerable.Range(0, 4).Select(offset => Trial(LeftWink, offset, (Closed, 0.46f), (0.11f, 0.45f))),
        .. Enumerable.Range(0, 4).Select(offset => Trial(Blink, offset, (Closed, Closed), (Closed, Closed))),
        new(Neither, Baseline(), 0)
    ];

    private static WinkCalibrationSample[] Baseline() =>
        Enumerable.Range(0, 60).Select(index => new WinkCalibrationSample(index * 50, Open, Open)).ToArray();

    private static WinkCalibrationTrial Trial(WinkCalibrationGesture actual, int startOffset, params (float Left, float Right)[] states)
    {
        var samples = Baseline();
        for (var index = 0; index < states.Length; index++)
        {
            var frame = 23 + startOffset + index;
            var (left, right) = states[index];
            samples[frame] = samples[frame] with { LeftOpenProbability = left, RightOpenProbability = right };
        }

        return new WinkCalibrationTrial(actual, samples);
    }
}
