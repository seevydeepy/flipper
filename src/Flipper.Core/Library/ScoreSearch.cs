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
            .Where(score => InFolder(score.RelativeFolder, selectedFolder))
            .ToArray();
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

        var list = result.ToArray();
        if (reversed)
        {
            Array.Reverse(list);
        }

        return list;
    }

    private static ScoreStats? Stats(IReadOnlyDictionary<string, ScoreStats> stats, ScoreEntry score)
    {
        return stats.TryGetValue(score.CanonicalPath, out var item) ? item : null;
    }
}
