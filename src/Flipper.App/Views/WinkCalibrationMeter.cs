using Flipper.Core.Reader;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Flipper.App.Views;

internal sealed class WinkCalibrationMeter : UserControl
{
    private readonly ProgressRing _ring = new()
    {
        Width = 136, Height = 136, Minimum = 0, Maximum = 100, IsIndeterminate = false, IsActive = true,
        Foreground = Brush("InkBrush"), Background = Brush("GoldSoftBrush")
    };
    private readonly TextBlock _value = Text("—", 30);
    private readonly TextBlock _stage = Text("", 12);
    private readonly TextBlock _matches = Text("", 12);
    private readonly TextBlock _status = Text("", 12);

    public WinkCalibrationMeter()
    {
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        Width = 196;
        _value.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _value.HorizontalAlignment = HorizontalAlignment.Center;
        _stage.HorizontalAlignment = HorizontalAlignment.Center;
        var title = Text("Calibration progress", 14);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var gauge = new Grid
        {
            Width = 136, Height = 136,
            Children =
            {
                _ring,
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { _value, _stage }
                }
            }
        };
        Content = new Border
        {
            Background = Brush("CardBrush"), BorderBrush = Brush("GoldSoftBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
            Child = new StackPanel { Spacing = 10, Children = { title, gauge, _matches, _status } }
        };
        Update(null, null, WinkCalibrationFailure.None, 0, 0, 0, 0, 0);
    }

    public void Update(WinkCalibrationAssessment? assessment, WinkCalibrationPhase? phase,
        WinkCalibrationFailure failure, int completedFreshChecks, int requiredFreshChecks,
        double stillProgress, int phaseCount, int phaseRequired)
    {
        var percentage = Completion(assessment, phase, completedFreshChecks, requiredFreshChecks,
            stillProgress, phaseCount, phaseRequired);
        _ring.Value = percentage;
        _value.Text = $"{Math.Floor(percentage):0}%";
        _stage.Text = phase switch
        {
            null => "Not started",
            WinkCalibrationPhase.Baseline => "Stage 1 of 3",
            WinkCalibrationPhase.Ready => "Ready to save",
            _ when IsChecking(phase) => "Stage 3 of 3",
            _ => "Stage 2 of 3"
        };
        _matches.Text = assessment is null ? "Right winks: —\nLeft winks: —\nBlinks: —\nOpen eyes: —"
            : $"Right winks: {Count(assessment.RightWinks)}\nLeft winks: {Count(assessment.LeftWinks)}"
                + $"\nBlinks: {Count(assessment.Blinks)}\nOpen eyes: {Count(assessment.OpenEyes)}";
        _status.Text = Status(assessment, phase, failure, completedFreshChecks, requiredFreshChecks);
        AutomationProperties.SetName(_ring,
            $"Calibration progress: {Math.Floor(percentage):0} percent. {_stage.Text}. {_status.Text}");
    }

    private static bool IsChecking(WinkCalibrationPhase? phase) => phase is WinkCalibrationPhase.RightCheck
        or WinkCalibrationPhase.LeftCheck or WinkCalibrationPhase.BlinkCheck or WinkCalibrationPhase.OpenCheck;

    private static double Completion(WinkCalibrationAssessment? assessment, WinkCalibrationPhase? phase,
        int completedFreshChecks, int requiredFreshChecks, double stillProgress, int phaseCount, int phaseRequired)
    {
        if (phase is null) return 0;
        if (phase == WinkCalibrationPhase.Ready) return 100;
        if (phase == WinkCalibrationPhase.Baseline) return 100d / 3 * Math.Clamp(stillProgress, 0, 1);
        if (IsChecking(phase))
            return 100d / 3 * (2 + (requiredFreshChecks > 0
                ? Math.Clamp((double)completedFreshChecks / requiredFreshChecks, 0, 1) : 0));

        // The three stages have equal space. Within collection, progress follows
        // the weaker eye and sample minimums, rather than exposing the fit threshold.
        double collected;
        if (assessment is not null)
        {
            collected = new[]
            {
                assessment.WinkMatchPercentage / WinkCalibration.WinkTargetPercentage,
                (double)assessment.RightWinks.Matched / WinkCalibration.MinimumMatchedWinks,
                (double)assessment.LeftWinks.Matched / WinkCalibration.MinimumMatchedWinks,
                (double)assessment.Blinks.Matched / Math.Max(assessment.Blinks.Required, assessment.Blinks.Total),
                (double)assessment.OpenEyes.Matched / Math.Max(assessment.OpenEyes.Required, assessment.OpenEyes.Total)
            }.Min();
        }
        else
        {
            var gesture = phase switch
            {
                WinkCalibrationPhase.LeftWinks => 1,
                WinkCalibrationPhase.Blinks => 2,
                WinkCalibrationPhase.Fitting => 3,
                _ => 0
            };
            collected = (gesture + (phaseRequired > 0 && phase != WinkCalibrationPhase.Fitting
                ? (double)phaseCount / phaseRequired : 0)) / 3;
        }
        return 100d / 3 * (1 + Math.Clamp(collected, 0, 1));
    }

    private static string Status(WinkCalibrationAssessment? assessment, WinkCalibrationPhase? phase,
        WinkCalibrationFailure failure, int completedFreshChecks, int requiredFreshChecks)
    {
        if (phase is null) return "Start calibration when you are ready.";
        if (phase == WinkCalibrationPhase.Ready) return "Calibration checks complete.";
        if (phase == WinkCalibrationPhase.Fitting) return "Assessing the latest samples…";
        if (phase == WinkCalibrationPhase.Baseline) return "Measuring your open eyes.";
        if (IsChecking(phase)) return $"Fresh checks: {completedFreshChecks} of {requiredFreshChecks} complete.";
        if (assessment is null)
        {
            return failure switch
            {
                WinkCalibrationFailure.UnstableTracking => "Tracking was not steady enough.",
                WinkCalibrationFailure.InsufficientEyeRange => "Open and closed eye readings are too similar.",
                WinkCalibrationFailure.InsufficientSamples => "More usable samples are needed.",
                _ => "A reading follows the first round of winks and blinks."
            };
        }

        (string Name, WinkCalibrationMatchCount Count)[] gestures =
        [
            ("right wink", assessment.RightWinks), ("left wink", assessment.LeftWinks),
            ("blink", assessment.Blinks), ("open-eye", assessment.OpenEyes)
        ];
        foreach (var (name, count) in gestures)
        {
            if (count.Total < count.Required) return $"Need at least {count.Required} {name} samples.";
        }
        if (assessment.Blinks.Matched < assessment.Blinks.Total) return "Blink checks must pass without a page turn.";
        if (assessment.OpenEyes.Matched < assessment.OpenEyes.Total) return "Open-eye checks must pass without a page turn.";
        if (!assessment.IsSafe) return "Some samples failed the turn or tracking checks.";
        if (!assessment.MeetsRepeatedTarget) return "More consistent wink samples are needed.";
        return "Sample checks complete. Finish this round for fresh checks.";
    }

    private static string Count(WinkCalibrationMatchCount count) => $"{count.Matched}/{count.Total} matched";
    private static Brush Brush(string name) => (Brush)Application.Current.Resources[name];
    private static TextBlock Text(string value, double size) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush("InkBrush")
    };
}
