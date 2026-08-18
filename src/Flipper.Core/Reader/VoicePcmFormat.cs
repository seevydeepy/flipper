namespace Flipper.Core.Reader;

public readonly record struct VoicePcmLayout(bool IeeeFloat, int Channels, int Frames);

public static class VoicePcmFormat
{
    public static VoicePcmLayout Inspect(int byteLength, int quantum)
    {
        var frames = quantum > 0 ? quantum : 160;
        if (byteLength >= 4 && byteLength % 4 == 0)
        {
            var floatSamples = byteLength / 4;
            if (floatSamples % frames == 0)
            {
                var channels = floatSamples / frames;
                if (channels is >= 1 and <= 8)
                {
                    return new VoicePcmLayout(true, channels, frames);
                }
            }
        }

        if (byteLength >= 2 && byteLength % 2 == 0)
        {
            var intSamples = byteLength / 2;
            if (intSamples % frames == 0)
            {
                var channels = intSamples / frames;
                if (channels is >= 1 and <= 8)
                {
                    return new VoicePcmLayout(false, channels, frames);
                }
            }
        }

        return new VoicePcmLayout(false, 1, Math.Max(1, byteLength / 2));
    }
}
