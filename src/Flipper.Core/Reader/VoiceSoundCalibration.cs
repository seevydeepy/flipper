namespace Flipper.Core.Reader;

public sealed record VoiceSoundCalibrationResult(VoiceSoundProfile? Profile, string? Error);

/// <param name="RetryStep">
/// True when the recording was not usable input, for example silence, two sounds or distorted audio.
/// The same step can then be recorded again. False when a usable recording failed, so the
/// command sound must be recorded again.
/// </param>
public sealed record VoiceSoundCheckResult(bool Passed, string? Error, VoiceSoundTemplate? Template = null,
    bool RetryStep = false);

public static class VoiceSoundCalibration
{
    public const int SampleRate = 16_000;
    internal const float MinimumTrigger = 0.0015f;
    internal const float MaximumTrigger = 0.5f;
    internal const float MaximumMatchThreshold = 1.6f;
    private const float MinimumMatchThreshold = 0.42f;
    private const string Distorted = "The sound was too loud and was distorted. Move back from the microphone a little, then try again.";
    private const string NotHeard = "No word was heard. Say it at your normal volume, close to the microphone, then try again.";
    private const string MoreThanOnce = "More than one sound was heard. Say the word once, then stay quiet until the recording ends.";
    private const string WrongLength = "The sound was too short or too long. Say the word once, at your usual pace.";

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
            return new(null, "Record the word three times, clearly.");
        }

        var others = OtherProfiles(existing, id, deviceId);
        var minimumPeak = recordings.Min(audio => FrameLevels(audio)[^1]);
        var trigger = Trigger(quiet, background, minimumPeak, others);
        if (QuietError(trigger, minimumPeak, others) is { } quietError)
        {
            return new(null, quietError);
        }

        List<VoiceSoundTemplate> templates = [];
        foreach (var audio in recordings)
        {
            var (count, template) = Events(audio, trigger);
            if (count != 1 || template is null)
            {
                return new(null, "Say the word once in each recording, with a pause before and after it.");
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
            return new(null, "The three recordings sounded too different. Say the word the same way each time, at your usual pace.");
        }

        var candidate = new VoiceSoundProfile
        {
            Id = id, Label = label, Command = command, MicrophoneDeviceId = deviceId, CreatedUtc = DateTimeOffset.UtcNow,
            TriggerRms = trigger,
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
            ? new(null, "Your room or playing sound was too similar to this word. Choose a different word.")
            : new(candidate, null);
    }

    /// <summary>Checks one quiet or playing recording before it is used for calibration.</summary>
    public static VoiceSoundCheckResult CheckBackgroundSample(float[] audio) =>
        IsUsableClip(audio) ? new(true, null) : new(false, Distorted, RetryStep: true);

    /// <summary>
    /// Checks one recording of the command sound as soon as it is made, with the level rules that
    /// <see cref="Fit"/> uses. A failed recording can be repeated without losing the others.
    /// </summary>
    public static VoiceSoundCheckResult CheckCommandSample(string id, string deviceId, float[] quiet, float[] background,
        float[] audio, IReadOnlyList<VoiceSoundProfile> existing)
    {
        if (!IsUsableClip(quiet) || !IsUsableClip(background))
        {
            return new(false, "Record the quiet and background samples again. Check the microphone level.");
        }
        if (!IsUsableClip(audio))
        {
            return new(false, Distorted, RetryStep: true);
        }

        var others = OtherProfiles(existing, id, deviceId);
        var peak = FrameLevels(audio)[^1];
        var trigger = Trigger(quiet, background, peak, others);
        if (peak < trigger && trigger <= MaximumTrigger)
        {
            return new(false, NotHeard, RetryStep: true);
        }
        if (QuietError(trigger, peak, others) is { } quietError)
        {
            return new(false, quietError, RetryStep: true);
        }

        var (count, template) = Events(audio, trigger);
        return count switch
        {
            0 => new(false, NotHeard, RetryStep: true),
            > 1 => new(false, MoreThanOnce, RetryStep: true),
            _ when template is null => new(false, WrongLength, RetryStep: true),
            _ => new(true, null)
        };
    }

    public static VoiceSoundCheckResult Check(VoiceSoundProfile candidate, float[] audio, bool expectCommand,
        IReadOnlyList<VoiceSoundProfile> existing)
    {
        if (!candidate.IsValid)
        {
            return new(false, "This sample could not be checked. Check the microphone level and record again.");
        }
        if (!IsUsableClip(audio))
        {
            return new(false, Distorted, RetryStep: true);
        }

        var others = OtherProfiles(existing, candidate.Id, candidate.MicrophoneDeviceId);
        var recognizer = new VoiceSoundRecognizer([.. others, candidate]);
        var eventCount = 0;
        var matchCount = 0;
        var incomplete = false;
        VoiceSoundMatch? heard = null;
        VoiceSoundTemplate? captured = null;
        recognizer.Scan(audio, (template, match) =>
        {
            eventCount++;
            incomplete |= template is null;
            if (match is not null)
            {
                matchCount++;
                heard ??= match;
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
                : new(false, $"The background sound was mistaken for “{heard!.Label}”. Choose a different word or move the microphone.");
        }
        // Only a single complete sound is a fair test of the profile. Anything else is repeated.
        if (eventCount == 0)
        {
            return new(false, NotHeard, RetryStep: true);
        }
        if (eventCount > 1)
        {
            return new(false, MoreThanOnce, RetryStep: true);
        }
        if (incomplete)
        {
            return new(false, WrongLength, RetryStep: true);
        }
        if (heard is not null && heard.Command != candidate.Command)
        {
            return new(false, $"This sounded like “{heard.Label}”, which does a different action. Choose a different word.");
        }
        if (captured is null)
        {
            return new(false, "The word was not recognised. Record it again and say it the same way each time.");
        }
        if (FindConflict(candidate, [captured], others) is { } conflict)
        {
            return new(false, ConflictMessage(conflict));
        }
        return new(true, null, captured);
    }

    private static bool IsUsableClip(float[] audio) => audio is { Length: >= SampleRate / 5 and <= 20 * SampleRate }
        && VoiceSoundDetector.HasUsableSamples(audio);

    // Changing the shared threshold would change how existing sounds are split into events.
    private static float Trigger(float[] quiet, float[] background, float minimumPeak, VoiceSoundProfile[] others) =>
        others.Length == 0 ? MeasureTrigger(quiet, background, minimumPeak) : others.Min(profile => profile.TriggerRms);

    private static string? QuietError(float trigger, float peak, VoiceSoundProfile[] others) =>
        trigger <= MaximumTrigger && peak >= trigger * 1.5f ? null
        : others.Length == 0
            ? "The word was too quiet compared with the background. Move closer to the microphone and try again."
            : "The word was too quiet for the other words on this microphone. Speak louder or move closer and try again.";

    private static float MeasureTrigger(float[] quiet, float[] background, float minimumPeak)
    {
        var quietLevels = FrameLevels(quiet);
        var playingLevels = FrameLevels(background);
        // Ignore brief peaks in the playing sample but keep sustained background below the offset gate.
        var ambient = Math.Max(quietLevels[quietLevels.Length * 9 / 10], playingLevels[playingLevels.Length * 3 / 10]);
        return Math.Max(MinimumTrigger, Math.Max(ambient * 1.7f, minimumPeak * 0.055f));
    }

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

    private static (int Count, VoiceSoundTemplate? Template) Events(float[] audio, float trigger)
    {
        VoiceSoundTemplate? result = null;
        var count = 0;
        VoiceSoundRecognizer.Scan(new VoiceSoundDetector(trigger), audio, (template, _) =>
        {
            count++;
            result = template;
        });
        return (count, result);
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
        $"This word sounds too similar to “{profile.Label}”, which does a different action. Choose a different word.";
}
