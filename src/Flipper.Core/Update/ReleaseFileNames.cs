namespace Flipper.Core.Update;

public static class ReleaseFileNames
{
    public static string Zip(string rid)
    {
        return $"Carousel-{rid}.zip";
    }

    public static string Setup(Version version, string rid)
    {
        return $"Carousel.Setup-{version.Major}.{version.Minor}.{version.Build}-{rid}.exe";
    }

    public static bool TryReadSetupRid(string setupPath, out string rid)
    {
        rid = "";
        if (string.IsNullOrWhiteSpace(setupPath))
        {
            return false;
        }

        var name = Path.GetFileName(setupPath);
        if (!name.StartsWith("Carousel.Setup", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase))
        {
            rid = "win-x64";
            return true;
        }

        if (name.EndsWith("-win-arm64.exe", StringComparison.OrdinalIgnoreCase))
        {
            rid = "win-arm64";
            return true;
        }

        return false;
    }
}
