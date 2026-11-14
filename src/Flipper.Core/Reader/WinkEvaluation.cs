namespace Flipper.Core.Reader;

public enum WinkGestureKind
{
    RightWink,
    LeftWink,
    Blink,
    Ignore
}

public sealed record WinkAnnotation(WinkGestureKind Kind, double StartMs, double EndMs);

public sealed record WinkTurn(double TimeMs, int Direction);

public sealed record WinkMatch(WinkAnnotation Annotation, WinkTurn Turn);

public sealed record WinkDirectionTotals(int Matched, int Missed, int Unexpected)
{
    public int Expected => Matched + Missed;
}

public sealed record WinkEvaluationResult(
    IReadOnlyList<WinkMatch> Matches,
    IReadOnlyList<WinkAnnotation> MissedWinks,
    IReadOnlyList<WinkTurn> UnexpectedTurns,
    IReadOnlyList<WinkTurn> IgnoredTurns,
    WinkDirectionTotals Right,
    WinkDirectionTotals Left)
{
    public int Expected => Matches.Count + MissedWinks.Count;
    public int ScoredTurns => Matches.Count + UnexpectedTurns.Count;

    public double? Precision => ScoredTurns == 0 ? null : (double)Matches.Count / ScoredTurns;
    public double? Recall => Expected == 0 ? null : (double)Matches.Count / Expected;
}

public static class WinkEvaluation
{
    public static WinkEvaluationResult Compare(
        IReadOnlyList<WinkAnnotation> annotations,
        IReadOnlyList<WinkTurn> turns,
        double durationMs,
        double responseAllowanceMs = 400)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(turns);
        if (!double.IsFinite(durationMs) || durationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs), "The recording duration must be finite and positive.");
        }

        if (!double.IsFinite(responseAllowanceMs) || responseAllowanceMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(responseAllowanceMs), "The response allowance must be finite and non-negative.");
        }

        var labels = OrderAnnotations(annotations, durationMs, responseAllowanceMs);
        if (turns.Any(turn => turn is null || !double.IsFinite(turn.TimeMs) || turn.TimeMs < 0
            || turn.TimeMs > durationMs || turn.Direction is not (1 or -1)))
        {
            throw new ArgumentException("Each turn must have a timestamp within the recording and a direction of +1 or -1.", nameof(turns));
        }

        var winks = labels.Where(label => label.Kind is WinkGestureKind.RightWink or WinkGestureKind.LeftWink).ToArray();
        var matched = new HashSet<WinkAnnotation>();
        var matches = new List<WinkMatch>();
        var unexpected = new List<WinkTurn>();
        var ignored = new List<WinkTurn>();
        foreach (var turn in turns.OrderBy(turn => turn.TimeMs))
        {
            var active = labels.FirstOrDefault(label => turn.TimeMs >= label.StartMs && turn.TimeMs <= label.EndMs);
            if (active?.Kind == WinkGestureKind.Ignore)
            {
                ignored.Add(turn);
                continue;
            }

            var kind = turn.Direction == 1 ? WinkGestureKind.RightWink : WinkGestureKind.LeftWink;
            // A label at the time of the turn takes precedence over an earlier wink's response window.
            var wink = active ?? winks.FirstOrDefault(label => label.Kind == kind
                && turn.TimeMs >= label.StartMs
                && turn.TimeMs - label.EndMs <= responseAllowanceMs);
            if (wink?.Kind == kind && matched.Add(wink))
            {
                matches.Add(new WinkMatch(wink, turn));
            }
            else
            {
                unexpected.Add(turn);
            }
        }

        var missed = winks.Where(label => !matched.Contains(label)).ToArray();
        return new WinkEvaluationResult(
            matches,
            missed,
            unexpected,
            ignored,
            Totals(WinkGestureKind.RightWink, 1),
            Totals(WinkGestureKind.LeftWink, -1));

        WinkDirectionTotals Totals(WinkGestureKind kind, int direction) => new(
            matches.Count(match => match.Annotation.Kind == kind),
            missed.Count(annotation => annotation.Kind == kind),
            unexpected.Count(turn => turn.Direction == direction));
    }

    private static WinkAnnotation[] OrderAnnotations(
        IReadOnlyList<WinkAnnotation> annotations,
        double durationMs,
        double responseAllowanceMs)
    {
        if (annotations.Any(label => label is null || !Enum.IsDefined(label.Kind)
            || !double.IsFinite(label.StartMs) || !double.IsFinite(label.EndMs)
            || label.StartMs < 0 || label.EndMs <= label.StartMs || label.EndMs > durationMs))
        {
            throw new ArgumentException("Each annotation must have a valid gesture and a start before its end within the recording.", nameof(annotations));
        }

        var labels = annotations.OrderBy(label => label.StartMs).ToArray();
        WinkAnnotation? previous = null;
        var lastWinkByKind = new Dictionary<WinkGestureKind, WinkAnnotation>();
        foreach (var label in labels)
        {
            if (previous is not null && label.StartMs <= previous.EndMs)
            {
                throw new ArgumentException($"Annotations at {previous.StartMs} ms and {label.StartMs} ms overlap.", nameof(annotations));
            }

            previous = label;
            if (label.Kind is WinkGestureKind.RightWink or WinkGestureKind.LeftWink)
            {
                if (lastWinkByKind.TryGetValue(label.Kind, out var lastWink)
                    && label.StartMs - lastWink.EndMs <= responseAllowanceMs)
                {
                    throw new ArgumentException($"Response windows for {label.Kind} annotations at {lastWink.StartMs} ms and {label.StartMs} ms overlap.", nameof(annotations));
                }

                lastWinkByKind[label.Kind] = label;
            }
        }

        return labels;
    }
}
