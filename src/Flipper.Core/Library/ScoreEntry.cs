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
    public CatalogProvenance? Provenance { get; init; }

    public ScoreCardText CardText
    {
        get
        {
            var text = ScoreLabel.Card(Title, Subtitle, Composer, DisplayName);
            bool Manual(string field) => Provenance?.Fields.GetValueOrDefault(field)?.Origin == ScoreFieldOrigin.Manual;
            return text with
            {
                Title = Manual("title") && !string.IsNullOrWhiteSpace(Title) ? Title.Trim() : text.Title,
                Subtitle = Manual("subtitle") ? Subtitle?.Trim() ?? string.Empty : text.Subtitle,
                Composer = Manual("composer") ? Composer?.Trim() ?? string.Empty : text.Composer
            };
        }
    }
    public string CardTitle => CardText.Title;
    public string CardSubtitle => CardText.Subtitle;
    public string CardComposer => CardText.Composer;
}
