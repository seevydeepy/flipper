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
    private readonly SemaphoreSlim _copyGate = new(1);
    private LibrarySnapshot? _savedSnapshot;

    public LibraryMirror(string storageDirectory, string libraryPath)
    {
        _libraryRoot = NormalizePath(libraryPath);
        _libraryPrefix = Path.EndsInDirectorySeparator(_libraryRoot)
            ? _libraryRoot
            : _libraryRoot + Path.DirectorySeparatorChar;
        _directory = Path.Combine(storageDirectory, Hash(_libraryRoot));
        _snapshotPath = Path.Combine(_directory, "library.json");
    }

    public MirroredLibrary? Load()
    {
        try
        {
            using var stream = new FileStream(_snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var library = JsonSerializer.Deserialize<MirroredLibrary>(stream, JsonOptions);
            if (library?.Snapshot is not { Scores: not null } snapshot || library.Playlists is null || !MatchesRoot(snapshot))
            {
                return null;
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

            return library with { Snapshot = snapshot with { RootReachable = false } };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public bool SaveSnapshot(LibrarySnapshot snapshot, IReadOnlyList<Playlist> playlists)
    {
        if (!IsComplete(snapshot)) return false;

        try
        {
            Directory.CreateDirectory(_directory);
            var library = new MirroredLibrary(snapshot, playlists.ToList());
            SidecarReplace.Write(_snapshotPath, JsonSerializer.Serialize(library, JsonOptions));
            Volatile.Write(ref _savedSnapshot, snapshot);
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
        return File.Exists(path) ? path : null;
    }

    public async Task<string?> CopyScoreAsync(ScoreEntry score, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = CopyPath(score);
        if (path is null) return null;

        await CopyCurrentScoreAsync(score, path, token).ConfigureAwait(false);
        return File.Exists(path) ? path : null;
    }

    public async Task<bool> CopyScoresAsync(LibrarySnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsComplete(snapshot)) return false;

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
            // A newer scan can publish its library and supply on-demand copies during this pass.
            var saved = Volatile.Read(ref _savedSnapshot);
            if (saved is not null && !ReferenceEquals(snapshot, saved) && !snapshot.SameMembership(saved, includeLabels: false))
            {
                return true;
            }

            if (!Directory.Exists(_directory)) return true;

            foreach (var path in Directory.EnumerateFiles(_directory, "*.pdf"))
            {
                token.ThrowIfCancellationRequested();
                if (!expected.Contains(path)) File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            _copyGate.Release();
        }
    }

    private bool IsComplete(LibrarySnapshot snapshot) =>
        snapshot.RootReachable && snapshot.SkippedPaths.Count == 0 && MatchesRoot(snapshot);

    private bool MatchesRoot(LibrarySnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.RootDisplayPath) && NormalizePath(snapshot.RootDisplayPath) == _libraryRoot;

    private string? CopyPath(ScoreEntry score)
    {
        // A mapped drive's canonical UNC path can change when the mapping cannot be queried.
        var source = NormalizePath(score.DisplayFullPath);
        return source.StartsWith(_libraryPrefix, StringComparison.Ordinal)
            ? Path.Combine(_directory, Hash(source) + ".pdf")
            : null;
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
