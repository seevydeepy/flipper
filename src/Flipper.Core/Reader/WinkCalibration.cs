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

public enum WinkCalibrationTrialOutcome { Match, Miss, UnsafeTurn, Invalid }

public sealed record WinkCalibrationMatchCount(int Matched, int Total, int Required)
{
    public double MatchPercentage => Total == 0 ? 0 : 100d * Matched / Total;
}

public sealed record WinkCalibrationAssessment(
    WinkCalibrationProfile Candidate,
    WinkCalibrationMatchCount RightWinks,
    WinkCalibrationMatchCount LeftWinks,
    WinkCalibrationMatchCount Blinks,
    WinkCalibrationMatchCount OpenEyes,
    bool IsSafe)
{
    public int MatchedCount => RightWinks.Matched + LeftWinks.Matched + Blinks.Matched + OpenEyes.Matched;
    public int SampleCount => RightWinks.Total + LeftWinks.Total + Blinks.Total + OpenEyes.Total;
    public double MatchPercentage => SampleCount == 0 ? 0 : 100d * MatchedCount / SampleCount;
    public double WinkMatchPercentage => Math.Min(RightWinks.MatchPercentage, LeftWinks.MatchPercentage);
    public bool HasRequiredSamples => RightWinks.Total >= RightWinks.Required && LeftWinks.Total >= LeftWinks.Required
        && Blinks.Total >= Blinks.Required && OpenEyes.Total >= OpenEyes.Required;
    public bool MeetsRepeatedTarget => IsSafe && HasRequiredSamples
        && RightWinks.Matched >= WinkCalibration.MinimumMatchedWinks && LeftWinks.Matched >= WinkCalibration.MinimumMatchedWinks
        && WinkMatchPercentage >= WinkCalibration.WinkTargetPercentage
        && Blinks.Matched == Blinks.Total && OpenEyes.Matched == OpenEyes.Total;
}

/// <param name="Dropped">
/// Training trials left out of a successful repeated fit, because each one alone blocked it. The profile turns no
/// page on them.
/// </param>
public sealed record WinkCalibrationResult(
    WinkCalibrationProfile? Profile,
    WinkCalibrationFailure Failure,
    WinkCalibrationAssessment? Assessment = null,
    IReadOnlyList<WinkCalibrationTrial>? Dropped = null);

public static class WinkCalibration
{
    public const int WinkTargetPercentage = 80;
    public const int MinimumMatchedWinks = 4;
    public const int FreshWinkAttempts = 5;
    public const int FreshBlinkAttempts = 3;
    public const int FreshCheckTotal = 2 * FreshWinkAttempts + FreshBlinkAttempts + 1;
    private const double BlinkFloor = 0.12;
    private const int MaximumDroppedTrials = 2;
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
        var result = Search(cameraId, openSamples, trainingTrials, [], repeated, out var blocker);
        if (!repeated || result.Profile is not null) return result;

        // One unusable sample must not block every candidate for the rest of the calibration. A blocker is left out
        // only when it is the single failure of a candidate that otherwise meets the target. It still rejects any
        // candidate that turns a page on it, and fresh checks still follow.
        var dropped = new List<WinkCalibrationTrial>();
        var remaining = trainingTrials;
        while (blocker is { } trial && dropped.Count < MaximumDroppedTrials)
        {
            dropped.Add(trial);
            remaining = remaining.Where(value => !ReferenceEquals(value, trial)).ToArray();
            var retry = Search(cameraId, openSamples, remaining, dropped, repeated, out blocker);
            if (retry.Profile is not null) return retry with { Dropped = dropped };
        }
        return result;
    }

    private static WinkCalibrationResult Search(
        string cameraId,
        IReadOnlyList<WinkCalibrationSample> openSamples,
        IReadOnlyList<WinkCalibrationTrial> trainingTrials,
        IReadOnlyList<WinkCalibrationTrial> leftOut,
        bool repeated,
        out WinkCalibrationTrial? blocker)
    {
        blocker = null;
        var required = repeated ? MinimumMatchedWinks : 2;
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
        WinkCalibrationAssessment? best = null;
        WinkCalibrationAssessment? bestAccepted = null;
        var failures = new List<(int Index, WinkCalibrationTrialOutcome Outcome)>();
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
                    failures.Clear();
                    var assessment = Assess(candidate, trainingTrials, repeated, failures);
                    if (leftOut.Any(trial => Replay(candidate, trial) == WinkCalibrationTrialOutcome.UnsafeTurn))
                    {
                        assessment = assessment with { IsSafe = false };
                        failures.Clear();
                    }

                    if (repeated && blocker is null && failures is [var failure]
                        && IsDroppable(trainingTrials[failure.Index], failure.Outcome)
                        && Without(assessment, trainingTrials[failure.Index].Actual).MeetsRepeatedTarget)
                    {
                        blocker = trainingTrials[failure.Index];
                    }

                    var completeMatch = assessment.HasRequiredSamples && assessment.MatchedCount == assessment.SampleCount;
                    var meetsTarget = repeated ? assessment.MeetsRepeatedTarget : completeMatch;
                    if (meetsTarget && completeMatch)
                    {
                        return new(candidate, WinkCalibrationFailure.None, assessment);
                    }

                    if (meetsTarget && (bestAccepted is null || IsBetterWinkMatch(assessment, bestAccepted)))
                    {
                        bestAccepted = assessment;
                    }

                    if (best is null || IsBetterAssessment(assessment, best))
                    {
                        best = assessment;
                    }
                }
            }
        }

        return bestAccepted is { } accepted
            ? new(accepted.Candidate, WinkCalibrationFailure.None, accepted)
            : new(null, WinkCalibrationFailure.NoSeparation, best);
    }

    public static bool Matches(WinkCalibrationProfile profile, WinkCalibrationTrial trial) =>
        Evaluate(profile, trial) == WinkCalibrationTrialOutcome.Match;

    public static WinkCalibrationTrialOutcome Evaluate(WinkCalibrationProfile profile, WinkCalibrationTrial trial) =>
        profile.IsValid && IsCompleteTrial(trial) ? Replay(profile, trial) : WinkCalibrationTrialOutcome.Invalid;

    public static bool IsIncidentalBlink(WinkCalibrationProfile profile, WinkCalibrationTrial trial) =>
        profile.IsValid && IsCompleteTrial(trial) && ReplayIncidentalBlink(profile, trial);

    public static WinkCalibrationAssessment? AssessRepeated(
        WinkCalibrationProfile profile,
        IReadOnlyList<WinkCalibrationTrial> trials) =>
        profile.IsValid && trials.Count > 0 && trials.All(IsCompleteTrial) ? Assess(profile, trials, repeated: true) : null;

    /// <summary>
    /// A wink or blink sample that turned no page but could not be scored (for example, the eyes did not reopen
    /// fully, or a blink was too shallow) can be left out. A sample that turned a page is evidence against the
    /// candidate and is never left out, and neither is an open-eye sample.
    /// </summary>
    private static bool IsDroppable(WinkCalibrationTrial trial, WinkCalibrationTrialOutcome outcome) =>
        trial.Actual is WinkCalibrationGesture.RightWink or WinkCalibrationGesture.LeftWink or WinkCalibrationGesture.Blink
        && outcome == WinkCalibrationTrialOutcome.Invalid;

    private static WinkCalibrationAssessment Without(WinkCalibrationAssessment assessment, WinkCalibrationGesture gesture)
    {
        static WinkCalibrationMatchCount Less(WinkCalibrationMatchCount count) => count with { Total = count.Total - 1 };
        return assessment with
        {
            IsSafe = true,
            RightWinks = gesture == WinkCalibrationGesture.RightWink ? Less(assessment.RightWinks) : assessment.RightWinks,
            LeftWinks = gesture == WinkCalibrationGesture.LeftWink ? Less(assessment.LeftWinks) : assessment.LeftWinks,
            Blinks = gesture == WinkCalibrationGesture.Blink ? Less(assessment.Blinks) : assessment.Blinks
        };
    }

    private static WinkCalibrationAssessment Assess(
        WinkCalibrationProfile profile,
        IReadOnlyList<WinkCalibrationTrial> trials,
        bool repeated,
        List<(int Index, WinkCalibrationTrialOutcome Outcome)>? failures = null)
    {
        Span<int> matched = stackalloc int[4];
        Span<int> totals = stackalloc int[4];
        matched.Clear();
        totals.Clear();
        var safe = true;
        for (var trialIndex = 0; trialIndex < trials.Count; trialIndex++)
        {
            var trial = trials[trialIndex];
            var outcome = Replay(profile, trial);
            if (repeated && (outcome is WinkCalibrationTrialOutcome.Miss or WinkCalibrationTrialOutcome.Invalid)
                && ReplayIncidentalBlink(profile, trial))
            {
                continue;
            }

            var index = (int)trial.Actual;
            totals[index]++;
            if (outcome == WinkCalibrationTrialOutcome.Match)
            {
                matched[index]++;
            }
            else if (outcome != WinkCalibrationTrialOutcome.Miss)
            {
                safe = false;
                failures?.Add((trialIndex, outcome));
            }
        }

        var required = repeated ? MinimumMatchedWinks : 2;
        var right = (int)WinkCalibrationGesture.RightWink;
        var left = (int)WinkCalibrationGesture.LeftWink;
        var blink = (int)WinkCalibrationGesture.Blink;
        var open = (int)WinkCalibrationGesture.Neither;
        return new(profile, new(matched[right], totals[right], required), new(matched[left], totals[left], required),
            new(matched[blink], totals[blink], required), new(matched[open], totals[open], 1), safe);
    }

    private static bool IsBetterAssessment(WinkCalibrationAssessment candidate, WinkCalibrationAssessment best)
    {
        if (candidate.IsSafe != best.IsSafe)
        {
            return candidate.IsSafe;
        }

        var candidateHasWinks = candidate.RightWinks.Matched >= candidate.RightWinks.Required
            && candidate.LeftWinks.Matched >= candidate.LeftWinks.Required;
        var bestHasWinks = best.RightWinks.Matched >= best.RightWinks.Required && best.LeftWinks.Matched >= best.LeftWinks.Required;
        if (candidateHasWinks != bestHasWinks)
        {
            return candidateHasWinks;
        }

        return candidate.MatchPercentage > best.MatchPercentage
            || (candidate.MatchPercentage == best.MatchPercentage && candidate.MatchedCount > best.MatchedCount);
    }

    private static bool IsBetterWinkMatch(WinkCalibrationAssessment candidate, WinkCalibrationAssessment best) =>
        candidate.WinkMatchPercentage > best.WinkMatchPercentage
        || (candidate.WinkMatchPercentage == best.WinkMatchPercentage
            && (candidate.RightWinks.Matched + candidate.LeftWinks.Matched) * (long)(best.RightWinks.Total + best.LeftWinks.Total)
                > (best.RightWinks.Matched + best.LeftWinks.Matched) * (long)(candidate.RightWinks.Total + candidate.LeftWinks.Total));

    private static bool ReplayIncidentalBlink(WinkCalibrationProfile profile, WinkCalibrationTrial trial) =>
        BlinkTrialIfIncidental(profile, trial) is { } blink && Replay(profile, blink) == WinkCalibrationTrialOutcome.Match;

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

    private static WinkCalibrationTrialOutcome Replay(WinkCalibrationProfile profile, WinkCalibrationTrial trial)
    {
        var expectedTurn = trial.Actual switch
        {
            WinkCalibrationGesture.RightWink => 1,
            WinkCalibrationGesture.LeftWink => -1,
            _ => 0
        };
        var detector = new WinkPageTurnDetector();
        var matched = false;
        var interrupted = false;
        TimeSpan? previousTimestamp = null;
        double? bothOpenSince = null;
        foreach (var sample in trial.Samples)
        {
            if (!IsUsable(sample))
            {
                detector.Reset();
                interrupted = true;
                previousTimestamp = null;
                bothOpenSince = null;
                continue;
            }

            var (left, right) = WinkProbabilityClassifier.Classify(profile, sample.LeftOpenProbability, sample.RightOpenProbability);
            var timestamp = TimeSpan.FromMilliseconds(sample.TimeMs);
            if (previousTimestamp is { } previous && timestamp - previous > MaximumSampleGap)
            {
                bothOpenSince = null;
                interrupted = true;
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
                    return WinkCalibrationTrialOutcome.UnsafeTurn;
                }

                matched = true;
            }
        }

        if (bothOpenSince is not { } finalOpenSince || trial.Samples[^1].TimeMs - finalOpenSince < MinimumFinalOpenMs
            || (trial.Actual == WinkCalibrationGesture.Blink
                && (BlinkAnchors(trial) is not { } blink
                    || blink.Left > (profile.LeftClosed + profile.LeftOpen) / 2
                    || blink.Right > (profile.RightClosed + profile.RightOpen) / 2)))
        {
            return WinkCalibrationTrialOutcome.Invalid;
        }

        if (matched || expectedTurn == 0)
        {
            return WinkCalibrationTrialOutcome.Match;
        }

        return interrupted ? WinkCalibrationTrialOutcome.Invalid : WinkCalibrationTrialOutcome.Miss;
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
