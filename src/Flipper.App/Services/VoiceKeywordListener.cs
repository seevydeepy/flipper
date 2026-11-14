using Flipper.Core.Reader;

namespace Flipper.App.Services;

public sealed class VoiceKeywordListener : IDisposable
{
    private readonly VoiceAudioCapture _capture = new();
    private CancellationTokenSource? _cts;
    private int _epoch;
    private bool _disposed;
    public float LastRms => _capture.LastRms;

    public async Task<string?> StartAsync(string? deviceId, Action<VoiceSoundMatch> onCommand,
        IReadOnlyList<VoiceSoundProfile> profiles)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        var epoch = _epoch;
        var saved = profiles.Where(profile => profile.IsValid
            && profile.VerificationTemplates.Count == VoiceSoundProfile.VerificationRecordingCount).ToArray();
        if (saved.Length == 0) return "Voice off — add and calibrate a command in Settings";
        var pending = new Queue<float[]>();
        var queueLock = new object();
        var gap = false;
        var failure = await _capture.StartAsync(deviceId, samples =>
        {
            lock (queueLock)
            {
                if (pending.Count >= 50)
                {
                    pending.Clear();
                    gap = true;
                }
                pending.Enqueue(samples);
            }
        }, requireSelectedDevice: true);
        if (epoch != _epoch) return "Voice off";
        if (failure is not null) return failure;
        var active = saved.Where(profile => profile.MatchesDevice(_capture.DeviceId)).ToArray();
        if (active.Length == 0)
        {
            _capture.Stop();
            return "Voice off — calibrate a command for this microphone";
        }
        var recognizer = new VoiceSoundRecognizer(active);
        lock (queueLock) { pending.Clear(); gap = false; }
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using (cts)
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        float[][] batch;
                        lock (queueLock)
                        {
                            if (gap) { recognizer.Reset(); gap = false; }
                            batch = pending.ToArray();
                            pending.Clear();
                        }
                        foreach (var samples in batch)
                        {
                            if (cts.IsCancellationRequested || epoch != _epoch) break;
                            foreach (var match in recognizer.Accept(samples))
                            {
                                if (!cts.IsCancellationRequested && epoch == _epoch) onCommand(match);
                            }
                        }
                        await Task.Delay(20, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
