using System.IO.Compression;
using System.Text.Json;
using Flipper.App.Services;
using Flipper.Core.Reader;

namespace Flipper.WinkLab;

internal static class TrialReplay
{
    private sealed record FrameTrace(
        double TimeMs, WinkEyeObservation BaselineEyes, int BaselineTurn, WinkEyeObservation CurrentEyes, int CurrentTurn);

    private sealed record ReplayResult(TrialSummary Baseline, TrialSummary Current, FrameTrace[] Frames);

    public static async Task RunAsync(string path, string reportPath)
    {
        string[] trials = Directory.Exists(path)
            ? Directory.GetFiles(path, "trial-*.zip").Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [path];
        if (trials.Length == 0)
            throw new InvalidDataException("No labelled trial archives were found.");

        var results = new List<ReplayResult>();
        foreach (var trial in trials)
        {
            try
            {
                using var estimator = new WinkEyeEstimator();
                results.Add(await ReplayAsync(Path.GetFullPath(trial), estimator).ConfigureAwait(false));
            }
            catch (Exception error) when (error is InvalidDataException or IOException or JsonException)
            {
                throw new InvalidDataException($"Cannot replay {Path.GetFileName(trial)}. {error.Message}", error);
            }
        }

        await using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(report, results, TrialData.JsonOptions).ConfigureAwait(false);
    }

    private static async Task<ReplayResult> ReplayAsync(string path, WinkEyeEstimator estimator)
    {
        using var zip = ZipFile.OpenRead(path);
        await using var metadata = OpenEntry(zip, TrialData.ManifestName);
        var manifest = await JsonSerializer.DeserializeAsync<TrialManifest>(metadata, TrialData.JsonOptions).ConfigureAwait(false)
            ?? throw new InvalidDataException("Trial manifest is empty.");
        TrialData.Validate(manifest);

        var detector = new WinkPageTurnDetector();
        (int Width, int Height, int RotationQuarterTurns)? lastGeometry = null;
        var current = new TrialFrameInfo[manifest.Frames.Length];
        var trace = new FrameTrace[manifest.Frames.Length];
        byte[] pixels = [];
        for (var index = 0; index < manifest.Frames.Length; index++)
        {
            var frame = manifest.Frames[index];
            var entryName = TrialData.FrameName(index);
            var entry = GetEntry(zip, entryName);
            var size = TrialData.PixelBytes(frame);
            if (entry.Length != size)
                throw new InvalidDataException($"Incorrect BGRA byte count in {entryName}.");

            if (pixels.Length != size) pixels = new byte[size];
            await using var image = entry.Open();
            await image.ReadExactlyAsync(pixels).ConfigureAwait(false);
            var eyes = estimator.ObserveDetailed(pixels, frame.Width, frame.Height);
            var geometry = (frame.Width, frame.Height, eyes.RotationQuarterTurns);
            if (lastGeometry != geometry) detector.Reset();
            lastGeometry = geometry;
            var timestamp = TimeSpan.FromTicks(checked((long)Math.Round(frame.TimeMs * TimeSpan.TicksPerMillisecond)));
            var direction = detector.Observe(eyes.Left, eyes.Right, timestamp);
            current[index] = frame with { Eyes = eyes, Turn = direction };
            trace[index] = new FrameTrace(frame.TimeMs, frame.Eyes, frame.Turn, eyes, direction);
        }

        return new ReplayResult(
            TrialData.Evaluate(path, manifest, manifest.Frames),
            TrialData.Evaluate(path, manifest, current),
            trace);
    }

    private static Stream OpenEntry(ZipArchive zip, string name) => GetEntry(zip, name).Open();

    private static ZipArchiveEntry GetEntry(ZipArchive zip, string name) =>
        zip.GetEntry(name) ?? throw new InvalidDataException($"Missing {name}.");
}
