namespace Flipper.Core.Library;

public sealed class SessionScoreFactsOverlay
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OverlayItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private string? _root;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public void SetRoot(string? root)
    {
        lock (_gate)
        {
            if (string.Equals(_root, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _items.Clear();
            _root = root;
        }
    }

    public void Add(ScoreEntry entry, ScoreFacts facts)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_root))
            {
                return;
            }

            _items[Key(entry)] = new OverlayItem(entry.Length, entry.LastWriteUtc, facts);
        }
    }

    public void Remove(ScoreEntry entry)
    {
        lock (_gate)
        {
            _items.Remove(Key(entry));
        }
    }

    public LibrarySnapshot Apply(LibrarySnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_items.Any()
                || !string.Equals(_root, snapshot.RootDisplayPath, StringComparison.OrdinalIgnoreCase))
            {
                return snapshot;
            }

            var present = snapshot.Scores
                .Select(Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _items.Keys.Where(key => !present.Contains(key)).ToArray())
            {
                _items.Remove(key);
            }

            if (_items.Count == 0)
            {
                return snapshot;
            }

            var scores = new ScoreEntry[snapshot.Scores.Count];
            for (var index = 0; index < snapshot.Scores.Count; index++)
            {
                var score = snapshot.Scores[index];
                var key = Key(score);
                if (!_items.TryGetValue(key, out var item))
                {
                    scores[index] = score;
                    continue;
                }

                if (score.HasCatalogEntry
                    || score.Length != item.Length
                    || score.LastWriteUtc != item.LastWriteUtc)
                {
                    _items.Remove(key);
                    scores[index] = score;
                    continue;
                }

                scores[index] = score with
                {
                    Title = item.Facts.Title,
                    Composer = item.Facts.Composer,
                    Subtitle = item.Facts.Subtitle
                };
            }

            return snapshot with { Scores = scores };
        }
    }

    private static string Key(ScoreEntry entry)
    {
        return ScoreCatalog.Key(entry.RelativeFolder, Path.GetFileName(entry.DisplayFullPath));
    }

    private sealed record OverlayItem(long Length, DateTime LastWriteUtc, ScoreFacts Facts);
}
