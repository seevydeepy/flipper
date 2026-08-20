namespace Flipper.Core.Update;

public static class ReleaseAssets
{
    public static bool TryForRid(GitHubRelease release, string rid, out RidReleaseAssets assets)
    {
        assets = null!;
        if (!AppVersion.TryParse(release.TagName, out var version))
        {
            return false;
        }

        if (!TryZipForRid(release, rid, out var zip))
        {
            return false;
        }

        var setup = Find(release, ReleaseFileNames.Setup(version, rid));
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
        var found = Find(release, ReleaseFileNames.Zip(rid));
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
