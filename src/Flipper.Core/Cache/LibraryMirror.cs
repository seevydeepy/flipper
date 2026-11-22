using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flipper.Core.Library;

namespace Flipper.Core.Cache;

public sealed record MirroredLibrary(LibrarySnapshot Snapshot, List<Playlist> Playlists);

public sealed class LibraryMirror
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IgnoreReadOnlyProperties = true };

    private readonly string _libraryRoot;
    private readonly string _libraryPrefix;
    private readonly string _directory;
    private readonly string _snapshotPath;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _copyGate = new(1);
    private bool _loaded;
    private MirroredLibrary? _published;
    private MirroredLibrary? _staged;

    public LibraryMirror(string storageDirectory, string libraryPath)
    {
        _libraryRoot = NormalizePath(libraryPath);
        _libraryPrefix = Path.EndsInDirectorySeparator(_libraryRoot)
            ? _libraryRoot
            : _libraryRoot + Path.DirectorySeparatorChar;
        _directory = Path.Combine(storageDirectory, Hash(_libraryRoot));
        _snapshotPath = Path.Combine(_directory, "library.json");
    }

    // Keep the published view stable between updates. Copy its playlists before editing them.
    public MirroredLibrary? Published => Volatile.Read(ref _published);

    public MirroredLibrary? Load()
    {
        lock (_stateGate)
        {
            return TryLoadPublished() ? _published : null;
        }
    }

    private bool TryLoadPublished()
    {
        if (_loaded) return true;

        try
        {
            using var stream = new FileStream(_snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var library = JsonSerializer.Deserialize<MirroredLibrary>(stream, JsonOptions);
            if (library?.Snapshot is not { Scores: not null } snapshot || library.Playlists is null || !MatchesRoot(snapshot))
            {
                _loaded = true;
                return true;
            }

            foreach (var score in snapshot.Scores)
            {
                RestoreFieldComparer(score.Provenance);
            }

            if (snapshot.CatalogEntries is { } catalog)
            {
                foreach (var entry in catalog.Values)
                {
                    RestoreFieldComparer(entry.Provenance);
                }

                snapshot = snapshot with { CatalogEntries = new Dictionary<string, ScoreCatalogEntry>(catalog, StringComparer.OrdinalIgnoreCase) };
            }

            Volatile.Write(ref _published, library with { Snapshot = snapshot with { RootReachable = false } });
            _loaded = true;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException or ArgumentException)
        {
            _loaded = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool StageSnapshot(LibrarySnapshot snapshot, IReadOnlyList<Playlist> playlists)
    {
        if (!IsComplete(snapshot)) return false;

        lock (_stateGate)
        {
            if (!TryLoadPublished()) return false;

            var next = new MirroredLibrary(snapshot with { RootReachable = false }, PlaylistBook.Copy(playlists));
            // The first library can be browsed while it downloads. Later file changes wait for their copies.
            if ((_published is null || SameFiles(_published.Snapshot, next.Snapshot)) && !Publish(next)) return false;

            _staged = next;
            return true;
        }
    }

    private bool Publish(MirroredLibrary library)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            SidecarReplace.Write(_snapshotPath, JsonSerializer.Serialize(library, JsonOptions));
            Volatile.Write(ref _published, library);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string? FindCopy(ScoreEntry score)
    {
        var path = CopyPath(score);
        if (path is null || File.Exists(path)) return path;

        var previous = Published?.Snapshot.Scores.FirstOrDefault(entry =>
            string.Equals(entry.DisplayFullPath, score.DisplayFullPath, StringComparison.OrdinalIgnoreCase));
        if (previous is null) return null;

        var fallback = CopyPath(previous);
        return File.Exists(fallback) ? fallback : null;
    }

    public async Task<string?> CopyScoreAsync(ScoreEntry score, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = CopyPath(score);
        if (path is null) return null;

        await CopyCurrentScoreAsync(score, path, token).ConfigureAwait(false);
        return FindCopy(score);
    }

    public async Task<bool> CopyScoresAsync(LibrarySnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsComplete(snapshot)) return false;

        var files = snapshot with { RootReachable = false };
        lock (_stateGate)
        {
            if (!TryLoadPublished()) return false;
            if ((_staged ?? _published) is not { } library || !SameFiles(library.Snapshot, files)) return true;
        }

        var expected = new HashSet<string>(StringComparer.Ordinal);
        var copiedAll = true;
        foreach (var score in snapshot.Scores)
        {
            token.ThrowIfCancellationRequested();
            var path = CopyPath(score);
            if (path is null)
            {
                copiedAll = false;
                continue;
            }

            expected.Add(path);
            copiedAll &= await CopyCurrentScoreAsync(score, path, token).ConfigureAwait(false);
        }

        if (!copiedAll) return false;

        await _copyGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                token.ThrowIfCancellationRequested();
                // A newer scan can stage its metadata and supply on-demand copies during this pass.
                if ((_staged ?? _published) is not { } latest || !SameFiles(latest.Snapshot, files)) return true;
                if (!ReferenceEquals(latest, _published) && !Publish(latest)) return false;

                RemoveObsoleteCopies(expected);
                return true;
            }
        }
        finally
        {
            _copyGate.Release();
        }
    }

    private async Task<bool> CopyCurrentScoreAsync(ScoreEntry score, string path, CancellationToken token)
    {
        await _copyGate.WaitAsync(token).ConfigureAwait(false);
        string? temp = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var copy = new FileInfo(path);
            if (copy.Exists && copy.Length == score.Length && copy.LastWriteTimeUtc == score.LastWriteUtc)
            {
                return true;
            }

            Directory.CreateDirectory(_directory);
            using var source = new FileStream(score.DisplayFullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != score.Length || File.GetLastWriteTimeUtc(source.SafeFileHandle) != score.LastWriteUtc)
            {
                return false;
            }

            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(target, token).ConfigureAwait(false);
                if (target.Length != score.Length || File.GetLastWriteTimeUtc(source.SafeFileHandle) != score.LastWriteUtc)
                {
                    return false;
                }
            }

            File.SetLastWriteTimeUtc(temp, score.LastWriteUtc);
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
            temp = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (temp is not null)
            {
                TryDelete(temp);
            }

            _copyGate.Release();
        }
    }

    private bool IsComplete(LibrarySnapshot snapshot) =>
        snapshot.RootReachable && snapshot.SkippedPaths.Count == 0 && MatchesRoot(snapshot);

    private bool MatchesRoot(LibrarySnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.RootDisplayPath) && NormalizePath(snapshot.RootDisplayPath) == _libraryRoot;

    private static bool SameFiles(LibrarySnapshot left, LibrarySnapshot right) =>
        ReferenceEquals(left.Scores, right.Scores) || left.SameMembership(right, includeLabels: false);

    private string? CopyPath(ScoreEntry score)
    {
        // A mapped drive's canonical UNC path can change when the mapping cannot be queried.
        var source = NormalizePath(score.DisplayFullPath);
        // Keep the old version readable until the whole staged library has been published.
        return source.StartsWith(_libraryPrefix, StringComparison.Ordinal)
            ? Path.Combine(_directory, $"{Hash(source)}.{score.Length:x}-{score.LastWriteUtc.Ticks:x}.pdf")
            : null;
    }

    private void RemoveObsoleteCopies(HashSet<string> expected)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.pdf"))
            {
                if (!expected.Contains(path)) TryDelete(path);
            }

            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var path in Directory.EnumerateFiles(_directory, "*.pdf.*.tmp"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) TryDelete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private static void RestoreFieldComparer(CatalogProvenance? provenance)
    {
        if (provenance?.Fields is { } fields)
        {
            provenance.Fields = new Dictionary<string, FieldProvenance>(fields, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
}
