namespace Flipper.Core.Update;

public static class InstallManifest
{
    public const string FileName = "Carousel.installed.txt";

    public static string FilePath(string targetDir)
    {
        return Path.Combine(targetDir, FileName);
    }

    public static void Write(string targetDir, IEnumerable<string> relativePaths)
    {
        Directory.CreateDirectory(targetDir);
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(relative) || !seen.Add(relative))
            {
                continue;
            }

            lines.Add(relative);
        }

        File.WriteAllLines(FilePath(targetDir), lines);
    }

    public static IReadOnlyList<string> Read(string targetDir)
    {
        var path = FilePath(targetDir);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    public static bool TryResolveOwned(string targetDir, string relative, out string dest)
    {
        dest = "";
        if (string.IsNullOrWhiteSpace(targetDir) || string.IsNullOrWhiteSpace(relative))
        {
            return false;
        }

        var root = Path.GetFullPath(targetDir);
        dest = Path.GetFullPath(Path.Combine(root, relative));
        return dest.Equals(root, StringComparison.OrdinalIgnoreCase)
            || dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryDeleteListed(string targetDir, IReadOnlyList<string> relativePaths, int retries = 5)
    {
        foreach (var relative in relativePaths)
        {
            if (!TryResolveOwned(targetDir, relative, out var dest))
            {
                return false;
            }

            if (!File.Exists(dest) && !Directory.Exists(dest))
            {
                continue;
            }

            if (File.Exists(dest) && !TryDeleteFile(dest, retries))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryRefreshOwned(string targetDir, IReadOnlyList<string> newRelative, int retries = 5)
    {
        var obsolete = Read(targetDir)
            .Where(path => newRelative.All(candidate => !candidate.Equals(path, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (!TryDeleteListed(targetDir, obsolete, retries))
        {
            return false;
        }

        Write(targetDir, newRelative);
        return true;
    }

    public static void RemoveEmptyDirectories(string targetDir)
    {
        if (!Directory.Exists(targetDir))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(targetDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(targetDir).Any())
        {
            Directory.Delete(targetDir);
        }
    }

    public static bool TryDeleteFile(string path, int retries = 5)
    {
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (IOException)
            {
                if (attempt == retries)
                {
                    return false;
                }

                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt == retries)
                {
                    return false;
                }

                Thread.Sleep(200);
            }
        }

        return false;
    }
}
