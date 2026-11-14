using Flipper.Core.Reader;

namespace Flipper.Core.Tests;

public sealed class VoiceSoundTests
{
    [Fact]
    public void HeldOutSoundMatchesDespiteChangesInPaceAndVolume()
    {
        var profile = Fit("next", "Finish the score", VoiceCommand.Next, FirstSound);
        var quieter = Recording(FirstSound, 1.08, 0.55f, leadMs: 87);
        var faster = Recording(FirstSound, 0.91, 1.32f, leadMs: 74);

        Assert.True(VoiceSoundCalibration.Check(profile, quieter, true, []).Passed);
        Assert.True(VoiceSoundCalibration.Check(profile, faster, true, []).Passed);
        Assert.Empty(VoiceSoundRecognizer.Replay(Recording(FirstSound.Reverse().ToArray()), [profile]));
        Assert.Equal(new VoiceSoundMatch(profile.Id, "Finish the score", VoiceCommand.Next),
            Assert.Single(VoiceSoundRecognizer.Replay(quieter, [profile])));
        var renamed = profile with { Label = "製本 🪁" };
        Assert.Equal(new VoiceSoundMatch(renamed.Id, "製本 🪁", VoiceCommand.Next),
            Assert.Single(VoiceSoundRecognizer.Replay(quieter, [renamed])));
    }

    [Fact]
    public void TwoIndependentlyCalibratedAliasesUseTheSameAction()
    {
        var first = Fit("alias-a", "One", VoiceCommand.Next, FirstSound);
        var second = Fit("alias-b", "Two", VoiceCommand.Next, SecondSound, [first]);
        var overlapping = Fit("overlap", "Three", VoiceCommand.Next, FirstSound, [first, second]);

        var firstMatch = Assert.Single(VoiceSoundRecognizer.Replay(Recording(FirstSound), [first, second, overlapping]));
        var secondMatch = Assert.Single(VoiceSoundRecognizer.Replay(Recording(SecondSound), [first, second, overlapping]));
        Assert.Equal(VoiceCommand.Next, firstMatch.Command);
        Assert.Equal(new VoiceSoundMatch(second.Id, second.Label, VoiceCommand.Next), secondMatch);
        Assert.False(VoiceSoundCalibration.Check(second, Recording(FirstSound), true, [first]).Passed);
    }

    [Fact]
    public void AddingAQuieterAliasPreservesRecognitionOfAnExistingSoundWithAQuietTail()
    {
        var first = Fit("first", "First", VoiceCommand.Next, FirstSound);
        var withTail = Recording(FirstSound, lengthMs: 4000);
        var tailStart = VoiceSoundCalibration.SampleRate * 440 / 1000;
        for (var i = tailStart; i < withTail.Length; i++)
        {
            withTail[i] += 0.004f * MathF.Sin(2 * MathF.PI * 420 * i / VoiceSoundCalibration.SampleRate);
        }
        var originalMatch = new VoiceSoundMatch(first.Id, first.Label, first.Command);
        Assert.Equal(originalMatch, Assert.Single(VoiceSoundRecognizer.Replay(withTail, [first])));

        var quieterSamples = Training(SecondSound).Select(clip => clip.Select(sample => sample * 0.25f).ToArray()).ToArray();
        var fit = VoiceSoundCalibration.Fit("second", "Second", VoiceCommand.Next, "mic",
            Silence(), Silence(), quieterSamples, [first]);
        Assert.True(fit.Profile is not null, fit.Error);

        Assert.Equal(originalMatch, Assert.Single(VoiceSoundRecognizer.Replay(withTail, [first, fit.Profile!])));
        var quieterMatch = Assert.Single(VoiceSoundRecognizer.Replay(Recording(SecondSound, volume: 0.25f), [first, fit.Profile!]));
        Assert.Equal("second", quieterMatch.Id);
    }

    [Fact]
    public void RecalibratingTheOnlyAliasCanUseAQuieterLevel()
    {
        var first = Fit("first", "First", VoiceCommand.Next, FirstSound);
        var quietSamples = Training(FirstSound).Select(clip => clip.Select(sample => sample * 0.035f).ToArray()).ToArray();
        var quietSound = Recording(FirstSound, volume: 0.035f);
        Assert.Empty(VoiceSoundRecognizer.Replay(quietSound, [first]));

        var fit = VoiceSoundCalibration.Fit(first.Id, first.Label, first.Command, "mic", Silence(), Silence(), quietSamples, [first]);
        Assert.True(fit.Profile is not null, fit.Error);
        Assert.Single(VoiceSoundRecognizer.Replay(quietSound, [fit.Profile!]));
    }

    [Fact]
    public void SimilarSoundsOnDifferentActionsAreRejectedDuringFitAndLive()
    {
        var first = Fit("first", "First", VoiceCommand.Next, FirstSound);
        var conflict = VoiceSoundCalibration.Fit("second", "Second", VoiceCommand.Back, "mic",
            Silence(), Silence(), Training(FirstSound), [first]);

        Assert.Null(conflict.Profile);
        Assert.Contains("First", conflict.Error);
        Assert.Empty(VoiceSoundRecognizer.Replay(Recording(FirstSound), [first, first with { Command = VoiceCommand.Back }]));
        Assert.NotNull(VoiceSoundCalibration.Fit(first.Id, "First", VoiceCommand.Back, "mic",
            Silence(), Silence(), Training(FirstSound), [first]).Profile);
        Assert.NotNull(VoiceSoundCalibration.Fit("other", "First", VoiceCommand.Back, "other-mic",
            Silence(), Silence(), Training(FirstSound), [first]).Profile);
    }

    [Fact]
    public void FitDetectsAConflictWithAnExistingHeldOutTemplate()
    {
        var existing = Fit("old", "Old", VoiceCommand.Back, SecondSound);
        var newSound = Fit("new", "New", VoiceCommand.Next, FirstSound);
        existing = existing with { VerificationTemplates = [newSound.Templates[0], existing.Templates[0]] };

        var result = VoiceSoundCalibration.Fit("new", "New", VoiceCommand.Next, "mic",
            Silence(), Silence(), Training(FirstSound), [existing]);

        Assert.Null(result.Profile);
        Assert.Contains("Old", result.Error);
    }

    [Fact]
    public void CalibrationChecksSilenceSeesPlayingAndRejectsBackgroundThatLooksLikeTheCommand()
    {
        var playing = Enumerable.Range(0, VoiceSoundCalibration.SampleRate * 2)
            .Select(sample => 0.018f * MathF.Sin(2 * MathF.PI * 523 * sample / VoiceSoundCalibration.SampleRate)).ToArray();
        var fit = VoiceSoundCalibration.Fit("next", "Beep", VoiceCommand.Next, "mic",
            Silence(), playing, Training(FirstSound), []);
        var profile = Assert.IsType<VoiceSoundProfile>(fit.Profile);

        Assert.True(VoiceSoundCalibration.Check(profile, Silence(), false, []).Passed);
        Assert.True(VoiceSoundCalibration.Check(profile, playing, false, []).Passed);
        Assert.False(VoiceSoundCalibration.Check(profile, Recording(FirstSound), false, []).Passed);
        Assert.False(VoiceSoundCalibration.Check(profile, Silence(), true, []).Passed);
        Assert.Empty(VoiceSoundRecognizer.Replay(playing, [profile]));
        var mixed = WithBackground(Recording(FirstSound, lengthMs: 2000), playing, 0.05f);
        Assert.Single(VoiceSoundRecognizer.Replay(mixed, [profile]));

        var badBackground = VoiceSoundCalibration.Fit("next", "Beep", VoiceCommand.Next, "mic",
            Silence(), Recording(FirstSound, lengthMs: 2500), Training(FirstSound), []);
        Assert.Null(badBackground.Profile);
    }

    [Fact]
    public void CalibrationRejectsInconsistentClippedAndRepeatedTrainingSounds()
    {
        var training = Training(FirstSound);
        Assert.Null(TryFit([training[0], training[1], Recording(SecondSound)]).Profile);
        Assert.Null(TryFit([training[0], training[1], Silence()]).Profile);
        Assert.Null(TryFit([training[0], training[1]]).Profile);

        var clipped = (float[])training[2].Clone();
        Array.Fill(clipped, 1, 1200, 500);
        Assert.Null(TryFit([training[0], training[1], clipped]).Profile);
        var invalid = (float[])training[2].Clone();
        invalid[^1] = float.NaN;
        Assert.Null(TryFit([training[0], training[1], invalid]).Profile);

        float[] repeated = [.. training[2], .. training[2]];
        Assert.Null(TryFit([training[0], training[1], repeated]).Profile);

        static VoiceSoundCalibrationResult TryFit(IReadOnlyList<float[]> samples) =>
            VoiceSoundCalibration.Fit("tone", "A tone", VoiceCommand.Next, "mic", Silence(), Silence(), samples, []);
    }

    [Fact]
    public void RecognitionIsIndependentOfChunkBoundariesAndUsesSampleTimeForCooldown()
    {
        var profile = Fit("next", "Beep", VoiceCommand.Next, FirstSound);
        var sound = Recording(FirstSound, lengthMs: 900);
        float[] stream = [.. sound, .. sound, .. sound, .. Silence(320)];
        var expected = VoiceSoundRecognizer.Replay(stream, [profile]);

        Assert.Equal(3, expected.Count);
        var recognizer = new VoiceSoundRecognizer([profile]);
        var actual = stream.Chunk(337).SelectMany(recognizer.Accept).ToArray();
        Assert.Equal(expected, actual);

        var closeReplay = Recording(FirstSound, lengthMs: 800);
        Assert.Single(VoiceSoundRecognizer.Replay([.. closeReplay, .. closeReplay], [profile]));
        Assert.False(VoiceSoundCalibration.Check(profile, [.. closeReplay, .. closeReplay], true, []).Passed);
    }

    [Fact]
    public void ReplayFinishesAnUtteranceThatEndsInsideAFrame()
    {
        var profile = Fit("next", "Beep", VoiceCommand.Next, FirstSound);
        var partial = Recording(FirstSound, lengthMs: 431);

        Assert.Single(VoiceSoundRecognizer.Replay(partial, [profile]));
        Assert.True(VoiceSoundCalibration.Check(profile, partial, true, []).Passed);
    }

    [Fact]
    public void OverlongSoundsAreDiscardedUntilQuietAndResetDropsBufferedSound()
    {
        var profile = Fit("next", "Beep", VoiceCommand.Next, FirstSound);
        var recognizer = new VoiceSoundRecognizer([profile]);
        var overlong = Recording([420], pace: 32, leadMs: 0, lengthMs: 3450);
        var recording = Recording(FirstSound);

        Assert.Empty(recognizer.Accept([.. overlong, .. recording[1280..]]));
        Assert.Single(recognizer.Accept(recording));
        Assert.Empty(recognizer.Accept(recording[..5744]));
        recognizer.Reset();
        Assert.Empty(recognizer.Accept(recording[5744..]));
        recognizer.Reset();
        Assert.Single(recognizer.Accept(recording));
    }

    [Fact]
    public void InvalidProfilesCannotTriggerMatches()
    {
        var profile = Fit("next", "Beep", VoiceCommand.Next, FirstSound);
        VoiceSoundProfile[] invalid =
        [
            profile with { Version = 2 },
            profile with { Id = " " },
            profile with { Label = new string('x', VoiceSoundProfile.MaximumLabelLength + 1) },
            profile with { Command = VoiceCommand.None },
            profile with { Command = (VoiceCommand)17 },
            profile with { MicrophoneDeviceId = " " },
            profile with { TriggerRms = float.NaN },
            profile with { MatchThreshold = float.PositiveInfinity },
            profile with { Templates = null! },
            profile with { VerificationTemplates = null! },
            profile with { Templates = [null!, profile.Templates[1], profile.Templates[2]] },
            profile with { Templates = [profile.Templates[0] with { Features = [float.NaN] }, profile.Templates[1], profile.Templates[2]] }
        ];

        Assert.All(invalid, item => Assert.False(item.IsValid));
        Assert.Empty(VoiceSoundRecognizer.Replay(Recording(FirstSound), invalid));
    }

    internal static readonly int[] FirstSound = [420, 810, 1270];
    internal static readonly int[] SecondSound = [2050, 1500, 670];

    internal static VoiceSoundProfile Fit(string id, string label, VoiceCommand command, int[] frequencies,
        IReadOnlyList<VoiceSoundProfile>? others = null)
    {
        var result = VoiceSoundCalibration.Fit(id, label, command, "mic", Silence(), Silence(), Training(frequencies), others ?? []);
        Assert.True(result.Profile is not null, result.Error);
        return result.Profile!;
    }

    private static float[][] Training(int[] frequencies) =>
        [Recording(frequencies), Recording(frequencies, 1.05, 0.92f, leadMs: 94), Recording(frequencies, 0.97, 1.05f, leadMs: 63)];

    internal static float[] Recording(int[] frequencies, double pace = 1, float volume = 1, int leadMs = 80, int lengthMs = 1150)
    {
        var clip = Silence(lengthMs);
        var start = leadMs * VoiceSoundCalibration.SampleRate / 1000;
        var duration = (int)(0.12 * VoiceSoundCalibration.SampleRate * pace);
        foreach (var frequency in frequencies)
        {
            for (var i = 0; i < duration && start + i < clip.Length; i++)
            {
                var edge = MathF.Min(1f, MathF.Min(i, duration - i) / 160f);
                var phase = 2 * Math.PI * frequency * i / VoiceSoundCalibration.SampleRate;
                clip[start + i] = volume * edge * (0.22f * (float)Math.Sin(phase) + 0.06f * (float)Math.Sin(2 * phase));
            }
            start += duration;
        }
        return clip;
    }

    private static float[] WithBackground(float[] audio, float[] background, float scale) =>
        audio.Zip(background, (sample, noise) => sample + noise * scale).ToArray();

    private static float[] Silence(int durationMs = 1000) => new float[durationMs * VoiceSoundCalibration.SampleRate / 1000];
}
