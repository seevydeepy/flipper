using Flipper.Core.Library;
using Flipper.Core.Settings;

namespace Flipper.Core.Tests;

public sealed class LibraryPathRewriteTests
{
    [Fact]
    public void RewriteRootFolder_RewritesSettingsPaths()
    {
        var settings = new AppSettings
        {
            LibraryPath = @"\\Alexandria\Charles\Scores",
            LastScoreCanonicalPath = @"\\Alexandria\Charles\Scores\Downloads\Air.pdf",
            Scores =
            {
                [@"\\Alexandria\Charles\Scores\Downloads\Air.pdf"] = new ScoreStats { Favourite = true, PlayCount = 3 },
                [@"\\Alexandria\Charles\Scores\Corpus\Bach\Suite.pdf"] = new ScoreStats { PlayCount = 1 }
            },
            Playlists =
            {
                new Playlist
                {
                    Id = "p1",
                    Name = "Gig",
                    CanonicalPaths = [@"\\Alexandria\Charles\Scores\Downloads\Air.pdf"]
                }
            },
            FolderExpanded =
            {
                ["Downloads"] = true,
                [@"Corpus\Bach"] = false
            }
        };

        Assert.True(LibraryPathRewrite.RewriteRootFolder(settings, "Downloads", "K's Collection"));
        Assert.False(settings.Scores.ContainsKey(@"\\Alexandria\Charles\Scores\Downloads\Air.pdf"));
        Assert.True(settings.Scores[@"\\Alexandria\Charles\Scores\K's Collection\Air.pdf"].Favourite);
        Assert.Equal(1, settings.Scores[@"\\Alexandria\Charles\Scores\Corpus\Bach\Suite.pdf"].PlayCount);
        Assert.Equal(@"\\Alexandria\Charles\Scores\K's Collection\Air.pdf", settings.LastScoreCanonicalPath);
        Assert.Equal(@"\\Alexandria\Charles\Scores\K's Collection\Air.pdf", settings.Playlists[0].CanonicalPaths[0]);
        Assert.True(settings.FolderIsExpanded("K's Collection", false));
        Assert.False(settings.FolderExpanded.ContainsKey("Downloads"));
    }

    [Fact]
    public void RewriteRootFolder_RewritesUncFolderSegmentWhenLibraryRootDiffers()
    {
        var settings = new AppSettings
        {
            LibraryPath = @"S:\Scores",
            LastScoreCanonicalPath = @"\\Alexandria\Charles\Scores\Downloads\Air.pdf",
            Scores =
            {
                [@"\\Alexandria\Charles\Scores\Downloads\Air.pdf"] = new ScoreStats { Favourite = true }
            }
        };

        Assert.True(LibraryPathRewrite.RewriteRootFolder(settings, "Downloads", "K's Collection"));
        Assert.True(settings.Scores[@"\\Alexandria\Charles\Scores\K's Collection\Air.pdf"].Favourite);
        Assert.Equal(@"\\Alexandria\Charles\Scores\K's Collection\Air.pdf", settings.LastScoreCanonicalPath);
    }

    [Fact]
    public void RewriteCatalogKeys_RewritesOnlyRootFolder()
    {
        var catalog = new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase)
        {
            [@"Downloads\Air.pdf"] = new() { Title = "Air" },
            [@"Corpus\Bach\Suite.pdf"] = new() { Title = "Suite" }
        };

        Assert.True(LibraryPathRewrite.RewriteCatalogKeys(catalog, "Downloads", "K's Collection"));
        Assert.Equal("Air", catalog[@"K's Collection\Air.pdf"].Title);
        Assert.Equal("Suite", catalog[@"Corpus\Bach\Suite.pdf"].Title);
        Assert.False(catalog.ContainsKey(@"Downloads\Air.pdf"));
    }
}
