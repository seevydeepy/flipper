namespace Flipper.Core.Update;

public sealed class GitHubReleaseAsset
{
    public string Name { get; init; } = "";
    public string BrowserDownloadUrl { get; init; } = "";
}

public sealed class GitHubRelease
{
    public string TagName { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public IReadOnlyList<GitHubReleaseAsset> Assets { get; init; } = Array.Empty<GitHubReleaseAsset>();
}

public sealed class RidReleaseAssets
{
    public required GitHubReleaseAsset Zip { get; init; }
    public required GitHubReleaseAsset Setup { get; init; }
}
