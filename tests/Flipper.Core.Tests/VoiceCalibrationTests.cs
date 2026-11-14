using System.Globalization;
using System.Text.Json;
using Flipper.Core.Reader;
using Flipper.Core.Settings;

namespace Flipper.Core.Tests;

public sealed class VoiceCalibrationTests
{
    [Fact]
    public void ApplyPreset_PreservesDefaultDefinitionBytes()
    {
        const string definitions = "F L IH1 P :2.5 #0.16 @FLIP\r\nR IY0 S T AA1 T :2.2 #0.18 @RESTART\r\n";

        Assert.Equal(definitions, VoiceCalibration.ApplyPreset(definitions, 2));
    }

    [Theory]
    [InlineData(0, ":2.3 #0.32", ":2.2 #0.36", ":2 #0.36")]
    [InlineData(1, ":2.5 #0.224", ":2.4 #0.252", ":2.2 #0.252")]
    [InlineData(3, ":2.7 #0.12", ":2.6 #0.135", ":2.4 #0.135")]
    [InlineData(4, ":2.9 #0.08", ":2.8 #0.09", ":2.6 #0.09")]
    public void ApplyPreset_AdjustsTagsAndRetainsBothRestartPronunciations(
        int preset, string flipTags, string restartTags, string alternateTags)
    {
        const string definitions = "F L IH1 P :2.5 #0.16 @FLIP\r\n"
            + "R IY0 S T AA1 R T :2.4 #0.18 @RESTART\r\n"
            + "R IY0 S T AA1 T :2.2 #0.18 @RESTART";
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.Equal($"F L IH1 P {flipTags} @FLIP\nR IY0 S T AA1 R T {restartTags} @RESTART\n"
                + $"R IY0 S T AA1 T {alternateTags} @RESTART",
                VoiceCalibration.ApplyPreset(definitions, preset));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void ApplyPreset_LimitsValuesAndLeavesOtherTokensAlone()
    {
        const string definitions = "B AE1 K :0.15 #0.5 @BACK\nT ER1 N :manual #0.01 @TURN";

        Assert.Equal("B AE1 K :0.1 #0.6 @BACK\nT ER1 N :manual #0.05 @TURN",
            VoiceCalibration.ApplyPreset(definitions, 0));
    }

    [Fact]
    public void Keywords_IncludeEveryCommandOnce()
    {
        Assert.Equal(["flip", "turn", "next", "page", "back", "previous", "restart", "beginning", "finish"],
            VoiceCalibration.Keywords);
    }

    [Fact]
    public void HasUsableSpeech_RequiresSpeechAboveAmbientLevel()
    {
        var ambient = Signal(0.005f);
        var utterance = new float[VoiceCalibration.SampleRate];
        Signal(0.02f, 2400).CopyTo(utterance, 320);

        Assert.True(VoiceCalibration.HasUsableSpeech(utterance, ambient));
        Assert.False(VoiceCalibration.HasUsableSpeech(utterance, Signal(0.011f)));
        Assert.False(VoiceCalibration.HasUsableSpeech(Signal(0.0005f), []));
        Assert.False(VoiceCalibration.HasUsableSpeech(Signal(0.02f, 2240), ambient));
        Assert.False(VoiceCalibration.HasUsableSpeech(new float[VoiceCalibration.SampleRate], ambient));
    }

    [Fact]
    public void HasUsableSpeech_RejectsFullScaleSamplesAndInvalidAudio()
    {
        var utterance = Signal(0.02f);
        Array.Fill(utterance, 32767f / 32768, 0, 160);
        Assert.False(VoiceCalibration.HasUsableSpeech(utterance, []));

        utterance = Signal(0.02f);
        utterance[^1] = float.NaN;
        Assert.False(VoiceCalibration.HasUsableSpeech(utterance, []));
        Assert.False(VoiceCalibration.HasUsableSpeech(Signal(0.02f), [float.PositiveInfinity]));
    }

    [Fact]
    public void MeasureGain_UsesSpeechLevelDespitePadding()
    {
        var utterance = new float[VoiceCalibration.SampleRate * 3];
        Signal(0.04f, 3200).CopyTo(utterance, VoiceCalibration.SampleRate);

        var gain = VoiceCalibration.MeasureGain([utterance, []]);
        VoiceCalibration.ApplyGain(utterance, gain);

        Assert.Equal(2f, gain, precision: 5);
        Assert.Equal(0.08f, utterance.Max(), precision: 5);
    }

    [Fact]
    public void MeasureGain_StaysBoundedAndLimitsPeaks()
    {
        var utterance = Signal(0.02f);
        utterance[0] = 0.8f;
        var gain = VoiceCalibration.MeasureGain([utterance]);
        VoiceCalibration.ApplyGain(utterance, gain);

        Assert.Equal(1.1875f, gain, precision: 5);
        Assert.Equal(0.95f, utterance[0], precision: 5);
        Assert.Equal(4f, VoiceCalibration.MeasureGain([Signal(0.01f)]));
        Assert.Equal(0.25f, VoiceCalibration.MeasureGain([Signal(0.8f)]));
        Assert.Equal(1f, VoiceCalibration.MeasureGain([]));
        Assert.Equal(1f, VoiceCalibration.MeasureGain([Signal(float.NaN)]));
    }

    [Fact]
    public void ApplyGain_ClampsUnexpectedPeaks()
    {
        float[] samples = [0.8f, -0.8f, 0.2f, float.NaN];
        VoiceCalibration.ApplyGain(samples, 2f);

        Assert.Equal([1f, -1f, 0.4f, 0f], samples);
    }

    [Fact]
    public void MatchesPrompt_RequiresExactlyOneMatchingWordOrNoDetectionsForNoise()
    {
        Assert.True(VoiceCalibration.MatchesPrompt("next", ["NEXT"]));
        Assert.True(VoiceCalibration.MatchesPrompt(null, []));
        Assert.False(VoiceCalibration.MatchesPrompt(null, ["NEXT"]));
        Assert.False(VoiceCalibration.MatchesPrompt("next", []));
        Assert.False(VoiceCalibration.MatchesPrompt("next", ["PAGE"]));
        Assert.False(VoiceCalibration.MatchesPrompt("next", ["NEXT", "NEXT"]));
        Assert.False(VoiceCalibration.MatchesPrompt("next", ["NEXT PAGE"]));
    }

    [Fact]
    public void Profile_MatchesOnlyTheMicrophoneUsedToCalibrateIt()
    {
        var profile = new VoiceCalibrationProfile { MicrophoneDeviceId = "front-mic" };

        Assert.True(profile.MatchesDevice("front-mic"));
        Assert.False(profile.MatchesDevice("external-mic"));
        Assert.False(profile.MatchesDevice(null));
        Assert.False(profile.MatchesDevice(string.Empty));
        Assert.False((profile with { Version = 2 }).MatchesDevice("front-mic"));
        Assert.False((profile with { MicrophoneDeviceId = " " }).IsValid);
        Assert.False((profile with { PresetIndex = -1 }).IsValid);
        Assert.False((profile with { PresetIndex = 5 }).IsValid);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0.24f)]
    [InlineData(4.01f)]
    public void Normalize_DropsOnlyInvalidCalibration(float gain)
    {
        var settings = new AppSettings
        {
            VoiceTurningEnabled = false,
            MicrophoneDeviceId = "front-mic",
            VoiceCalibration = new VoiceCalibrationProfile { MicrophoneDeviceId = "front-mic", Gain = gain }
        };

        settings.Normalize();

        Assert.Null(settings.VoiceCalibration);
        Assert.False(settings.VoiceTurningEnabled);
        Assert.Equal("front-mic", settings.MicrophoneDeviceId);
    }

    [Fact]
    public void SaveLoad_RoundTripsVoiceCalibration()
    {
        var path = Path.GetTempFileName();
        try
        {
            var profile = new VoiceCalibrationProfile
            {
                MicrophoneDeviceId = "front-mic",
                CreatedUtc = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero),
                Gain = 0.5f,
                PresetIndex = 4
            };
            new SettingsStore(path).Save(new AppSettings { VoiceCalibration = profile });

            Assert.Equal(profile, new SettingsStore(path).Load().VoiceCalibration);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var serialized = document.RootElement.GetProperty("VoiceCalibration");
            Assert.False(serialized.TryGetProperty("IsValid", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OlderSettingsKeepTheirMicrophoneAndVoicePreference()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{ "VoiceTurningEnabled": false, "MicrophoneDeviceId": "front-mic" }""");

            var loaded = new SettingsStore(path).Load();

            Assert.Null(loaded.VoiceCalibration);
            Assert.False(loaded.VoiceTurningEnabled);
            Assert.Equal("front-mic", loaded.MicrophoneDeviceId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static float[] Signal(float amplitude, int count = VoiceCalibration.SampleRate) =>
        Enumerable.Range(0, count).Select(index => index % 2 == 0 ? amplitude : -amplitude).ToArray();
}
