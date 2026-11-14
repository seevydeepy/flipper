using System.Text.Json;
using Flipper.Core.Reader;
using Flipper.Core.Settings;

namespace Flipper.Core.Tests;

public sealed class VoiceSoundSettingsTests
{
    [Fact]
    public void SavedAliasesRoundTripWithTheirOwnSettingsAndCalibrations()
    {
        var first = VerifiedProfile("one", "Close", VoiceSoundTests.FirstSound);
        var second = VerifiedProfile("two", "🚩", VoiceSoundTests.SecondSound);
        var path = Path.GetTempFileName();
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { VoiceCommands = [first, second], VoiceTurningEnabled = false, MicrophoneDeviceId = "mic" });

            var loaded = store.Load();
            Assert.False(loaded.VoiceTurningEnabled);
            Assert.Equal("mic", loaded.MicrophoneDeviceId);
            Assert.Equal(2, loaded.VoiceCommands.Count);
            AssertSaved(first, loaded.VoiceCommands[0]);
            AssertSaved(second, loaded.VoiceCommands[1]);
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            var sound = saved.RootElement.GetProperty("VoiceCommands")[0];
            Assert.False(sound.TryGetProperty("IsValid", out _));
            Assert.False(sound.GetProperty("Templates")[0].TryGetProperty("IsValid", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadRemovesInvalidOrIncompleteProfilesAndPreservesOtherPreferences()
    {
        var profile = VerifiedProfile("valid", "Clean", VoiceSoundTests.FirstSound);
        var path = Path.GetTempFileName();
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                VoiceCommands =
                [
                    null!, profile with { Id = "invalid", Templates = null! },
                    profile with { Id = "incomplete", VerificationTemplates = [] },
                    profile, profile with { Label = "Duplicate" }
                ],
                VoiceTurningEnabled = false,
                MicrophoneDeviceId = "mic",
                CropToInk = true
            });

            var loaded = store.Load();
            AssertSaved(profile, Assert.Single(loaded.VoiceCommands));
            Assert.False(loaded.VoiceTurningEnabled);
            Assert.Equal("mic", loaded.MicrophoneDeviceId);
            Assert.True(loaded.CropToInk);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EarlierSensitivitySettingsKeepTheMicrophoneAndVoicePreference()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  "VoiceTurningEnabled": false,
                  "MicrophoneDeviceId": "front-mic",
                  "VoiceCalibration": { "Version": 1, "MicrophoneDeviceId": "front-mic", "Gain": 0.5, "PresetIndex": 2 }
                }
                """);
            var loaded = new SettingsStore(path).Load();

            Assert.False(loaded.VoiceTurningEnabled);
            Assert.Equal("front-mic", loaded.MicrophoneDeviceId);
            Assert.Empty(loaded.VoiceCommands);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static VoiceSoundProfile VerifiedProfile(string id, string label, int[] frequencies)
    {
        var profile = VoiceSoundTests.Fit(id, label, VoiceCommand.Next, frequencies);
        var first = VoiceSoundCalibration.Check(profile, VoiceSoundTests.Recording(frequencies, 1.08, 0.55f), true, []);
        var second = VoiceSoundCalibration.Check(profile, VoiceSoundTests.Recording(frequencies, 0.91, 1.32f), true, []);
        Assert.True(first.Passed, first.Error);
        Assert.True(second.Passed, second.Error);
        return profile with { VerificationTemplates = [first.Template!, second.Template!] };
    }

    private static void AssertSaved(VoiceSoundProfile expected, VoiceSoundProfile loaded)
    {
        Assert.True(loaded.IsValid);
        Assert.Equal(expected.Id, loaded.Id);
        Assert.Equal(expected.Label, loaded.Label);
        Assert.Equal(expected.Command, loaded.Command);
        Assert.True(loaded.MatchesDevice(expected.MicrophoneDeviceId));
        Assert.Equal(expected.CreatedUtc, loaded.CreatedUtc);
        Assert.Equal(expected.MatchThreshold, loaded.MatchThreshold);
        Assert.Equal(expected.TriggerRms, loaded.TriggerRms);
        foreach (var (first, second) in expected.Templates.Concat(expected.VerificationTemplates)
                     .Zip(loaded.Templates.Concat(loaded.VerificationTemplates)))
        {
            Assert.Equal(first.FrameCount, second.FrameCount);
            Assert.Equal(first.Features, second.Features);
        }
        Assert.Equal(VoiceSoundProfile.VerificationRecordingCount, loaded.VerificationTemplates.Count);
    }
}
