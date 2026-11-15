using Flipper.Core.Reader;
using static Flipper.Core.Reader.WinkCalibrationPhase;

namespace Flipper.Core.Tests;

public sealed class WinkCalibrationSessionTests
{
    private const float Open = 0.9f;
    private const float Closed = 0.1f;
    private const float PartlyClosed = 0.45f;
    private readonly WinkCalibrationSession _session = new("front-camera");
    private int _timeMs;

    [Fact]
    public void ContinuousFrames_CollectTrainingAndFreshChecksBeforeReady()
    {
        var request = CollectTraining(incidentalRightPhaseBlinks: 1);
        Assert.Equal("front-camera", request.CameraId);
        Assert.Equal(6, request.Trials.Count(trial => trial.Actual == WinkCalibrationGesture.RightWink));
        var fit = Fit(request);
        Assert.Equal(WinkCalibrationFailure.None, fit.Failure);
        Assert.NotNull(fit.Profile);
        _session.ApplyFit(fit);

        Assert.Equal(RightCheck, _session.Phase);
        Assert.Equal(14, _session.FreshAttemptsRequired);
        Assert.Equal(0, _session.FreshAttemptsCompleted);
        FeedOpen(2300);
        FeedGesture(Closed, Closed);
        Assert.Equal(RightCheck, _session.Phase);
        Assert.Equal(0, _session.PhaseSamples);
        FeedGesture(0.65f, 0.65f);
        Assert.Equal(1, _session.FreshAttemptsCompleted);
        Assert.Equal(0, _session.FreshCorrectCount);
        Assert.Equal(fit.Assessment, _session.Assessment);
        FeedGestures(RightCheck, LeftCheck, PartlyClosed, Closed, count: 4);
        Assert.Equal(5, _session.FreshAttemptsCompleted);
        Assert.Equal(4, _session.FreshCorrectCount);
        FeedOpen(2300);
        FeedGesture(0.65f, 0.65f);
        FeedGestures(LeftCheck, BlinkCheck, Closed, PartlyClosed, count: 4);
        Assert.Equal(10, _session.FreshAttemptsCompleted);
        FeedGestures(BlinkCheck, OpenCheck, Closed, Closed, count: 3);
        Assert.Equal(OpenCheck, _session.Phase);
        Assert.Equal(13, _session.FreshAttemptsCompleted);

        FeedOpen(5000);

        Assert.Equal(Ready, _session.Phase);
        Assert.Equal(14, _session.FreshAttemptsCompleted);
        Assert.Equal(12, _session.FreshCorrectCount);
        Assert.Equal(fit.Profile, _session.Profile);
        Assert.Null(_session.TakeFitRequest());
    }

    [Fact]
    public void WrongEyeDuringFreshCheck_IsNotCountedAndKeepsTheCandidate()
    {
        var fit = Fit(CollectTraining());
        Assert.NotNull(fit.Profile);
        _session.ApplyFit(fit);
        FeedGestures(RightCheck, LeftCheck, PartlyClosed, Closed, count: 5);
        FeedOpen(2300);
        FeedGesture(PartlyClosed, Closed); // A right wink during the left-wink check.

        Assert.Equal(LeftCheck, _session.Phase);
        Assert.Equal(1, _session.RejectedAttempts);
        Assert.Equal(0, _session.PhaseSamples);
        Assert.Equal(5, _session.FreshAttemptsCompleted);
        Assert.Equal(fit.Profile, _session.Profile);
        Assert.Null(_session.TakeFitRequest());
        FeedGesture(Closed, PartlyClosed);
        Assert.Equal(1, _session.PhaseSamples);
    }

    [Fact]
    public void WrongEyeAndOneEyeGesturesDuringCollection_AreNotCounted()
    {
        FeedOpen(5000);
        FeedOpen(2300);
        FeedGesture(Closed, PartlyClosed); // A left wink when right winks are asked for.
        Assert.Equal(RightWinks, _session.Phase);
        Assert.Equal(0, _session.PhaseSamples);
        Assert.Equal(1, _session.RejectedAttempts);
        FeedGesture(PartlyClosed, Closed);
        Assert.Equal(1, _session.PhaseSamples);
        for (var count = 1; count < 6; count++) FeedGesture(PartlyClosed, Closed);
        FeedGestures(LeftWinks, Blinks, Closed, PartlyClosed, count: 6);

        FeedOpen(2300);
        FeedGesture(Open, Closed); // The left eye stayed open, so this is a wink, not a blink.
        Assert.Equal(0, _session.PhaseSamples);
        Assert.Equal(2, _session.RejectedAttempts);
        FeedGesture(Closed, PartlyClosed); // An uneven blink is still a blink.
        Assert.Equal(1, _session.PhaseSamples);
        Assert.Equal(2, _session.RejectedAttempts);
    }

    [Fact]
    public void WinkCheck_ComparesEachEyeWithItsOwnOpenLevel()
    {
        const float rightOpen = 0.35f; // This eye reads low even when open.
        Feed(5000, Open, rightOpen);
        Assert.Equal(RightWinks, _session.Phase);
        Feed(2300, Open, rightOpen);
        Feed(150, 0.25f, 0.05f); // The left eye drops further in raw terms, but the right eye closes more of its range.
        Feed(600, Open, rightOpen);

        Assert.Equal(1, _session.PhaseSamples);
        Assert.Equal(0, _session.RejectedAttempts);
    }

    [Fact]
    public void RepeatedRejections_CountTheFourthGestureSoNoStepCanBlockForever()
    {
        FeedOpen(5000);
        FeedOpen(2300);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            FeedGesture(Closed, PartlyClosed);
            Assert.Equal(attempt, _session.RejectedAttempts);
            Assert.Equal(0, _session.PhaseSamples);
        }

        FeedGesture(Closed, PartlyClosed);

        Assert.Equal(3, _session.RejectedAttempts);
        Assert.Equal(1, _session.PhaseSamples);
    }

    [Fact]
    public void RepeatedFailedFits_StopAfterThreeExtraRounds()
    {
        CollectTraining();
        for (var round = 2; round <= 4; round++)
        {
            _session.ApplyFit(new WinkCalibrationResult(null, WinkCalibrationFailure.NoSeparation));
            Assert.Equal(round, _session.CollectionRound);
            FeedGestures(RightWinks, LeftWinks, PartlyClosed, Closed, count: 3);
            FeedGestures(LeftWinks, Blinks, Closed, PartlyClosed, count: 3);
            FeedGestures(Blinks, Fitting, Closed, Closed, count: 3);
            Assert.NotNull(_session.TakeFitRequest());
        }

        _session.ApplyFit(new WinkCalibrationResult(null, WinkCalibrationFailure.NoSeparation));

        Assert.Equal(Stopped, _session.Phase);
        Assert.Equal(4, _session.CollectionRound);
        Assert.Null(_session.Profile);
        FeedOpen(2300);
        FeedGesture(PartlyClosed, Closed);
        Assert.Equal(Stopped, _session.Phase);
        Assert.Null(_session.TakeFitRequest());
    }

    [Fact]
    public void RepeatedFailedChecks_StopAfterThreeExtraRounds()
    {
        var fit = Fit(CollectTraining());
        _session.ApplyFit(fit);
        for (var round = 1; round <= 4; round++)
        {
            Assert.Equal(RightCheck, _session.Phase);
            Assert.Equal(round, _session.CheckRound);
            FeedOpen(2300);
            FeedGesture(0.65f, 0.65f);
            FeedGesture(0.66f, 0.66f);
            Assert.Equal(Fitting, _session.Phase);
            Assert.NotNull(_session.TakeFitRequest());
            _session.ApplyFit(fit);
        }

        Assert.Equal(Stopped, _session.Phase);
        Assert.Equal(4, _session.CheckRound);
        Assert.Null(_session.Profile);
    }

    [Fact]
    public void SecondMissInOneEye_RefitsWithMissesAndStartsAnotherFreshCheck()
    {
        var request = CollectTraining();
        _session.ApplyFit(Fit(request));
        FeedOpen(2300);
        FeedGesture(0.65f, 0.65f);
        Assert.Equal(RightCheck, _session.Phase);
        Assert.Equal(1, _session.FreshAttemptsCompleted);
        Assert.Null(_session.TakeFitRequest());
        FeedGesture(PartlyClosed, Closed);
        FeedGesture(0.66f, 0.66f);

        Assert.Equal(Fitting, _session.Phase);
        Assert.Null(_session.Profile);
        Assert.Equal(0, _session.FreshAttemptsCompleted);
        var refit = Assert.IsType<WinkCalibrationFitRequest>(_session.TakeFitRequest());
        Assert.Equal(request.Trials.Count + 2, refit.Trials.Count);
        Assert.Contains(refit.Trials[^2].Samples, sample => sample.RightOpenProbability == 0.65f);
        Assert.Contains(refit.Trials[^1].Samples, sample => sample.RightOpenProbability == 0.66f);
        _session.ApplyFit(Fit(refit));
        Assert.Equal(RightWinks, _session.Phase);
        Assert.True(_session.NeedsMoreSamples);
        Assert.Equal(0, _session.FreshAttemptsCompleted);
    }

    [Fact]
    public void TurnDuringFreshBlinkCheck_RejectsTheCandidate()
    {
        var request = CollectTraining();
        _session.ApplyFit(Fit(request));
        FeedGestures(RightCheck, LeftCheck, PartlyClosed, Closed, count: 5);
        FeedGestures(LeftCheck, BlinkCheck, Closed, PartlyClosed, count: 5);
        FeedOpen(2300);
        FeedGesture(PartlyClosed, Closed);

        Assert.Equal(Fitting, _session.Phase);
        Assert.Null(_session.Profile);
        var refit = Assert.IsType<WinkCalibrationFitRequest>(_session.TakeFitRequest());
        Assert.Equal(WinkCalibrationGesture.Blink, refit.Trials[^1].Actual);
        Assert.Equal(WinkCalibrationFailure.NoSeparation, Fit(refit).Failure);
    }

    [Fact]
    public void FailedFitAfterIncidentalBlinks_CollectsMoreOfOnlyTheShortGesture()
    {
        var request = CollectTraining(incidentalRightPhaseBlinks: 3);
        var failed = Fit(request);
        Assert.Equal(WinkCalibrationFailure.NoSeparation, failed.Failure);
        _session.ApplyFit(failed);

        Assert.Equal(RightWinks, _session.Phase);
        Assert.Equal([RightWinks], _session.CollectionGestures);
        Assert.True(_session.NeedsMoreSamples);
        Assert.Null(_session.Profile);
        Assert.Equal(3, _session.Assessment!.RightWinks.Matched);
        Assert.False(_session.Assessment.HasRequiredSamples);
        FeedOpen(2300);
        FeedGesture(PartlyClosed, Closed);
        Assert.Equal(4, _session.Assessment.RightWinks.Matched);
        Assert.True(_session.Assessment.HasRequiredSamples);
        Assert.Equal(100, _session.Assessment.MatchPercentage);
        Assert.Null(_session.Profile);
        Assert.Equal(RightWinks, _session.Phase);
        FeedGestures(RightWinks, Fitting, PartlyClosed, Closed, count: 2);
        var next = Assert.IsType<WinkCalibrationFitRequest>(_session.TakeFitRequest());
        Assert.Equal(request.Trials.Count + 3, next.Trials.Count);
        Assert.Equal(WinkCalibrationFailure.None, Fit(next).Failure);
    }

    [Fact]
    public void LostTrackingAndFrameGap_DiscardUnfinishedBaselineAndGesture()
    {
        FeedOpen(3500);
        Assert.InRange(_session.StillProgress, 0.6, 0.9);
        _session.Observe(new(_timeMs, null, null, IsTracked: false));
        _timeMs += 50;
        Assert.Equal(0, _session.StillProgress);
        FeedOpen(2800);
        Assert.Equal(Baseline, _session.Phase);
        FeedOpen(1800);
        Assert.Equal(RightWinks, _session.Phase);

        FeedOpen(2300);
        Feed(100, PartlyClosed, Closed);
        _timeMs += 300;
        FeedOpen(2300);
        Assert.Equal(0, _session.PhaseSamples);
        FeedGesture(PartlyClosed, Closed);
        Assert.Equal(1, _session.PhaseSamples);
    }

    [Fact]
    public void RightWinkCollection_AdvancesWhenRightEyeOpensMoreThanDuringBaseline()
    {
        Feed(5000, Open, 0.84f);
        Assert.Equal(RightWinks, _session.Phase);
        Feed(2300, Open, 1f);
        Feed(150, PartlyClosed, 0f);
        Feed(600, Open, 1f);

        Assert.Equal(RightWinks, _session.Phase);
        Assert.Equal(1, _session.PhaseSamples);
    }

    private WinkCalibrationFitRequest CollectTraining(int incidentalRightPhaseBlinks = 0)
    {
        FeedOpen(5000);
        Assert.Equal(RightWinks, _session.Phase);
        FeedOpen(2300);
        for (var count = 0; count < incidentalRightPhaseBlinks; count++)
        {
            FeedGesture(Closed, Closed);
        }
        Assert.Equal(incidentalRightPhaseBlinks, _session.PhaseSamples);
        FeedGestures(RightWinks, LeftWinks, PartlyClosed, Closed, count: 6 - incidentalRightPhaseBlinks);
        FeedGestures(LeftWinks, Blinks, Closed, PartlyClosed, count: 6);
        FeedGestures(Blinks, Fitting, Closed, Closed, count: 6);
        var request = Assert.IsType<WinkCalibrationFitRequest>(_session.TakeFitRequest());
        Assert.Null(_session.TakeFitRequest());
        return request;
    }

    private void FeedGestures(
        WinkCalibrationPhase phase, WinkCalibrationPhase next, float left, float right, int count)
    {
        Assert.Equal(phase, _session.Phase);
        FeedOpen(2300);
        for (var index = 0; index < count; index++)
        {
            FeedGesture(left, right);
        }
        Assert.Equal(next, _session.Phase);
    }

    private void FeedGesture(float left, float right)
    {
        Feed(150, left, right);
        FeedOpen(600);
    }

    private void FeedOpen(int durationMs) => Feed(durationMs, Open, Open);

    private void Feed(int durationMs, float left, float right)
    {
        var stopAt = _timeMs + durationMs;
        for (; _timeMs < stopAt; _timeMs += 50)
        {
            _session.Observe(new(_timeMs, left, right));
        }
    }

    private static WinkCalibrationResult Fit(WinkCalibrationFitRequest request) =>
        WinkCalibration.FitRepeated(request.CameraId, request.OpenSamples, request.Trials);
}
