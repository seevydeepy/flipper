namespace Flipper.Core.Update;

public static class ProtectedInstallPath
{
    public static bool IsProtected(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        return IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
            || IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    private static bool IsUnder(string full, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFull = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedFull.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedFull.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
