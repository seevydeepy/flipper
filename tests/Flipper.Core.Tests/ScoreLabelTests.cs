using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class ScoreLabelTests
{
    [Theory]
    [InlineData("Finale 2005b - [Ave Maria.MUS]")]
    [InlineData("Public Domain")]
    [InlineData("Creative Commons Attribution-ShareAlike 3.0")]
    [InlineData("Copyright © 2007.")]
    [InlineData("• Free to download, with the freedom to distribute, modify and perform.")]
    public void Title_Junk_FallsBackToFileName(string junk)
    {
        Assert.Equal("Invention 4", ScoreLabel.Title(junk, "Invention 4"));
    }

    [Fact]
    public void Title_KeepsRealName()
    {
        Assert.Equal("Clair de Lune", ScoreLabel.Title("Clair de Lune", "file"));
    }
}
