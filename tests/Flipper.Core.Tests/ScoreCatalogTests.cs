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
}
