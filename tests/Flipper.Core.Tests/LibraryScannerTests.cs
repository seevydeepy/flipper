using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class LibraryScannerTests
{
    [Fact]
    public void Scan_FindsNestedPdfs_AndIgnoresOtherFiles()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Root.pdf"), "a");
        File.WriteAllText(Path.Combine(root.Path, "notes.txt"), "no");
        Directory.CreateDirectory(Path.Combine(root.Path, "Bach"));
        File.WriteAllText(Path.Combine(root.Path, "Bach", "Suite.PDF"), "b");

        var snapshot = LibraryScanner.Scan(root.Path);

        Assert.True(snapshot.RootReachable);
        Assert.Equal(2, snapshot.Scores.Count);
        Assert.Contains(snapshot.Scores, score => score.DisplayName == "Root" && score.RelativeFolder == string.Empty);
        Assert.Contains(snapshot.Scores, score => score.DisplayName == "Suite" && score.RelativeFolder == "Bach");
    }

    [Fact]
    public void Scan_RefreshesSizeAndTimestampAfterPdfChanges()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, "Score.pdf");
        File.WriteAllBytes(path, [1, 2, 3]);
        var firstWrite = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, firstWrite);
        var first = Assert.Single(LibraryScanner.Scan(root.Path).Scores);
        Assert.Equal(3, first.Length);
        Assert.Equal(firstWrite, first.LastWriteUtc);

        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        var nextWrite = firstWrite.AddMinutes(1);
        File.SetLastWriteTimeUtc(path, nextWrite);
        var next = Assert.Single(LibraryScanner.Scan(root.Path).Scores);
        Assert.Equal(5, next.Length);
        Assert.Equal(nextWrite, next.LastWriteUtc);
    }

    [Fact]
    public void Scan_AppliesCatalogSubtitle()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "List.pdf"), "a");
        File.WriteAllText(
            Path.Combine(root.Path, ScoreCatalog.FileName),
            """{"List.pdf":{"title":"Schindler's List","subtitle":"Main Theme","composer":"John Williams"}}""");

        var snapshot = LibraryScanner.Scan(root.Path);
        var score = Assert.Single(snapshot.Scores);
        Assert.Equal("Schindler's List", score.CardTitle);
        Assert.Equal("Main Theme", score.CardSubtitle);
        Assert.Equal("John Williams", score.CardComposer);
        Assert.True(score.HasCatalogEntry);
    }

    [Fact]
    public void Scan_ReportsAnUnchangedButLockedCatalogueAsIncomplete()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Score.pdf"), "a");
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        File.WriteAllText(path, """{"Score.pdf":{"title":"Library title"}}""");
        var cache = new ScoreCatalogCache();
        Assert.Equal("Library title", Assert.Single(LibraryScanner.Scan(root.Path, cache).Scores).Title);

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var snapshot = LibraryScanner.Scan(root.Path, cache);
            Assert.True(snapshot.RootReachable);
            Assert.Single(snapshot.Scores);
            Assert.Equal(path, Assert.Single(snapshot.SkippedPaths).Path);
        }

        var recovered = LibraryScanner.Scan(root.Path, cache);
        Assert.Empty(recovered.SkippedPaths);
        Assert.Equal("Library title", Assert.Single(recovered.Scores).Title);
    }

    [Fact]
    public void Scan_RetriesMalformedCatalogueAtTheSameSizeAndTimestamp()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Score.pdf"), "a");
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        var stamp = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(path, """{"Score.pdf":{"title":"Old title"}}""");
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(-1));
        var cache = new ScoreCatalogCache();
        Assert.Equal("Old title", cache.LoadEntries(root.Path)["Score.pdf"].Facts.Title);

        const string current = """{"Score.pdf":{"title":"New title"}}""";
        File.WriteAllText(path, current[..^1] + "]");
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.ThrowsAny<IOException>(() => cache.LoadEntries(root.Path));
        Assert.Empty(ScoreCatalog.LoadEntries(root.Path));
        Assert.Equal(path, Assert.Single(LibraryScanner.Scan(root.Path).SkippedPaths).Path);
        var incomplete = LibraryScanner.Scan(root.Path, cache);
        Assert.Equal(path, Assert.Single(incomplete.SkippedPaths).Path);
        Assert.Single(incomplete.Scores);

        File.WriteAllText(path, current);
        File.SetLastWriteTimeUtc(path, stamp);
        var recovered = LibraryScanner.Scan(root.Path, cache);
        Assert.Empty(recovered.SkippedPaths);
        Assert.Equal("New title", Assert.Single(recovered.Scores).Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public void Scan_TreatsAnAbsentOrEmptyCatalogueAsComplete(string? content)
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Score.pdf"), "a");
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        File.WriteAllText(path, """{"Score.pdf":{"title":"Old title"}}""");
        var cache = new ScoreCatalogCache();
        Assert.Equal("Old title", Assert.Single(LibraryScanner.Scan(root.Path, cache).Scores).Title);

        if (content is null) File.Delete(path);
        else File.WriteAllText(path, content);

        var snapshot = LibraryScanner.Scan(root.Path, cache);
        Assert.Empty(snapshot.SkippedPaths);
        Assert.False(Assert.Single(snapshot.Scores).HasCatalogEntry);
        Assert.Empty(cache.LoadEntries(root.Path));
    }

    [Fact]
    public void Scan_DistinguishesMissingEntryFromExistingNullEntry()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Existing.pdf"), "a");
        File.WriteAllText(Path.Combine(root.Path, "Missing.pdf"), "b");
        File.WriteAllText(
            Path.Combine(root.Path, ScoreCatalog.FileName),
            """{"Existing.pdf":{"title":null}}""");

        var snapshot = LibraryScanner.Scan(root.Path);

        Assert.True(snapshot.Scores.Single(score => score.DisplayName == "Existing").HasCatalogEntry);
        Assert.False(snapshot.Scores.Single(score => score.DisplayName == "Missing").HasCatalogEntry);
    }

    [Fact]
    public void Scan_IncludesTrashScores_AndOmitsTrashFolder()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Visible.pdf"), "a");
        var trash = Path.Combine(root.Path, ScoreTrash.FolderName);
        Directory.CreateDirectory(trash);
        File.WriteAllText(Path.Combine(trash, "Hidden.pdf"), "b");

        var snapshot = LibraryScanner.Scan(root.Path);

        Assert.Contains(snapshot.Scores, score => score.DisplayName == "Visible");
        Assert.Contains(snapshot.Scores, score => score.DisplayName == "Hidden" && score.RelativeFolder == ScoreTrash.FolderName);
        Assert.DoesNotContain(snapshot.Folders, folder => ScoreTrash.IsHiddenFolder(folder));
    }

    [Fact]
    public void Scan_ContinuesAfterUnreadableSubdirectory()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Visible.pdf"), "a");
        var locked = Path.Combine(root.Path, "Locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "Hidden.pdf"), "b");

        var user = Environment.UserName;
        var deny = RunIcacls($"\"{locked}\" /deny {user}:(OI)(CI)(R)");
        try
        {
            if (deny != 0)
            {
                return;
            }

            var snapshot = LibraryScanner.Scan(root.Path);
            Assert.True(snapshot.RootReachable);
            Assert.Contains(snapshot.Scores, score => score.DisplayName == "Visible");
            Assert.DoesNotContain(snapshot.Scores, score => score.DisplayName == "Hidden");
        }
        finally
        {
            RunIcacls($"\"{locked}\" /remove:d {user}");
        }
    }

    [Fact]
    public void Scan_MissingRoot_IsUnreachable()
    {
        var snapshot = LibraryScanner.Scan(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.False(snapshot.RootReachable);
        Assert.Empty(snapshot.Scores);
    }

    [Fact]
    public void Scan_SkippedFolders_AreVisible_AndDoNotAbortOthers()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Visible.pdf"), "a");
        // A reparse point (symlink/junction) child is skipped without error.
        var link = Path.Combine(root.Path, "Link");
        try
        {
            Directory.CreateSymbolicLink(link, root.Path);
        }
        catch (IOException)
        {
            // Privilege absent: the scan still reports the visible score.
            var fallback = LibraryScanner.Scan(root.Path);
            Assert.Contains(fallback.Scores, score => score.DisplayName == "Visible");
            return;
        }
        catch (UnauthorizedAccessException)
        {
            var fallback = LibraryScanner.Scan(root.Path);
            Assert.Contains(fallback.Scores, score => score.DisplayName == "Visible");
            return;
        }

        var snapshot = LibraryScanner.Scan(root.Path);
        Assert.Contains(snapshot.Scores, score => score.DisplayName == "Visible");
        Assert.DoesNotContain(snapshot.Scores, score => score.RelativeFolder == "Link");
    }

    [Fact]
    public void Scan_RecordsSkippedEntries_ForUnreadableFiles()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Visible.pdf"), "a");
        // A directory masquerading as a .pdf: stat succeeds, extension matches,
        // content unreadable as a file — scan must not abort.
        Directory.CreateDirectory(Path.Combine(root.Path, "Weird.pdf"));

        var snapshot = LibraryScanner.Scan(root.Path);
        Assert.True(snapshot.RootReachable);
        Assert.Contains(snapshot.Scores, score => score.DisplayName == "Visible");
    }

    private static int RunIcacls(string args)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "icacls",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null)
        {
            return -1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }
}
