using System.Globalization;
using System.Text.RegularExpressions;

namespace Flipper.Core.Reader;

public static class VoiceCalibration
{
    public const int SampleRate = 16_000;
    internal const int DefaultPresetIndex = 2;
    internal const float MinimumGain = 0.25f;
    internal const float MaximumGain = 4f;

    private const int FrameSamples = SampleRate * 20 / 1000;
    private const int MinimumSpeechSamples = SampleRate * 150 / 1000;
    private const float ClippingLevel = 32767f / 32768;
    private static readonly (double ThresholdScale, double ScoreAdjustment)[] Presets =
        [(2, -0.2), (1.4, 0), (1, 0), (0.75, 0.2), (0.5, 0.4)];
    private static readonly Regex NumericTag = new(@"(?<!\S)([:#])(\S+)");

    public static IReadOnlyList<string> Keywords { get; } = Array.AsReadOnly(
        new[] { "flip", "turn", "next", "page", "back", "previous", "restart", "beginning", "finish" });

    public static int PresetCount => Presets.Length;

    public static string ApplyPreset(string keywordDefinitions, int presetIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(presetIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(presetIndex, PresetCount);
        if (presetIndex == DefaultPresetIndex)
        {
            return keywordDefinitions;
        }

        var (thresholdScale, scoreAdjustment) = Presets[presetIndex];
        return NumericTag.Replace(keywordDefinitions.ReplaceLineEndings("\n"), tag =>
        {
            var culture = CultureInfo.InvariantCulture;
            if (!double.TryParse(tag.Groups[2].ValueSpan, NumberStyles.Float, culture, out var value)
                || !double.IsFinite(value))
            {
                return tag.Value;
            }

            var marker = tag.Value[0];
            var adjusted = marker == ':'
                ? Math.Max(0.1, value + scoreAdjustment)
                : Math.Clamp(value * thresholdScale, 0.05, 0.6);
            return marker + adjusted.ToString("G15", culture);
        });
    }

    public static float MeasureGain(IReadOnlyList<float[]> positiveSamples)
    {
        var levels = new List<float>(positiveSamples.Count);
        var peak = 0f;
        foreach (var clip in positiveSamples)
        {
            var loudestFrame = 0f;
            foreach (var sample in clip)
            {
                if (float.IsFinite(sample))
                {
                    peak = Math.Max(peak, Math.Abs(sample));
                }
            }

            for (var offset = 0; offset + FrameSamples <= clip.Length; offset += FrameSamples)
            {
                var level = Rms(clip.AsSpan(offset, FrameSamples));
                if (level > loudestFrame)
                {
                    loudestFrame = level;
                }
            }

            if (loudestFrame > 0)
            {
                levels.Add(loudestFrame);
            }
        }

        if (levels.Count == 0)
        {
            return 1f;
        }

        // The loudest frame of each utterance keeps padding out of the gain estimate.
        levels.Sort();
        var speechLevel = levels[(int)Math.Ceiling(levels.Count * 0.75) - 1];
        return Math.Clamp(Math.Min(0.08f / speechLevel, 0.95f / peak), MinimumGain, MaximumGain);
    }

    public static bool HasUsableSpeech(float[] samples, float[] background)
    {
        var backgroundLevel = Rms(background);
        if (!float.IsFinite(backgroundLevel))
        {
            return false;
        }

        var clippedSamples = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                return false;
            }

            if (Math.Abs(sample) >= ClippingLevel)
            {
                clippedSamples++;
            }
        }

        if (clippedSamples * 100L >= samples.Length)
        {
            return false;
        }

        var speechThreshold = Math.Max(0.001f, backgroundLevel * 2);
        var speechSamples = 0;
        for (var offset = 0; offset < samples.Length; offset += FrameSamples)
        {
            var length = Math.Min(FrameSamples, samples.Length - offset);
            if (Rms(samples.AsSpan(offset, length)) > speechThreshold)
            {
                speechSamples += length;
            }
        }

        return speechSamples >= MinimumSpeechSamples;
    }

    public static void ApplyGain(float[] samples, float gain)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            samples[i] = float.IsFinite(sample) ? Math.Clamp(sample * gain, -1f, 1f) : 0f;
        }
    }

    public static bool MatchesPrompt(string? expectedKeyword, IReadOnlyList<string> detectedKeywords) =>
        expectedKeyword is null
            ? detectedKeywords.Count == 0
            : detectedKeywords.Count == 1
                && string.Equals(expectedKeyword, detectedKeywords[0], StringComparison.OrdinalIgnoreCase);

    private static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0f;
        }

        var energy = 0d;
        foreach (var sample in samples)
        {
            energy += (double)sample * sample;
        }

        return (float)Math.Sqrt(energy / samples.Length);
    }
}
