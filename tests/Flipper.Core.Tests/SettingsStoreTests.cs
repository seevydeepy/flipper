using Flipper.Core.Library;
using Flipper.Core.Settings;

namespace Flipper.Core.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void SaveLoad_RoundTripsLibraryGridState()
    {
        var path = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                SearchQuery = "chopin",
                Sort = SortMode.Recent,
                SortReversed = true,
                UiScalePercent = 150
            });

            var loaded = store.Load();
            Assert.Equal("chopin", loaded.SearchQuery);
            Assert.Equal(SortMode.Recent, loaded.Sort);
            Assert.True(loaded.SortReversed);
            Assert.Equal(150, loaded.UiScalePercent);
            Assert.True(loaded.VoiceTurningEnabled);
        }
        finally
        {
            DeleteParent(path);
        }
    }

    [Fact]
    public void NewSettings_VoiceTurningEnabledDefaultsOn()
    {
        Assert.True(new AppSettings().VoiceTurningEnabled);
    }

    [Fact]
    public void SaveLoad_RoundTripsVoiceTurningEnabledOff()
    {
        var path = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { VoiceTurningEnabled = false });

            var loaded = store.Load();
            Assert.False(loaded.VoiceTurningEnabled);
        }
        finally
        {
            DeleteParent(path);
        }
    }

    [Fact]
    public void Load_MissingVoiceTurningEnabled_DefaultsOn()
    {
        var path = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{ "Sort": "Name" }""");

            var loaded = new SettingsStore(path).Load();
            Assert.True(loaded.VoiceTurningEnabled);
        }
        finally
        {
            DeleteParent(path);
        }
    }

    [Fact]
    public void Load_MissingSearchQuery_IsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{ "Sort": "MostPlayed", "SortReversed": true }""");

            var loaded = new SettingsStore(path).Load();
            Assert.Equal(string.Empty, loaded.SearchQuery);
            Assert.Equal(SortMode.MostPlayed, loaded.Sort);
            Assert.True(loaded.SortReversed);
            Assert.Equal(100, loaded.UiScalePercent);
        }
        finally
        {
            DeleteParent(path);
        }
    }

    [Fact]
    public void Normalize_SnapsUiScaleToNearestStop()
    {
        var settings = new AppSettings { UiScalePercent = 110 };
        settings.Normalize();
        Assert.Equal(100, settings.UiScalePercent);

        settings.UiScalePercent = 190;
        settings.Normalize();
        Assert.Equal(200, settings.UiScalePercent);

        settings.UiScalePercent = 0;
        settings.Normalize();
        Assert.Equal(100, settings.UiScalePercent);
    }

    [Fact]
    public void Load_OldSettingsFile_StillReadsPlaylistsForMigration()
    {
        var path = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                {
                  "SelectedPlaylistId": "gig1",
                  "Playlists": [
                    { "Id": "gig1", "Name": "Gig", "CanonicalPaths": ["C:\\lib\\Air.pdf"] }
                  ]
                }
                """);

            var loaded = new SettingsStore(path).Load();
            Assert.Equal("gig1", loaded.SelectedPlaylistId);
            var playlist = Assert.Single(loaded.Playlists);
            Assert.Equal("Gig", playlist.Name);
            Assert.Equal(@"C:\lib\Air.pdf", Assert.Single(playlist.CanonicalPaths));
        }
        finally
        {
            DeleteParent(path);
        }
    }

    [Fact]
    public void SaveLoad_KeepsSelectionAndDropsPlaylists()
    {
        var path = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                SelectedPlaylistId = "gig1",
                Playlists =
                [
                    new Playlist
                    {
                        Id = "gig1",
                        Name = "Gig",
                        CanonicalPaths = [@"C:\lib\Air.pdf"]
                    }
                ]
            });

            var loaded = store.Load();
            Assert.Equal("gig1", loaded.SelectedPlaylistId);
            Assert.Empty(loaded.Playlists);
        }
        finally
        {
            DeleteParent(path);
        }
    }

    [Fact]
    public void Normalize_DropsBlankPlaylistAndStaleSelection()
    {
        var settings = new AppSettings
        {
            SelectedPlaylistId = "gone",
            Playlists =
            [
                new Playlist { Id = "", Name = "BlankId" },
                new Playlist { Id = "keep", Name = "  " },
                new Playlist
                {
                    Id = "keep",
                    Name = "Keep",
                    CanonicalPaths = [@"C:\lib\Air.pdf", @"c:\lib\air.pdf", @"C:\lib\Nocturne.pdf"]
                }
            ]
        };

        settings.Normalize();

        var playlist = Assert.Single(settings.Playlists);
        Assert.Equal("keep", playlist.Id);
        Assert.Equal("Keep", playlist.Name);
        Assert.Equal([@"C:\lib\Air.pdf", @"C:\lib\Nocturne.pdf"], playlist.CanonicalPaths);
        Assert.Null(settings.SelectedPlaylistId);
    }

    [Fact]
    public void SaveLoad_RoundTripsFolderExpanded()
    {
        var path = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings();
            Assert.True(settings.SetFolderExpanded("Corpus", false));
            Assert.True(settings.SetFolderExpanded(@"Corpus\Piano", true));
            store.Save(settings);

            var loaded = store.Load();
            Assert.False(loaded.FolderIsExpanded("Corpus", true));
            Assert.True(loaded.FolderIsExpanded(@"corpus\piano", false));
            Assert.True(loaded.FolderIsExpanded("Downloads", true));
        }
        finally
        {
            DeleteParent(path);
        }
    }

    [Fact]
    public void FolderExpanded_IgnoresBlankAndUnchanged()
    {
        var settings = new AppSettings
        {
            FolderExpanded = new Dictionary<string, bool>
            {
                [""] = true,
                ["  "] = false,
                ["Corpus"] = true,
                ["corpus"] = false
            }
        };

        settings.Normalize();

        Assert.False(settings.FolderIsExpanded("Corpus", true));
        Assert.False(settings.SetFolderExpanded("Corpus", false));
        Assert.False(settings.SetFolderExpanded(null, true));
        Assert.False(settings.SetFolderExpanded("", true));
        Assert.True(settings.SetFolderExpanded("Downloads", true));
        Assert.Single(settings.FolderExpanded.Keys.Where(key => string.Equals(key, "Corpus", StringComparison.OrdinalIgnoreCase)));
    }

    private static void DeleteParent(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
