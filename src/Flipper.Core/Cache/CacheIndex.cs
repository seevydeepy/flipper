namespace Flipper.Core.Cache;

public sealed class CacheIndex
{
    public List<CacheIndexEntry> Entries { get; set; } = new();
}

public sealed class CacheIndexEntry
{
    public string CanonicalPath { get; set; } = string.Empty;
    public string DisplayPath { get; set; } = string.Empty;
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime LastOpenedUtc { get; set; }
    public string FileName { get; set; } = string.Empty;
}
