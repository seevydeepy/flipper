using Flipper.Core.Library;

namespace Flipper.App.Services;

public sealed class AutomaticScoreCatalog : IDisposable
{
    private const int BatchSize = 8;
    private readonly object _gate = new();
    private readonly PdfScoreFactExtractor _extractor = new();
    private readonly ScoreCatalogCache _catalogCache = new();
    private readonly SessionScoreFactsOverlay _overlay = new();
    private readonly Queue<(ScoreEntry Entry, bool Forced)> _queue = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _forced = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingFacts> _pending = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _cts = new();
    private Task? _worker;
    private string? _root;
    private int _generation;
    private bool _retryPaused;
    private bool _paused = true;
    private bool _disposed;
    private JevReanalysisProgress? _jevProgress;

    public JevReanalysisProgress? JevProgress
    {
        get
        {
            lock (_gate) return _jevProgress is null ? null : _jevProgress with
            {
                Paused = _paused,
                WaitingForSave = _retryPaused
            };
        }
    }

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
            _forced.Clear();
            _pending.Clear();
            _retryPaused = false;
            _jevProgress = null;
            _overlay.SetRoot(root);
        }

        dispose?.Dispose();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _paused = true;
            _cts.Cancel();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _paused = false;
            StartWorkerLocked();
        }
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
                if (ScoreTrash.IsHiddenFolder(score.RelativeFolder))
                {
                    continue;
                }

                var key = CatalogKey(score);
                var stored = snapshot.CatalogEntries?.GetValueOrDefault(key);
                if (score.HasCatalogEntry && (snapshot.CatalogEntries is null
                    || !ScoreCatalog.NeedsReanalysis(stored, score, includeExtractorUpgrade: false)))
                {
                    continue;
                }

                var id = WorkId(score);
                if (_seen.Add(id))
                {
                    _queue.Enqueue((score, false));
                }
            }

            StartWorkerLocked();
        }
    }

    public int QueueJevReanalysis(string root, LibrarySnapshot snapshot)
    {
        SetRoot(root);
        if (!snapshot.RootReachable) return 0;
        var count = 0;
        lock (_gate)
        {
            if (_disposed || !string.Equals(_root, root, StringComparison.OrdinalIgnoreCase)) return 0;
            if (_jevProgress?.IsRunning == true)
            {
                _retryPaused = false;
                StartWorkerLocked();
                return 0;
            }
            var skipped = 0;
            foreach (var score in snapshot.Scores)
            {
                if (ScoreTrash.IsHiddenFolder(score.RelativeFolder)) continue;
                var stored = snapshot.CatalogEntries?.GetValueOrDefault(CatalogKey(score));
                if (stored is null || stored.IsLegacy || new[] { "title", "composer", "subtitle" }.All(
                    field => stored.OriginOf(field) is ScoreFieldOrigin.Manual or ScoreFieldOrigin.Legacy))
                {
                    skipped++;
                    continue;
                }
                var id = WorkId(score);
                if (!_forced.Add(id)) continue;
                count++;
                _seen.Add(id);
                _queue.Enqueue((score, true));
            }
            _jevProgress = new JevReanalysisProgress(count, skipped);
            _retryPaused = false;
            StartWorkerLocked();
        }
        return count;
    }

    private void StartWorkerLocked()
    {
        if (_disposed
            || _paused
            || _retryPaused
            || string.IsNullOrWhiteSpace(_root)
            || (_queue.Count == 0 && _pending.Count == 0)
            || (_worker is not null && !_worker.IsCompleted))
        {
            return;
        }

        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
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
        var lastFlush = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!await FlushPendingAsync(root, generation, token))
            {
                return;
            }

            while (!token.IsCancellationRequested)
            {
                ScoreEntry? entry;
                var forced = false;
                lock (_gate)
                {
                    if (!IsCurrent(root, generation) || _queue.Count == 0)
                    {
                        entry = null;
                    }
                    else
                    {
                        (entry, forced) = _queue.Dequeue();
                        // A deliberate request owns its own extraction, even if automatic
                        // work for this score was already queued or awaiting a save.
                        if (!forced && _forced.Contains(WorkId(entry))) continue;
                        if (forced && _jevProgress is not null)
                            _jevProgress = _jevProgress with { CurrentScore = entry.DisplayName };
                    }
                }

                if (entry is null)
                {
                    await FlushPendingAsync(root, generation, token);
                    return;
                }

                if (!IsStable(entry))
                {
                    DiscardForRetry(root, generation, entry, forced, "The PDF changed or is no longer available.");
                    continue;
                }

                // A scan may predate our last flush or a manual correction.
                // Check the cached on-disk state on this worker before doing PDF work.
                ScoreCatalogEntry? stored;
                try
                {
                    stored = _catalogCache.LoadEntries(root).GetValueOrDefault(CatalogKey(entry));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiscardForRetry(root, generation, entry, forced, "Could not read the catalogue.");
                    lock (_gate)
                    {
                        if (IsCurrent(root, generation)) _retryPaused = true;
                    }
                    return;
                }
                if (!forced && !ScoreCatalog.NeedsReanalysis(stored, entry, includeExtractorUpgrade: false))
                {
                    lock (_gate)
                    {
                        if (IsCurrent(root, generation)) _seen.Remove(WorkId(entry));
                    }
                    continue;
                }

                ScoreExtractionResult result;
                try
                {
                    result = await _extractor.ExtractWithStatusAsync(entry, token);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                    {
                        if (IsCurrent(root, generation)) _queue.Enqueue((entry, forced));
                    }
                    throw;
                }
                catch (Exception)
                {
                    DiscardForRetry(root, generation, entry, forced, "Could not analyse the PDF. Try again.");
                    continue;
                }
                var facts = result.Facts;
                if (forced && !result.JevEvaluated)
                {
                    DiscardForRetry(root, generation, entry, forced, result.JevError
                        ?? (result.Status == ExtractionStatus.FailedTransient ? "Could not extract score text." : null));
                    continue;
                }
                if (!IsStable(entry))
                {
                    DiscardForRetry(root, generation, entry, forced, "The PDF changed during analysis. Try again.");
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
                    var provenance = CatalogProvenance.ForGenerated(
                        ScoreFacts.CurrentExtractorVersion,
                        entry.Length,
                        entry.LastWriteUtc,
                        facts,
                        result.Status,
                        explanation: result.Detail);
                    if (result.Decision is { } decision)
                    {
                        provenance.Field("title").Explanation = decision.TitleEvidence;
                        provenance.Field("composer").Explanation = decision.ComposerEvidence;
                        provenance.Field("title").Confidence = decision.TitleProbability;
                        provenance.Field("composer").Confidence = decision.ComposerProbability;
                    }
                    var refreshFields = (result.JevTitleEvaluated
                            ? ScoreRefreshFields.Title | ScoreRefreshFields.Subtitle : ScoreRefreshFields.None)
                        | (result.JevComposerEvaluated ? ScoreRefreshFields.Composer : ScoreRefreshFields.None);
                    _pending[key] = new PendingFacts(entry, facts, provenance, forced,
                        forced ? refreshFields : ScoreRefreshFields.All,
                        result.JevTitleEvaluated && facts.Title is not null
                            || result.JevComposerEvaluated && facts.Composer is not null);
                    _overlay.Add(entry, facts);
                    flush = _pending.Count >= BatchSize || lastFlush.Elapsed >= TimeSpan.FromSeconds(2);
                }

                if (flush)
                {
                    if (!await FlushPendingAsync(root, generation, token)) return;
                    lastFlush.Restart();
                }

                // Yield between scores; never queue several PDF/OCR jobs at once.
                await Task.Delay(50, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_gate)
            {
                if (source != _cts || _disposed) source.Dispose();
                _worker = null;
                StartWorkerLocked();
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

            batch = new Dictionary<string, PendingFacts>(_pending, StringComparer.OrdinalIgnoreCase);
        }

        // File access must not hold the lock used by UI scheduling and pause.
        foreach (var pair in batch.Where(pair => !IsStable(pair.Value.Entry)).ToArray())
        {
            batch.Remove(pair.Key);
            DiscardForRetry(root, generation, pair.Value.Entry, pair.Value.ForceRefresh,
                "The PDF changed before its result could be saved.");
        }
        if (batch.Count == 0) return true;

        var generated = batch.ToDictionary(
            pair => pair.Key,
            pair => new CatalogMergeCandidate(
                pair.Value.Facts,
                pair.Value.Entry.DisplayFullPath,
                pair.Value.Entry.Length,
                pair.Value.Entry.LastWriteUtc,
                pair.Value.Provenance,
                pair.Value.ForceRefresh,
                pair.Value.RefreshFields),
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

            Changed?.Invoke(); // Publish the session overlay once for this batch.
            return false;
        }

        lock (_gate)
        {
            if (!IsCurrent(root, generation))
            {
                return false;
            }

            _retryPaused = false;
            var rejected = result.RejectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in batch)
            {
                _pending.Remove(pair.Key);
                _seen.Remove(WorkId(pair.Value.Entry));
                if (pair.Value.ForceRefresh)
                    CompleteJevLocked(pair.Value.Entry, pair.Value.Matched,
                        rejected.Contains(pair.Key) ? "The PDF changed before its result could be saved." : null);
                if (rejected.Contains(pair.Key))
                {
                    _overlay.Remove(pair.Value.Entry);
                }
            }
        }

        Changed?.Invoke();
        return true;
    }

    private void DiscardForRetry(string root, int generation, ScoreEntry entry, bool forced, string? error)
    {
        lock (_gate)
        {
            if (!IsCurrent(root, generation))
            {
                return;
            }

            _seen.Remove(WorkId(entry));
            if (forced) CompleteJevLocked(entry, false, error);
            _pending.Remove(CatalogKey(entry));
            _overlay.Remove(entry);
        }
    }

    private void CompleteJevLocked(ScoreEntry entry, bool matched, string? error)
    {
        if (!_forced.Remove(WorkId(entry)) || _jevProgress is null) return;
        _jevProgress = _jevProgress with
        {
            Completed = _jevProgress.Completed + 1,
            Matched = _jevProgress.Matched + (error is null && matched ? 1 : 0),
            Unmatched = _jevProgress.Unmatched + (error is null && !matched ? 1 : 0),
            Failed = _jevProgress.Failed + (error is null ? 0 : 1),
            CurrentScore = null,
            LastIssue = error is null ? _jevProgress.LastIssue : $"{entry.DisplayName}: {error}"
        };
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
            if (_worker is null || _worker.IsCompleted) _cts.Dispose();
            _queue.Clear();
            _seen.Clear();
            _forced.Clear();
            _pending.Clear();
            _overlay.SetRoot(null);
        }
    }

    private sealed record PendingFacts(ScoreEntry Entry, ScoreFacts Facts, CatalogProvenance Provenance,
        bool ForceRefresh, ScoreRefreshFields RefreshFields, bool Matched);
}

public sealed record JevReanalysisProgress(int Total, int Skipped, int Completed = 0,
    int Matched = 0, int Unmatched = 0, int Failed = 0, string? CurrentScore = null,
    string? LastIssue = null, bool Paused = false, bool WaitingForSave = false)
{
    public bool IsRunning => Completed < Total;
}
