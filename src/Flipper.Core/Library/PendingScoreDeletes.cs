namespace Flipper.Core.Library;

public sealed record PendingScoreDelete(
    Guid Id,
    string CanonicalPath,
    string DisplayFullPath,
    long Length,
    DateTime LastWriteUtc);

public sealed record PendingDeleteCommit(
    Guid Id,
    string CanonicalPath,
    string DisplayFullPath,
    long Length,
    DateTime LastWriteUtc,
    bool Failed);

public sealed class PendingScoreDeletes
{
    private readonly Dictionary<string, PendingScoreDelete> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, PendingScoreDelete> _byId = new();

    public IReadOnlySet<string> CanonicalPaths => _byPath.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public PendingScoreDelete Arm(ScoreEntry entry, out bool created)
    {
        if (_byPath.TryGetValue(entry.CanonicalPath, out var existing))
        {
            created = false;
            return existing;
        }

        var item = new PendingScoreDelete(
            Guid.NewGuid(),
            entry.CanonicalPath,
            entry.DisplayFullPath,
            entry.Length,
            entry.LastWriteUtc);
        _byPath[item.CanonicalPath] = item;
        _byId[item.Id] = item;
        created = true;
        return item;
    }

    public bool Contains(string canonicalPath) => _byPath.ContainsKey(canonicalPath);

    public bool TryGet(Guid id, out PendingScoreDelete item) => _byId.TryGetValue(id, out item!);

    public bool TryUndo(Guid id)
    {
        if (!_byId.TryGetValue(id, out var item))
        {
            return false;
        }

        _byId.Remove(id);
        _byPath.Remove(item.CanonicalPath);
        return true;
    }

    public PendingDeleteCommit? Commit(Guid id)
    {
        if (!_byId.TryGetValue(id, out var item))
        {
            return null;
        }

        var failed = false;
        try
        {
            if (File.Exists(item.DisplayFullPath))
            {
                File.Delete(item.DisplayFullPath);
            }
        }
        catch (IOException)
        {
            failed = true;
        }
        catch (UnauthorizedAccessException)
        {
            failed = true;
        }

        if (!failed)
        {
            _byId.Remove(id);
            _byPath.Remove(item.CanonicalPath);
        }

        return new PendingDeleteCommit(
            item.Id,
            item.CanonicalPath,
            item.DisplayFullPath,
            item.Length,
            item.LastWriteUtc,
            failed);
    }
}
