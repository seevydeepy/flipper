using Flipper.Core.Cache;
using Flipper.Core.Library;

namespace Flipper.App.Services;

internal sealed class NetworkLibraryCopy(LibraryMirror mirror)
{
    public LibraryMirror Mirror { get; } = mirror;
    private readonly CancellationTokenSource _lifetime = new();
    private LibrarySnapshot? _saved;
    private int _playlistRevision;
    private LibrarySnapshot? _wantedFiles;
    private LibrarySnapshot? _copiedFiles;
    private bool _copying;
    private DateTime _retryFilesAfter;
    private DateTime _retrySaveAfter;

    public void Stop() => _lifetime.Cancel();

    // The library page awaits each metadata save. Copying the PDFs can continue in the reader.
    public async Task RefreshAsync(LibrarySnapshot snapshot, List<Playlist> playlists,
        int playlistRevision, bool playlistsChanged)
    {
        if (playlistsChanged || playlistRevision != _playlistRevision) _saved = null;
        if (_saved is null || !_saved.SameMembership(snapshot))
        {
            if (DateTime.UtcNow < _retrySaveAfter) return;
            var detached = PlaylistBook.Copy(playlists);
            if (!await Task.Run(() => !_lifetime.IsCancellationRequested && Mirror.SaveSnapshot(snapshot, detached)))
            {
                _retrySaveAfter = DateTime.UtcNow.AddSeconds(30);
                return;
            }
            _saved = snapshot;
            _playlistRevision = playlistRevision;
        }

        if (_lifetime.IsCancellationRequested) return;
        if (_wantedFiles is null || !_wantedFiles.SameMembership(snapshot, includeLabels: false))
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
