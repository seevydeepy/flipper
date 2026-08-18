using Flipper.Core.Cache;

namespace Flipper.Core.Tests;

public sealed class ScoreCacheTests
{
    [Fact]
    public void TryOpen_CopiesLiveFile_ThenReopensFromCache()
    {
        using var live = new TempDir();
        using var cacheDir = new TempDir();
        var liveFile = Path.Combine(live.Path, "score.pdf");
        File.WriteAllText(liveFile, "pdf-bytes");
        var cache = new ScoreCache(cacheDir.Path);

        var first = cache.TryOpen(@"\\share\score.pdf", liveFile, liveFile, currentlyOpenCanonical: null);
        Assert.NotNull(first);
        Assert.Equal("pdf-bytes", File.ReadAllText(first!));

        File.Delete(liveFile);
        var second = cache.TryOpen(@"\\share\score.pdf", liveFile, liveFile, currentlyOpenCanonical: null);
        Assert.NotNull(second);
        Assert.Equal("pdf-bytes", File.ReadAllText(second!));
    }

    [Fact]
    public void TryOpen_KeepsTwentyAndNeverEvictsOpenScore()
    {
        using var live = new TempDir();
        using var cacheDir = new TempDir();
        var cache = new ScoreCache(cacheDir.Path);
        var openCanonical = @"\\share\open.pdf";
        var openLive = Path.Combine(live.Path, "open.pdf");
        File.WriteAllText(openLive, "open");
        Assert.NotNull(cache.TryOpen(openCanonical, openLive, openLive, null));

        for (var i = 0; i < ScoreCache.MaxEntries; i++)
        {
            var path = Path.Combine(live.Path, $"s{i}.pdf");
            File.WriteAllText(path, i.ToString());
            Assert.NotNull(cache.TryOpen($@"\\share\s{i}.pdf", path, path, openCanonical));
        }

        Assert.True(cache.HasCopy(openCanonical));
        Assert.Equal(ScoreCache.MaxEntries, cache.ListRecent().Count);
        Assert.False(cache.HasCopy(@"\\share\s0.pdf"));
    }
}
