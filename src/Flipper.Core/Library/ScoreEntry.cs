namespace Flipper.Core.Library;

public sealed record ScoreEntry(
    string DisplayName,
    string RelativeFolder,
    string DisplayFullPath,
    string CanonicalPath,
    long Length,
    DateTime LastWriteUtc,
    string? Title = null,
    string? Composer = null,
    string? Subtitle = null,
    bool HasCatalogEntry = false)
{
    public ScoreCardText CardText => ScoreLabel.Card(Title, Subtitle, Composer, DisplayName);
    public string CardTitle => CardText.Title;
    public string CardSubtitle => CardText.Subtitle;
    public string CardComposer => CardText.Composer;
}
