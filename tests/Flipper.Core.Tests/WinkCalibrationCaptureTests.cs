using Flipper.Core.Reader;
using static Flipper.Core.Reader.WinkCalibrationGesture;

namespace Flipper.Core.Tests;

public sealed class WinkCalibrationCaptureTests
{
    [Fact]
    public void Observe_CollectsRepeatedWinksWithPreRollAndStableReopening()
    {
        var capture = NewCapture(RightWink);
        var trials = new List<WinkCalibrationTrial>();
        for (var time = 1000; time <= 2600; time += 50)
        {
            var sample = time switch
            {
                1700 or 1750 => new WinkCalibrationSample(time, 0.57f, 0.64f),
                2200 or 2250 => new WinkCalibrationSample(time, 0.52f, 0.4f),
                _ => Open(time)
            };
            var trial = capture.Observe(sample);
            if (trial is not null)
            {
                trials.Add(trial);
            }
        }

        Assert.Equal(2, trials.Count);
        Assert.All(trials, trial =>
        {
            Assert.Equal(RightWink, trial.Actual);
            Assert.Equal(0, trial.WarmupMs);
            Assert.Equal(0, trial.Samples[0].TimeMs);
        });
        Assert.Equal(750, trials[0].Samples[^1].TimeMs);
        Assert.Equal(700, trials[1].Samples[^1].TimeMs);
        Assert.Equal(16, trials[0].Samples.Count);
        Assert.Equal(15, trials[1].Samples.Count);
        Assert.Equal(new WinkCalibrationSample(450, 0.57f, 0.64f), trials[0].Samples[9]);
        Assert.Equal(new WinkCalibrationSample(400, 0.52f, 0.4f), trials[1].Samples[8]);
    }

    [Fact]
    public void Observe_HigherConfidenceOpenEyeCanArmAndConfirmReopening()
    {
        var capture = NewCapture(RightWink);
        for (var time = 0; time <= 300; time += 50)
        {
            Assert.Null(capture.Observe(new(time, 0.90f, 1f)));
        }

        ObserveClosure(capture, 350, 0.53f, 0f);
        for (var time = 450; time < 650; time += 50)
        {
            Assert.Null(capture.Observe(new(time, 0.90f, 1f)));
        }

        var trial = Assert.IsType<WinkCalibrationTrial>(capture.Observe(new(650, 0.90f, 1f)));
        Assert.Equal(RightWink, trial.Actual);
        Assert.Contains(new WinkCalibrationSample(350, 0.53f, 0f), trial.Samples);
    }

    [Fact]
    public void Observe_DoesNotArmWithLessThanThreeHundredMillisecondsObserved()
    {
        var capture = NewCapture(LeftWink);
        ObserveOpen(capture, 0, 250);
        ObserveClosure(capture, 300, 0.2f, 0.75f);
        ObserveOpen(capture, 400, 700);
        ObserveClosure(capture, 750, 0.2f, 0.75f);

        var trial = Finish(capture, 850);
        Assert.Equal(LeftWink, trial.Actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observe_CapturesBilateralAndStaggeredBlinks(bool staggered)
    {
        var capture = NewCapture(Blink);
        ObserveOpen(capture, 0, 300);
        if (staggered)
        {
            Assert.Null(capture.Observe(new(350, 0.14f, 0.84f)));
            Assert.Null(capture.Observe(new(400, 0.9f, 0.17f)));
        }
        else
        {
            ObserveClosure(capture, 350, 0.14f, 0.17f);
        }

        var trial = Finish(capture, 450);
        Assert.Equal(Blink, trial.Actual);
        Assert.Contains(trial.Samples, sample => sample.LeftOpenProbability == 0.14f);
        Assert.Contains(trial.Samples, sample => sample.RightOpenProbability == 0.17f);
    }

    [Fact]
    public void Observe_CapturesWrongEyeUnderTheRequestedLabel()
    {
        var capture = NewCapture(RightWink);
        ObserveOpen(capture, 0, 300);
        ObserveClosure(capture, 350, 0.17f, 0.84f);

        var trial = Finish(capture, 450);
        Assert.Equal(RightWink, trial.Actual);
        Assert.Contains(new WinkCalibrationSample(350, 0.17f, 0.84f), trial.Samples);
    }

    [Fact]
    public void Observe_WaitsForStableReopeningAfterAnOpenReadingDuringClosure()
    {
        var capture = NewCapture(RightWink);
        ObserveOpen(capture, 0, 300);
        ObserveClosure(capture, 350, 0.9f, 0.2f);
        ObserveOpen(capture, 450, 550);
        ObserveClosure(capture, 600, 0.9f, 0.25f);

        var trial = Finish(capture, 700);
        Assert.Contains(new WinkCalibrationSample(350, 0.9f, 0.2f), trial.Samples);
        Assert.Contains(new WinkCalibrationSample(600, 0.9f, 0.25f), trial.Samples);
    }

    [Fact]
    public void Observe_DoesNotReturnATrialForOpenEyesOrSubthresholdMotion()
    {
        var capture = NewCapture(RightWink);
        ObserveOpen(capture, 0, 1000);
        ObserveClosure(capture, 1050, 0.9f, 0.73f);
        ObserveOpen(capture, 1150, 1450);
        ObserveClosure(capture, 1500, 0.9f, 0.2f);

        var trial = Finish(capture, 1600);
        Assert.DoesNotContain(trial.Samples, sample => sample.RightOpenProbability == 0.73f);
    }

    [Theory]
    [InlineData(RightWink)]
    [InlineData(LeftWink)]
    public void Observe_DiscardsOneFrameDuringAWinkPhaseAndCapturesTheNextWink(WinkCalibrationGesture gesture)
    {
        var capture = NewCapture(gesture);
        var (left, right) = gesture == RightWink ? (0.9f, 0.2f) : (0.2f, 0.84f);
        ObserveOpen(capture, 0, 300);
        Assert.Null(capture.Observe(new(350, left, right)));
        ObserveOpen(capture, 400, 700);
        ObserveClosure(capture, 750, left, right);

        var next = Finish(capture, 850);
        Assert.Equal(gesture, next.Actual);
        Assert.Equal(2, next.Samples.Count(sample => sample.LeftOpenProbability == 0.2f || sample.RightOpenProbability == 0.2f));
    }

    [Fact]
    public void Observe_KeepsASingleFrameBlinkWithStableReopening()
    {
        var capture = NewCapture(Blink);
        ObserveOpen(capture, 0, 300);
        Assert.Null(capture.Observe(new(350, 0.15f, 0.14f)));

        var blink = Finish(capture, 400);
        Assert.Equal(Blink, blink.Actual);
        Assert.Contains(new WinkCalibrationSample(350, 0.15f, 0.14f), blink.Samples);
    }

    [Theory]
    [InlineData(450, false, 0.9f, 0)]
    [InlineData(450, true, null, 0)]
    [InlineData(651, true, 0.9f, 0)]
    [InlineData(450, true, 0.9f, 1)]
    [InlineData(400, true, 0.9f, 0)]
    [InlineData(399, true, 0.9f, 0)]
    public void Observe_TrackingBreaksDiscardTheAttemptAndRequireNewOpenEvidence(
        int breakAt, bool tracked, float? probability, int rotation)
    {
        var capture = NewCapture(RightWink);
        ObserveOpen(capture, 0, 300);
        ObserveClosure(capture, 350, 0.9f, 0.2f);
        Assert.Null(capture.Observe(new(breakAt, probability, 0.84f, tracked, rotation)));
        ObserveOpen(capture, breakAt + 50, breakAt + 250, rotation);
        ObserveClosure(capture, breakAt + 300, 0.9f, 0.2f, rotation);
        ObserveOpen(capture, breakAt + 400, breakAt + 700, rotation);
        ObserveClosure(capture, breakAt + 750, 0.9f, 0.2f, rotation);

        var trial = Finish(capture, breakAt + 850, rotation);
        Assert.All(trial.Samples, sample => Assert.Equal(rotation, sample.RotationQuarterTurns));
    }

    [Theory]
    [InlineData(1500, true)]
    [InlineData(1501, false)]
    public void Observe_LimitsExcursionDurationBeforeReopening(int duration, bool expectedTrial)
    {
        var capture = NewCapture(RightWink);
        ObserveOpen(capture, 0, 300);
        for (var time = 350; time < 350 + duration; time += 100)
        {
            Assert.Null(capture.Observe(new(time, 0.9f, 0.13f)));
        }

        var reopenedAt = 350 + duration;
        ObserveOpen(capture, reopenedAt, reopenedAt + 150);
        var trial = capture.Observe(Open(reopenedAt + 200));
        Assert.Equal(expectedTrial, trial is not null);
        ObserveOpen(capture, reopenedAt + 250, reopenedAt + 300);
        ObserveClosure(capture, reopenedAt + 350, 0.9f, 0.2f);
        Assert.Equal(RightWink, Finish(capture, reopenedAt + 450).Actual);
    }

    [Fact]
    public void Reset_ClearsAnInProgressAttempt()
    {
        var capture = NewCapture(RightWink);
        ObserveOpen(capture, 0, 300);
        ObserveClosure(capture, 350, 0.9f, 0.2f);
        capture.Reset();

        ObserveOpen(capture, 450, 750);
        ObserveClosure(capture, 800, 0.9f, 0.2f);
        Assert.Equal(RightWink, Finish(capture, 900).Actual);
    }

    private static WinkCalibrationCapture NewCapture(WinkCalibrationGesture gesture) => new(gesture, 0.90, 0.84);

    private static WinkCalibrationSample Open(int timeMs, int rotation = 0) =>
        new(timeMs, 0.9f, 0.84f, RotationQuarterTurns: rotation);

    private static void ObserveOpen(WinkCalibrationCapture capture, int first, int last, int rotation = 0)
    {
        for (var time = first; time <= last; time += 50)
        {
            Assert.Null(capture.Observe(Open(time, rotation)));
        }
    }

    private static void ObserveClosure(WinkCalibrationCapture capture, int time, float left, float right, int rotation = 0)
    {
        Assert.Null(capture.Observe(new(time, left, right, RotationQuarterTurns: rotation)));
        Assert.Null(capture.Observe(new(time + 50, left, right, RotationQuarterTurns: rotation)));
    }

    private static WinkCalibrationTrial Finish(WinkCalibrationCapture capture, int reopenAt, int rotation = 0)
    {
        ObserveOpen(capture, reopenAt, reopenAt + 150, rotation);
        return Assert.IsType<WinkCalibrationTrial>(capture.Observe(Open(reopenAt + 200, rotation)));
    }
}
