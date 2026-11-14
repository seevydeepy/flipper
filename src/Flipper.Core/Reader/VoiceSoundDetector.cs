namespace Flipper.Core.Reader;

internal sealed class VoiceSoundDetector(float triggerRms)
{
    internal const int FrameSamples = VoiceSoundCalibration.SampleRate / 50;
    internal const int SilenceFrames = 15;
    private const int PreRollFrames = 2;
    private const int MinimumActiveFrames = 6;
    private const int MaximumActiveFrames = 150;
    private readonly float[] _frame = new float[FrameSamples];
    private readonly float[] _event = new float[(PreRollFrames + MaximumActiveFrames + SilenceFrames) * FrameSamples];
    private int _frameLength;
    private int _eventFrames;
    private int _lastActiveFrame;
    private int _activeFrames;
    private int _silenceFrames;
    private bool _firstOnsetFrame;
    private bool _discarding;
    private long _sampleClock;

    internal void Accept(ReadOnlySpan<float> samples, Action<VoiceSoundTemplate?, long> onEvent)
    {
        while (!samples.IsEmpty)
        {
            var copied = Math.Min(_frame.Length - _frameLength, samples.Length);
            samples[..copied].CopyTo(_frame.AsSpan(_frameLength));
            _frameLength += copied;
            samples = samples[copied..];
            if (_frameLength == _frame.Length)
            {
                _sampleClock += _frame.Length;
                _frameLength = 0;
                ProcessFrame(onEvent);
            }
        }
    }

    internal void Reset()
    {
        _frameLength = 0;
        _sampleClock = 0;
        _discarding = false;
        ClearEvent();
    }

    private void ProcessFrame(Action<VoiceSoundTemplate?, long> onEvent)
    {
        var level = Rms(_frame);
        var aboveOffset = level >= triggerRms * 0.70f;
        if (!float.IsFinite(level))
        {
            if (_eventFrames != 0)
            {
                onEvent(null, _sampleClock);
            }
            ClearEvent();
            _discarding = true;
            return;
        }

        if (_discarding)
        {
            _silenceFrames = aboveOffset ? 0 : _silenceFrames + 1;
            if (_silenceFrames == SilenceFrames)
            {
                _discarding = false;
                ClearEvent();
            }
            return;
        }

        if (_eventFrames == 0)
        {
            var aboveOnset = level >= triggerRms;
            if (aboveOnset && _firstOnsetFrame)
            {
                _eventFrames = PreRollFrames + 1;
                _lastActiveFrame = _eventFrames;
                _activeFrames = 2;
                _frame.CopyTo(_event, PreRollFrames * FrameSamples);
            }
            else
            {
                _event.AsSpan(FrameSamples, FrameSamples).CopyTo(_event);
                _frame.CopyTo(_event, FrameSamples);
                _firstOnsetFrame = aboveOnset;
            }
            return;
        }

        _frame.CopyTo(_event, _eventFrames * FrameSamples);
        _eventFrames++;
        if (aboveOffset)
        {
            _activeFrames++;
            _lastActiveFrame = _eventFrames;
            _silenceFrames = 0;
            if (_eventFrames - PreRollFrames + 1 > MaximumActiveFrames)
            {
                ClearEvent();
                _discarding = true;
                onEvent(null, _sampleClock);
            }
        }
        else if (++_silenceFrames == SilenceFrames)
        {
            var audio = _event.AsSpan(0, (_lastActiveFrame + 1) * FrameSamples);
            var enoughAudio = _activeFrames >= MinimumActiveFrames && HasUsableSamples(audio);
            var template = enoughAudio ? VoiceSoundFeatures.Extract(audio) : null;
            ClearEvent();
            onEvent(template, _sampleClock);
        }
    }

    private void ClearEvent()
    {
        _eventFrames = _lastActiveFrame = _activeFrames = _silenceFrames = 0;
        _firstOnsetFrame = false;
        _event.AsSpan(0, PreRollFrames * FrameSamples).Clear();
    }

    internal static float Rms(ReadOnlySpan<float> frame)
    {
        var sum = 0d;
        var energy = 0d;
        foreach (var sample in frame)
        {
            sum += sample;
            energy += (double)sample * sample;
        }
        var mean = sum / frame.Length;
        return (float)Math.Sqrt(Math.Max(0, energy / frame.Length - mean * mean));
    }

    internal static bool HasUsableSamples(ReadOnlySpan<float> audio)
    {
        const float clippingLevel = 32767f / 32768;
        var clipped = 0;
        foreach (var sample in audio)
        {
            if (!float.IsFinite(sample) || Math.Abs(sample) > 1)
            {
                return false;
            }
            if (Math.Abs(sample) >= clippingLevel)
            {
                clipped++;
            }
        }
        return clipped * 100L < audio.Length;
    }
}
