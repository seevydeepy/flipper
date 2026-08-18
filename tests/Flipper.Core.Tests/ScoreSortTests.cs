using Flipper.Core.Library;
using Flipper.Core.Settings;

namespace Flipper.Core.Tests;

public sealed class ScoreSortTests
{
    private static readonly ScoreEntry Air = new("Air", "Bach", @"C:\lib\Air.pdf", "air", 1, DateTime.UtcNow);
    private static readonly ScoreEntry Nocturne = new("Nocturne", "Chopin", @"C:\lib\Nocturne.pdf", "noc", 1, DateTime.UtcNow);
    private static readonly ScoreEntry Prelude = new("Prelude", "Bach", @"C:\lib\Prelude.pdf", "pre", 1, DateTime.UtcNow);

    [Fact]
    public void Sort_Name_IsAlphabetical()
    {
        var result = ScoreSearch.Sort([Prelude, Air, Nocturne], SortMode.Name, new Dictionary<string, ScoreStats>());
        Assert.Equal(["Air", "Nocturne", "Prelude"], result.Select(score => score.DisplayName));
    }

    [Fact]
    public void Sort_RecentAndMostPlayed_UseStats()
    {
        var stats = new Dictionary<string, ScoreStats>(StringComparer.OrdinalIgnoreCase)
        {
            ["air"] = new() { PlayCount = 1, LastPlayedUtc = DateTime.UtcNow.AddDays(-2) },
            ["noc"] = new() { PlayCount = 9, LastPlayedUtc = DateTime.UtcNow.AddDays(-1) },
            ["pre"] = new() { PlayCount = 3, LastPlayedUtc = DateTime.UtcNow, Favourite = true }
        };

        var recent = ScoreSearch.Sort([Air, Nocturne, Prelude], SortMode.Recent, stats);
        Assert.Equal("Prelude", recent[0].DisplayName);

        var most = ScoreSearch.Sort([Air, Nocturne, Prelude], SortMode.MostPlayed, stats);
        Assert.Equal("Nocturne", most[0].DisplayName);

        var fav = ScoreSearch.Sort([Air, Nocturne, Prelude], SortMode.Favourites, stats);
        Assert.Equal("Prelude", Assert.Single(fav).DisplayName);
    }
}
