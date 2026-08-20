using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flipper.Core.Library;

public sealed class ScoreFacts
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Composer { get; set; }
}

public sealed class ScoreCatalogCache
{
    private string? _path;
    private long _length;
    private DateTime _lastWriteUtc;
    private IReadOnlyDictionary<string, ScoreFacts> _map =
        new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ScoreFacts> Load(string root)
    {
        var path = Path.Combine(root, ScoreCatalog.FileName);
        if (!File.Exists(path))
        {
            Clear();
            return _map;
        }

        var info = new FileInfo(path);
        if (_path is not null
            && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)
            && _length == info.Length
            && _lastWriteUtc == info.LastWriteTimeUtc)
        {
            return _map;
        }

        _map = ScoreCatalog.Load(root);
        _path = path;
        _length = info.Length;
        _lastWriteUtc = info.LastWriteTimeUtc;
        return _map;
    }

    private void Clear()
    {
        _path = null;
        _length = 0;
        _lastWriteUtc = default;
        _map = new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);
    }
}

public static class ScoreCatalog
{
    public const string FileName = ".flipper-catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IReadOnlyDictionary<string, ScoreFacts> Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, ScoreFacts>>(json, JsonOptions)
                ?? new Dictionary<string, ScoreFacts>();
            var map = new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in raw)
            {
                map[pair.Key.Replace('/', '\\')] = pair.Value;
            }

            return map;
        }
        catch (JsonException)
        {
            return new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static CatalogMergeResult TryMergeMissing(
        string root,
        IReadOnlyDictionary<string, ScoreFacts> generatedFacts,
        CancellationToken cancellationToken = default)
    {
        var candidates = generatedFacts.ToDictionary(
            pair => pair.Key,
            pair => new CatalogMergeCandidate(pair.Value),
            StringComparer.OrdinalIgnoreCase);
        return TryMergeMissing(root, candidates, cancellationToken);
    }

    public static CatalogMergeResult TryMergeMissing(
        string root,
        IReadOnlyDictionary<string, CatalogMergeCandidate> generatedFacts,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var writeLock = CatalogWriteLock.TryAcquire(root, cancellationToken);
            if (writeLock is null)
            {
                return new CatalogMergeResult(CatalogMergeStatus.Busy, 0);
            }

            var path = Path.Combine(root, FileName);
            JsonObject catalog;
            if (File.Exists(path))
            {
                var parsed = JsonNode.Parse(File.ReadAllText(path));
                if (parsed is not JsonObject parsedObject)
                {
                    return new CatalogMergeResult(CatalogMergeStatus.Failed, 0);
                }

                catalog = parsedObject;
            }
            else
            {
                catalog = new JsonObject();
            }

            var inserted = 0;
            var rejected = new List<string>();
            var sourceLocks = new List<FileStream>();
            try
            {
                foreach (var pair in generatedFacts)
                {
                    var key = pair.Key.Replace('/', '\\');
                    if (ContainsKey(catalog, key))
                    {
                        continue;
                    }

                    if (pair.Value.SourcePath is not null)
                    {
                        var sourceLock = TryLockSource(pair.Value);
                        if (sourceLock is null)
                        {
                            rejected.Add(key);
                            continue;
                        }

                        sourceLocks.Add(sourceLock);
                    }

                    catalog[key] = JsonSerializer.SerializeToNode(pair.Value.Facts, SaveOptions);
                    inserted++;
                }

                if (inserted == 0)
                {
                    return new CatalogMergeResult(CatalogMergeStatus.NoChanges, 0, rejected);
                }

                SidecarReplace.Write(path, catalog.ToJsonString(SaveOptions));
                return new CatalogMergeResult(CatalogMergeStatus.Inserted, inserted, rejected);
            }
            finally
            {
                foreach (var sourceLock in sourceLocks)
                {
                    sourceLock.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            return new CatalogMergeResult(CatalogMergeStatus.Failed, 0);
        }
        catch (IOException)
        {
            return new CatalogMergeResult(CatalogMergeStatus.Failed, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return new CatalogMergeResult(CatalogMergeStatus.Failed, 0);
        }
    }

    public static bool TryRewriteRootFolder(string root, string oldName, string newName)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        try
        {
            using var writeLock = CatalogWriteLock.TryAcquire(root, CancellationToken.None);
            if (writeLock is null)
            {
                return false;
            }

            var parsed = JsonNode.Parse(File.ReadAllText(path));
            if (parsed is not JsonObject catalog)
            {
                return false;
            }

            var replacements = new List<(string Old, string Next)>();
            foreach (var key in catalog.Select(pair => pair.Key).ToArray())
            {
                if (LibraryPathRewrite.TryRewriteRelative(key, oldName, newName, out var next)
                    && !string.Equals(next, key, StringComparison.Ordinal))
                {
                    replacements.Add((key, next));
                }
            }

            if (replacements.Count == 0)
            {
                return false;
            }

            foreach (var (oldKey, next) in replacements)
            {
                var value = catalog[oldKey];
                catalog.Remove(oldKey);
                if (!ContainsKey(catalog, next))
                {
                    catalog[next] = value;
                }
            }

            SidecarReplace.Write(path, catalog.ToJsonString(SaveOptions));
            return true;
        }
        catch (JsonException)
        {
            return false;
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

    private static FileStream? TryLockSource(CatalogMergeCandidate candidate)
    {
        try
        {
            var sourceLock = new FileStream(
                candidate.SourcePath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (sourceLock.Length != candidate.Length
                || File.GetLastWriteTimeUtc(candidate.SourcePath!) != candidate.LastWriteUtc)
            {
                sourceLock.Dispose();
                return null;
            }

            return sourceLock;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ContainsKey(JsonObject catalog, string key)
    {
        return catalog.Any(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public static string Key(string relativeFolder, string fileName)
    {
        if (string.IsNullOrEmpty(relativeFolder) || relativeFolder == ".")
        {
            return fileName;
        }

        return relativeFolder.Replace('/', '\\').TrimEnd('\\') + "\\" + fileName;
    }
}

public enum CatalogMergeStatus
{
    Inserted,
    NoChanges,
    Busy,
    Failed
}

public sealed record CatalogMergeCandidate(
    ScoreFacts Facts,
    string? SourcePath = null,
    long Length = 0,
    DateTime LastWriteUtc = default);

public readonly record struct CatalogMergeResult(
    CatalogMergeStatus Status,
    int InsertedCount,
    IReadOnlyList<string>? Rejected = null)
{
    public IReadOnlyList<string> RejectedKeys => Rejected ?? Array.Empty<string>();
}
