namespace Flipper.Core.Reader;

public enum WinkCalibrationPhase
{
    Baseline, RightWinks, LeftWinks, Blinks, Fitting, RightCheck, LeftCheck, BlinkCheck, OpenCheck, Ready
}

public sealed record WinkCalibrationFitRequest(string CameraId,
    IReadOnlyList<WinkCalibrationSample> OpenSamples, IReadOnlyList<WinkCalibrationTrial> Trials);

// One owner feeds frames and applies fit results. Fitting can run outside the capture thread.
public sealed class WinkCalibrationSession(string cameraId)
{
    private const int InitialSamples = 6;
    private const int ExtraSamples = 3;
    private const int CheckSamples = 3;
    private const int RetainedSamples = 12;
    private const double StillTimeMs = 2500;
    private readonly List<WinkCalibrationSample> _still = [];
    private readonly List<WinkCalibrationTrial> _training = [];
    private WinkCalibrationSample[] _baseline = [];
    private WinkCalibrationCapture? _capture;
    private double _leftOpen;
    private double _rightOpen;
    private double? _readyAt;
    private WinkCalibrationSample? _previous;
    private bool _fitTaken;
    private bool _extraRound;

    public WinkCalibrationPhase Phase { get; private set; } = WinkCalibrationPhase.Baseline;
    public int PhaseSamples { get; private set; }
    public int RequiredSamples { get; private set; }
    public double StillProgress { get; private set; }
    public bool IsSettling { get; private set; } = true;
    public bool NeedsMoreSamples { get; private set; }
    public WinkCalibrationFailure LastFailure { get; private set; }
    public WinkCalibrationProfile? Profile { get; private set; }

    public void Pause()
    {
        _capture?.Reset();
        _still.Clear();
        _previous = null;
        _readyAt = null;
        StillProgress = 0;
        IsSettling = true;
    }

    public void Observe(WinkCalibrationSample sample)
    {
        if (Phase is WinkCalibrationPhase.Fitting or WinkCalibrationPhase.Ready) return;
        if (!sample.IsTracked || !WinkProbabilityClassifier.HasProbabilities(sample.LeftOpenProbability, sample.RightOpenProbability)
            || !double.IsFinite(sample.TimeMs))
        {
            Pause();
            return;
        }
        if (_previous is { } previous && (sample.TimeMs <= previous.TimeMs || sample.TimeMs - previous.TimeMs > 250
            || sample.RotationQuarterTurns != previous.RotationQuarterTurns)) Pause();
        _previous = sample;
        _readyAt ??= sample.TimeMs + 1500;
        IsSettling = sample.TimeMs < _readyAt;
        if (IsSettling) return;

        if (Phase is WinkCalibrationPhase.Baseline or WinkCalibrationPhase.OpenCheck)
        {
            _still.Add(sample);
            StillProgress = Math.Clamp((sample.TimeMs - _still[0].TimeMs) / StillTimeMs, 0, 1);
            if (StillProgress < 1 || _still.Count < 20) return;
            var start = _still[0].TimeMs;
            var samples = _still.Select(value => value with { TimeMs = value.TimeMs - start }).ToArray();
            var trial = new WinkCalibrationTrial(WinkCalibrationGesture.Neither, samples, 0);
            if (Phase == WinkCalibrationPhase.Baseline)
            {
                _baseline = samples;
                _leftOpen = Median(samples.Select(value => (double)value.LeftOpenProbability!.Value));
                _rightOpen = Median(samples.Select(value => (double)value.RightOpenProbability!.Value));
                AddTraining(trial);
                Begin(WinkCalibrationPhase.RightWinks, InitialSamples);
            }
            else if (WinkCalibration.Matches(Profile!, trial))
            {
                Phase = WinkCalibrationPhase.Ready;
            }
            else
            {
                AddTraining(trial);
                Refit();
            }
            return;
        }

        if (_capture?.Observe(sample) is not { } completed) return;
        if (Phase is WinkCalibrationPhase.RightCheck or WinkCalibrationPhase.LeftCheck or WinkCalibrationPhase.BlinkCheck)
        {
            if (WinkCalibration.IsIncidentalBlink(Profile!, completed)) return;
            if (!WinkCalibration.Matches(Profile!, completed))
            {
                // A failed check becomes training evidence. All checks must then use fresh gestures.
                AddTraining(completed);
                Refit();
                return;
            }
        }
        else AddTraining(completed);
        if (++PhaseSamples < RequiredSamples) return;
        switch (Phase)
        {
            case WinkCalibrationPhase.RightWinks:
                Begin(WinkCalibrationPhase.LeftWinks, _extraRound ? ExtraSamples : InitialSamples);
                break;
            case WinkCalibrationPhase.LeftWinks:
                Begin(WinkCalibrationPhase.Blinks, _extraRound ? ExtraSamples : InitialSamples);
                break;
            case WinkCalibrationPhase.Blinks:
                Phase = WinkCalibrationPhase.Fitting;
                _fitTaken = false;
                break;
            case WinkCalibrationPhase.RightCheck:
                Begin(WinkCalibrationPhase.LeftCheck, CheckSamples);
                break;
            case WinkCalibrationPhase.LeftCheck:
                Begin(WinkCalibrationPhase.BlinkCheck, CheckSamples);
                break;
            case WinkCalibrationPhase.BlinkCheck:
                Begin(WinkCalibrationPhase.OpenCheck, 0);
                break;
        }
    }

    public WinkCalibrationFitRequest? TakeFitRequest()
    {
        if (Phase != WinkCalibrationPhase.Fitting || _fitTaken) return null;
        _fitTaken = true;
        return new(cameraId, _baseline, _training.ToArray());
    }

    public void ApplyFit(WinkCalibrationResult result)
    {
        if (Phase != WinkCalibrationPhase.Fitting || !_fitTaken) return;
        LastFailure = result.Failure;
        if (result.Profile is not { IsValid: true } profile)
        {
            NeedsMoreSamples = true;
            _extraRound = true;
            Begin(WinkCalibrationPhase.RightWinks, ExtraSamples);
            return;
        }
        Profile = profile;
        NeedsMoreSamples = false;
        Begin(WinkCalibrationPhase.RightCheck, CheckSamples);
    }

    private void Begin(WinkCalibrationPhase phase, int required)
    {
        Phase = phase;
        PhaseSamples = 0;
        RequiredSamples = required;
        Pause();
        var gesture = phase switch
        {
            WinkCalibrationPhase.RightWinks or WinkCalibrationPhase.RightCheck => WinkCalibrationGesture.RightWink,
            WinkCalibrationPhase.LeftWinks or WinkCalibrationPhase.LeftCheck => WinkCalibrationGesture.LeftWink,
            _ => WinkCalibrationGesture.Blink
        };
        _capture = required > 0 ? new(gesture, _leftOpen, _rightOpen) : null;
    }

    private void Refit()
    {
        Profile = null;
        NeedsMoreSamples = true;
        LastFailure = WinkCalibrationFailure.NoSeparation;
        Phase = WinkCalibrationPhase.Fitting;
        _fitTaken = false;
    }

    private void AddTraining(WinkCalibrationTrial trial)
    {
        _training.Add(trial);
        var limit = trial.Actual == WinkCalibrationGesture.Neither ? 3 : RetainedSamples;
        while (_training.Count(value => value.Actual == trial.Actual) > limit)
        {
            _training.RemoveAt(_training.FindIndex(value => value.Actual == trial.Actual));
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }
}
