namespace Flipper.Core.Library;

public static class LibraryScanner
{
    public static LibrarySnapshot Scan(string? displayRoot)
    {
        if (string.IsNullOrWhiteSpace(displayRoot) || !Directory.Exists(displayRoot))
        {
            return new LibrarySnapshot(displayRoot ?? string.Empty, Array.Empty<ScoreEntry>(), false);
        }

        var catalog = ScoreCatalog.Load(displayRoot);
        var scores = new List<ScoreEntry>();
        ScanDirectory(displayRoot, displayRoot, scores, catalog, isRoot: true);
        return new LibrarySnapshot(displayRoot, scores, true);
    }

    private static void ScanDirectory(
        string root,
        string current,
        List<ScoreEntry> scores,
        IReadOnlyDictionary<string, ScoreFacts> catalog,
        bool isRoot)
    {
        DirectoryInfo info;
        try
        {
            info = new DirectoryInfo(current);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        if (!isRoot && (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return;
        }

        try
        {
            foreach (var file in info.EnumerateFiles())
            {
                if (!file.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file.DirectoryName ?? root);
                if (relative == ".")
                {
                    relative = string.Empty;
                }

                catalog.TryGetValue(ScoreCatalog.Key(relative, file.Name), out var facts);
                scores.Add(new ScoreEntry(
                    Path.GetFileNameWithoutExtension(file.Name),
                    relative,
                    file.FullName,
                    file.FullName,
                    file.Length,
                    file.LastWriteTimeUtc,
                    facts?.Title,
                    facts?.Composer));
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        IEnumerable<DirectoryInfo> children;
        try
        {
            children = info.EnumerateDirectories();
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        foreach (var child in children)
        {
            ScanDirectory(root, child.FullName, scores, catalog, isRoot: false);
        }
    }
}
