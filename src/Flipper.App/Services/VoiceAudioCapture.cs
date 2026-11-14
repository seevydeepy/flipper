using System.Runtime.InteropServices;
using Flipper.Core.Reader;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using WinRT;

namespace Flipper.App.Services;

// The reader and calibration use the same PCM path and Windows speech processing.
internal sealed class VoiceAudioCapture : IDisposable
{
    private AudioGraph? _graph;
    private AudioFrameOutputNode? _frames;
    private Action<float[]>? _onSamples;
    private int _epoch;
    public string? DeviceId { get; private set; }
    public string? DeviceName { get; private set; }
    public float LastRms { get; private set; }
    public string? Failure { get; private set; }

    public async Task<string?> StartAsync(string? deviceId, Action<float[]> onSamples, bool requireSelectedDevice = false)
    {
        Stop();
        var epoch = _epoch;
        AudioGraph? graph = null;
        try
        {
            var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech)
            {
                EncodingProperties = AudioEncodingProperties.CreatePcm(VoiceCalibration.SampleRate, 1, 16),
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired,
                DesiredSamplesPerQuantum = 320
            });
            graph = created.Graph;
            if (epoch != _epoch) return "Voice off";
            if (created.Status != AudioGraphCreationStatus.Success || graph is null)
                return "Voice off — cannot start audio";

            DeviceInformation? device = null;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try { device = await DeviceInformation.CreateFromIdAsync(deviceId); }
                catch when (!requireSelectedDevice) { }
                if (requireSelectedDevice && device is null) return "Cannot open the selected microphone.";
            }
            if (device is null)
            {
                var defaultId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
                if (string.IsNullOrWhiteSpace(defaultId)) return "Voice off — cannot open microphone";
                device = await DeviceInformation.CreateFromIdAsync(defaultId);
            }
            if (epoch != _epoch) return "Voice off";
            var input = await graph.CreateDeviceInputNodeAsync(MediaCategory.Speech, graph.EncodingProperties, device);
            if (epoch != _epoch) return "Voice off";
            if (input.Status != AudioDeviceNodeCreationStatus.Success || input.DeviceInputNode is null)
                return "Voice off — cannot open microphone";

            DeviceId = device.Id;
            DeviceName = device.Name;
            _frames = graph.CreateFrameOutputNode(graph.EncodingProperties);
            input.DeviceInputNode.AddOutgoingConnection(_frames);
            _onSamples = onSamples;
            _graph = graph;
            graph.QuantumStarted += OnQuantumStarted;
            graph.UnrecoverableErrorOccurred += OnError;
            graph.Start();
            graph = null;
            return null;
        }
        catch (Exception ex)
        {
            if (epoch == _epoch && _graph == graph) { _graph = null; _frames = null; _onSamples = null; }
            return "Voice off — " + ex.GetBaseException().Message;
        }
        finally { graph?.Dispose(); }
    }

    private void OnError(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
    {
        if (sender == _graph) Failure = "Microphone disconnected. Reopen it and try again.";
    }

    public void Stop()
    {
        _epoch++;
        var graph = _graph;
        _graph = null;
        _onSamples = null;
        if (graph is not null)
        {
            graph.QuantumStarted -= OnQuantumStarted;
            graph.UnrecoverableErrorOccurred -= OnError;
            try { graph.Stop(); } catch { }
            graph.Dispose();
        }
        _frames = null;
        DeviceId = DeviceName = Failure = null;
        LastRms = 0;
    }

    private void OnQuantumStarted(AudioGraph sender, object args)
    {
        try
        {
            if (sender != _graph || _frames is not { } node) return;
            using var frame = node.GetFrame();
            using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
            if (buffer.Length < 2) return;
            using var reference = buffer.CreateReference();
            float[] samples;
            unsafe
            {
                reference.As<IMemoryBufferByteAccess>().GetBuffer(out var data, out var capacity);
                var layout = VoicePcmFormat.Inspect((int)Math.Min(buffer.Length, capacity), Math.Max(1, sender.SamplesPerQuantum));
                samples = new float[layout.Frames];
                for (var i = 0; i < samples.Length; i++)
                {
                    var offset = i * layout.Channels * (layout.IeeeFloat ? 4 : 2);
                    samples[i] = layout.IeeeFloat
                        ? BitConverter.ToSingle(new ReadOnlySpan<byte>(data + offset, 4))
                        : (short)(data[offset] | data[offset + 1] << 8) / 32768f;
                }
            }
            if (samples.Length == 0) return;
            LastRms = MathF.Sqrt(samples.Sum(value => value * value) / samples.Length);
            _onSamples?.Invoke(samples);
        }
        catch (Exception)
        {
            if (sender == _graph) Failure = "Cannot read the microphone. Reopen it and try again.";
        }
    }

    public void Dispose() => Stop();
}

[ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
