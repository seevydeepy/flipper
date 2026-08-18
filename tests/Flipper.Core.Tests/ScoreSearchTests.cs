using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class ScoreSearchTests
{
    private static readonly ScoreEntry[] Scores =
    [
        new("Air", "Bach", @"C:\lib\Bach\Air.pdf", @"C:\lib\Bach\Air.pdf", 1, DateTime.UtcNow),
        new("Prelude", "Bach", @"C:\lib\Bach\Prelude.pdf", @"C:\lib\Bach\Prelude.pdf", 1, DateTime.UtcNow),
        new("Nocturne", "Chopin", @"C:\lib\Chopin\Nocturne.pdf", @"C:\lib\Chopin\Nocturne.pdf", 1, DateTime.UtcNow)
    ];

    [Fact]
    public void Filter_WithoutQuery_UsesSelectedFolder()
    {
        var result = ScoreSearch.Filter(Scores, query: null, selectedFolder: "Bach");
        Assert.Equal(2, result.Count);
        Assert.All(result, score => Assert.Equal("Bach", score.RelativeFolder));
    }

    [Fact]
    public void Filter_ParentFolder_IncludesNestedScores()
    {
        var nested = new ScoreEntry("Suite", @"Bach\Cello", @"C:\lib\Bach\Cello\Suite.pdf", "suite", 1, DateTime.UtcNow);
        var result = ScoreSearch.Filter([..Scores, nested], query: null, selectedFolder: "Bach");
        Assert.Equal(3, result.Count);
        Assert.Contains(result, score => score.DisplayName == "Suite");
    }

    [Fact]
    public void Filter_WithQuery_MatchesFileNameAndIgnoresFolder()
    {
        var result = ScoreSearch.Filter(Scores, query: "noc", selectedFolder: "Bach");
        var match = Assert.Single(result);
        Assert.Equal("Nocturne", match.DisplayName);
        Assert.Equal("Chopin", match.RelativeFolder);
    }

    [Fact]
    public void Filter_Query_MatchesComposer()
    {
        var named = Scores[2] with { Title = "Nocturne in E flat", Composer = "Frédéric Chopin" };
        var result = ScoreSearch.Filter([named], query: "chopin", selectedFolder: null);
        Assert.Equal("Nocturne", Assert.Single(result).DisplayName);
    }
}
