using Flipper.Core.Library;
using Flipper.Core.Settings;

namespace Flipper.Core.Tests;

public sealed class PlaylistLibraryTests
{
    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        using var root = new TempDir();
        Assert.Empty(PlaylistLibrary.Load(root.Path));
        Assert.Empty(PlaylistLibrary.Load(null));
    }

    [Fact]
    public void SaveLoad_RoundTripsSanitizedPlaylists()
    {
        using var root = new TempDir();
        var saved = new List<Playlist>
        {
            new() { Id = "", Name = "Blank" },
            new()
            {
                Id = "gig1",
                Name = "Gig",
                CanonicalPaths = [@"C:\lib\Air.pdf", @"c:\lib\air.pdf", @"C:\lib\Nocturne.pdf"]
            }
        };

        Assert.True(PlaylistLibrary.Save(root.Path, saved));
        var loaded = PlaylistLibrary.Load(root.Path);
        var playlist = Assert.Single(loaded);
        Assert.Equal("gig1", playlist.Id);
        Assert.Equal("Gig", playlist.Name);
        Assert.Equal([@"C:\lib\Air.pdf", @"C:\lib\Nocturne.pdf"], playlist.CanonicalPaths);
    }

    [Fact]
    public void Hydrate_MigratesSettingsWhenLibraryFileIsMissing()
    {
        using var root = new TempDir();
        var settings = new AppSettings
        {
            LibraryPath = root.Path,
            Playlists =
            [
                new Playlist
                {
                    Id = "church",
                    Name = "Church",
                    CanonicalPaths = [@"C:\lib\Ave.pdf"]
                }
            ]
        };

        Assert.True(PlaylistLibrary.Hydrate(settings));
        var path = Path.Combine(root.Path, PlaylistLibrary.FileName);
        Assert.True(File.Exists(path));
        Assert.Equal("Church", Assert.Single(PlaylistLibrary.Load(root.Path)).Name);
    }

    [Fact]
    public void Hydrate_PrefersLibraryFileAndMergesLocalPlaylists()
    {
        using var root = new TempDir();
        PlaylistLibrary.Save(root.Path, [
            new Playlist
            {
                Id = "xmas",
                Name = "Christmas",
                CanonicalPaths = [@"C:\lib\Carol.pdf"]
            }
        ]);
        var settings = new AppSettings
        {
            LibraryPath = root.Path,
            Playlists =
            [
                new Playlist { Id = "church", Name = "Church", CanonicalPaths = [@"C:\lib\Ave.pdf"] },
                new Playlist
                {
                    Id = "xmas-local",
                    Name = "Christmas",
                    CanonicalPaths = [@"C:\lib\Snow.pdf"]
                }
            ]
        };

        Assert.True(PlaylistLibrary.Hydrate(settings));
        Assert.Equal(2, settings.Playlists.Count);
        var christmas = Assert.Single(settings.Playlists, item => item.Name == "Christmas");
        Assert.Equal("xmas", christmas.Id);
        Assert.Equal([@"C:\lib\Carol.pdf", @"C:\lib\Snow.pdf"], christmas.CanonicalPaths);
        Assert.Equal("Church", Assert.Single(settings.Playlists, item => item.Name == "Church").Name);
    }

    [Fact]
    public void Cache_ReloadsAfterWrite()
    {
        using var root = new TempDir();
        var settings = new AppSettings { LibraryPath = root.Path };
        var cache = new PlaylistLibraryCache();
        Assert.False(cache.TryRefresh(settings));

        PlaylistLibrary.Save(root.Path, [new Playlist { Id = "p1", Name = "One" }]);
        Assert.True(cache.TryRefresh(settings));
        Assert.Equal("One", Assert.Single(settings.Playlists).Name);
        Assert.False(cache.TryRefresh(settings));

        PlaylistLibrary.Save(root.Path, [new Playlist { Id = "p2", Name = "Two" }]);
        Assert.True(cache.TryRefresh(settings));
        Assert.Equal("Two", Assert.Single(settings.Playlists).Name);
    }

    [Fact]
    public void BindToRoot_DoesNotCarryPlaylistsFromThePreviousLibrary()
    {
        using var previous = new TempDir();
        using var next = new TempDir();
        PlaylistLibrary.Save(next.Path, [new Playlist { Id = "xmas", Name = "Christmas" }]);
        var settings = new AppSettings
        {
            LibraryPath = previous.Path,
            SelectedPlaylistId = "church",
            Playlists = [new Playlist { Id = "church", Name = "Church" }]
        };

        Assert.False(PlaylistLibrary.BindToRoot(settings, next.Path));
        Assert.Equal(next.Path, settings.LibraryPath);
        Assert.Null(settings.SelectedPlaylistId);
        var playlist = Assert.Single(settings.Playlists);
        Assert.Equal("Christmas", playlist.Name);
        Assert.False(File.Exists(Path.Combine(previous.Path, PlaylistLibrary.FileName)));
    }
}
