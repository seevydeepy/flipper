using Flipper.Core.Settings;

namespace Flipper.Core.Library;

public static class ScoreSearch
{
    public static IReadOnlyList<ScoreEntry> Filter(
        IEnumerable<ScoreEntry> scores,
        string? query,
        string? selectedFolder,
        bool favouritesOnly = false,
        IReadOnlyDictionary<string, ScoreStats>? stats = null)
    {
        IReadOnlyList<ScoreEntry> list = scores as IReadOnlyList<ScoreEntry> ?? scores.ToArray();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            list = list.Where(score => Matches(score, needle)).ToArray();
        }
        else if (selectedFolder is not null)
        {
            list = list.Where(score => InFolder(score.RelativeFolder, selectedFolder)).ToArray();
        }

        if (favouritesOnly)
        {
            list = list
                .Where(score =>
                    stats is not null
                    && stats.TryGetValue(score.CanonicalPath, out var item)
                    && item.Favourite)
                .ToArray();
        }

        return list;
    }

    public static bool InFolder(string relativeFolder, string selectedFolder)
    {
        if (string.Equals(relativeFolder, selectedFolder, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrEmpty(selectedFolder))
        {
            return false;
        }

        var prefix = selectedFolder.TrimEnd('\\', '/') + "\\";
        var normalised = relativeFolder.Replace('/', '\\');
        return normalised.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<ScoreEntry> Sort(
        IEnumerable<ScoreEntry> scores,
        SortMode mode,
        IReadOnlyDictionary<string, ScoreStats> stats,
        bool reversed = false)
    {
        IEnumerable<ScoreEntry> result = mode switch
        {
            SortMode.Recent => scores
                .OrderByDescending(score => Stats(stats, score)?.LastPlayedUtc ?? DateTime.MinValue)
                .ThenBy(score => score.CardTitle, StringComparer.OrdinalIgnoreCase),
            SortMode.MostPlayed => scores
                .OrderByDescending(score => Stats(stats, score)?.PlayCount ?? 0)
                .ThenBy(score => score.CardTitle, StringComparer.OrdinalIgnoreCase),
            _ => scores.OrderBy(score => score.CardTitle, StringComparer.OrdinalIgnoreCase)
        };

        var list = result.ToArray();
        if (reversed)
        {
            Array.Reverse(list);
        }

        return list;
    }

    private static bool Matches(ScoreEntry score, string needle)
    {
        return score.CardTitle.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || score.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(score.Composer)
                && score.Composer.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static ScoreStats? Stats(IReadOnlyDictionary<string, ScoreStats> stats, ScoreEntry score)
    {
        return stats.TryGetValue(score.CanonicalPath, out var item) ? item : null;
    }
}
