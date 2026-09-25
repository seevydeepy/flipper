namespace Flipper.Core.Library;

/// <summary>Why a directory was skipped during a scan.</summary>
public sealed record ScanSkipped(string Path, string Reason);

public static class LibraryScanner
{
    public static LibrarySnapshot Scan(string? displayRoot, ScoreCatalogCache? catalogCache = null)
    {
        if (string.IsNullOrWhiteSpace(displayRoot) || !Directory.Exists(displayRoot))
        {
            return new LibrarySnapshot(displayRoot ?? string.Empty, Array.Empty<ScoreEntry>(), false);
        }

        var catalog = catalogCache is null
            ? ScoreCatalog.LoadEntries(displayRoot)
            : catalogCache.LoadEntries(displayRoot);
        ScoreTrash.Ensure(displayRoot);
        var trashIndex = ScoreTrash.LoadIndex(displayRoot);
        var scores = new List<ScoreEntry>();
        var skipped = new List<ScanSkipped>();
        ScanDirectory(displayRoot, displayRoot, scores, catalog, trashIndex, skipped, isRoot: true);
        return new LibrarySnapshot(displayRoot, scores, true, [.. skipped], catalog);
    }

    private static void ScanDirectory(
        string root,
        string current,
        List<ScoreEntry> scores,
        IReadOnlyDictionary<string, ScoreCatalogEntry> catalog,
        IReadOnlyList<TrashRecord> trashIndex,
        List<ScanSkipped> skipped,
        bool isRoot)
    {
        DirectoryInfo info;
        try
        {
            info = new DirectoryInfo(current);
        }
        catch (UnauthorizedAccessException)
        {
            skipped.Add(new ScanSkipped(current, "unauthorised"));
            return;
        }
        catch (IOException)
        {
            skipped.Add(new ScanSkipped(current, "unreadable"));
            return;
        }
        catch (System.Security.SecurityException)
        {
            skipped.Add(new ScanSkipped(current, "unauthorised"));
            return;
        }

        // Attribute access itself can throw on disappearing drives; handle the
        // folder locally so other readable folders still scan.
        try
        {
            if (!isRoot && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            skipped.Add(new ScanSkipped(current, "unauthorised"));
            return;
        }
        catch (IOException)
        {
            skipped.Add(new ScanSkipped(current, "unreadable"));
            return;
        }

        // Lazy enumerations throw mid-iteration when a child vanishes: force
        // iteration inside the guard so one disappearing file cannot abort the
        // scan of unrelated folders.
        try
        {
            foreach (var file in info.EnumerateFiles())
            {
                FileInfo stable;
                try
                {
                    // Touch the attributes now; a file deleted between
                    // enumeration and stat is skipped, not fatal.
                    stable = new FileInfo(file.FullName);
                    _ = stable.Length;
                    _ = stable.LastWriteTimeUtc;
                }
                catch (UnauthorizedAccessException)
                {
                    skipped.Add(new ScanSkipped(file.FullName, "unauthorised"));
                    continue;
                }
                catch (IOException)
                {
                    skipped.Add(new ScanSkipped(file.FullName, "unreadable"));
                    continue;
                }

                if (!stable.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, stable.DirectoryName ?? root);
                if (relative == ".")
                {
                    relative = string.Empty;
                }

                var catalogKey = ScoreCatalog.Key(relative, stable.Name);
                if (ScoreTrash.IsHiddenFolder(relative))
                {
                    var record = trashIndex.FirstOrDefault(item =>
                        string.Equals(item.FileName, stable.Name, StringComparison.OrdinalIgnoreCase));
                    if (record is not null && !string.IsNullOrWhiteSpace(record.OriginalRelativePath))
                    {
                        catalogKey = record.OriginalRelativePath.Replace('/', '\\');
                    }
                }

                var hasCatalogEntry = catalog.TryGetValue(catalogKey, out var stored);
                var facts = stored?.Facts;
                scores.Add(new ScoreEntry(
                    Path.GetFileNameWithoutExtension(stable.Name),
                    relative,
                    stable.FullName,
                    stable.FullName,
                    stable.Length,
                    stable.LastWriteTimeUtc,
                    facts?.Title,
                    facts?.Composer,
                    facts?.Subtitle,
                    hasCatalogEntry) { Provenance = stored?.Provenance });
            }
        }
        catch (UnauthorizedAccessException)
        {
            skipped.Add(new ScanSkipped(current, "unauthorised"));
        }
        catch (IOException)
        {
            skipped.Add(new ScanSkipped(current, "unreadable"));
        }

        List<DirectoryInfo> children;
        try
        {
            // Materialise: a directory deleted mid-scan ends here, not in the
            // caller's foreach.
            children = info.EnumerateDirectories().ToList();
        }
        catch (UnauthorizedAccessException)
        {
            skipped.Add(new ScanSkipped(current, "unauthorised"));
            return;
        }
        catch (IOException)
        {
            skipped.Add(new ScanSkipped(current, "unreadable"));
            return;
        }

        foreach (var child in children)
        {
            string childPath;
            try
            {
                childPath = child.FullName;
            }
            catch (IOException)
            {
                skipped.Add(new ScanSkipped(current, "unreadable-child"));
                continue;
            }

            ScanDirectory(root, childPath, scores, catalog, trashIndex, skipped, isRoot: false);
        }
    }
}
