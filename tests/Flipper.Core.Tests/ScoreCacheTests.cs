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
    public void FindCopy_UsesOnlyLocalFilesAndKeepsTheLastOpenedTime()
    {
        using var live = new TempDir();
        using var cacheDir = new TempDir();
        var liveFile = Path.Combine(live.Path, "score.pdf");
        var canonicalPath = @"\\share\score.pdf";
        File.WriteAllText(liveFile, "pdf-bytes");
        var cache = new ScoreCache(cacheDir.Path);
        var copy = cache.TryOpen(canonicalPath, liveFile, liveFile, null);
        Assert.NotNull(copy);
        var lastOpened = Assert.Single(cache.ListRecent()).LastOpenedUtc;
        File.Delete(liveFile);

        Assert.Equal(copy, cache.FindCopy(canonicalPath.ToUpperInvariant()));
        Assert.Equal(lastOpened, Assert.Single(cache.ListRecent()).LastOpenedUtc);
        File.Delete(copy);
        Assert.Null(cache.FindCopy(canonicalPath));
    }

    [Fact]
    public async Task TryOpen_ConcurrentCallsKeepEveryIndexEntry()
    {
        using var live = new TempDir();
        using var cacheDir = new TempDir();
        var cache = new ScoreCache(cacheDir.Path);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = Enumerable.Range(0, ScoreCache.MaxEntries).Select(i =>
        {
            var path = Path.Combine(live.Path, $"score-{i}.pdf");
            File.WriteAllText(path, $"score-{i}");
            return path;
        }).ToArray();
        var opens = files.Select(path => Task.Run(async () =>
        {
            await start.Task;
            return cache.TryOpen(path, path, path, null);
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(opens);

        Assert.Equal(ScoreCache.MaxEntries, cache.ListRecent().Count);
        Assert.All(files, path => Assert.NotNull(cache.FindCopy(path)));
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

    [Fact]
    public void Remove_DropsIndexAndCacheFile_KeepsOthers()
    {
        using var live = new TempDir();
        using var cacheDir = new TempDir();
        var cache = new ScoreCache(cacheDir.Path);
        var keepLive = Path.Combine(live.Path, "keep.pdf");
        var dropLive = Path.Combine(live.Path, "drop.pdf");
        File.WriteAllText(keepLive, "keep");
        File.WriteAllText(dropLive, "drop");
        var keepCanonical = @"\\share\keep.pdf";
        var dropCanonical = @"\\share\drop.pdf";
        Assert.NotNull(cache.TryOpen(keepCanonical, keepLive, keepLive, null));
        Assert.NotNull(cache.TryOpen(dropCanonical, dropLive, dropLive, keepCanonical));

        cache.Remove(dropCanonical);

        Assert.True(cache.HasCopy(keepCanonical));
        Assert.False(cache.HasCopy(dropCanonical));
        Assert.Single(cache.ListRecent());
    }
}
