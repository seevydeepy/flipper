using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class FolderTreeTests
{
    [Fact]
    public void FromRelativeFolders_BuildsAncestorsAndSorts()
    {
        var tree = FolderTree.FromRelativeFolders(["Corpus\\Piano", "Downloads", "Corpus\\Violin"]);
        Assert.Equal(["Corpus", "Downloads"], tree.Select(item => item.Name));
        Assert.Equal(["Piano", "Violin"], tree[0].Children.Select(item => item.Name));
        Assert.Equal(@"Corpus\Piano", tree[0].Children[0].Key);
    }
}
