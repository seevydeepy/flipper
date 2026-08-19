namespace Flipper.Core.Update;

public static class ReleaseAssets
{
    public static bool TryForRid(GitHubRelease release, string rid, out RidReleaseAssets assets)
    {
        assets = null!;
        var zipName = $"Carousel-{rid}.zip";
        var setupName = $"Carousel.Setup-{rid}.exe";
        var zip = Find(release, zipName);
        var setup = Find(release, setupName);
        if (zip is null || setup is null)
        {
            return false;
        }

        assets = new RidReleaseAssets { Zip = zip, Setup = setup };
        return true;
    }

    private static GitHubReleaseAsset? Find(GitHubRelease release, string name)
    {
        return release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
