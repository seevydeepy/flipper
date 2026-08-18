namespace Flipper.Core.Library;

public static class ScoreSearch
{
    public static IReadOnlyList<ScoreEntry> Filter(
        IEnumerable<ScoreEntry> scores,
        string? query,
        string? selectedFolder)
    {
        var list = scores as IReadOnlyList<ScoreEntry> ?? scores.ToArray();
        if (string.IsNullOrWhiteSpace(query))
        {
            if (selectedFolder is null)
            {
                return list;
            }

            return list
                .Where(score => string.Equals(score.RelativeFolder, selectedFolder, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return list
            .Where(score => score.DisplayName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
