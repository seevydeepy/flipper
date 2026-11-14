namespace Flipper.Core.Reader;

public sealed class WinkCalibrationCapture
{
    private const double BaselineTolerance = 0.10;
    private const double ExcursionDrop = 0.12;
    private const double InitialOpenMs = 300;
    private const double PreRollMs = 400;
    private const double ReopenMs = 200;
    private const double MaximumGapMs = 250;
    private const double MaximumExcursionMs = 1500;
    private const int MaximumSamples = 1024;

    private readonly WinkCalibrationGesture _gesture;
    private readonly int _minimumExcursionSamples;
    private readonly double _leftOpenBaseline;
    private readonly double _rightOpenBaseline;
    private readonly List<WinkCalibrationSample> _samples = [];
    private WinkCalibrationSample? _previousSample;
    private double? _openSince;
    private double? _excursionStartedAt;
    private int _excursionSamples;

    public WinkCalibrationCapture(WinkCalibrationGesture gesture, double leftOpenBaseline, double rightOpenBaseline)
    {
        if (!Enum.IsDefined(gesture))
        {
            throw new ArgumentOutOfRangeException(nameof(gesture));
        }

        if (!WinkCalibrationProfile.IsProbability(leftOpenBaseline))
        {
            throw new ArgumentOutOfRangeException(nameof(leftOpenBaseline));
        }

        if (!WinkCalibrationProfile.IsProbability(rightOpenBaseline))
        {
            throw new ArgumentOutOfRangeException(nameof(rightOpenBaseline));
        }

        _gesture = gesture;
        _minimumExcursionSamples = gesture is WinkCalibrationGesture.RightWink or WinkCalibrationGesture.LeftWink ? 2 : 1;
        _leftOpenBaseline = leftOpenBaseline;
        _rightOpenBaseline = rightOpenBaseline;
    }

    public WinkCalibrationTrial? Observe(WinkCalibrationSample sample)
    {
        if (!sample.IsTracked || !WinkProbabilityClassifier.HasProbabilities(sample.LeftOpenProbability, sample.RightOpenProbability)
            || !double.IsFinite(sample.TimeMs) || sample.TimeMs < 0 || sample.TimeMs >= TimeSpan.MaxValue.TotalMilliseconds)
        {
            Reset();
            return null;
        }

        if (_samples.Count >= MaximumSamples
            || (_previousSample is { } previous && (sample.TimeMs <= previous.TimeMs
                || sample.TimeMs - previous.TimeMs > MaximumGapMs || sample.RotationQuarterTurns != previous.RotationQuarterTurns)))
        {
            Reset();
        }

        var leftDrop = _leftOpenBaseline - sample.LeftOpenProbability!.Value;
        var rightDrop = _rightOpenBaseline - sample.RightOpenProbability!.Value;
        // A higher open-eye probability still counts as open when it exceeds the baseline.
        var bothOpen = leftDrop <= BaselineTolerance && rightDrop <= BaselineTolerance;
        var previousSample = _previousSample;
        _previousSample = sample;
        if (_excursionStartedAt is null)
        {
            if (bothOpen)
            {
                _openSince ??= sample.TimeMs;
                _samples.Add(sample);
                TrimPreRoll(sample.TimeMs);
                return null;
            }

            if (_openSince is not { } openSince || previousSample is not { } prior
                || prior.TimeMs - openSince < InitialOpenMs)
            {
                _samples.Clear();
                _openSince = null;
                return null;
            }

            // Keep the transition from baseline, then require a larger drop before returning a trial.
            _excursionStartedAt = sample.TimeMs;
        }

        _openSince = bothOpen ? _openSince ?? sample.TimeMs : null;
        if ((_openSince ?? sample.TimeMs) - _excursionStartedAt.Value > MaximumExcursionMs)
        {
            Reset();
            if (bothOpen)
            {
                _previousSample = sample;
                _openSince = sample.TimeMs;
                _samples.Add(sample);
            }

            return null;
        }

        _samples.Add(sample);
        if (leftDrop >= ExcursionDrop || rightDrop >= ExcursionDrop)
        {
            _excursionSamples++;
        }

        if (_openSince is not { } reopened || sample.TimeMs - reopened < ReopenMs)
        {
            return null;
        }

        WinkCalibrationTrial? trial = null;
        if (_excursionSamples >= _minimumExcursionSamples)
        {
            var firstTimeMs = _samples[0].TimeMs;
            var frames = _samples.Select(frame => frame with { TimeMs = frame.TimeMs - firstTimeMs }).ToArray();
            trial = new WinkCalibrationTrial(_gesture, frames, WarmupMs: 0);
        }

        _samples.RemoveRange(0, _samples.FindIndex(frame => frame.TimeMs == reopened));
        _excursionStartedAt = null;
        _excursionSamples = 0;
        return trial;
    }

    public void Reset()
    {
        _previousSample = null;
        _samples.Clear();
        _openSince = null;
        _excursionStartedAt = null;
        _excursionSamples = 0;
    }

    private void TrimPreRoll(double timeMs)
    {
        var first = 0;
        while (first + 1 < _samples.Count && _samples[first + 1].TimeMs <= timeMs - PreRollMs)
        {
            first++;
        }

        // Retain the sample immediately before the cutoff as evidence of the open interval.
        _samples.RemoveRange(0, first);
    }
}
