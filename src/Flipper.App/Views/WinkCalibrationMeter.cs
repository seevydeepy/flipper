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
    private readonly TextBlock _matches = Text("", 12);
    private readonly TextBlock _status = Text("", 12);

    public WinkCalibrationMeter()
    {
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        Width = 196;
        _value.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _value.HorizontalAlignment = HorizontalAlignment.Center;
        var target = Text("Target 100%", 12);
        target.HorizontalAlignment = HorizontalAlignment.Center;
        var title = Text("Scored sample match", 14);
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
                    Children = { _value, target }
                }
            }
        };
        Content = new Border
        {
            Background = Brush("CardBrush"), BorderBrush = Brush("GoldSoftBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
            Child = new StackPanel { Spacing = 10, Children = { title, gauge, _matches, _status } }
        };
        Update(null, null, WinkCalibrationFailure.None, 0, 0);
    }

    public void Update(WinkCalibrationAssessment? assessment, WinkCalibrationPhase? phase,
        WinkCalibrationFailure failure, int passedFreshChecks, int requiredFreshChecks)
    {
        var hasReading = assessment is { SampleCount: > 0 };
        var percentage = hasReading ? assessment!.MatchPercentage : 0;
        _ring.Value = percentage;
        // Never round a failed sample up to the 100% target.
        _value.Text = hasReading ? $"{Math.Floor(percentage):0}%" : "—";
        _matches.Text = assessment is null ? "Right winks: —\nLeft winks: —\nBlinks: —\nOpen eyes: —"
            : $"Right winks: {Count(assessment.RightWinks)}\nLeft winks: {Count(assessment.LeftWinks)}"
                + $"\nBlinks: {Count(assessment.Blinks)}\nOpen eyes: {Count(assessment.OpenEyes)}";
        _status.Text = Status(assessment, phase, failure, passedFreshChecks, requiredFreshChecks);
        AutomationProperties.SetName(_ring, hasReading
            ? $"Scored sample match: {Math.Floor(percentage):0} percent. Target 100 percent."
                + $" {assessment!.MatchedCount} of {assessment.SampleCount} scored samples matched."
            : "Scored sample match: no reading yet. Target 100 percent.");
    }

    private static string Status(WinkCalibrationAssessment? assessment, WinkCalibrationPhase? phase,
        WinkCalibrationFailure failure, int passedFreshChecks, int requiredFreshChecks)
    {
        if (phase is null) return "Start calibration to measure your sample matches.";
        if (phase == WinkCalibrationPhase.Ready) return "All fresh checks passed.";
        if (phase == WinkCalibrationPhase.Fitting) return "Assessing the latest samples…";
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
        foreach (var (name, count) in gestures)
        {
            if (count.Matched < count.Total) return $"Some {name} samples do not match yet.";
        }
        return phase is WinkCalibrationPhase.RightCheck or WinkCalibrationPhase.LeftCheck
            or WinkCalibrationPhase.BlinkCheck or WinkCalibrationPhase.OpenCheck
            ? $"Fresh checks: {passedFreshChecks} of {requiredFreshChecks} passed."
            : "Sample target met. Finish this round for fresh checks.";
    }

    private static string Count(WinkCalibrationMatchCount count) => $"{count.Matched}/{count.Total} matched";
    private static Brush Brush(string name) => (Brush)Application.Current.Resources[name];
    private static TextBlock Text(string value, double size) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush("InkBrush")
    };
}
