using System.Globalization;
using System.Text;
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
            list = list.Where(score => Matches(score, query)).ToArray();
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

    private static bool Matches(ScoreEntry score, string query)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
        {
            return false;
        }

        var fields = new[]
        {
            Fold(score.CardTitle),
            Fold(score.DisplayName),
            Fold(Path.GetFileName(score.DisplayFullPath)),
            Fold(score.CardComposer)
        };

        return tokens.All(token => FieldContains(fields, token));
    }

    private static List<string> Tokenize(string query)
    {
        var tokens = new List<string>();
        foreach (var part in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = Fold(part);
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static bool FieldContains(string[] fields, string token)
    {
        foreach (var field in fields)
        {
            if (field.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format)
            {
                continue;
            }

            AppendMapped(builder, ch);
        }

        return builder.ToString();
    }

    private static void AppendMapped(StringBuilder builder, char ch)
    {
        switch (ch)
        {
            case 'ł':
            case 'Ł':
                builder.Append('l');
                return;
            case 'ø':
            case 'Ø':
                builder.Append('o');
                return;
            case 'æ':
            case 'Æ':
                builder.Append("ae");
                return;
            case 'œ':
            case 'Œ':
                builder.Append("oe");
                return;
            case 'ß':
            case 'ẞ':
                builder.Append("ss");
                return;
            case 'đ':
            case 'Đ':
                builder.Append('d');
                return;
            default:
                builder.Append(char.ToLowerInvariant(ch));
                return;
        }
    }

    private static ScoreStats? Stats(IReadOnlyDictionary<string, ScoreStats> stats, ScoreEntry score)
    {
        return stats.TryGetValue(score.CanonicalPath, out var item) ? item : null;
    }
}
