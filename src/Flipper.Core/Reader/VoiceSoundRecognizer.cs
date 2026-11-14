namespace Flipper.Core.Reader;

public sealed class VoiceSoundRecognizer
{
    private const int CooldownSamples = VoiceSoundCalibration.SampleRate * 9 / 10;
    internal const float AmbiguityMargin = 0.12f;
    private static readonly float[] EndingSilence = new float[VoiceSoundDetector.FrameSamples * (VoiceSoundDetector.SilenceFrames + 1)];
    private readonly VoiceSoundProfile[] _profiles;
    private readonly VoiceSoundDetector? _detector;
    private long _nextMatch;

    public VoiceSoundRecognizer(IReadOnlyList<VoiceSoundProfile> profiles)
    {
        _profiles = profiles.Where(profile => profile is { IsValid: true }).ToArray();
        if (_profiles.Length != 0)
        {
            _detector = new VoiceSoundDetector(_profiles.Min(profile => profile.TriggerRms));
        }
    }

    public IReadOnlyList<VoiceSoundMatch> Accept(float[] samples)
    {
        List<VoiceSoundMatch>? matches = null;
        _detector?.Accept(samples, (template, clock) =>
        {
            if (Match(template, clock) is { } match)
            {
                (matches ??= []).Add(match);
            }
        });
        return matches ?? (IReadOnlyList<VoiceSoundMatch>)Array.Empty<VoiceSoundMatch>();
    }

    public void Reset()
    {
        _detector?.Reset();
        _nextMatch = 0;
    }

    public static IReadOnlyList<VoiceSoundMatch> Replay(float[] audio, IReadOnlyList<VoiceSoundProfile> profiles)
    {
        List<VoiceSoundMatch> matches = [];
        new VoiceSoundRecognizer(profiles).Scan(audio, (_, match) =>
        {
            if (match is not null)
            {
                matches.Add(match);
            }
        });
        return matches;
    }

    internal void Scan(float[] audio, Action<VoiceSoundTemplate?, VoiceSoundMatch?> onEvent)
    {
        if (_detector is not null)
        {
            Scan(_detector, audio, (template, clock) => onEvent(template, Match(template, clock)));
        }
    }

    internal static void Scan(VoiceSoundDetector detector, float[] audio, Action<VoiceSoundTemplate?, long> onEvent)
    {
        detector.Accept(audio, onEvent);
        detector.Accept(EndingSilence, onEvent);
    }

    private VoiceSoundMatch? Match(VoiceSoundTemplate? template, long clock)
    {
        if (template is null || clock < _nextMatch)
        {
            return null;
        }

        Span<float> scores = stackalloc float[(int)VoiceCommand.Finish + 1];
        scores.Fill(float.PositiveInfinity);
        VoiceSoundProfile? winner = null;
        var bestScore = float.PositiveInfinity;
        foreach (var profile in _profiles)
        {
            var score = Score(template, profile);
            var action = (int)profile.Command;
            scores[action] = Math.Min(scores[action], score);
            if (score <= 1 && score < bestScore)
            {
                winner = profile;
                bestScore = score;
            }
        }

        if (winner is null)
        {
            return null;
        }
        for (var action = 1; action < scores.Length; action++)
        {
            if (action != (int)winner.Command && scores[action] <= bestScore + AmbiguityMargin)
            {
                return null;
            }
        }

        _nextMatch = clock + CooldownSamples;
        return new VoiceSoundMatch(winner.Id, winner.Label, winner.Command);
    }

    internal static float Score(VoiceSoundTemplate template, VoiceSoundProfile profile)
    {
        var nearest = float.PositiveInfinity;
        foreach (var example in profile.Templates)
        {
            nearest = Math.Min(nearest, VoiceSoundFeatures.Distance(template, example));
        }
        return nearest / profile.MatchThreshold;
    }
}
