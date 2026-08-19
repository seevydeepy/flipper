using Flipper.Core.Update;

namespace Flipper.Core.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3+build", 1, 2, 3)]
    [InlineData("1.2.3-beta", 1, 2, 3)]
    public void TryParse_AcceptsTagAndInformationalForms(string text, int major, int minor, int patch)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("v")]
    public void TryParse_RejectsGarbage(string text)
    {
        Assert.False(AppVersion.TryParse(text, out _));
    }

    [Fact]
    public void IsNewer_IsFalseForEqualAndOlder()
    {
        var current = new Version(1, 0, 1);
        Assert.False(AppVersion.IsNewer(current, new Version(1, 0, 1)));
        Assert.False(AppVersion.IsNewer(current, new Version(1, 0, 0)));
        Assert.True(AppVersion.IsNewer(current, new Version(1, 0, 2)));
    }

    [Fact]
    public void NextPatch_IncrementsPatchOnly()
    {
        Assert.Equal(new Version(1, 0, 1), AppVersion.NextPatch(new Version(1, 0, 0)));
    }

    [Fact]
    public void FromTags_Empty_IsFirstRelease()
    {
        Assert.Equal(new Version(1, 0, 0), AppVersion.FromTags(Array.Empty<string>()));
    }

    [Fact]
    public void FromTags_PicksHighestParsedTag()
    {
        Assert.Equal(new Version(1, 0, 2), AppVersion.FromTags(new[] { "v1.0.0", "v1.0.2" }));
    }
}
