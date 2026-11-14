using System.Text;
using Flipper.Core.Reader;
using SherpaOnnx;

namespace Flipper.App.Services;

// A complete keyword buffer is needed: CreateStream(keywords) also adds the defaults.
internal sealed class VoiceRecognizer : IDisposable
{
    private readonly KeywordSpotter _spotter;
    public const int CooldownSamples = VoiceCalibration.SampleRate * 900 / 1000;

    public VoiceRecognizer(int presetIndex = 2)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Voice");
        foreach (var name in new[] { "encoder.int8.onnx", "decoder.onnx", "joiner.int8.onnx", "tokens.txt", "keywords.txt" })
            if (!File.Exists(Path.Combine(dir, name))) throw new FileNotFoundException("Missing voice files.");
        var keywords = VoiceCalibration.ApplyPreset(File.ReadAllText(Path.Combine(dir, "keywords.txt")), presetIndex);
        _spotter = new KeywordSpotter(new KeywordSpotterConfig
        {
            FeatConfig = { SampleRate = VoiceCalibration.SampleRate, FeatureDim = 80 },
            ModelConfig =
            {
                Tokens = Path.Combine(dir, "tokens.txt"), Provider = "cpu", NumThreads = 1, Debug = 0,
                Transducer =
                {
                    Encoder = Path.Combine(dir, "encoder.int8.onnx"),
                    Decoder = Path.Combine(dir, "decoder.onnx"), Joiner = Path.Combine(dir, "joiner.int8.onnx")
                }
            },
            MaxActivePaths = 4, KeywordsBuf = keywords, KeywordsBufSize = Encoding.UTF8.GetByteCount(keywords),
            KeywordsScore = 2, KeywordsThreshold = .16f
        });
    }

    public SherpaOnnx.OnlineStream CreateStream() => _spotter.CreateStream();

    public void Accept(SherpaOnnx.OnlineStream stream, float[] samples, Action<string> detected)
    {
        stream.AcceptWaveform(VoiceCalibration.SampleRate, samples);
        while (_spotter.IsReady(stream))
        {
            _spotter.Decode(stream);
            var keyword = _spotter.GetResult(stream).Keyword;
            if (string.IsNullOrWhiteSpace(keyword)) continue;
            _spotter.Reset(stream);
            detected(keyword);
        }
    }

    public IReadOnlyList<string> Replay(float[] recording, float gain, CancellationToken token)
    {
        using var stream = CreateStream();
        var heard = new List<string>();
        var nextAllowed = 0;
        // Match live chunking and cooldown. Tail silence lets the final word complete.
        for (var offset = 0; offset < recording.Length + VoiceCalibration.SampleRate; offset += 320)
        {
            token.ThrowIfCancellationRequested();
            var chunk = new float[320];
            if (offset < recording.Length)
                Array.Copy(recording, offset, chunk, 0, Math.Min(chunk.Length, recording.Length - offset));
            VoiceCalibration.ApplyGain(chunk, gain);
            Accept(stream, chunk, word =>
            {
                if (offset < nextAllowed) return;
                heard.Add(word);
                nextAllowed = offset + CooldownSamples;
            });
        }
        return heard;
    }

    public void Dispose() => _spotter.Dispose();
}
