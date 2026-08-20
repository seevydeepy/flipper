using Flipper.Core.Library;

namespace Flipper.App.Services;

public sealed class AutomaticScoreCatalog : IDisposable
{
    private const int BatchSize = 8;
    private readonly object _gate = new();
    private readonly PdfScoreFactExtractor _extractor = new();
    private readonly SessionScoreFactsOverlay _overlay = new();
    private readonly Queue<ScoreEntry> _queue = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingFacts> _pending = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _cts = new();
    private Task? _worker;
    private string? _root;
    private int _generation;
    private bool _retryPaused;
    private bool _disposed;

    public event Action? Changed;

    public LibrarySnapshot ApplyOverlay(LibrarySnapshot snapshot) => _overlay.Apply(snapshot);

    public void SetRoot(string? root)
    {
        CancellationTokenSource? dispose = null;
        lock (_gate)
        {
            if (_disposed || string.Equals(_root, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var previous = _cts;
            previous.Cancel();
            if (_worker is null || _worker.IsCompleted)
            {
                dispose = previous;
            }

            _cts = new CancellationTokenSource();
            _root = root;
            _generation++;
            _queue.Clear();
            _seen.Clear();
            _pending.Clear();
            _retryPaused = false;
            _worker = null;
            _overlay.SetRoot(root);
        }

        dispose?.Dispose();
    }

    public void Schedule(string root, LibrarySnapshot snapshot)
    {
        SetRoot(root);
        if (!snapshot.RootReachable)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || !string.Equals(_root, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _retryPaused = false;
            foreach (var score in snapshot.Scores)
            {
                if (score.HasCatalogEntry)
                {
                    _seen.Remove(WorkId(score));
                    continue;
                }

                if (ScoreTrash.IsHiddenFolder(score.RelativeFolder))
                {
                    continue;
                }

                var id = WorkId(score);
                if (_seen.Add(id))
                {
                    _queue.Enqueue(score);
                }
            }

            StartWorkerLocked();
        }
    }

    private void StartWorkerLocked()
    {
        if (_disposed
            || _retryPaused
            || string.IsNullOrWhiteSpace(_root)
            || (_queue.Count == 0 && _pending.Count == 0)
            || (_worker is not null && !_worker.IsCompleted))
        {
            return;
        }

        var root = _root;
        var generation = _generation;
        var source = _cts;
        _worker = Task.Run(() => ProcessLoopAsync(root, generation, source));
    }

    private async Task ProcessLoopAsync(
        string root,
        int generation,
        CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            if (!await FlushPendingAsync(root, generation, token))
            {
                return;
            }

            while (!token.IsCancellationRequested)
            {
                ScoreEntry? entry;
                lock (_gate)
                {
                    if (!IsCurrent(root, generation) || _queue.Count == 0)
                    {
                        entry = null;
                    }
                    else
                    {
                        entry = _queue.Dequeue();
                    }
                }

                if (entry is null)
                {
                    await FlushPendingAsync(root, generation, token);
                    return;
                }

                if (!IsStable(entry))
                {
                    continue;
                }

                var facts = await _extractor.ExtractAsync(entry, token);
                if (!IsStable(entry))
                {
                    continue;
                }

                var flush = false;
                lock (_gate)
                {
                    if (!IsCurrent(root, generation))
                    {
                        return;
                    }

                    var key = CatalogKey(entry);
                    _pending[key] = new PendingFacts(entry, facts);
                    _overlay.Add(entry, facts);
                    flush = _pending.Count >= BatchSize;
                }

                Changed?.Invoke();
                if (flush && !await FlushPendingAsync(root, generation, token))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_gate)
            {
                if (generation != _generation)
                {
                    source.Dispose();
                }
                else
                {
                    _worker = null;
                    StartWorkerLocked();
                }
            }
        }
    }

    private async Task<bool> FlushPendingAsync(
        string root,
        int generation,
        CancellationToken token)
    {
        Dictionary<string, PendingFacts> batch;
        lock (_gate)
        {
            if (!IsCurrent(root, generation) || _pending.Count == 0)
            {
                return true;
            }

            var stale = _pending
                .Where(pair => !IsStable(pair.Value.Entry))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in stale)
            {
                _pending.Remove(key);
            }

            if (_pending.Count == 0)
            {
                return true;
            }

            batch = new Dictionary<string, PendingFacts>(_pending, StringComparer.OrdinalIgnoreCase);
        }

        var generated = batch.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Facts,
            StringComparer.OrdinalIgnoreCase);
        var result = await Task.Run(
            () => ScoreCatalog.TryMergeMissing(root, generated, token),
            token);
        if (result.Status is CatalogMergeStatus.Busy or CatalogMergeStatus.Failed)
        {
            lock (_gate)
            {
                if (IsCurrent(root, generation))
                {
                    _retryPaused = true;
                }
            }

            return false;
        }

        lock (_gate)
        {
            if (!IsCurrent(root, generation))
            {
                return false;
            }

            _retryPaused = false;

            foreach (var key in batch.Keys)
            {
                _pending.Remove(key);
            }
        }

        Changed?.Invoke();
        return true;
    }

    private bool IsCurrent(string root, int generation)
    {
        return !_disposed
            && generation == _generation
            && string.Equals(root, _root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStable(ScoreEntry entry)
    {
        try
        {
            var info = new FileInfo(entry.DisplayFullPath);
            return info.Exists
                && info.Length == entry.Length
                && info.LastWriteTimeUtc == entry.LastWriteUtc;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string CatalogKey(ScoreEntry entry)
    {
        return ScoreCatalog.Key(entry.RelativeFolder, Path.GetFileName(entry.DisplayFullPath));
    }

    private static string WorkId(ScoreEntry entry)
    {
        return $"{CatalogKey(entry)}|{entry.Length}|{entry.LastWriteUtc.Ticks}";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            _cts.Cancel();
            _queue.Clear();
            _seen.Clear();
            _pending.Clear();
            _overlay.SetRoot(null);
        }
    }

    private sealed record PendingFacts(ScoreEntry Entry, ScoreFacts Facts);
}
