namespace Flipper.Core.Settings;

public enum SortMode
{
    Name,
    Recent,
    MostPlayed,
    Favourites
}

public sealed class ScoreStats
{
    public bool Favourite { get; set; }
    public int PlayCount { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
}

public sealed class AppSettings
{
    public string? LibraryPath { get; set; }
    public string? LastScoreCanonicalPath { get; set; }
    public int LastPageIndex { get; set; }
    public SortMode Sort { get; set; } = SortMode.Name;
    public Dictionary<string, ScoreStats> Scores { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ScoreStats StatsFor(string canonicalPath)
    {
        if (!Scores.TryGetValue(canonicalPath, out var stats))
        {
            stats = new ScoreStats();
            Scores[canonicalPath] = stats;
        }

        return stats;
    }
}
