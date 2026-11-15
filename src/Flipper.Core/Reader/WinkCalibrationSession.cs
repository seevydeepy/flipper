namespace Flipper.Core.Reader;

public enum WinkCalibrationPhase
{
    Baseline, RightWinks, LeftWinks, Blinks, Fitting, RightCheck, LeftCheck, BlinkCheck, OpenCheck, Ready, Stopped
}

public sealed record WinkCalibrationFitRequest(string CameraId,
    IReadOnlyList<WinkCalibrationSample> OpenSamples, IReadOnlyList<WinkCalibrationTrial> Trials);

// One owner feeds frames and applies fit results. Fitting can run outside the capture thread.
public sealed class WinkCalibrationSession(string cameraId)
{
    private const int InitialSamples = 6;
    private const int ExtraSamples = 3;
    private const int RetainedSamples = 12;
    private const double StillTimeMs = 2500;
    public const int MaximumExtraRounds = 3;
    private const int MaximumRejectionsInARow = 3;
    private static readonly WinkCalibrationPhase[] AllGestures =
        [WinkCalibrationPhase.RightWinks, WinkCalibrationPhase.LeftWinks, WinkCalibrationPhase.Blinks];
    private readonly List<WinkCalibrationSample> _still = [];
    private readonly List<WinkCalibrationTrial> _training = [];
    private readonly List<WinkCalibrationTrial> _freshMisses = [];
    private WinkCalibrationSample[] _baseline = [];
    private WinkCalibrationCapture? _capture;
    private double _leftOpen;
    private double _rightOpen;
    private double? _readyAt;
    private WinkCalibrationSample? _previous;
    private bool _fitTaken;
    private WinkCalibrationPhase[] _roundGestures = AllGestures;
    private int _roundSamples = InitialSamples;
    private int _extraRounds;
    private int _rejectionsInARow;

    public WinkCalibrationPhase Phase { get; private set; } = WinkCalibrationPhase.Baseline;
    /// <summary>The gestures that the current collection round asks for, in order.</summary>
    public IReadOnlyList<WinkCalibrationPhase> CollectionGestures => _roundGestures;
    /// <summary>The number of gestures that were not counted because they were clearly not the gesture asked for.</summary>
    public int RejectedAttempts { get; private set; }
    public int PhaseSamples { get; private set; }
    public int RequiredSamples { get; private set; }
    public int CollectionRound { get; private set; }
    public int CheckRound { get; private set; }
    public double StillProgress { get; private set; }
    public bool IsSettling { get; private set; } = true;
    public bool NeedsMoreSamples { get; private set; }
    public WinkCalibrationFailure LastFailure { get; private set; }
    public WinkCalibrationProfile? Profile { get; private set; }
    public WinkCalibrationAssessment? Assessment { get; private set; }
    public int FreshAttemptsRequired => WinkCalibration.FreshCheckTotal;
    public int FreshAttemptsCompleted { get; private set; }
    public int FreshCorrectCount { get; private set; }

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
        if (Phase is WinkCalibrationPhase.Fitting or WinkCalibrationPhase.Ready or WinkCalibrationPhase.Stopped) return;
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
                StartCollection(AllGestures, InitialSamples);
            }
            else if (WinkCalibration.Matches(Profile!, trial))
            {
                FreshAttemptsCompleted++;
                FreshCorrectCount++;
                _freshMisses.Clear();
                Phase = WinkCalibrationPhase.Ready;
            }
            else
            {
                Refit(trial);
            }
            return;
        }

        if (_capture?.Observe(sample) is not { } completed) return;
        // A wink of the wrong eye is a mistake, not evidence. Counting it would block every fit. After several
        // rejections in a row, the gesture may be how this person's eyes move, so it is counted and the fit decides.
        if (_capture.IsWrongGesture(completed) && _rejectionsInARow < MaximumRejectionsInARow)
        {
            _rejectionsInARow++;
            RejectedAttempts++;
            return;
        }
        _rejectionsInARow = 0;
        if (Phase is WinkCalibrationPhase.RightCheck or WinkCalibrationPhase.LeftCheck or WinkCalibrationPhase.BlinkCheck)
        {
            var outcome = WinkCalibration.Evaluate(Profile!, completed);
            if ((outcome is WinkCalibrationTrialOutcome.Miss or WinkCalibrationTrialOutcome.Invalid)
                && WinkCalibration.IsIncidentalBlink(Profile!, completed)) return;
            if (outcome == WinkCalibrationTrialOutcome.Match)
            {
                FreshCorrectCount++;
            }
            else if (outcome == WinkCalibrationTrialOutcome.Miss
                && _freshMisses.Count(trial => trial.Actual == completed.Actual)
                    < WinkCalibration.FreshWinkAttempts - WinkCalibration.MinimumMatchedWinks)
            {
                _freshMisses.Add(completed);
            }
            else
            {
                Refit(completed);
                return;
            }

            FreshAttemptsCompleted++;
        }
        else AddTraining(completed);
        if (++PhaseSamples < RequiredSamples) return;
        switch (Phase)
        {
            case WinkCalibrationPhase.RightWinks or WinkCalibrationPhase.LeftWinks or WinkCalibrationPhase.Blinks:
                var next = Array.IndexOf(_roundGestures, Phase) + 1;
                if (next < _roundGestures.Length)
                {
                    Begin(_roundGestures[next], _roundSamples);
                }
                else
                {
                    Phase = WinkCalibrationPhase.Fitting;
                    _fitTaken = false;
                }
                break;
            case WinkCalibrationPhase.RightCheck:
                Begin(WinkCalibrationPhase.LeftCheck, WinkCalibration.FreshWinkAttempts);
                break;
            case WinkCalibrationPhase.LeftCheck:
                Begin(WinkCalibrationPhase.BlinkCheck, WinkCalibration.FreshBlinkAttempts);
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
        Assessment = result.Assessment;
        if (result.Profile is not { IsValid: true } profile)
        {
            NeedsMoreSamples = true;
            if (!TryStartExtraRound()) return;
            StartCollection(NeededGestures(result), ExtraSamples);
            return;
        }
        // Checking again after a failed check is also an extra round.
        if (CheckRound > 0 && !TryStartExtraRound()) return;
        Profile = profile;
        NeedsMoreSamples = false;
        Begin(WinkCalibrationPhase.RightCheck, WinkCalibration.FreshWinkAttempts);
    }

    /// <summary>Counts an extra round, or stops the calibration when the limit is reached.</summary>
    private bool TryStartExtraRound()
    {
        if (++_extraRounds <= MaximumExtraRounds) return true;
        Profile = null;
        _capture = null;
        Phase = WinkCalibrationPhase.Stopped;
        return false;
    }

    private void StartCollection(WinkCalibrationPhase[] gestures, int samples)
    {
        _roundGestures = gestures;
        _roundSamples = samples;
        CollectionRound++;
        Begin(gestures[0], samples);
    }

    /// <summary>The gestures whose samples did not reach the target. More of the others would not help the fit.</summary>
    private WinkCalibrationPhase[] NeededGestures(WinkCalibrationResult result)
    {
        if (result.Failure == WinkCalibrationFailure.InsufficientEyeRange) return [WinkCalibrationPhase.Blinks];
        var needed = new List<WinkCalibrationPhase>();
        if (result.Assessment is { } assessment)
        {
            if (!ReachesWinkTarget(assessment.RightWinks)) needed.Add(WinkCalibrationPhase.RightWinks);
            if (!ReachesWinkTarget(assessment.LeftWinks)) needed.Add(WinkCalibrationPhase.LeftWinks);
            if (assessment.Blinks.Total < assessment.Blinks.Required || assessment.Blinks.Matched < assessment.Blinks.Total)
            {
                needed.Add(WinkCalibrationPhase.Blinks);
            }
        }
        else if (result.Failure == WinkCalibrationFailure.InsufficientSamples)
        {
            (WinkCalibrationGesture Gesture, WinkCalibrationPhase Phase)[] gestures =
            [
                (WinkCalibrationGesture.RightWink, WinkCalibrationPhase.RightWinks),
                (WinkCalibrationGesture.LeftWink, WinkCalibrationPhase.LeftWinks),
                (WinkCalibrationGesture.Blink, WinkCalibrationPhase.Blinks)
            ];
            needed.AddRange(gestures
                .Where(item => _training.Count(trial => trial.Actual == item.Gesture) < WinkCalibration.MinimumMatchedWinks)
                .Select(item => item.Phase));
        }
        return needed.Count > 0 ? needed.ToArray() : AllGestures;
    }

    private static bool ReachesWinkTarget(WinkCalibrationMatchCount count) =>
        count.Matched >= WinkCalibration.MinimumMatchedWinks && count.MatchPercentage >= WinkCalibration.WinkTargetPercentage;

    private void Begin(WinkCalibrationPhase phase, int required)
    {
        if (phase == WinkCalibrationPhase.RightCheck) CheckRound++;
        Phase = phase;
        PhaseSamples = 0;
        RequiredSamples = required;
        _rejectionsInARow = 0;
        Pause();
        var gesture = phase switch
        {
            WinkCalibrationPhase.RightWinks or WinkCalibrationPhase.RightCheck => WinkCalibrationGesture.RightWink,
            WinkCalibrationPhase.LeftWinks or WinkCalibrationPhase.LeftCheck => WinkCalibrationGesture.LeftWink,
            _ => WinkCalibrationGesture.Blink
        };
        _capture = required > 0 ? new(gesture, _leftOpen, _rightOpen) : null;
    }

    private void Refit(WinkCalibrationTrial failed)
    {
        // Once the candidate is rejected, keep failed checks as training evidence.
        foreach (var missed in _freshMisses)
        {
            AddTraining(missed);
        }
        AddTraining(failed);
        _freshMisses.Clear();
        FreshAttemptsCompleted = 0;
        FreshCorrectCount = 0;
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
        if (Assessment is { } assessment)
        {
            Assessment = WinkCalibration.AssessRepeated(assessment.Candidate, _training);
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }
}
