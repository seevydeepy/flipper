using System.Text.Json.Serialization;

namespace Flipper.Core.Reader;

public sealed record VoiceSoundProfile
{
    public const int MaximumLabelLength = 80;
    public const int TrainingRecordingCount = 3;
    public const int VerificationRecordingCount = 2;

    public int Version { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Label { get; init; } = string.Empty;
    public VoiceCommand Command { get; init; }
    public string MicrophoneDeviceId { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public float TriggerRms { get; init; }
    public float MatchThreshold { get; init; }
    public List<VoiceSoundTemplate> Templates { get; init; } = [];
    public List<VoiceSoundTemplate> VerificationTemplates { get; init; } = [];

    [JsonIgnore]
    public bool IsValid => Version == 1
        && IsValidId(Id) && IsValidLabel(Label) && IsValidCommand(Command)
        && IsValidDeviceId(MicrophoneDeviceId)
        && TriggerRms is >= VoiceSoundCalibration.MinimumTrigger and <= VoiceSoundCalibration.MaximumTrigger
        && MatchThreshold is > 0 and <= VoiceSoundCalibration.MaximumMatchThreshold
        && Templates is { Count: TrainingRecordingCount }
        && Templates.All(template => template is { IsValid: true })
        && VerificationTemplates is { Count: <= VerificationRecordingCount }
        && VerificationTemplates.All(template => template is { IsValid: true });

    public bool MatchesDevice(string? deviceId) =>
        string.Equals(MicrophoneDeviceId, deviceId, StringComparison.Ordinal) && IsValid;

    internal static bool IsValidId(string? id) => id is { Length: <= 80 } && !string.IsNullOrWhiteSpace(id);

    internal static bool IsValidLabel(string? label) => label is { Length: > 0 and <= MaximumLabelLength }
        && !string.IsNullOrWhiteSpace(label) && label == label.Trim();

    internal static bool IsValidCommand(VoiceCommand command) => command != VoiceCommand.None && Enum.IsDefined(command);

    internal static bool IsValidDeviceId(string? deviceId) =>
        deviceId is { Length: <= 1024 } && !string.IsNullOrWhiteSpace(deviceId);
}

public sealed record VoiceSoundTemplate
{
    public int FrameCount { get; init; }
    public float[] Features { get; init; } = [];

    [JsonIgnore]
    public bool IsValid => FrameCount is >= VoiceSoundFeatures.MinimumFrames and <= VoiceSoundFeatures.MaximumFrames
        && Features is not null && Features.Length == FrameCount * VoiceSoundFeatures.CoefficientCount
        && Features.All(value => value is >= -VoiceSoundFeatures.MaximumCoefficient and <= VoiceSoundFeatures.MaximumCoefficient);
}

public sealed record VoiceSoundMatch(string Id, string Label, VoiceCommand Command);
