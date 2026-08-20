using System.Text.Json.Nodes;
using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class ScoreCatalogTests
{
    [Fact]
    public void Key_JoinsFolderAndFile()
    {
        Assert.Equal(@"Corpus\Bach\Air.pdf", ScoreCatalog.Key(@"Corpus\Bach", "Air.pdf"));
        Assert.Equal("Root.pdf", ScoreCatalog.Key(string.Empty, "Root.pdf"));
    }

    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        var catalog = ScoreCatalog.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Empty(catalog);
    }

    [Fact]
    public void Cache_ReusesUnchangedFile_AndReloadsAfterWrite()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        File.WriteAllText(path, """{"A.pdf":{"title":"Air","composer":"Bach"}}""");

        var cache = new ScoreCatalogCache();
        var first = cache.Load(root.Path);
        var second = cache.Load(root.Path);
        Assert.Same(first, second);
        Assert.Equal("Air", first["A.pdf"].Title);

        File.WriteAllText(path, """{"A.pdf":{"title":"Prelude","composer":"Bach"}}""");
        var third = cache.Load(root.Path);
        Assert.NotSame(first, third);
        Assert.Equal("Prelude", third["A.pdf"].Title);
    }

    [Fact]
    public void TryRewriteRootFolder_RewritesKeysOnDisk()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        File.WriteAllText(path, """{"Downloads\\Air.pdf":{"title":"Air","composer":"Bach"},"Corpus\\Suite.pdf":{"title":"Suite"}}""");

        Assert.True(ScoreCatalog.TryRewriteRootFolder(root.Path, "Downloads", "K's Collection"));
        var catalog = ScoreCatalog.Load(root.Path);
        Assert.Equal("Air", catalog[@"K's Collection\Air.pdf"].Title);
        Assert.Equal("Suite", catalog[@"Corpus\Suite.pdf"].Title);
        Assert.False(catalog.ContainsKey(@"Downloads\Air.pdf"));
    }

    [Fact]
    public void TryRewriteRootFolder_DoesNotWriteWhileCatalogLockIsBusy()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        const string original = """{"Downloads\\Air.pdf":{"title":"Air"}}""";
        File.WriteAllText(path, original);
        using var held = new FileStream(
            Path.Combine(root.Path, ".flipper-catalog.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var changed = ScoreCatalog.TryRewriteRootFolder(root.Path, "Downloads", "Collection");

        Assert.False(changed);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void TryRewriteRootFolder_PreservesUnknownFields()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        File.WriteAllText(path, """{"Downloads\\Air.pdf":{"title":"Air","future":{"level":3},"composer":null}}""");
        var before = JsonNode.Parse(File.ReadAllText(path))!.AsObject()[@"Downloads\Air.pdf"]!.DeepClone();

        Assert.True(ScoreCatalog.TryRewriteRootFolder(root.Path, "Downloads", "Collection"));
        var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Assert.True(JsonNode.DeepEquals(before, after[@"Collection\Air.pdf"]));
    }

    [Fact]
    public void Load_ReadsSubtitle()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        File.WriteAllText(path, """{"A.pdf":{"title":"Schindler's List","subtitle":"Main Theme","composer":"John Williams"}}""");

        var catalog = ScoreCatalog.Load(root.Path);
        Assert.Equal("Schindler's List", catalog["A.pdf"].Title);
        Assert.Equal("Main Theme", catalog["A.pdf"].Subtitle);
        Assert.Equal("John Williams", catalog["A.pdf"].Composer);
    }

    [Fact]
    public void TryMergeMissing_AddsNewKeysWithoutReplacingExistingKeys()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        File.WriteAllText(path, """{"Existing.pdf":{"title":"Curated"}}""");
        var generated = new Dictionary<string, ScoreFacts>
        {
            ["Existing.pdf"] = new() { Title = "Automatic" },
            ["New.pdf"] = new() { Title = "New Title", Composer = "Composer" }
        };

        var result = ScoreCatalog.TryMergeMissing(root.Path, generated);
        var catalog = ScoreCatalog.Load(root.Path);

        Assert.Equal(CatalogMergeStatus.Inserted, result.Status);
        Assert.Equal(1, result.InsertedCount);
        Assert.Equal("Curated", catalog["Existing.pdf"].Title);
        Assert.Equal("New Title", catalog["New.pdf"].Title);
        Assert.Equal("Composer", catalog["New.pdf"].Composer);
    }

    [Fact]
    public void TryMergeMissing_IgnoresAStaleFixedTemporaryFile()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        File.WriteAllText(path, "{}");
        using var stale = new FileStream(path + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var result = ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, ScoreFacts> { ["New.pdf"] = new() { Title = "New Title" } });

        Assert.Equal(CatalogMergeStatus.Inserted, result.Status);
        Assert.Equal("New Title", ScoreCatalog.Load(root.Path)["New.pdf"].Title);
    }

    [Fact]
    public void TryMergeMissing_PreservesExistingRawValues()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        File.WriteAllText(
            path,
            """{"MissingFields.pdf":{"title":"Air","custom":{"level":3}},"NullFields.pdf":{"title":null,"composer":null,"future":true}}""");
        var before = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var missingFields = before["MissingFields.pdf"]!.DeepClone();
        var nullFields = before["NullFields.pdf"]!.DeepClone();

        var result = ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, ScoreFacts> { ["New.pdf"] = new() { Title = "New" } });
        var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Assert.Equal(CatalogMergeStatus.Inserted, result.Status);
        Assert.True(JsonNode.DeepEquals(missingFields, after["MissingFields.pdf"]));
        Assert.True(JsonNode.DeepEquals(nullFields, after["NullFields.pdf"]));
    }

    [Fact]
    public void TryMergeMissing_DoesNotRewriteWhenEveryKeyExists()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        File.WriteAllText(path, """{"Existing.pdf":{"title":"Curated"}}""");
        var stamp = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(path, stamp);

        var result = ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, ScoreFacts> { ["Existing.pdf"] = new() { Title = "Automatic" } });

        Assert.Equal(CatalogMergeStatus.NoChanges, result.Status);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void TryMergeMissing_TreatsKeysAsCaseInsensitive()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        File.WriteAllText(path, """{"air.pdf":{"title":"Curated"}}""");

        var result = ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, ScoreFacts> { ["AIR.PDF"] = new() { Title = "Automatic" } });

        Assert.Equal(CatalogMergeStatus.NoChanges, result.Status);
        Assert.Single(JsonNode.Parse(File.ReadAllText(path))!.AsObject());
    }

    [Fact]
    public void TryMergeMissing_LeavesMalformedCatalogUnchanged()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, ScoreCatalog.FileName);
        const string malformed = "{not json";
        File.WriteAllText(path, malformed);

        var result = ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, ScoreFacts> { ["New.pdf"] = new() { Title = "New" } });

        Assert.Equal(CatalogMergeStatus.Failed, result.Status);
        Assert.Equal(malformed, File.ReadAllText(path));
    }

    [Fact]
    public async Task TryMergeMissing_ConcurrentWritersRetainBothKeys()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, ScoreCatalog.FileName), "{}");
        using var start = new Barrier(2);

        Task<CatalogMergeResult> Merge(string key) => Task.Run(() =>
        {
            start.SignalAndWait();
            return ScoreCatalog.TryMergeMissing(
                root.Path,
                new Dictionary<string, ScoreFacts> { [key] = new() { Title = key } });
        });

        var results = await Task.WhenAll(Merge("A.pdf"), Merge("B.pdf"));
        var catalog = ScoreCatalog.Load(root.Path);

        Assert.All(results, result => Assert.Equal(CatalogMergeStatus.Inserted, result.Status));
        Assert.Equal("A.pdf", catalog["A.pdf"].Title);
        Assert.Equal("B.pdf", catalog["B.pdf"].Title);
    }

    [Fact]
    public async Task TryMergeMissing_RejectsSourceThatChangesWhileWaitingForCatalogLock()
    {
        using var root = new TempDir();
        var catalogPath = Path.Combine(root.Path, ScoreCatalog.FileName);
        var sourcePath = Path.Combine(root.Path, "New.pdf");
        File.WriteAllText(catalogPath, "{}");
        File.WriteAllText(sourcePath, "old");
        var source = new FileInfo(sourcePath);
        using var held = new FileStream(
            Path.Combine(root.Path, ".flipper-catalog.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var candidate = new CatalogMergeCandidate(
            new ScoreFacts { Title = "Old Title" },
            sourcePath,
            source.Length,
            source.LastWriteTimeUtc);

        var merge = Task.Run(() => ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["New.pdf"] = candidate }));
        await Task.Delay(100);
        File.WriteAllText(sourcePath, "new and different");
        held.Dispose();

        var result = await merge;

        Assert.Contains("New.pdf", result.RejectedKeys, StringComparer.OrdinalIgnoreCase);
        Assert.False(ScoreCatalog.Load(root.Path).ContainsKey("New.pdf"));
    }
}
