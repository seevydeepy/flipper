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

    [Fact]
    public void Filter_Query_IsCaseInsensitive()
    {
        var result = ScoreSearch.Filter(Scores, query: "NOC", selectedFolder: null);
        Assert.Equal("Nocturne", Assert.Single(result).DisplayName);
    }

    [Fact]
    public void Filter_Query_IgnoresWhitespace()
    {
        var named = Scores[2] with { Title = "Nocturne in E flat" };
        var spaced = ScoreSearch.Filter([named], query: "e  flat", selectedFolder: null);
        Assert.Equal("Nocturne", Assert.Single(spaced).DisplayName);

        var glued = ScoreSearch.Filter([named], query: "eflat", selectedFolder: null);
        Assert.Equal("Nocturne", Assert.Single(glued).DisplayName);
    }

    [Fact]
    public void Filter_Query_IgnoresAccents()
    {
        var named = Scores[2] with { Title = "Nocturne in E flat", Composer = "Frédéric Chopin" };
        var fromPlain = ScoreSearch.Filter([named], query: "frederic", selectedFolder: null);
        Assert.Equal("Nocturne", Assert.Single(fromPlain).DisplayName);

        var etude = Scores[0] with { Title = "Etude" };
        var fromAccent = ScoreSearch.Filter([etude], query: "étude", selectedFolder: null);
        Assert.Equal("Air", Assert.Single(fromAccent).DisplayName);
    }

    [Fact]
    public void Filter_Query_MapsStrokeLetters()
    {
        var named = Scores[0] with { Title = "Funeral Music", Composer = "Witold Lutosławski" };
        var result = ScoreSearch.Filter([named], query: "lutoslawski", selectedFolder: null);
        Assert.Equal("Air", Assert.Single(result).DisplayName);
    }

    [Fact]
    public void Filter_Query_MapsOSlash()
    {
        var named = Scores[0] with { Title = "Søren" };
        var result = ScoreSearch.Filter([named], query: "soren", selectedFolder: null);
        Assert.Equal("Air", Assert.Single(result).DisplayName);
    }

    [Fact]
    public void Filter_Query_MapsCapitalSharpS()
    {
        var named = Scores[0] with { Title = "STRAẞE" };
        var result = ScoreSearch.Filter([named], query: "strasse", selectedFolder: null);
        Assert.Equal("Air", Assert.Single(result).DisplayName);
    }

    [Fact]
    public void Filter_Query_IgnoresJunkComposer()
    {
        var named = Scores[0] with { Composer = "Public Domain" };
        var result = ScoreSearch.Filter([named], query: "public", selectedFolder: null);
        Assert.Empty(result);
    }

    [Fact]
    public void Filter_Query_WithOnlyMarks_MatchesNothing()
    {
        var result = ScoreSearch.Filter(Scores, query: "\u0301", selectedFolder: "Bach");
        Assert.Empty(result);
    }

    [Fact]
    public void Filter_Query_MatchesTitle()
    {
        var named = Scores[0] with { Title = "Air on the G String" };
        var result = ScoreSearch.Filter([named], query: "g string", selectedFolder: null);
        Assert.Equal("Air", Assert.Single(result).DisplayName);
    }

    [Fact]
    public void Filter_Query_MatchesFileNameWhenTitleDiffers()
    {
        var named = new ScoreEntry(
            "Suite3",
            "Bach",
            @"C:\lib\Bach\Air.pdf",
            "suite3",
            1,
            DateTime.UtcNow,
            Title: "Orchestral Suite No. 3");
        var result = ScoreSearch.Filter([named], query: "air", selectedFolder: null);
        Assert.Equal("Suite3", Assert.Single(result).DisplayName);
    }

    [Fact]
    public void Filter_Query_MatchesTokensAcrossTitleAndComposer()
    {
        var named = Scores[2] with { Title = "Nocturne in E flat", Composer = "Frédéric Chopin" };
        var both = ScoreSearch.Filter([named, Scores[0]], query: "chopin nocturne", selectedFolder: null);
        Assert.Equal("Nocturne", Assert.Single(both).DisplayName);

        var missing = ScoreSearch.Filter([named], query: "chopin prelude", selectedFolder: null);
        Assert.Empty(missing);
    }

    [Fact]
    public void Filter_ExcludedCanonicalPaths_AreDropped()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Scores[0].CanonicalPath };
        var result = ScoreSearch.Filter(Scores, query: null, selectedFolder: null, excludedCanonicalPaths: excluded);
        Assert.DoesNotContain(result, score => score.CanonicalPath == Scores[0].CanonicalPath);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Filter_PlaylistPaths_KeepOnlySnapshotMatches()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Scores[0].CanonicalPath,
            @"C:\lib\Missing.pdf"
        };
        var result = ScoreSearch.Filter(Scores, query: null, selectedFolder: null, playlistCanonicalPaths: paths);
        var match = Assert.Single(result);
        Assert.Equal(Scores[0].CanonicalPath, match.CanonicalPath);
    }

    [Fact]
    public void Filter_Query_IgnoresPlaylistSet()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Scores[0].CanonicalPath };
        var result = ScoreSearch.Filter(Scores, query: "noc", selectedFolder: "Bach", playlistCanonicalPaths: paths);
        var match = Assert.Single(result);
        Assert.Equal("Nocturne", match.DisplayName);
    }
}
