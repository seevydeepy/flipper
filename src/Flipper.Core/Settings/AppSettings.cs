using Flipper.Core.Library;

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
    public bool SortReversed { get; set; }
    public string? SearchQuery { get; set; }
    public bool ShowFavourites { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public int UiScalePercent { get; set; } = DefaultUiScalePercent;
    public Dictionary<string, ScoreStats> Scores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Playlist> Playlists { get; set; } = new();
    public string? SelectedPlaylistId { get; set; }

    public const int DefaultUiScalePercent = 100;
    public static readonly int[] UiScaleStops = new[] { 75, 100, 125, 150, 200 };

    public void Normalize()
    {
        Scores ??= new Dictionary<string, ScoreStats>(StringComparer.OrdinalIgnoreCase);
        Playlists ??= new List<Playlist>();
        SearchQuery ??= string.Empty;
        UiScalePercent = SnapUiScalePercent(UiScalePercent);
        Playlists = Playlists
            .Where(playlist => !string.IsNullOrWhiteSpace(playlist.Id) && !string.IsNullOrWhiteSpace(playlist.Name))
            .Select(DistinctPaths)
            .ToList();
        if (SelectedPlaylistId is not null
            && Playlists.All(playlist => !string.Equals(playlist.Id, SelectedPlaylistId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedPlaylistId = null;
        }
        if (Sort == SortMode.Favourites)
        {
            ShowFavourites = true;
            Sort = SortMode.Name;
        }
    }

    public static int SnapUiScalePercent(int percent)
    {
        if (percent <= 0)
        {
            return DefaultUiScalePercent;
        }

        var best = DefaultUiScalePercent;
        var bestDist = int.MaxValue;
        foreach (var stop in UiScaleStops)
        {
            var dist = Math.Abs(stop - percent);
            if (dist < bestDist)
            {
                best = stop;
                bestDist = dist;
            }
        }

        return best;
    }

    public static int IndexOfUiScale(int percent)
    {
        return Array.IndexOf(UiScaleStops, SnapUiScalePercent(percent));
    }

    public ScoreStats StatsFor(string canonicalPath)
    {
        if (!Scores.TryGetValue(canonicalPath, out var stats))
        {
            stats = new ScoreStats();
            Scores[canonicalPath] = stats;
        }

        return stats;
    }

    private static Playlist DistinctPaths(Playlist playlist)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        playlist.CanonicalPaths ??= new List<string>();
        playlist.CanonicalPaths = playlist.CanonicalPaths
            .Where(path => !string.IsNullOrEmpty(path) && seen.Add(path))
            .ToList();
        return playlist;
    }
}
