using System.Collections.Concurrent;
using Flipper.Core.Reader;

namespace Flipper.App.Services;

public sealed class VoiceKeywordListener : IDisposable
{
    private readonly VoiceAudioCapture _capture = new();
    private CancellationTokenSource? _cts;
    private int _epoch;
    private bool _disposed;
    public float LastRms => _capture.LastRms;

    public async Task<string?> StartAsync(string? deviceId, Action<string> onKeyword, VoiceCalibrationProfile? profile = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        var epoch = _epoch;
        var pending = new ConcurrentQueue<float[]>();
        var failure = await _capture.StartAsync(deviceId, samples =>
        {
            if (pending.Count >= 50) pending.TryDequeue(out _);
            pending.Enqueue(samples);
        });
        if (epoch != _epoch) return "Voice off";
        if (failure is not null) return failure;
        var calibration = profile?.MatchesDevice(_capture.DeviceId) == true ? profile : null;
        VoiceRecognizer recognizer;
        try { recognizer = await Task.Run(() => new VoiceRecognizer(calibration?.PresetIndex ?? 2)); }
        catch (Exception ex)
        {
            if (epoch == _epoch) _capture.Stop();
            return "Voice off — " + ex.GetBaseException().Message;
        }
        if (epoch != _epoch) { recognizer.Dispose(); return "Voice off"; }
        while (pending.TryDequeue(out _)) { }
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using (recognizer)
            using (var stream = recognizer.CreateStream())
            using (cts)
            {
                var nextAllowed = DateTime.MinValue;
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        while (!cts.IsCancellationRequested && pending.TryDequeue(out var samples))
                        {
                            VoiceCalibration.ApplyGain(samples, calibration?.Gain ?? 1);
                            recognizer.Accept(stream, samples, keyword =>
                            {
                                var now = DateTime.UtcNow;
                                if (cts.IsCancellationRequested || epoch != _epoch || now < nextAllowed) return;
                                nextAllowed = now.AddMilliseconds(900);
                                onKeyword(keyword);
                            });
                        }
                        await Task.Delay(20, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
            }
        });
        return null;
    }

    public void Stop()
    {
        _epoch++;
        _capture.Stop();
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts = null;
        // Each worker owns its native objects until decoding has stopped.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
