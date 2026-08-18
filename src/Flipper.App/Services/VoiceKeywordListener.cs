using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Flipper.Core.Reader;
using SherpaOnnx;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using WinRT;

namespace Flipper.App.Services;

public sealed class VoiceKeywordListener : IDisposable
{
    private const int SampleRate = 16000;
    private const int CooldownMs = 900;

    private static readonly object SpotterGate = new();
    private static KeywordSpotter? SharedSpotter;

    private readonly ConcurrentQueue<float[]> _pending = new();
    private readonly AutoResetEvent _signal = new(false);
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _input;
    private AudioFrameOutputNode? _frames;
    private OnlineStream? _stream;
    private Action<string>? _onKeyword;
    private DateTime _nextAllowedUtc = DateTime.MinValue;
    private int _quantum = 160;
    private bool _disposed;

    public float LastRms { get; private set; }

    public async Task<string?> StartAsync(string? deviceId, Action<string> onKeyword)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        KeywordSpotter? spotter;
        try
        {
            spotter = await Task.Run(TryCreateSpotter).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            return "Voice off — " + ShortError(ex);
        }

        if (spotter is null)
        {
            return "Voice off — missing voice files";
        }

        _onKeyword = onKeyword;
        _stream = spotter.CreateStream();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _worker = Task.Run(() => DecodeLoop(spotter, token), token);

        try
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Speech)
            {
                EncodingProperties = AudioEncodingProperties.CreatePcm(SampleRate, 1, 16),
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired,
                DesiredSamplesPerQuantum = 320
            };

            var created = await AudioGraph.CreateAsync(settings);
            if (created.Status != AudioGraphCreationStatus.Success || created.Graph is null)
            {
                Stop();
                return "Voice off — cannot start audio";
            }

            _graph = created.Graph;
            _quantum = Math.Max(1, _graph.SamplesPerQuantum);
            DeviceInformation? device = null;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    device = await DeviceInformation.CreateFromIdAsync(deviceId);
                }
                catch (Exception)
                {
                    device = null;
                }
            }

            var input = device is null
                ? await _graph.CreateDeviceInputNodeAsync(MediaCategory.Speech, _graph.EncodingProperties)
                : await _graph.CreateDeviceInputNodeAsync(MediaCategory.Speech, _graph.EncodingProperties, device);
            if (input.Status != AudioDeviceNodeCreationStatus.Success || input.DeviceInputNode is null)
            {
                Stop();
                return "Voice off — cannot open microphone";
            }

            _input = input.DeviceInputNode;
            _frames = _graph.CreateFrameOutputNode(_graph.EncodingProperties);
            _input.AddOutgoingConnection(_frames);
            _graph.QuantumStarted += OnQuantumStarted;
            _graph.Start();
            return null;
        }
        catch (Exception ex)
        {
            Stop();
            return "Voice off — " + ShortError(ex);
        }
    }

    public void Stop()
    {
        var graph = _graph;
        if (graph is not null)
        {
            try
            {
                graph.QuantumStarted -= OnQuantumStarted;
                graph.Stop();
            }
            catch (Exception)
            {
            }
        }

        try
        {
            _cts?.Cancel();
        }
        catch (Exception)
        {
        }

        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
        }

        _worker = null;
        _cts?.Dispose();
        _cts = null;
        _signal.Set();

        _input?.Dispose();
        _input = null;
        _frames?.Dispose();
        _frames = null;
        graph?.Dispose();
        _graph = null;
        _stream?.Dispose();
        _stream = null;
        _onKeyword = null;
        while (_pending.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _signal.Dispose();
    }

    private void OnQuantumStarted(AudioGraph sender, object args)
    {
        try
        {
            var node = _frames;
            if (node is null)
            {
                return;
            }

            var frame = node.GetFrame();
            var samples = ToFloatMono(frame, _quantum);
            if (samples.Length == 0)
            {
                return;
            }

            var energy = 0f;
            for (var i = 0; i < samples.Length; i++)
            {
                energy += samples[i] * samples[i];
            }

            LastRms = MathF.Sqrt(energy / samples.Length);

            if (_pending.Count > 50)
            {
                _pending.TryDequeue(out _);
            }

            _pending.Enqueue(samples);
            _signal.Set();
        }
        catch (Exception)
        {
        }
    }

    private void DecodeLoop(KeywordSpotter spotter, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                _signal.WaitOne(80);
                var stream = _stream;
                if (stream is null)
                {
                    continue;
                }

                while (_pending.TryDequeue(out var samples))
                {
                    stream.AcceptWaveform(SampleRate, samples);
                }

                while (spotter.IsReady(stream))
                {
                    spotter.Decode(stream);
                    var keyword = spotter.GetResult(stream).Keyword;
                    if (string.IsNullOrWhiteSpace(keyword))
                    {
                        continue;
                    }

                    spotter.Reset(stream);
                    var now = DateTime.UtcNow;
                    if (now < _nextAllowedUtc)
                    {
                        continue;
                    }

                    _nextAllowedUtc = now.AddMilliseconds(CooldownMs);
                    _onKeyword?.Invoke(keyword);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private static KeywordSpotter? TryCreateSpotter()
    {
        lock (SpotterGate)
        {
            if (SharedSpotter is not null)
            {
                return SharedSpotter;
            }

            var dir = Path.Combine(AppContext.BaseDirectory, "Voice");
            var encoder = Path.Combine(dir, "encoder.int8.onnx");
            var decoder = Path.Combine(dir, "decoder.onnx");
            var joiner = Path.Combine(dir, "joiner.int8.onnx");
            var tokens = Path.Combine(dir, "tokens.txt");
            var keywords = Path.Combine(dir, "keywords.txt");
            if (!File.Exists(encoder) || !File.Exists(decoder) || !File.Exists(joiner)
                || !File.Exists(tokens) || !File.Exists(keywords))
            {
                return null;
            }

            var config = new KeywordSpotterConfig
            {
                FeatConfig =
                {
                    SampleRate = SampleRate,
                    FeatureDim = 80
                },
                ModelConfig =
                {
                    Tokens = tokens,
                    Provider = "cpu",
                    NumThreads = 1,
                    Debug = 0,
                    Transducer =
                    {
                        Encoder = encoder,
                        Decoder = decoder,
                        Joiner = joiner
                    }
                },
                MaxActivePaths = 6,
                KeywordsFile = keywords,
                KeywordsScore = 2.2f,
                KeywordsThreshold = 0.13f
            };

            SharedSpotter = new KeywordSpotter(config);
            return SharedSpotter;
        }
    }

    private static float[] ToFloatMono(AudioFrame frame, int quantum)
    {
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        if (buffer.Length < 2)
        {
            return [];
        }

        using var reference = buffer.CreateReference();
        unsafe
        {
            reference.As<IMemoryBufferByteAccess>().GetBuffer(out var data, out var capacity);
            var bytes = (int)Math.Min(buffer.Length, capacity);
            var layout = VoicePcmFormat.Inspect(bytes, quantum);
            var samples = new float[layout.Frames];
            if (layout.IeeeFloat)
            {
                for (var i = 0; i < layout.Frames; i++)
                {
                    samples[i] = BitConverter.ToSingle(new ReadOnlySpan<byte>(data + (i * layout.Channels * 4), 4));
                }
            }
            else
            {
                for (var i = 0; i < layout.Frames; i++)
                {
                    var offset = i * layout.Channels * 2;
                    var value = (short)(data[offset] | (data[offset + 1] << 8));
                    samples[i] = value / 32768f;
                }
            }

            return samples;
        }
    }

    private static string ShortError(Exception ex)
    {
        var text = ex.GetBaseException().Message;
        if (string.IsNullOrWhiteSpace(text))
        {
            return ex.GetType().Name;
        }

        return text.Length <= 80 ? text : text[..80];
    }
}

[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
