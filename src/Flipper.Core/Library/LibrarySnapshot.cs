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

        var left = Scores.OrderBy(score => score.CanonicalPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var right = other.Scores.OrderBy(score => score.CanonicalPath, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i].CanonicalPath, right[i].CanonicalPath, StringComparison.OrdinalIgnoreCase)
                || left[i].Length != right[i].Length
                || left[i].LastWriteUtc != right[i].LastWriteUtc)
            {
                return false;
            }
        }

        return true;
    }
}
