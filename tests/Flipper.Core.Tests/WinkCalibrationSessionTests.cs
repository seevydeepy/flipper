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
        FeedOpen(2300);
        FeedGesture(Closed, Closed);
        Assert.Equal(RightCheck, _session.Phase);
        Assert.Equal(0, _session.PhaseSamples);
        FeedGestures(RightCheck, LeftCheck, PartlyClosed, Closed, count: 3);
        FeedGestures(LeftCheck, BlinkCheck, Closed, PartlyClosed, count: 3);
        FeedGestures(BlinkCheck, OpenCheck, Closed, Closed, count: 3);
        Assert.Equal(OpenCheck, _session.Phase);

        FeedOpen(5000);

        Assert.Equal(Ready, _session.Phase);
        Assert.Equal(fit.Profile, _session.Profile);
        Assert.Null(_session.TakeFitRequest());
    }

    [Fact]
    public void FailedFreshCheck_InvalidatesCandidateAndBecomesTrainingEvidence()
    {
        var request = CollectTraining();
        var fit = Fit(request);
        Assert.NotNull(fit.Profile);
        _session.ApplyFit(fit);
        FeedGestures(RightCheck, LeftCheck, PartlyClosed, Closed, count: 3);
        FeedOpen(2300);
        FeedGesture(PartlyClosed, Closed); // A right wink during the left-wink check.

        Assert.Equal(Fitting, _session.Phase);
        Assert.Null(_session.Profile);
        Assert.True(_session.NeedsMoreSamples);
        var refit = Assert.IsType<WinkCalibrationFitRequest>(_session.TakeFitRequest());
        Assert.Equal(request.Trials.Count + 1, refit.Trials.Count);
        var failedCheck = refit.Trials[^1];
        Assert.Equal(WinkCalibrationGesture.LeftWink, failedCheck.Actual);
        Assert.Contains(failedCheck.Samples, sample => sample.LeftOpenProbability == PartlyClosed
            && sample.RightOpenProbability == Closed);
        Assert.Equal(WinkCalibrationFailure.NoSeparation, Fit(refit).Failure);
    }

    [Fact]
    public void FailedFitAfterIncidentalBlinks_AutomaticallyCollectsAnotherRound()
    {
        var request = CollectTraining(incidentalRightPhaseBlinks: 3);
        var failed = Fit(request);
        Assert.Equal(WinkCalibrationFailure.NoSeparation, failed.Failure);
        _session.ApplyFit(failed);

        Assert.Equal(RightWinks, _session.Phase);
        Assert.True(_session.NeedsMoreSamples);
        Assert.Null(_session.Profile);
        FeedGestures(RightWinks, LeftWinks, PartlyClosed, Closed, count: 3);
        FeedGestures(LeftWinks, Blinks, Closed, PartlyClosed, count: 3);
        FeedGestures(Blinks, Fitting, Closed, Closed, count: 3);
        var next = Assert.IsType<WinkCalibrationFitRequest>(_session.TakeFitRequest());
        Assert.Equal(request.Trials.Count + 9, next.Trials.Count);
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
