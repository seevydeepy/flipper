using Flipper.Core.Library;
using Flipper.Core.Reader;
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
                SelectedFolder = @"Practice\Piano",
                Sort = SortMode.Recent,
                SortReversed = true,
                UiScalePercent = 150,
                CropToInk = true,
                WinkTurningEnabled = true,
                JevImageFallbackEnabled = true
            });

            var loaded = store.Load();
            Assert.Equal("chopin", loaded.SearchQuery);
            Assert.Equal(@"Practice\Piano", loaded.SelectedFolder);
            Assert.Equal(SortMode.Recent, loaded.Sort);
            Assert.True(loaded.SortReversed);
            Assert.Equal(150, loaded.UiScalePercent);
            Assert.True(loaded.CropToInk);
            Assert.True(loaded.WinkTurningEnabled);
            Assert.True(loaded.JevImageFallbackEnabled);
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
        Assert.False(new AppSettings().WinkTurningEnabled);
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
    public void SaveLoad_RoundTripsWinkCalibrationAcrossStoreInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var profile = new WinkCalibrationProfile
            {
                CameraId = "front-camera",
                CreatedUtc = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero),
                LeftOpen = 0.89,
                LeftClosed = 0.10,
                RightOpen = 0.86,
                RightClosed = 0.12,
                ClosedThreshold = 0.25,
                LeftWinkOtherEyeMinimum = 0.45,
                LeftWinkMinimumDifference = 0.3,
                RightWinkOtherEyeMinimum = 0.3,
                RightWinkMinimumDifference = 0.2
            };
            new SettingsStore(path).Save(new AppSettings { WinkTurningEnabled = true, WinkCalibration = profile });

            var loaded = new SettingsStore(path).Load();
            Assert.True(loaded.WinkTurningEnabled);
            Assert.Equal(profile, loaded.WinkCalibration);
        }
        finally
        {
            DeleteParent(path);
        }
    }

    [Fact]
    public void Load_LegacyWinkSettings_HaveNoCalibration()
    {
        var path = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{ "WinkTurningEnabled": true }""");

            var loaded = new SettingsStore(path).Load();
            Assert.True(loaded.WinkTurningEnabled);
            Assert.Null(loaded.WinkCalibration);
        }
        finally
        {
            DeleteParent(path);
        }
    }

    [Fact]
    public void Load_InvalidWinkCalibration_DropsOnlyCalibration()
    {
        var path = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                {
                  "WinkTurningEnabled": true,
                  "VoiceTurningEnabled": false,
                  "SearchQuery": "chopin",
                  "WinkCalibration": {
                    "Version": 1,
                    "CameraId": "front-camera",
                    "CreatedUtc": "2026-11-14T12:00:00+00:00",
                    "LeftOpen": 0.60,
                    "LeftClosed": 0.53,
                    "RightOpen": 0.86,
                    "RightClosed": 0.12,
                    "ClosedThreshold": 0.25,
                    "LeftWinkOtherEyeMinimum": 0.45,
                    "LeftWinkMinimumDifference": 0.3,
                    "RightWinkOtherEyeMinimum": 0.3,
                    "RightWinkMinimumDifference": 0.2
                  }
                }
                """);

            var loaded = new SettingsStore(path).Load();
            Assert.Null(loaded.WinkCalibration);
            Assert.True(loaded.WinkTurningEnabled);
            Assert.False(loaded.VoiceTurningEnabled);
            Assert.Equal("chopin", loaded.SearchQuery);
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
            Assert.False(loaded.WinkTurningEnabled);
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
            Assert.False(loaded.CropToInk);
            Assert.False(loaded.JevImageFallbackEnabled);
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
    public void Save_KeepsOnlyPlaylistsStillAwaitingMigration()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var settings = new AppSettings { Playlists = [new Playlist { Id = "remote", Name = "On the share" }] };
        store.Save(settings, [new Playlist { Id = "old", Name = "Before the update" }]);
        Assert.Equal("old", Assert.Single(store.Load().Playlists).Id);
        Assert.Equal("remote", Assert.Single(settings.Playlists).Id);
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
