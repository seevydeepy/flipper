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

        if (ReleaseFileNames.TryReadSetupRid(setupPath, out rid))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(runtimeRid))
        {
            return false;
        }

        rid = runtimeRid;
        return true;
    }

    public static string DefaultTarget()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Carousel");
    }

    public static string StartMenuShortcutPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Carousel.lnk");
    }
}
