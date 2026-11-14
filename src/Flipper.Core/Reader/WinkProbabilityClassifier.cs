namespace Flipper.Core.Reader;

public static class WinkProbabilityClassifier
{
    private const double OpenThreshold = 0.65;
    private const double BlinkThreshold = 0.12;

    public static (EyeState Left, EyeState Right) Classify(WinkCalibrationProfile profile, float? leftOpen, float? rightOpen)
    {
        if (!profile.IsValid || !HasProbabilities(leftOpen, rightOpen))
        {
            return (EyeState.Unknown, EyeState.Unknown);
        }

        var left = Normalise(leftOpen!.Value, profile.LeftClosed, profile.LeftOpen);
        var right = Normalise(rightOpen!.Value, profile.RightClosed, profile.RightOpen);
        if (left >= OpenThreshold && right >= OpenThreshold)
        {
            return (EyeState.Open, EyeState.Open);
        }

        if (left <= BlinkThreshold && right <= BlinkThreshold)
        {
            return (EyeState.Closed, EyeState.Closed);
        }

        if (right <= profile.ClosedThreshold && left >= profile.RightWinkOtherEyeMinimum
            && left - right >= profile.RightWinkMinimumDifference)
        {
            return (EyeState.Open, EyeState.Closed);
        }

        if (left <= profile.ClosedThreshold && right >= profile.LeftWinkOtherEyeMinimum
            && right - left >= profile.LeftWinkMinimumDifference)
        {
            return (EyeState.Closed, EyeState.Open);
        }

        // Only the joint rules can confirm unilateral closure.
        return (EyeStateFor(left, profile.ClosedThreshold), EyeStateFor(right, profile.ClosedThreshold)) switch
        {
            (EyeState.Open, EyeState.Closed) => (EyeState.Open, EyeState.Unknown),
            (EyeState.Closed, EyeState.Open) => (EyeState.Unknown, EyeState.Open),
            var eyes => eyes
        };
    }

    internal static bool HasProbabilities(float? left, float? right) =>
        left is { } l && WinkCalibrationProfile.IsProbability(l) && right is { } r && WinkCalibrationProfile.IsProbability(r);

    private static double Normalise(float probability, double closed, double open) =>
        Math.Clamp((probability - closed) / (open - closed), 0, 1);

    private static EyeState EyeStateFor(double probability, double closedThreshold)
    {
        if (probability >= OpenThreshold)
        {
            return EyeState.Open;
        }

        return probability <= closedThreshold ? EyeState.Closed : EyeState.Unknown;
    }
}
