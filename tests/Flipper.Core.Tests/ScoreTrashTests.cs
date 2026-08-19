using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class ScoreTrashTests
{
    [Fact]
    public void IsHiddenFolder_MatchesTrashRootAndChildren()
    {
        Assert.True(ScoreTrash.IsHiddenFolder(".trash"));
        Assert.True(ScoreTrash.IsHiddenFolder("trash"));
        Assert.True(ScoreTrash.IsHiddenFolder(@"trash\old"));
        Assert.True(ScoreTrash.IsHiddenFolder(@".trash\old"));
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
    public void TryMove_RelocatesFileIntoHiddenTrash_AndRemembersPlaylists()
    {
        using var root = new TempDir();
        var source = Path.Combine(root.Path, "Downloads", "Air.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "pdf");

        Assert.True(ScoreTrash.TryMove(source, root.Path, ["gig1"], out var result));
        Assert.NotNull(result);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(result.DestinationPath));
        Assert.Equal(Path.Combine(root.Path, ScoreTrash.FolderName, "Air.pdf"), result.DestinationPath);
        Assert.Equal(@"Downloads\Air.pdf", result.OriginalRelativePath);
        Assert.Equal("gig1", Assert.Single(result.PlaylistIds));
        Assert.True(ScoreTrash.TryGetOriginalRelative(root.Path, "Air.pdf", out var original));
        Assert.Equal(@"Downloads\Air.pdf", original);
    }

    [Fact]
    public void TryMove_RejectsFileAlreadyInTrash()
    {
        using var root = new TempDir();
        var source = Path.Combine(root.Path, ScoreTrash.FolderName, "Air.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "pdf");

        Assert.False(ScoreTrash.TryMove(source, root.Path, [], out var result));
        Assert.Null(result);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void TryRestore_ReturnsFileAndPlaylistIds()
    {
        using var root = new TempDir();
        var source = Path.Combine(root.Path, "K's Collection", "Air.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "pdf");
        Assert.True(ScoreTrash.TryMove(source, root.Path, ["gig1", "church"], out _));

        Assert.True(ScoreTrash.TryRestore(root.Path, "Air.pdf", out var restored, out var ids));
        Assert.True(File.Exists(restored));
        Assert.Equal(source, restored);
        Assert.False(File.Exists(Path.Combine(root.Path, ScoreTrash.FolderName, "Air.pdf")));
        Assert.Equal(["church", "gig1"], ids.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }
}
