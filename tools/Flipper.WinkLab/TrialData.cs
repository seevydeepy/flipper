using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flipper.App.Services;
using Flipper.Core.Reader;

namespace Flipper.WinkLab;

internal enum ActualGesture { RightWink, LeftWink, Blink, Neither }

internal enum TrialOutcome { Correct, Missed, WrongDirection, ExtraTurn, UnwantedTurn }

internal sealed record TrialFrame(double TimeMs, int Width, int Height, byte[] Pixels, WinkEyeObservation Eyes, int Turn);

internal sealed record TrialSummary(
    string FilePath, ActualGesture Actual, TrialOutcome Outcome, int Matched, int Missed, int Unexpected,
    string Partition, double DurationMs, int FrameCount);

internal sealed record TrialFrameInfo(
    [property: JsonRequired] double TimeMs,
    [property: JsonRequired] int Width,
    [property: JsonRequired] int Height,
    [property: JsonRequired] WinkEyeObservation Eyes,
    [property: JsonRequired] int Turn);

internal sealed record TrialManifest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] int TrialNumber,
    [property: JsonRequired] ActualGesture Actual,
    [property: JsonRequired] double WarmupMs,
    [property: JsonRequired] double DurationMs,
    [property: JsonRequired] TrialFrameInfo[] Frames);

internal static class TrialData
{
    private const int SchemaVersion = 1;
    internal const string ManifestName = "manifest.json";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static async Task<TrialSummary> SaveAsync(
        string sessionDirectory, int trialNumber, ActualGesture actual, IReadOnlyList<TrialFrame> frames,
        double warmupMs = 1000)
    {
        var manifest = new TrialManifest(SchemaVersion, trialNumber, actual, warmupMs,
            frames.Count == 0 ? 0 : frames[^1].TimeMs,
            frames.Select(frame => new TrialFrameInfo(frame.TimeMs, frame.Width, frame.Height, frame.Eyes, frame.Turn)).ToArray());
        Validate(manifest);
        var path = Path.GetFullPath(Path.Combine(sessionDirectory, $"trial-{trialNumber:D4}.zip"));
        var summary = Evaluate(path, manifest, manifest.Frames);
        Directory.CreateDirectory(sessionDirectory);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames[index];
                    if (frame.Pixels.Length != PixelBytes(manifest.Frames[index]))
                    {
                        throw new InvalidDataException($"Frame {index} has an incorrect BGRA byte count.");
                    }

                    await using var image = zip.CreateEntry(FrameName(index), CompressionLevel.Fastest).Open();
                    await image.WriteAsync(frame.Pixels).ConfigureAwait(false);
                }

                await using var metadata = zip.CreateEntry(ManifestName, CompressionLevel.Fastest).Open();
                await JsonSerializer.SerializeAsync(metadata, manifest, JsonOptions).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path);
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        return summary;
    }

    internal static TrialSummary Evaluate(string path, TrialManifest manifest, IReadOnlyList<TrialFrameInfo> observations)
    {
        var winks = manifest.Actual switch
        {
            ActualGesture.RightWink => WinkGestureKind.RightWink,
            ActualGesture.LeftWink => WinkGestureKind.LeftWink,
            _ => WinkGestureKind.Blink
        };
        WinkAnnotation[] labels = manifest.Actual == ActualGesture.Neither
            ? []
            : [new(winks, manifest.WarmupMs, manifest.DurationMs)];
        var turns = observations.Where(frame => frame.TimeMs >= manifest.WarmupMs && frame.Turn != 0)
            .Select(frame => new WinkTurn(frame.TimeMs, frame.Turn)).ToArray();
        var score = WinkEvaluation.Compare(labels, turns, manifest.DurationMs, responseAllowanceMs: 0);
        var outcome = (score.Expected, score.Matches.Count, score.UnexpectedTurns.Count) switch
        {
            (0, _, > 0) => TrialOutcome.UnwantedTurn,
            (1, 0, 0) => TrialOutcome.Missed,
            (1, 0, _) => TrialOutcome.WrongDirection,
            (1, 1, > 0) => TrialOutcome.ExtraTurn,
            _ => TrialOutcome.Correct
        };
        return new TrialSummary(path, manifest.Actual, outcome, score.Matches.Count, score.MissedWinks.Count,
            score.UnexpectedTurns.Count, manifest.TrialNumber % 5 == 0 ? "validation" : "tuning",
            manifest.DurationMs, observations.Count);
    }

    internal static void Validate(TrialManifest manifest)
    {
        if (manifest.Version != SchemaVersion)
            throw new InvalidDataException($"Unsupported trial format version {manifest.Version}.");

        if (manifest.TrialNumber <= 0 || !Enum.IsDefined(manifest.Actual)
            || !double.IsFinite(manifest.WarmupMs) || manifest.WarmupMs < 0
            || manifest.Frames is not { Length: > 0 } || manifest.Frames[^1] is not { } last
            || manifest.DurationMs != last.TimeMs || last.TimeMs <= manifest.WarmupMs)
        {
            throw new InvalidDataException("A labelled trial with recorded frames after warm-up is required.");
        }

        var previousTime = -1d;
        foreach (var frame in manifest.Frames)
        {
            if (frame is null || !double.IsFinite(frame.TimeMs) || frame.TimeMs < 0 || frame.TimeMs <= previousTime
                || frame.TimeMs >= TimeSpan.MaxValue.TotalMilliseconds || frame.Turn is < -1 or > 1
                || string.IsNullOrWhiteSpace(frame.Eyes.TrackingStatus))
            {
                throw new InvalidDataException("Trial frame timestamps, observations or turn directions are invalid.");
            }

            PixelBytes(frame);
            previousTime = frame.TimeMs;
        }
    }

    internal static int PixelBytes(TrialFrameInfo frame)
    {
        var pixels = (long)frame.Width * frame.Height;
        if (frame.Width <= 0 || frame.Height <= 0 || pixels > Array.MaxLength / 4)
            throw new InvalidDataException("Trial frame dimensions are invalid.");

        return (int)pixels * 4;
    }

    internal static string FrameName(int index) => $"frames/{index:D6}.bgra";
}
