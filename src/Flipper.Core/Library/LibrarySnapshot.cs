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
}
