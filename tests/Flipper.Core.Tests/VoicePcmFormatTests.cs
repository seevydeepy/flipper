using Flipper.Core.Reader;

namespace Flipper.Core.Tests;

public sealed class VoicePcmFormatTests
{
    [Fact]
    public void Inspect_640BytesAt160Quantum_IsFloatMono()
    {
        var layout = VoicePcmFormat.Inspect(byteLength: 640, quantum: 160);
        Assert.True(layout.IeeeFloat);
        Assert.Equal(1, layout.Channels);
        Assert.Equal(160, layout.Frames);
    }

    [Fact]
    public void Inspect_320BytesAt160Quantum_IsInt16Mono()
    {
        var layout = VoicePcmFormat.Inspect(byteLength: 320, quantum: 160);
        Assert.False(layout.IeeeFloat);
        Assert.Equal(1, layout.Channels);
        Assert.Equal(160, layout.Frames);
    }

    [Fact]
    public void Inspect_1280BytesAt160Quantum_IsFloatStereo()
    {
        var layout = VoicePcmFormat.Inspect(byteLength: 1280, quantum: 160);
        Assert.True(layout.IeeeFloat);
        Assert.Equal(2, layout.Channels);
        Assert.Equal(160, layout.Frames);
    }
}
