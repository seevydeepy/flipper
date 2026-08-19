using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class ScoreTrashTests
{
    [Fact]
    public void IsHiddenFolder_MatchesTrashRootAndChildren()
    {
        Assert.True(ScoreTrash.IsHiddenFolder("trash"));
        Assert.True(ScoreTrash.IsHiddenFolder("Trash"));
        Assert.True(ScoreTrash.IsHiddenFolder(@"trash\old"));
        Assert.False(ScoreTrash.IsHiddenFolder("Downloads"));
        Assert.False(ScoreTrash.IsHiddenFolder(string.Empty));
        Assert.False(ScoreTrash.IsHiddenFolder(@"Corpus\Piano"));
    }

    [Fact]
    public void UniqueDestination_AddsNumericSuffixWhenNameExists()
    {
        using var dir = new TempDir();
        var first = Path.Combine(dir.Path, "Air.pdf");
        File.WriteAllText(first, "a");

        var second = ScoreTrash.UniqueDestination(dir.Path, "Air.pdf");
        Assert.Equal(Path.Combine(dir.Path, "Air 2.pdf"), second);
        File.WriteAllText(second, "b");

        var third = ScoreTrash.UniqueDestination(dir.Path, "Air.pdf");
        Assert.Equal(Path.Combine(dir.Path, "Air 3.pdf"), third);
    }

    [Fact]
    public void TryMove_RelocatesFileIntoTrash()
    {
        using var root = new TempDir();
        var source = Path.Combine(root.Path, "Downloads", "Air.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "pdf");

        Assert.True(ScoreTrash.TryMove(source, root.Path, out var destination));
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.Equal(Path.Combine(root.Path, ScoreTrash.FolderName, "Air.pdf"), destination);
    }

    [Fact]
    public void TryMove_RejectsFileAlreadyInTrash()
    {
        using var root = new TempDir();
        var source = Path.Combine(root.Path, ScoreTrash.FolderName, "Air.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "pdf");

        Assert.False(ScoreTrash.TryMove(source, root.Path, out var destination));
        Assert.Equal(string.Empty, destination);
        Assert.True(File.Exists(source));
    }
}
