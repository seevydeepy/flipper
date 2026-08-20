namespace Flipper.Core.Update;

public static class FirstInstallPaths
{
    public static bool TryResolveRid(string setupPath, string? runtimeRid, out string rid)
    {
        rid = "";
        if (string.IsNullOrWhiteSpace(setupPath))
        {
            return false;
        }

        var name = Path.GetFileName(setupPath);
        if (name.Equals("Carousel.Setup-win-x64.exe", StringComparison.OrdinalIgnoreCase))
        {
            rid = "win-x64";
            return true;
        }

        if (name.Equals("Carousel.Setup-win-arm64.exe", StringComparison.OrdinalIgnoreCase))
        {
            rid = "win-arm64";
            return true;
        }

        if (string.IsNullOrWhiteSpace(runtimeRid))
        {
            return false;
        }

        rid = runtimeRid;
        return true;
    }

    public static bool TryResolve(string setupPath, string rid, out string targetDir, out string siblingZip)
    {
        targetDir = "";
        siblingZip = "";
        if (string.IsNullOrWhiteSpace(setupPath) || string.IsNullOrWhiteSpace(rid))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(setupPath));
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        targetDir = Path.Combine(directory, "Carousel");
        siblingZip = Path.Combine(directory, $"Carousel-{rid}.zip");
        return true;
    }
}
