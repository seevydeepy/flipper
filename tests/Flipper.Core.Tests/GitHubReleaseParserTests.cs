using Flipper.Core.Update;

namespace Flipper.Core.Tests;

public sealed class GitHubReleaseParserTests
{
    private const string LatestJson = """
        {
          "tag_name": "v1.0.1",
          "html_url": "https://github.com/seevydeepy/flipper/releases/tag/v1.0.1",
          "assets": [
            {"name": "Carousel-win-x64.zip", "browser_download_url": "https://example.com/x64.zip"},
            {"name": "Carousel-win-arm64.zip", "browser_download_url": "https://example.com/arm64.zip"},
            {"name": "Carousel.Setup-win-x64.exe", "browser_download_url": "https://example.com/setup-x64.exe"},
            {"name": "Carousel.Setup-win-arm64.exe", "browser_download_url": "https://example.com/setup-arm64.exe"}
          ]
        }
        """;

    [Fact]
    public void TryParse_ReadsTagAndFourAssets()
    {
        Assert.True(GitHubReleaseParser.TryParse(LatestJson, out var release));
        Assert.Equal("v1.0.1", release.TagName);
        Assert.Equal("https://github.com/seevydeepy/flipper/releases/tag/v1.0.1", release.HtmlUrl);
        Assert.Equal(4, release.Assets.Count);
        Assert.Equal("https://example.com/x64.zip", release.Assets.Single(asset => asset.Name == "Carousel-win-x64.zip").BrowserDownloadUrl);
    }

    [Fact]
    public void ForRid_ReturnsMatchingZipAndSetup()
    {
        Assert.True(GitHubReleaseParser.TryParse(LatestJson, out var release));
        Assert.True(ReleaseAssets.TryForRid(release, "win-x64", out var assets));
        Assert.Equal("Carousel-win-x64.zip", assets.Zip.Name);
        Assert.Equal("Carousel.Setup-win-x64.exe", assets.Setup.Name);
    }

    [Fact]
    public void ForRid_MissingSetup_Fails()
    {
        const string json = """
            {
              "tag_name": "v1.0.1",
              "html_url": "https://example.com",
              "assets": [
                {"name": "Carousel-win-x64.zip", "browser_download_url": "https://example.com/x64.zip"}
              ]
            }
            """;

        Assert.True(GitHubReleaseParser.TryParse(json, out var release));
        Assert.False(ReleaseAssets.TryForRid(release, "win-x64", out _));
    }

    [Fact]
    public void TryParse_RejectsGarbage()
    {
        Assert.False(GitHubReleaseParser.TryParse("not json", out _));
    }
}
