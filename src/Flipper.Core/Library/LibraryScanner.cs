namespace Flipper.Core.Library;

public static class LibraryScanner
{
    public static LibrarySnapshot Scan(string? displayRoot, ScoreCatalogCache? catalogCache = null)
    {
        if (string.IsNullOrWhiteSpace(displayRoot) || !Directory.Exists(displayRoot))
        {
            return new LibrarySnapshot(displayRoot ?? string.Empty, Array.Empty<ScoreEntry>(), false);
        }

        var catalog = catalogCache is null
            ? ScoreCatalog.Load(displayRoot)
            : catalogCache.Load(displayRoot);
        ScoreTrash.Ensure(displayRoot);
        var trashIndex = ScoreTrash.LoadIndex(displayRoot);
        var scores = new List<ScoreEntry>();
        ScanDirectory(displayRoot, displayRoot, scores, catalog, trashIndex, isRoot: true);
        return new LibrarySnapshot(displayRoot, scores, true);
    }

    private static void ScanDirectory(
        string root,
        string current,
        List<ScoreEntry> scores,
        IReadOnlyDictionary<string, ScoreFacts> catalog,
        IReadOnlyList<TrashRecord> trashIndex,
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

                var catalogKey = ScoreCatalog.Key(relative, file.Name);
                if (ScoreTrash.IsHiddenFolder(relative))
                {
                    var record = trashIndex.FirstOrDefault(item =>
                        string.Equals(item.FileName, file.Name, StringComparison.OrdinalIgnoreCase));
                    if (record is not null && !string.IsNullOrWhiteSpace(record.OriginalRelativePath))
                    {
                        catalogKey = record.OriginalRelativePath.Replace('/', '\\');
                    }
                }

                var hasCatalogEntry = catalog.TryGetValue(catalogKey, out var facts);
                scores.Add(new ScoreEntry(
                    Path.GetFileNameWithoutExtension(file.Name),
                    relative,
                    file.FullName,
                    file.FullName,
                    file.Length,
                    file.LastWriteTimeUtc,
                    facts?.Title,
                    facts?.Composer,
                    facts?.Subtitle,
                    hasCatalogEntry));
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
            ScanDirectory(root, child.FullName, scores, catalog, trashIndex, isRoot: false);
        }
    }
}
