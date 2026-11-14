namespace Flipper.Core.Reader;

public enum WinkCalibrationGesture { RightWink, LeftWink, Blink, Neither }

public readonly record struct WinkCalibrationSample(
    double TimeMs,
    float? LeftOpenProbability,
    float? RightOpenProbability,
    bool IsTracked = true,
    int RotationQuarterTurns = 0);

public sealed record WinkCalibrationTrial(
    WinkCalibrationGesture Actual,
    IReadOnlyList<WinkCalibrationSample> Samples,
    double WarmupMs = 1000);

public enum WinkCalibrationFailure { None, InsufficientSamples, UnstableTracking, InsufficientEyeRange, NoSeparation }

public sealed record WinkCalibrationResult(WinkCalibrationProfile? Profile, WinkCalibrationFailure Failure);

public static class WinkCalibration
{
    private const double BlinkFloor = 0.12;
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromMilliseconds(250);
    private const double MinimumFinalOpenMs = 100;
    private static readonly double[] ClosedThresholds = [0.25, 0.35, 0.45];
    private static readonly double[] OtherEyeMinimums = [0.6, 0.45, 0.3, 0.2];
    private static readonly double[] MinimumDifferences = [0.6, 0.45, 0.3, 0.2, 0.12];

    public static WinkCalibrationResult Fit(
        string cameraId,
        IReadOnlyList<WinkCalibrationSample> openSamples,
        IReadOnlyList<WinkCalibrationTrial> trainingTrials) =>
        Fit(cameraId, openSamples, trainingTrials, repeated: false);

    public static WinkCalibrationResult FitRepeated(
        string cameraId,
        IReadOnlyList<WinkCalibrationSample> openSamples,
        IReadOnlyList<WinkCalibrationTrial> trainingTrials) =>
        Fit(cameraId, openSamples, trainingTrials, repeated: true);

    private static WinkCalibrationResult Fit(
        string cameraId,
        IReadOnlyList<WinkCalibrationSample> openSamples,
        IReadOnlyList<WinkCalibrationTrial> trainingTrials,
        bool repeated)
    {
        var required = repeated ? 4 : 2;
        var baseline = openSamples.Where(IsUsable).ToArray();
        if (string.IsNullOrWhiteSpace(cameraId) || baseline.Length < 20
            || trainingTrials.Count(trial => trial.Actual == WinkCalibrationGesture.RightWink) < required
            || trainingTrials.Count(trial => trial.Actual == WinkCalibrationGesture.LeftWink) < required
            || trainingTrials.Count(trial => trial.Actual == WinkCalibrationGesture.Blink) < required
            || !trainingTrials.Any(trial => trial.Actual == WinkCalibrationGesture.Neither))
        {
            return new(null, WinkCalibrationFailure.InsufficientSamples);
        }

        if (!HasStableTracking(openSamples) || !trainingTrials.All(IsCompleteTrial))
        {
            return new(null, WinkCalibrationFailure.UnstableTracking);
        }

        var closed = new List<(double Left, double Right)>();
        foreach (var trial in trainingTrials.Where(trial => trial.Actual == WinkCalibrationGesture.Blink))
        {
            if (BlinkAnchors(trial) is not { } anchors)
            {
                return new(null, WinkCalibrationFailure.InsufficientSamples);
            }

            closed.Add(anchors);
        }

        var profile = new WinkCalibrationProfile
        {
            CameraId = cameraId,
            CreatedUtc = DateTimeOffset.UtcNow,
            LeftOpen = Median(baseline.Select(sample => (double)sample.LeftOpenProbability!.Value)),
            RightOpen = Median(baseline.Select(sample => (double)sample.RightOpenProbability!.Value)),
            LeftClosed = Median(closed.Select(anchors => anchors.Left)),
            RightClosed = Median(closed.Select(anchors => anchors.Right))
        };
        if (!profile.IsValid)
        {
            return new(null, WinkCalibrationFailure.InsufficientEyeRange);
        }

        var shapes = (from otherEyeMinimum in OtherEyeMinimums
                      from minimumDifference in MinimumDifferences
                      select (OtherEyeMinimum: otherEyeMinimum, MinimumDifference: minimumDifference)).ToArray();
        foreach (var closedThreshold in ClosedThresholds)
        {
            foreach (var left in shapes)
            {
                foreach (var right in shapes)
                {
                    var candidate = profile with
                    {
                        ClosedThreshold = closedThreshold,
                        LeftWinkOtherEyeMinimum = left.OtherEyeMinimum,
                        LeftWinkMinimumDifference = left.MinimumDifference,
                        RightWinkOtherEyeMinimum = right.OtherEyeMinimum,
                        RightWinkMinimumDifference = right.MinimumDifference
                    };
                    var matches = repeated
                        ? MatchesRepeated(candidate, trainingTrials, required)
                        : trainingTrials.All(trial => ReplayMatches(candidate, trial));
                    if (matches)
                    {
                        return new(candidate, WinkCalibrationFailure.None);
                    }
                }
            }
        }

        return new(null, WinkCalibrationFailure.NoSeparation);
    }

    public static bool Matches(WinkCalibrationProfile profile, WinkCalibrationTrial trial) =>
        profile.IsValid && IsCompleteTrial(trial) && ReplayMatches(profile, trial);

    public static bool IsIncidentalBlink(WinkCalibrationProfile profile, WinkCalibrationTrial trial) =>
        profile.IsValid && IsCompleteTrial(trial) && ReplayIncidentalBlink(profile, trial);

    private static bool MatchesRepeated(
        WinkCalibrationProfile profile,
        IReadOnlyList<WinkCalibrationTrial> trials,
        int required)
    {
        var left = 0;
        var right = 0;
        foreach (var trial in trials)
        {
            if (!ReplayMatches(profile, trial))
            {
                if (!ReplayIncidentalBlink(profile, trial))
                {
                    return false;
                }

                continue;
            }

            if (trial.Actual == WinkCalibrationGesture.LeftWink)
            {
                left++;
            }
            else if (trial.Actual == WinkCalibrationGesture.RightWink)
            {
                right++;
            }
        }

        return left >= required && right >= required;
    }

    private static bool ReplayIncidentalBlink(WinkCalibrationProfile profile, WinkCalibrationTrial trial) =>
        BlinkTrialIfIncidental(profile, trial) is { } blink && ReplayMatches(profile, blink);

    private static WinkCalibrationTrial? BlinkTrialIfIncidental(WinkCalibrationProfile profile, WinkCalibrationTrial trial)
    {
        if (trial.Actual is not (WinkCalibrationGesture.LeftWink or WinkCalibrationGesture.RightWink))
        {
            return null;
        }

        // Convert the classifier's bilateral blink floor back into raw probabilities.
        var leftLimit = profile.LeftClosed + BlinkFloor * (profile.LeftOpen - profile.LeftClosed);
        var rightLimit = profile.RightClosed + BlinkFloor * (profile.RightOpen - profile.RightClosed);
        return trial.Samples.Any(sample => sample.TimeMs >= trial.WarmupMs && IsUsable(sample)
            && sample.LeftOpenProbability!.Value <= leftLimit && sample.RightOpenProbability!.Value <= rightLimit)
            ? trial with { Actual = WinkCalibrationGesture.Blink }
            : null;
    }

    private static bool ReplayMatches(WinkCalibrationProfile profile, WinkCalibrationTrial trial)
    {
        if (trial.Actual == WinkCalibrationGesture.Blink
            && (BlinkAnchors(trial) is not { } blink
                || blink.Left > (profile.LeftClosed + profile.LeftOpen) / 2
                || blink.Right > (profile.RightClosed + profile.RightOpen) / 2))
        {
            return false;
        }

        var expectedTurn = trial.Actual switch
        {
            WinkCalibrationGesture.RightWink => 1,
            WinkCalibrationGesture.LeftWink => -1,
            _ => 0
        };
        var detector = new WinkPageTurnDetector();
        var matched = false;
        TimeSpan? previousTimestamp = null;
        double? bothOpenSince = null;
        foreach (var sample in trial.Samples)
        {
            if (!IsUsable(sample))
            {
                detector.Reset();
                previousTimestamp = null;
                bothOpenSince = null;
                continue;
            }

            var (left, right) = WinkProbabilityClassifier.Classify(profile, sample.LeftOpenProbability, sample.RightOpenProbability);
            var timestamp = TimeSpan.FromMilliseconds(sample.TimeMs);
            if (previousTimestamp is { } previous && timestamp - previous > MaximumSampleGap)
            {
                bothOpenSince = null;
            }

            bothOpenSince = (left, right) == (EyeState.Open, EyeState.Open)
                ? bothOpenSince ?? sample.TimeMs
                : null;
            previousTimestamp = timestamp;
            var turn = detector.Observe(left, right, timestamp);
            if (turn != 0)
            {
                // Confirmation is delayed. A wink already reopening before the prompt belongs to warm-up.
                if (bothOpenSince is not { } reopened || reopened < trial.WarmupMs || turn != expectedTurn || matched)
                {
                    return false;
                }

                matched = true;
            }
        }

        return (matched || expectedTurn == 0)
            && bothOpenSince is { } finalOpenSince
            && trial.Samples[^1].TimeMs - finalOpenSince >= MinimumFinalOpenMs;
    }

    private static (double Left, double Right)? BlinkAnchors(WinkCalibrationTrial trial)
    {
        var left = double.PositiveInfinity;
        var right = double.PositiveInfinity;
        for (var index = 1; index < trial.Samples.Count; index++)
        {
            var first = trial.Samples[index - 1];
            var second = trial.Samples[index];
            // Match the detector's maximum sample gap when seeking a two-frame closure.
            if (first.TimeMs >= trial.WarmupMs && second.TimeMs - first.TimeMs <= MaximumSampleGap.TotalMilliseconds
                && IsUsable(first) && IsUsable(second))
            {
                left = Math.Min(left, ((double)first.LeftOpenProbability!.Value + second.LeftOpenProbability!.Value) / 2);
                right = Math.Min(right, ((double)first.RightOpenProbability!.Value + second.RightOpenProbability!.Value) / 2);
            }
        }

        return double.IsFinite(left) ? (left, right) : null;
    }

    private static bool IsCompleteTrial(WinkCalibrationTrial trial) =>
        Enum.IsDefined(trial.Actual)
        && double.IsFinite(trial.WarmupMs)
        && trial.WarmupMs >= 0
        && HasStableTracking(trial.Samples)
        && trial.Samples.Any(sample => sample.TimeMs > trial.WarmupMs && IsUsable(sample));

    private static bool HasStableTracking(IReadOnlyList<WinkCalibrationSample> samples)
    {
        if (samples.Count < 2)
        {
            return false;
        }

        var rotation = samples[0].RotationQuarterTurns;
        var previousTime = -1d;
        var usable = 0;
        foreach (var sample in samples)
        {
            if (!double.IsFinite(sample.TimeMs) || sample.TimeMs < 0 || sample.TimeMs <= previousTime
                || sample.TimeMs >= TimeSpan.MaxValue.TotalMilliseconds || sample.RotationQuarterTurns != rotation)
            {
                return false;
            }

            previousTime = sample.TimeMs;
            if (IsUsable(sample))
            {
                usable++;
            }
        }

        return usable * 100L >= samples.Count * 85L;
    }

    private static bool IsUsable(WinkCalibrationSample sample) =>
        sample.IsTracked && WinkProbabilityClassifier.HasProbabilities(sample.LeftOpenProbability, sample.RightOpenProbability);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
