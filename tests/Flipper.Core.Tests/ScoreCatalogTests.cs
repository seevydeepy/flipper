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
}
