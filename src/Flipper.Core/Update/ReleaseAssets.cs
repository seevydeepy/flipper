namespace Flipper.Core.Update;

public static class ReleaseAssets
{
    public static bool TryForRid(GitHubRelease release, string rid, out RidReleaseAssets assets)
    {
        assets = null!;
        var setupName = $"Carousel.Setup-{rid}.exe";
        if (!TryZipForRid(release, rid, out var zip))
        {
            return false;
        }

        var setup = Find(release, setupName);
        if (setup is null)
        {
            return false;
        }

        assets = new RidReleaseAssets { Zip = zip, Setup = setup };
        return true;
    }

    public static bool TryZipForRid(GitHubRelease release, string rid, out GitHubReleaseAsset zip)
    {
        zip = null!;
        var found = Find(release, $"Carousel-{rid}.zip");
        if (found is null)
        {
            return false;
        }

        zip = found;
        return true;
    }

    private static GitHubReleaseAsset? Find(GitHubRelease release, string name)
    {
        return release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
