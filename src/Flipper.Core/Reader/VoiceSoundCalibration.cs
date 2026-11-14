namespace Flipper.Core.Reader;

public sealed record VoiceSoundCalibrationResult(VoiceSoundProfile? Profile, string? Error);
public sealed record VoiceSoundCheckResult(bool Passed, string? Error, VoiceSoundTemplate? Template = null);

public static class VoiceSoundCalibration
{
    public const int SampleRate = 16_000;
    internal const float MinimumTrigger = 0.0015f;
    internal const float MaximumTrigger = 0.5f;
    internal const float MaximumMatchThreshold = 1.6f;
    private const float MinimumMatchThreshold = 0.42f;

    public static VoiceSoundCalibrationResult Fit(string id, string label, VoiceCommand command, string deviceId,
        float[] quiet, float[] background, IReadOnlyList<float[]> recordings, IReadOnlyList<VoiceSoundProfile> existing)
    {
        label = label?.Trim() ?? string.Empty;
        if (!VoiceSoundProfile.IsValidId(id) || !VoiceSoundProfile.IsValidLabel(label)
            || !VoiceSoundProfile.IsValidCommand(command) || !VoiceSoundProfile.IsValidDeviceId(deviceId))
        {
            return new(null, "Enter a command label, select an action and connect a microphone.");
        }
        if (!IsUsableClip(quiet) || !IsUsableClip(background))
        {
            return new(null, "Record the quiet and background samples again. Check the microphone level.");
        }
        if (recordings.Count != VoiceSoundProfile.TrainingRecordingCount || recordings.Any(audio => !IsUsableClip(audio)))
        {
            return new(null, "Record three clear samples of the same command sound.");
        }

        var minimumPeak = recordings.Min(audio => FrameLevels(audio)[^1]);
        var quietLevels = FrameLevels(quiet);
        var playingLevels = FrameLevels(background);
        // Ignore brief peaks in the playing sample but keep sustained background below the offset gate.
        var ambient = Math.Max(quietLevels[quietLevels.Length * 9 / 10], playingLevels[playingLevels.Length * 3 / 10]);
        var trigger = Math.Max(MinimumTrigger, Math.Max(ambient * 1.7f, minimumPeak * 0.055f));
        if (trigger > MaximumTrigger || minimumPeak < trigger * 1.5f)
        {
            return new(null, "The command was too quiet for the background level. Move closer to the microphone and record again.");
        }

        var others = OtherProfiles(existing, id, deviceId);
        var combinedTrigger = others.Aggregate(trigger, (level, profile) => Math.Min(level, profile.TriggerRms));
        List<VoiceSoundTemplate> templates = [];
        foreach (var audio in recordings)
        {
            var template = SingleEvent(audio, trigger);
            if (template is null)
            {
                return new(null, "Say the command sound once in each recording and pause before and after it.");
            }
            if (combinedTrigger < trigger)
            {
                template = SingleEvent(audio, combinedTrigger);
                if (template is null)
                {
                    return new(null, "The background made it hard to separate this sound from the other calibrated commands.");
                }
            }
            templates.Add(template);
        }

        var nearest = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        for (var first = 0; first < templates.Count; first++)
        {
            for (var second = first + 1; second < templates.Count; second++)
            {
                var distance = VoiceSoundFeatures.Distance(templates[first], templates[second]);
                nearest[first] = Math.Min(nearest[first], distance);
                nearest[second] = Math.Min(nearest[second], distance);
            }
        }
        var observedSpread = nearest.Max();
        if (observedSpread > MaximumMatchThreshold * 0.8f)
        {
            return new(null, "The command recordings were too different. Repeat the same sound at your usual pace.");
        }

        var candidate = new VoiceSoundProfile
        {
            Id = id, Label = label, Command = command, MicrophoneDeviceId = deviceId, CreatedUtc = DateTimeOffset.UtcNow,
            TriggerRms = combinedTrigger,
            MatchThreshold = Math.Clamp(observedSpread * 1.6f, MinimumMatchThreshold, MaximumMatchThreshold),
            Templates = templates
        };
        if (FindConflict(candidate, candidate.Templates, others) is { } conflict)
        {
            return new(null, ConflictMessage(conflict));
        }

        var recognizer = new VoiceSoundRecognizer([.. others, candidate]);
        var candidateHeard = false;
        foreach (var negative in new[] { quiet, background })
        {
            recognizer.Reset();
            recognizer.Scan(negative, (template, _) => candidateHeard |= template is not null
                && VoiceSoundRecognizer.Score(template, candidate) <= 1);
        }
        return candidateHeard
            ? new(null, "The background sounded like this command. Choose a different sound and record again.")
            : new(candidate, null);
    }

    public static VoiceSoundCheckResult Check(VoiceSoundProfile candidate, float[] audio, bool expectCommand,
        IReadOnlyList<VoiceSoundProfile> existing)
    {
        if (!candidate.IsValid || !IsUsableClip(audio))
        {
            return new(false, "This sample could not be checked. Check the microphone level and record again.");
        }

        var others = OtherProfiles(existing, candidate.Id, candidate.MicrophoneDeviceId);
        var recognizer = new VoiceSoundRecognizer([.. others, candidate]);
        var eventCount = 0;
        var matchCount = 0;
        VoiceSoundTemplate? captured = null;
        recognizer.Scan(audio, (template, match) =>
        {
            eventCount++;
            if (match is not null)
            {
                matchCount++;
                if (match.Command == candidate.Command && template is not null && VoiceSoundRecognizer.Score(template, candidate) <= 1)
                {
                    captured = template;
                }
            }
        });

        if (!expectCommand)
        {
            return matchCount == 0
                ? new(true, null)
                : new(false, "The background triggered a command. Use a different sound or check the microphone position.");
        }
        if (eventCount != 1 || matchCount != 1 || captured is null)
        {
            return new(false, "The sample did not match this command once. Repeat the same sound and pause before and after it.");
        }
        if (FindConflict(candidate, [captured], others) is { } conflict)
        {
            return new(false, ConflictMessage(conflict));
        }
        return new(true, null, captured);
    }

    private static bool IsUsableClip(float[] audio) => audio is { Length: >= SampleRate / 5 and <= 20 * SampleRate }
        && VoiceSoundDetector.HasUsableSamples(audio);

    private static float[] FrameLevels(float[] audio)
    {
        var levels = new float[audio.Length / VoiceSoundDetector.FrameSamples];
        for (var frame = 0; frame < levels.Length; frame++)
        {
            levels[frame] = VoiceSoundDetector.Rms(audio.AsSpan(frame * VoiceSoundDetector.FrameSamples, VoiceSoundDetector.FrameSamples));
        }
        Array.Sort(levels);
        return levels;
    }

    private static VoiceSoundTemplate? SingleEvent(float[] audio, float trigger)
    {
        VoiceSoundTemplate? result = null;
        var count = 0;
        VoiceSoundRecognizer.Scan(new VoiceSoundDetector(trigger), audio, (template, _) =>
        {
            count++;
            result = template;
        });
        return count == 1 ? result : null;
    }

    private static VoiceSoundProfile[] OtherProfiles(IReadOnlyList<VoiceSoundProfile> existing, string id, string deviceId) =>
        existing.Where(profile => profile is not null && profile.Id != id && profile.MatchesDevice(deviceId)).ToArray();

    private static VoiceSoundProfile? FindConflict(VoiceSoundProfile candidate, IReadOnlyList<VoiceSoundTemplate> examples,
        IReadOnlyList<VoiceSoundProfile> others)
    {
        var separation = 1 + VoiceSoundRecognizer.AmbiguityMargin;
        return others.FirstOrDefault(other => other.Command != candidate.Command
            && (examples.Any(example => VoiceSoundRecognizer.Score(example, other) <= separation)
                || other.Templates.Concat(other.VerificationTemplates)
                    .Any(example => VoiceSoundRecognizer.Score(example, candidate) <= separation)));
    }

    private static string ConflictMessage(VoiceSoundProfile profile) =>
        $"This sound is too similar to “{profile.Label}”, which uses a different action. Choose a different sound.";
}
