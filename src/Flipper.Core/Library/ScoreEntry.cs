namespace Flipper.Core.Library;

public sealed record ScoreEntry(
    string DisplayName,
    string RelativeFolder,
    string DisplayFullPath,
    string CanonicalPath,
    long Length,
    DateTime LastWriteUtc,
    string? Title = null,
    string? Composer = null)
{
    public string CardTitle => ScoreLabel.Title(Title, DisplayName);
    public string CardComposer => ScoreLabel.Composer(Composer);
}
