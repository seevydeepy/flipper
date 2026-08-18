namespace Flipper.Core.Library;

public sealed record LibrarySnapshot(
    string RootDisplayPath,
    IReadOnlyList<ScoreEntry> Scores,
    bool RootReachable)
{
    public IReadOnlyList<string> Folders => Scores
        .Select(score => score.RelativeFolder)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public bool SameMembership(LibrarySnapshot other)
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
                || left.LastWriteUtc != match.LastWriteUtc)
            {
                return false;
            }
        }

        return true;
    }
}
