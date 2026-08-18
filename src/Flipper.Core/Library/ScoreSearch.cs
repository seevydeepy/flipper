using Flipper.Core.Settings;

namespace Flipper.Core.Library;

public static class ScoreSearch
{
    public static IReadOnlyList<ScoreEntry> Filter(
        IEnumerable<ScoreEntry> scores,
        string? query,
        string? selectedFolder)
    {
        var list = scores as IReadOnlyList<ScoreEntry> ?? scores.ToArray();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            return list
                .Where(score => score.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (selectedFolder is null)
        {
            return list;
        }

        return list
            .Where(score => string.Equals(score.RelativeFolder, selectedFolder, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static IReadOnlyList<ScoreEntry> Sort(
        IEnumerable<ScoreEntry> scores,
        SortMode mode,
        IReadOnlyDictionary<string, ScoreStats> stats)
    {
        IEnumerable<ScoreEntry> result = scores;
        if (mode == SortMode.Favourites)
        {
            result = result.Where(score =>
                stats.TryGetValue(score.CanonicalPath, out var item) && item.Favourite);
        }

        result = mode switch
        {
            SortMode.Recent => result
                .OrderByDescending(score => Stats(stats, score)?.LastPlayedUtc ?? DateTime.MinValue)
                .ThenBy(score => score.DisplayName, StringComparer.OrdinalIgnoreCase),
            SortMode.MostPlayed => result
                .OrderByDescending(score => Stats(stats, score)?.PlayCount ?? 0)
                .ThenBy(score => score.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => result.OrderBy(score => score.DisplayName, StringComparer.OrdinalIgnoreCase)
        };

        return result.ToArray();
    }

    private static ScoreStats? Stats(IReadOnlyDictionary<string, ScoreStats> stats, ScoreEntry score)
    {
        return stats.TryGetValue(score.CanonicalPath, out var item) ? item : null;
    }
}
