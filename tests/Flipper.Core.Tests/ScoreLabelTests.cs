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

    [Fact]
    public void Card_ParentheticalPiece_UsesWorkTitleFromFileName()
    {
        var card = ScoreLabel.Card(
            "(Main Theme)",
            subtitle: null,
            "John Williams",
            "Schindlers List - Main Theme Piano Version");

        Assert.Equal("Schindlers List", card.Title);
        Assert.Equal("Main Theme", card.Subtitle);
        Assert.Equal("John Williams", card.Composer);
    }

    [Fact]
    public void Card_UsesCatalogTitleSubtitleAndComposer()
    {
        var card = ScoreLabel.Card(
            "Schindler's List",
            "Main Theme",
            "John Williams",
            "Schindlers List Main Theme Piano Version");

        Assert.Equal("Schindler's List", card.Title);
        Assert.Equal("Main Theme", card.Subtitle);
        Assert.Equal("John Williams", card.Composer);
    }

    [Fact]
    public void Card_SplitsTrailingDescriptorParen()
    {
        var card = ScoreLabel.Card(
            "Schindler's List (Main Theme)",
            subtitle: null,
            "John Williams",
            "Schindlers List - Main Theme Piano Version");

        Assert.Equal("Schindler's List", card.Title);
        Assert.Equal("Main Theme", card.Subtitle);
        Assert.Equal("John Williams", card.Composer);
    }

    [Fact]
    public void Card_UnwrapsParentheticalWhenItIsTheWorkTitle()
    {
        var card = ScoreLabel.Card(
            "(Night on Bald Mountain)",
            subtitle: null,
            "Modest Mussorgsky",
            "Nobm");

        Assert.Equal("Night on Bald Mountain", card.Title);
        Assert.Equal(string.Empty, card.Subtitle);
        Assert.Equal("Modest Mussorgsky", card.Composer);
    }

    [Fact]
    public void Card_KeepsEmbeddedParentheticalInTitle()
    {
        var card = ScoreLabel.Card(
            "(Somewhere) Over the Rainbow",
            subtitle: null,
            "Harold Arlen",
            "Somewhere Over the Rainbow");

        Assert.Equal("(Somewhere) Over the Rainbow", card.Title);
        Assert.Equal(string.Empty, card.Subtitle);
        Assert.Equal("Harold Arlen", card.Composer);
    }

    [Fact]
    public void Card_DropsComposerThatRepeatsTheTitle()
    {
        var card = ScoreLabel.Card(
            "(Main Theme)",
            subtitle: null,
            "Schindler's List - Main Theme (Piano Version)",
            "Schindlers List Main Theme Piano Version");

        Assert.Equal("Schindlers List", card.Title);
        Assert.Equal("Main Theme", card.Subtitle);
        Assert.Equal(string.Empty, card.Composer);
    }

    [Fact]
    public void Entry_ParentheticalTitle_ExposesWorkAndSubtitle()
    {
        var entry = new ScoreEntry(
            "Schindlers List - Main Theme Piano Version",
            "K's Collection",
            @"C:\lib\Schindlers List - Main Theme Piano Version.pdf",
            "schindler",
            1,
            DateTime.UnixEpoch,
            Title: "(Main Theme)",
            Composer: "John Williams");

        Assert.Equal("Schindlers List", entry.CardTitle);
        Assert.Equal("Main Theme", entry.CardSubtitle);
        Assert.Equal("John Williams", entry.CardComposer);
    }
}
