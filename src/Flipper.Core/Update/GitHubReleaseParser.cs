using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flipper.Core.Update;

public static class GitHubReleaseParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParse(string? json, out GitHubRelease release)
    {
        release = new GitHubRelease();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ReleaseDto>(json, Options);
            if (dto is null || string.IsNullOrWhiteSpace(dto.TagName))
            {
                return false;
            }

            release = new GitHubRelease
            {
                TagName = dto.TagName,
                HtmlUrl = dto.HtmlUrl ?? "",
                Assets = (dto.Assets ?? Array.Empty<AssetDto>())
                    .Where(asset => !string.IsNullOrWhiteSpace(asset.Name) && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                    .Select(asset => new GitHubReleaseAsset
                    {
                        Name = asset.Name!,
                        BrowserDownloadUrl = asset.BrowserDownloadUrl!
                    })
                    .ToList()
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public AssetDto[]? Assets { get; set; }
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
