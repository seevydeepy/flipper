using System.Text.Json.Serialization;

namespace Flipper.Core.Reader;

public sealed record VoiceCalibrationProfile
{
    public int Version { get; init; } = 1;
    public string MicrophoneDeviceId { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public float Gain { get; init; } = 1f;
    public int PresetIndex { get; init; } = VoiceCalibration.DefaultPresetIndex;

    [JsonIgnore]
    public bool IsValid =>
        Version == 1 && !string.IsNullOrWhiteSpace(MicrophoneDeviceId)
        && Gain is >= VoiceCalibration.MinimumGain and <= VoiceCalibration.MaximumGain
        && PresetIndex >= 0 && PresetIndex < VoiceCalibration.PresetCount;

    public bool MatchesDevice(string? deviceId) =>
        IsValid && string.Equals(MicrophoneDeviceId, deviceId, StringComparison.Ordinal);
}
