namespace Flipper.Core.Library;

public sealed record ScoreEntry(
    string DisplayName,
    string RelativeFolder,
    string DisplayFullPath,
    string CanonicalPath,
    long Length,
    DateTime LastWriteUtc);
