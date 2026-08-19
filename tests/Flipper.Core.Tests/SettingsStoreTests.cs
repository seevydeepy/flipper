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

    private static void DeleteParent(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
