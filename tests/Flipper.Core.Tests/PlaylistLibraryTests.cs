using Flipper.Core.Library;
using Flipper.Core.Settings;

namespace Flipper.Core.Tests;

public sealed class PlaylistLibraryTests
{
    [Fact]
    public void Save_DoesNotRecreateAMissingLibrary()
    {
        using var root = new TempDir();
        var missing = Path.Combine(root.Path, "Music");
        Assert.False(PlaylistLibrary.Save(missing, [new Playlist { Id = "p", Name = "Practice" }]));
        Assert.False(Directory.Exists(missing));
    }

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
    public void LoadAndMigrate_FailedReadCanRetryWithoutLosingEitherPlaylist()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, PlaylistLibrary.FileName);
        var legacy = new List<Playlist> { new() { Id = "practice", Name = "Practice" } };
        File.WriteAllText(path, "[");

        Assert.Throws<IOException>(() => PlaylistLibrary.LoadAndMigrate(root.Path, legacy, out _));
        Assert.Equal("[", File.ReadAllText(path));
        Assert.Equal("practice", Assert.Single(legacy).Id);

        Assert.True(PlaylistLibrary.Save(root.Path, [new Playlist { Id = "concert", Name = "Concert" }]));
        var change = PlaylistLibrary.LoadAndMigrate(root.Path, legacy, out var migrated);

        Assert.True(migrated);
        Assert.Equal(["concert", "practice"], change.Playlists.Select(playlist => playlist.Id));
        Assert.Equal(["concert", "practice"], PlaylistLibrary.Load(root.Path).Select(playlist => playlist.Id));
    }

    [Fact]
    public void LoadAndMigrate_ReadableSidecarLockedAgainstReplacementReturnsOnlySharedPlaylists()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root = new TempDir();
        var path = Path.Combine(root.Path, PlaylistLibrary.FileName);
        Assert.True(PlaylistLibrary.Save(root.Path,
            [new Playlist { Id = "concert", Name = "Concert", CanonicalPaths = [@"C:\library\Concert.pdf"] }]));
        var legacy = new List<Playlist>
        {
            new() { Id = "legacy-concert", Name = "Concert", CanonicalPaths = [@"C:\library\Encore.pdf"] },
            new() { Id = "practice", Name = "Practice" }
        };
        var original = File.ReadAllText(path);

        using (File.OpenRead(path))
        {
            var change = PlaylistLibrary.LoadAndMigrate(root.Path, legacy, out var migrated);

            Assert.False(migrated);
            var playlist = Assert.Single(change.Playlists);
            Assert.Equal("concert", playlist.Id);
            Assert.Equal([@"C:\library\Concert.pdf"], playlist.CanonicalPaths);
        }

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal([@"C:\library\Encore.pdf"], legacy[0].CanonicalPaths);
    }

    [Fact]
    public void Cache_ReloadsAfterWrite()
    {
        using var root = new TempDir();
        var settings = new AppSettings { LibraryPath = root.Path };
        var cache = new PlaylistLibraryCache();
        Assert.True(cache.TryRefresh(settings));

        PlaylistLibrary.Save(root.Path, [new Playlist { Id = "p1", Name = "One" }]);
        Assert.True(cache.TryRefresh(settings));
        Assert.Equal("One", Assert.Single(settings.Playlists).Name);
        Assert.False(cache.TryRefresh(settings));

        PlaylistLibrary.Save(root.Path, [new Playlist { Id = "p2", Name = "Two" }]);
        Assert.True(cache.TryRefresh(settings));
        Assert.Equal("Two", Assert.Single(settings.Playlists).Name);
    }

    [Fact]
    public void Cache_FirstMissingFile_ClearsSavedPlaylistsOnce()
    {
        using var root = new TempDir();
        var settings = new AppSettings
        {
            LibraryPath = root.Path,
            SelectedPlaylistId = "p1",
            Playlists = [new Playlist { Id = "p1", Name = "Concert" }]
        };
        var cache = new PlaylistLibraryCache();

        Assert.True(cache.TryRefresh(settings));
        Assert.Empty(settings.Playlists);
        Assert.Null(settings.SelectedPlaylistId);
        Assert.False(cache.TryRefresh(settings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cache_DeletedOrEmptyFile_RemovesPlaylistsOnce(bool delete)
    {
        using var root = new TempDir();
        var settings = new AppSettings { LibraryPath = root.Path };
        var cache = new PlaylistLibraryCache();
        Assert.True(PlaylistLibrary.Save(root.Path, [new Playlist { Id = "p1", Name = "Concert" }]));
        Assert.True(cache.TryRefresh(settings));

        if (delete)
        {
            File.Delete(Path.Combine(root.Path, PlaylistLibrary.FileName));
        }
        else
        {
            Assert.True(PlaylistLibrary.Save(root.Path, []));
        }

        Assert.True(cache.TryRefresh(settings));
        Assert.Empty(settings.Playlists);
        Assert.False(cache.TryRefresh(settings));
    }

    [Fact]
    public void Cache_UnavailableRoot_KeepsPlaylistsAndRefreshesOnReturn()
    {
        using var root = new TempDir();
        var path = Directory.CreateDirectory(Path.Combine(root.Path, "library")).FullName;
        var disconnectedPath = Path.Combine(root.Path, "disconnected");
        var settings = new AppSettings { LibraryPath = path };
        var cache = new PlaylistLibraryCache();
        Assert.True(PlaylistLibrary.Save(path, [new Playlist { Id = "p1", Name = "Concert" }]));
        Assert.True(cache.TryRefresh(settings));

        Directory.Move(path, disconnectedPath);
        Assert.ThrowsAny<IOException>(() => cache.TryRefresh(settings));
        Assert.Equal("Concert", Assert.Single(settings.Playlists).Name);

        Assert.True(PlaylistLibrary.Save(disconnectedPath, [new Playlist { Id = "p2", Name = "Rehearsal" }]));
        Directory.Move(disconnectedPath, path);
        Assert.True(cache.TryRefresh(settings));
        Assert.Equal("Rehearsal", Assert.Single(settings.Playlists).Name);
    }

    [Theory]
    [InlineData("[")]
    [InlineData("null")]
    [InlineData("[null]")]
    public void Cache_InvalidFile_KeepsPlaylistsAndRetries(string invalidJson)
    {
        using var root = new TempDir();
        var settings = new AppSettings { LibraryPath = root.Path };
        var cache = new PlaylistLibraryCache();
        Assert.True(PlaylistLibrary.Save(root.Path, [new Playlist { Id = "p1", Name = "Concert" }]));
        Assert.True(cache.TryRefresh(settings));

        File.WriteAllText(Path.Combine(root.Path, PlaylistLibrary.FileName), invalidJson);
        Assert.Throws<IOException>(() => cache.TryRefresh(settings));
        Assert.Equal("Concert", Assert.Single(settings.Playlists).Name);

        Assert.True(PlaylistLibrary.Save(root.Path, [new Playlist { Id = "p2", Name = "Rehearsal" }]));
        Assert.True(cache.TryRefresh(settings));
        Assert.Equal("Rehearsal", Assert.Single(settings.Playlists).Name);
    }

    [Fact]
    public void Cache_LockedFile_KeepsPlaylistsAndRetries()
    {
        using var root = new TempDir();
        var settings = new AppSettings { LibraryPath = root.Path };
        var cache = new PlaylistLibraryCache();
        Assert.True(PlaylistLibrary.Save(root.Path, [new Playlist { Id = "p1", Name = "Concert" }]));
        Assert.True(cache.TryRefresh(settings));

        using (File.Open(Path.Combine(root.Path, PlaylistLibrary.FileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => cache.TryRefresh(settings));
            Assert.Equal("Concert", Assert.Single(settings.Playlists).Name);
        }

        Assert.False(cache.TryRefresh(settings));
    }

    [Fact]
    public void Hydrate_InvalidFile_DoesNotOverwriteSourceWithLegacySettings()
    {
        using var root = new TempDir();
        var path = Path.Combine(root.Path, PlaylistLibrary.FileName);
        File.WriteAllText(path, "[");
        var settings = new AppSettings
        {
            LibraryPath = root.Path,
            Playlists = [new Playlist { Id = "p1", Name = "Concert" }]
        };

        Assert.False(PlaylistLibrary.Hydrate(settings));
        Assert.Equal("Concert", Assert.Single(settings.Playlists).Name);
        Assert.Equal("[", File.ReadAllText(path));
    }

    [Fact]
    public void Hydrate_SharedPlaylistsWithoutLegacySettings_DoesNotRewriteFile()
    {
        using var root = new TempDir();
        PlaylistLibrary.Save(root.Path, [new Playlist { Id = "p1", Name = "Concert" }]);
        var path = Path.Combine(root.Path, PlaylistLibrary.FileName);
        var written = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);
        var settings = new AppSettings { LibraryPath = root.Path };

        Assert.False(PlaylistLibrary.Hydrate(settings));

        Assert.Equal("Concert", Assert.Single(settings.Playlists).Name);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Cache_DiscardedRead_IsAvailableOnNextRefresh()
    {
        using var root = new TempDir();
        PlaylistLibrary.Save(root.Path, [new Playlist { Id = "p1", Name = "Concert" }]);
        var settings = new AppSettings { LibraryPath = root.Path };
        var cache = new PlaylistLibraryCache();

        Assert.NotNull(cache.LoadIfChanged(root.Path));
        Assert.Empty(settings.Playlists);
        Assert.True(cache.TryRefresh(settings));
        Assert.Equal("Concert", Assert.Single(settings.Playlists).Name);
        Assert.False(cache.TryRefresh(settings));
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
