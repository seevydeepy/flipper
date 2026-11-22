using Flipper.Core.Cache;
using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class LibraryMirrorTests
{
    [Fact]
    public async Task PreservesACompleteLibraryAndAllItsCopiesAcrossAnOfflineRestart()
    {
        using var source = new TempDir();
        using var storage = new TempDir();
        var folder = Path.Combine(source.Path, "Recital");
        Directory.CreateDirectory(folder);
        for (var number = 0; number < 23; number++)
        {
            File.WriteAllText(Path.Combine(folder, $"Part {number}.pdf"), $"score {number}");
        }

        var snapshot = LibraryScanner.Scan(source.Path);
        var entry = Assert.Single(snapshot.Scores, score => score.DisplayName == "Part 0");
        var provenance = new CatalogProvenance();
        provenance.Field("TITLE").Origin = ScoreFieldOrigin.Manual;
        var catalogKey = ScoreCatalog.Key("Recital", "Part 0.pdf");
        var catalog = new Dictionary<string, ScoreCatalogEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [catalogKey] = new()
            {
                Facts = new ScoreFacts { Title = "Opening movement", Composer = "A. Composer", Subtitle = "Part I" },
                Provenance = provenance
            }
        };
        snapshot = snapshot.WithCatalog(catalog);
        var otherEntry = Assert.Single(snapshot.Scores, score => score.DisplayName == "Part 22");
        var playlist = new Playlist
        {
            Id = "recital",
            Name = "Programme",
            CanonicalPaths = [otherEntry.CanonicalPath, entry.CanonicalPath]
        };

        var mirror = new LibraryMirror(storage.Path, source.Path);
        Assert.True(mirror.SaveSnapshot(snapshot, [playlist]));
        Assert.NotNull(mirror.Load());
        Assert.Null(mirror.FindCopy(entry));
        Assert.True(await mirror.CopyScoresAsync(snapshot, CancellationToken.None));

        Directory.Delete(source.Path, recursive: true);
        var reopened = new LibraryMirror(storage.Path, source.Path + Path.DirectorySeparatorChar);
        var stored = Assert.IsType<MirroredLibrary>(reopened.Load());
        Assert.False(stored.Snapshot.RootReachable);
        Assert.Equal(source.Path, stored.Snapshot.RootDisplayPath);
        Assert.Equal("Recital", Assert.Single(stored.Snapshot.Folders));
        Assert.Equal(23, stored.Snapshot.Scores.Count);
        var restored = Assert.Single(stored.Snapshot.Scores, score => score.DisplayName == "Part 0");
        Assert.Equal(entry.CanonicalPath, restored.CanonicalPath);
        Assert.Equal(entry.DisplayFullPath, restored.DisplayFullPath);
        Assert.Equal("Opening movement", restored.Title);
        Assert.Equal("A. Composer", restored.Composer);
        Assert.Equal("Part I", restored.Subtitle);
        Assert.Equal(ScoreFieldOrigin.Manual, restored.Provenance?.Fields["title"].Origin);
        Assert.Equal("Opening movement", stored.Snapshot.CatalogEntries?[catalogKey.ToUpperInvariant()].Facts.Title);
        var restoredPlaylist = Assert.Single(stored.Playlists);
        Assert.Equal(playlist.Id, restoredPlaylist.Id);
        Assert.Equal(playlist.Name, restoredPlaylist.Name);
        Assert.Equal(playlist.CanonicalPaths, restoredPlaylist.CanonicalPaths);
        foreach (var score in stored.Snapshot.Scores)
        {
            Assert.Equal(score.DisplayName.Replace("Part", "score"), ReadCopy(reopened, score));
        }

        // Up-to-date local files are sufficient even though the original files are now gone.
        Assert.True(await reopened.CopyScoresAsync(snapshot, CancellationToken.None));
    }

    [Fact]
    public async Task RefreshReplacesChangedBytesAndRemovesAnObsoleteCopy()
    {
        using var source = new TempDir();
        using var storage = new TempDir();
        var retainedPath = Path.Combine(source.Path, "Retained.pdf");
        var removedPath = Path.Combine(source.Path, "Removed.pdf");
        File.WriteAllText(retainedPath, "first");
        File.WriteAllText(removedPath, "removed");
        var mirror = new LibraryMirror(storage.Path, source.Path);
        var first = LibraryScanner.Scan(source.Path);
        var removed = Assert.Single(first.Scores, score => score.DisplayFullPath == removedPath);
        Assert.True(mirror.SaveSnapshot(first, []));
        Assert.True(await mirror.CopyScoresAsync(first, CancellationToken.None));

        File.WriteAllText(retainedPath, "later");
        File.SetLastWriteTimeUtc(retainedPath, DateTime.UtcNow.AddMinutes(1));
        File.Delete(removedPath);
        var next = LibraryScanner.Scan(source.Path);
        var retained = Assert.Single(next.Scores);
        Assert.Equal("first", ReadCopy(mirror, retained));
        Assert.True(mirror.SaveSnapshot(next, []));
        Assert.Single(mirror.Load()!.Snapshot.Scores);

        Assert.True(await mirror.CopyScoresAsync(next, CancellationToken.None));
        Assert.Equal("later", ReadCopy(mirror, retained));
        Assert.Null(mirror.FindCopy(removed));
    }

    [Fact]
    public async Task RejectsIncompleteScansAndRetainsGoodBytesWhenAnUpdateFails()
    {
        using var source = new TempDir();
        using var storage = new TempDir();
        var scorePath = Path.Combine(source.Path, "Score.pdf");
        File.WriteAllText(scorePath, "good copy");
        var mirror = new LibraryMirror(storage.Path, source.Path);
        var first = LibraryScanner.Scan(source.Path);
        var entry = Assert.Single(first.Scores);
        Assert.True(mirror.SaveSnapshot(first, []));
        Assert.True(await mirror.CopyScoresAsync(first, CancellationToken.None));

        var partial = first with { Scores = [], Skipped = [new ScanSkipped(source.Path, "unreadable")] };
        var offline = first with { Scores = [], RootReachable = false };
        Assert.False(mirror.SaveSnapshot(partial, []));
        Assert.False(mirror.SaveSnapshot(offline, []));
        Assert.False(await mirror.CopyScoresAsync(partial, CancellationToken.None));
        Assert.False(await mirror.CopyScoresAsync(offline, CancellationToken.None));
        Assert.Equal(entry.CanonicalPath, Assert.Single(mirror.Load()!.Snapshot.Scores).CanonicalPath);

        File.WriteAllText(scorePath, "updated copy");
        var next = LibraryScanner.Scan(source.Path);
        var updated = Assert.Single(next.Scores);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mirror.CopyScoresAsync(next, cancellation.Token));
        Assert.Equal("good copy", ReadCopy(mirror, updated));

        File.Delete(scorePath);
        Assert.False(await mirror.CopyScoresAsync(next, CancellationToken.None));
        var local = await mirror.CopyScoreAsync(updated, CancellationToken.None);
        Assert.Equal("good copy", File.ReadAllText(Assert.IsType<string>(local)));
    }

    [Fact]
    public async Task APreviousCopyPassRetainsCopiesOpenedFromTheNewerLibrary()
    {
        using var source = new TempDir();
        using var storage = new TempDir();
        var mirror = new LibraryMirror(storage.Path, source.Path);
        File.WriteAllText(Path.Combine(source.Path, "First.pdf"), "first");
        var previous = LibraryScanner.Scan(source.Path);
        Assert.True(mirror.SaveSnapshot(previous, []));

        File.WriteAllText(Path.Combine(source.Path, "New.pdf"), "new");
        var latest = LibraryScanner.Scan(source.Path);
        var newScore = Assert.Single(latest.Scores, score => score.DisplayName == "New");
        Assert.True(mirror.SaveSnapshot(latest, []));
        Assert.NotNull(await mirror.CopyScoreAsync(newScore, CancellationToken.None));

        Assert.True(await mirror.CopyScoresAsync(previous, CancellationToken.None));
        Assert.Equal("new", ReadCopy(mirror, newScore));
    }

    [Fact]
    public async Task IsolatesLibrariesWithFilesOfTheSameName()
    {
        using var first = new TempDir();
        using var second = new TempDir();
        using var storage = new TempDir();
        File.WriteAllText(Path.Combine(first.Path, "Score.pdf"), "first library");
        File.WriteAllText(Path.Combine(second.Path, "Score.pdf"), "second library");
        var firstSnapshot = LibraryScanner.Scan(first.Path);
        var secondSnapshot = LibraryScanner.Scan(second.Path);
        var firstScore = Assert.Single(firstSnapshot.Scores);
        var secondScore = Assert.Single(secondSnapshot.Scores);
        var firstMirror = new LibraryMirror(storage.Path, first.Path);
        var secondMirror = new LibraryMirror(storage.Path, second.Path);

        Assert.True(firstMirror.SaveSnapshot(firstSnapshot, []));
        Assert.True(await firstMirror.CopyScoresAsync(firstSnapshot, CancellationToken.None));
        Assert.Null(secondMirror.Load());
        Assert.Null(secondMirror.FindCopy(firstScore));
        Assert.Null(await secondMirror.CopyScoreAsync(firstScore, CancellationToken.None));
        Assert.False(secondMirror.SaveSnapshot(firstSnapshot, []));
        Assert.False(await secondMirror.CopyScoresAsync(firstSnapshot, CancellationToken.None));
        Assert.True(secondMirror.SaveSnapshot(secondSnapshot, []));
        Assert.True(await secondMirror.CopyScoresAsync(secondSnapshot, CancellationToken.None));
        Assert.Equal("first library", ReadCopy(firstMirror, firstScore));
        Assert.Equal("second library", ReadCopy(secondMirror, secondScore));
        Assert.Null(firstMirror.FindCopy(secondScore));
    }

    private static string ReadCopy(LibraryMirror mirror, ScoreEntry score) =>
        File.ReadAllText(Assert.IsType<string>(mirror.FindCopy(score)));
}
