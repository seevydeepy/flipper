using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class SessionScoreFactsOverlayTests
{
    [Fact]
    public void Apply_UsesGeneratedFactsForMatchingUncataloguedFile()
    {
        var entry = Entry(@"C:\Scores\rubbish123.pdf", 10, DateTime.UnixEpoch);
        var overlay = new SessionScoreFactsOverlay();
        overlay.SetRoot(@"C:\Scores");
        overlay.Add(entry, new ScoreFacts
        {
            Title = "Clair de Lune",
            Composer = "Claude Debussy",
            Subtitle = "Suite bergamasque"
        });

        var applied = overlay.Apply(new LibrarySnapshot(@"C:\Scores", [entry], true));
        var score = Assert.Single(applied.Scores);

        Assert.Equal("Clair de Lune", score.Title);
        Assert.Equal("Claude Debussy", score.Composer);
        Assert.Equal("Suite bergamasque", score.Subtitle);
        Assert.Equal(1, overlay.Count);
    }

    [Fact]
    public void Apply_RemovesOverlayWhenCatalogSupersedesIt()
    {
        var entry = Entry(@"C:\Scores\rubbish123.pdf", 10, DateTime.UnixEpoch);
        var overlay = new SessionScoreFactsOverlay();
        overlay.SetRoot(@"C:\Scores");
        overlay.Add(entry, new ScoreFacts { Title = "Automatic" });
        var catalogued = entry with { Title = "Curated", HasCatalogEntry = true };

        var applied = overlay.Apply(new LibrarySnapshot(@"C:\Scores", [catalogued], true));

        Assert.Equal("Curated", Assert.Single(applied.Scores).Title);
        Assert.Equal(0, overlay.Count);
    }

    [Fact]
    public void Apply_RemovesOverlayWhenFileVersionChanges()
    {
        var entry = Entry(@"C:\Scores\rubbish123.pdf", 10, DateTime.UnixEpoch);
        var overlay = new SessionScoreFactsOverlay();
        overlay.SetRoot(@"C:\Scores");
        overlay.Add(entry, new ScoreFacts { Title = "Automatic" });
        var changed = entry with { Length = 11 };

        var applied = overlay.Apply(new LibrarySnapshot(@"C:\Scores", [changed], true));

        Assert.Null(Assert.Single(applied.Scores).Title);
        Assert.Equal(0, overlay.Count);
    }

    [Fact]
    public void SetRoot_ClearsFactsFromPreviousRoot()
    {
        var entry = Entry(@"C:\Scores\rubbish123.pdf", 10, DateTime.UnixEpoch);
        var overlay = new SessionScoreFactsOverlay();
        overlay.SetRoot(@"C:\Scores");
        overlay.Add(entry, new ScoreFacts { Title = "Automatic" });

        overlay.SetRoot(@"D:\Scores");

        Assert.Equal(0, overlay.Count);
    }

    [Fact]
    public void Apply_RemovesOverlayForDeletedFile()
    {
        var entry = Entry(@"C:\Scores\rubbish123.pdf", 10, DateTime.UnixEpoch);
        var overlay = new SessionScoreFactsOverlay();
        overlay.SetRoot(@"C:\Scores");
        overlay.Add(entry, new ScoreFacts { Title = "Automatic" });

        overlay.Apply(new LibrarySnapshot(@"C:\Scores", [], true));

        Assert.Equal(0, overlay.Count);
    }

    [Fact]
    public void Remove_DiscardsRejectedGeneratedFacts()
    {
        var entry = Entry(@"C:\Scores\rubbish123.pdf", 10, DateTime.UnixEpoch);
        var overlay = new SessionScoreFactsOverlay();
        overlay.SetRoot(@"C:\Scores");
        overlay.Add(entry, new ScoreFacts { Title = "Automatic" });

        overlay.Remove(entry);

        Assert.Equal(0, overlay.Count);
    }

    private static ScoreEntry Entry(string path, long length, DateTime lastWrite, bool hasCatalogEntry = false)
    {
        return new ScoreEntry(
            Path.GetFileNameWithoutExtension(path),
            string.Empty,
            path,
            path,
            length,
            lastWrite,
            HasCatalogEntry: hasCatalogEntry);
    }
}
