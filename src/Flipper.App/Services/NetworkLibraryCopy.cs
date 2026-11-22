using Flipper.Core.Cache;
using Flipper.Core.Library;

namespace Flipper.App.Services;

internal sealed class NetworkLibraryCopy(LibraryMirror mirror)
{
    public LibraryMirror Mirror { get; } = mirror;
    private readonly CancellationTokenSource _lifetime = new();
    private LibrarySnapshot? _staged;
    private int _playlistRevision;
    private LibrarySnapshot? _wantedFiles;
    private LibrarySnapshot? _copiedFiles;
    private bool _copying;
    private DateTime _retryFilesAfter;
    private DateTime _retrySaveAfter;

    public void Stop() => _lifetime.Cancel();

    // The library page awaits each metadata save. Copying the PDFs can continue in the reader.
    public async Task RefreshAsync(LibrarySnapshot snapshot, List<Playlist> playlists,
        int playlistRevision, bool playlistsChanged, bool reconnected)
    {
        if (reconnected)
        {
            _retryFilesAfter = default;
            _retrySaveAfter = default;
        }
        if (playlistsChanged || playlistRevision != _playlistRevision) _staged = null;
        if (DateTime.UtcNow < _retrySaveAfter) return;
        var prior = _staged;
        var wanted = _wantedFiles;
        var (ready, filesChanged) = await Task.Run(() =>
        {
            if (_lifetime.IsCancellationRequested) return (false, false);
            if (prior is not null && prior.SameMembership(snapshot)) return (true, false);
            if (!Mirror.StageSnapshot(snapshot, playlists)) return (false, false);
            return (true, wanted is null || !wanted.SameMembership(snapshot, includeLabels: false));
        });

        if (_lifetime.IsCancellationRequested) return;
        if (!ready)
        {
            _retrySaveAfter = DateTime.UtcNow.AddSeconds(30);
            return;
        }
        _staged = snapshot;
        _playlistRevision = playlistRevision;
        if (filesChanged)
        {
            _wantedFiles = snapshot;
            _retryFilesAfter = default;
        }
        if (!_copying && !ReferenceEquals(_copiedFiles, _wantedFiles) && DateTime.UtcNow >= _retryFilesAfter)
        {
            _ = CopyFilesAsync();
        }
    }

    private async Task CopyFilesAsync()
    {
        _copying = true;
        try
        {
            while (_wantedFiles is { } next && !ReferenceEquals(next, _copiedFiles))
            {
                if (!await Task.Run(() => Mirror.CopyScoresAsync(next, _lifetime.Token)))
                {
                    _retryFilesAfter = DateTime.UtcNow.AddSeconds(30);
                    return;
                }
                _copiedFiles = next;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _copying = false; }
    }
}
