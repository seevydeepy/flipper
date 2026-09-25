namespace Flipper.Core.Library;

public sealed record LibrarySnapshot(
    string RootDisplayPath,
    IReadOnlyList<ScoreEntry> Scores,
    bool RootReachable,
    IReadOnlyList<ScanSkipped>? Skipped = null,
    IReadOnlyDictionary<string, ScoreCatalogEntry>? CatalogEntries = null)
{
    public IReadOnlyList<ScanSkipped> SkippedPaths => Skipped ?? Array.Empty<ScanSkipped>();
    public LibrarySnapshot Without(string canonicalPath)
    {
        var next = Scores
            .Where(score => !string.Equals(score.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return this with { Scores = next };
    }

    public IReadOnlyList<string> Folders => Scores
        .Select(score => score.RelativeFolder)
        .Where(folder => !ScoreTrash.IsHiddenFolder(folder))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public LibrarySnapshot WithCatalog(IReadOnlyDictionary<string, ScoreCatalogEntry> catalog)
    {
        var scores = Scores.Select(score =>
        {
            // Trash entries use their original path; the filesystem scan owns that mapping.
            if (ScoreTrash.IsHiddenFolder(score.RelativeFolder)) return score;
            var key = ScoreCatalog.Key(score.RelativeFolder, Path.GetFileName(score.DisplayFullPath));
            var found = catalog.TryGetValue(key, out var stored);
            return score with
            {
                Title = stored?.Facts.Title,
                Composer = stored?.Facts.Composer,
                Subtitle = stored?.Facts.Subtitle,
                HasCatalogEntry = found,
                Provenance = stored?.Provenance
            };
        }).ToArray();
        return this with { Scores = scores, CatalogEntries = catalog };
    }

    public bool SameMembership(LibrarySnapshot other, bool includeLabels = true)
    {
        if (RootReachable != other.RootReachable)
        {
            return false;
        }

        if (Scores.Count != other.Scores.Count)
        {
            return false;
        }

        var right = new Dictionary<string, ScoreEntry>(other.Scores.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var score in other.Scores)
        {
            right[score.CanonicalPath] = score;
        }

        if (right.Count != other.Scores.Count)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var left in Scores)
        {
            if (!seen.Add(left.CanonicalPath)
                || !right.TryGetValue(left.CanonicalPath, out var match)
                || left.Length != match.Length
                || left.LastWriteUtc != match.LastWriteUtc
                || (includeLabels && left.CardText != match.CardText))
            {
                return false;
            }
        }

        return true;
    }
}
