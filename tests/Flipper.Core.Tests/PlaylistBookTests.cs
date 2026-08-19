using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class PlaylistBookTests
{
    [Fact]
    public void TryCreate_TrimsName()
    {
        var list = new List<Playlist>();

        Assert.True(PlaylistBook.TryCreate(list, "  Recital  ", out var playlist));
        Assert.Equal("Recital", playlist.Name);
        Assert.False(string.IsNullOrWhiteSpace(playlist.Id));
        Assert.Empty(playlist.CanonicalPaths);
        Assert.Same(playlist, Assert.Single(list));
    }

    [Fact]
    public void TryCreate_EmptyOrDuplicateName_Fails()
    {
        var list = new List<Playlist>();

        Assert.False(PlaylistBook.TryCreate(list, "   ", out _));
        Assert.Empty(list);

        Assert.True(PlaylistBook.TryCreate(list, "Gig", out _));
        Assert.False(PlaylistBook.TryCreate(list, "gig", out _));
        Assert.Single(list);
    }

    [Fact]
    public void AddScore_IsCaseInsensitiveAndDeduped()
    {
        var playlist = new Playlist { Id = "p1", Name = "Gig" };

        Assert.True(PlaylistBook.AddScore(playlist, @"C:\lib\Air.pdf"));
        Assert.False(PlaylistBook.AddScore(playlist, @"c:\lib\air.pdf"));
        Assert.False(PlaylistBook.AddScore(playlist, ""));
        Assert.Equal(@"C:\lib\Air.pdf", Assert.Single(playlist.CanonicalPaths));
    }

    [Fact]
    public void RemoveScore_RemovesCaseInsensitiveMatch()
    {
        var playlist = new Playlist
        {
            Id = "p1",
            Name = "Gig",
            CanonicalPaths = [@"C:\lib\Air.pdf", @"C:\lib\Nocturne.pdf"]
        };

        Assert.True(PlaylistBook.RemoveScore(playlist, @"c:\lib\air.pdf"));
        Assert.False(PlaylistBook.RemoveScore(playlist, @"c:\lib\air.pdf"));
        Assert.Equal(@"C:\lib\Nocturne.pdf", Assert.Single(playlist.CanonicalPaths));
    }

    [Fact]
    public void Delete_RemovesOnlyThatPlaylist()
    {
        var keep = new Playlist { Id = "keep", Name = "Keep" };
        var drop = new Playlist { Id = "drop", Name = "Drop" };
        var list = new List<Playlist> { keep, drop };

        Assert.True(PlaylistBook.Delete(list, "DROP"));
        Assert.False(PlaylistBook.Delete(list, "drop"));
        Assert.Same(keep, Assert.Single(list));
    }

    [Fact]
    public void RemovePath_ClearsPathFromEveryPlaylist()
    {
        var first = new Playlist
        {
            Id = "a",
            Name = "A",
            CanonicalPaths = [@"C:\lib\Air.pdf", @"C:\lib\Keep.pdf"]
        };
        var second = new Playlist
        {
            Id = "b",
            Name = "B",
            CanonicalPaths = [@"c:\lib\air.pdf"]
        };

        PlaylistBook.RemovePath([first, second], @"C:\lib\Air.pdf");

        Assert.Equal(@"C:\lib\Keep.pdf", Assert.Single(first.CanonicalPaths));
        Assert.Empty(second.CanonicalPaths);
    }

    [Fact]
    public void Members_DoNotTouchTheFileSystem()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "air.pdf");
        File.WriteAllText(path, "pdf");
        var list = new List<Playlist>();

        Assert.True(PlaylistBook.TryCreate(list, "Gig", out var playlist));
        Assert.True(PlaylistBook.AddScore(playlist, path));
        Assert.True(PlaylistBook.RemoveScore(playlist, path));
        Assert.True(PlaylistBook.AddScore(playlist, path));
        PlaylistBook.RemovePath(list, path);
        Assert.True(PlaylistBook.Delete(list, playlist.Id));

        Assert.True(File.Exists(path));
        Assert.Equal("pdf", File.ReadAllText(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(dir.Path));
    }
}
