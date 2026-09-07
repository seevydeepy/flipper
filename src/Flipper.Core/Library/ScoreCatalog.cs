using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flipper.Core.Library;

public sealed class ScoreFacts
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Composer { get; set; }

    /// <summary>
    /// Current extractor version. Bump when inference behaviour changes so
    /// existing entries become eligible for deliberate reanalysis.
    /// Mirrors <see cref="ScoreFactInference.InferenceVersion"/>.
    /// </summary>
    public const int CurrentExtractorVersion = ScoreFactInference.InferenceVersion;
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
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

    /// <summary>
    /// Merge generated facts into the catalog lifecycle-aware:
    /// insert unknown keys, refresh stale generated fields on source change or
    /// extractor upgrade, and leave manual/legacy values untouched.
    /// </summary>
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
            var updated = 0;
            var rejected = new List<string>();
            var sourceLocks = new List<FileStream>();
            try
            {
                foreach (var pair in generatedFacts)
                {
                    var key = pair.Key.Replace('/', '\\');
                    if (TryFindKey(catalog, key, out var actualKey))
                    {
                        // Existing entry: refresh only stale generated fields.
                        // Manual and legacy values are preserved unconditionally.
                        // Candidates without a source fingerprint carry no evidence
                        // of change; without provenance they cannot refresh.
                        if (pair.Value.SourcePath is null && pair.Value.Provenance is null)
                        {
                            continue;
                        }

                        if (ApplyRefresh(catalog, actualKey!, pair.Value))
                        {
                            updated++;
                        }

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

                    catalog[key] = CatalogProvenanceJson.ToNode(pair.Value.Facts, pair.Value.Provenance);
                    inserted++;
                }

                if (inserted == 0 && updated == 0)
                {
                    return new CatalogMergeResult(CatalogMergeStatus.NoChanges, 0, rejected);
                }

                SidecarReplace.Write(path, catalog.ToJsonString(SaveOptions));
                return new CatalogMergeResult(CatalogMergeStatus.Inserted, inserted + updated, rejected);
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

    /// <summary>
    /// Whether a catalogued score should be reanalysed: the source changed
    /// since extraction, the extractor moved on, an unresolved field or a
    /// transient failure's backoff expired. Manual-only and legacy entries
    /// are never scheduled here; legacy review goes through the dry-run
    /// proposal path instead.
    /// </summary>
    public static bool NeedsReanalysis(
        string root,
        ScoreEntry score,
        DateTime? nowUtc = null)
    {
        var key = Key(score.RelativeFolder, Path.GetFileName(score.DisplayFullPath)).Replace('/', '\\');
        var entry = LoadEntry(root, key);
        nowUtc ??= DateTime.UtcNow;
        if (entry is null)
        {
            return true;
        }

        if (entry.IsLegacy || entry.AllFieldsManual())
        {
            return false;
        }

        var provenance = entry.Provenance!;
        if (!provenance.MatchesSource(score.Length, score.LastWriteUtc))
        {
            return true;
        }

        if (provenance.ExtractorVersion < ScoreFacts.CurrentExtractorVersion)
        {
            return true;
        }

        if (provenance.Status == ExtractionStatus.FailedTransient
            && (!provenance.NextRetryUtc.HasValue || provenance.NextRetryUtc.Value <= nowUtc.Value))
        {
            return true;
        }

        return entry.OriginOf("title") == ScoreFieldOrigin.Unresolved
            || entry.OriginOf("composer") == ScoreFieldOrigin.Unresolved;
    }

    /// <summary>
    /// Read one raw entry (facts + provenance) without disturbing unknown fields.
    /// Null when the key is absent.
    /// </summary>
    public static ScoreCatalogEntry? LoadEntry(string root, string key)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var parsed = JsonNode.Parse(File.ReadAllText(path));
            if (parsed is not JsonObject catalog)
            {
                return null;
            }

            foreach (var pair in catalog)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return CatalogProvenanceJson.ParseEntry(pair.Value);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Record a manual correction (or a deliberate clearing: pass null to leave a
    /// field explicitly empty). Manual fields are never overwritten by merges.
    /// Creates the entry when absent. Unknown sibling fields are preserved.
    /// </summary>
    public static bool TryCorrect(
        string root,
        string key,
        string? title,
        string? composer,
        string? subtitle,
        bool clearTitle = false,
        bool clearComposer = false,
        bool clearSubtitle = false)
    {
        try
        {
            using var writeLock = CatalogWriteLock.TryAcquire(root, CancellationToken.None);
            if (writeLock is null)
            {
                return false;
            }

            var path = Path.Combine(root, FileName);
            JsonObject catalog;
            if (File.Exists(path))
            {
                var parsed = JsonNode.Parse(File.ReadAllText(path));
                if (parsed is not JsonObject parsedObject)
                {
                    return false;
                }

                catalog = parsedObject;
            }
            else
            {
                catalog = new JsonObject();
            }

            var stored = TryFindKey(catalog, key.Replace('/', '\\'), out var actualKey) && actualKey is not null
                ? CatalogProvenanceJson.ParseEntry(catalog[actualKey])
                : new ScoreCatalogEntry();
            var provenance = stored.Provenance ?? new CatalogProvenance
            {
                ExtractorVersion = ScoreFacts.CurrentExtractorVersion,
                Status = ExtractionStatus.Partial
            };
            ApplyCorrection(stored, provenance, "title", title, clearTitle);
            ApplyCorrection(stored, provenance, "composer", composer, clearComposer);
            ApplyCorrection(stored, provenance, "subtitle", subtitle, clearSubtitle);
            catalog[actualKey ?? key.Replace('/', '\\')] =
                CatalogProvenanceJson.ToNode(stored.Facts, provenance);
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

    private static void ApplyCorrection(
        ScoreCatalogEntry stored,
        CatalogProvenance provenance,
        string field,
        string? value,
        bool clear)
    {
        if (!clear && value is null)
        {
            return;
        }

        var trimmed = clear ? null : value?.Trim();
        switch (field)
        {
            case "title": stored.Facts.Title = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed; break;
            case "composer": stored.Facts.Composer = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed; break;
            case "subtitle": stored.Facts.Subtitle = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed; break;
            default: return;
        }

        var target = provenance.Field(field);
        target.Origin = ScoreFieldOrigin.Manual;
        target.Explanation = "manual correction";
    }

    /// <summary>
    /// Preview what a reanalysis merge would change, without writing anything.
    /// Manual and legacy values report as preserved; only stale generated fields
    /// count as changes.
    /// </summary>
    public static ReanalysisPreview PreviewReanalysis(
        string root,
        IReadOnlyDictionary<string, CatalogMergeCandidate> generatedFacts)
    {
        var changes = new List<ReanalysisChange>();
        var preserved = new List<string>();
        foreach (var pair in generatedFacts)
        {
            var key = pair.Key.Replace('/', '\\');
            var stored = LoadEntry(root, key);
            if (stored is null)
            {
                changes.Add(new ReanalysisChange(key, "insert", null, pair.Value.Facts));
                continue;
            }

            if (stored.IsLegacy)
            {
                preserved.Add(key);
                continue;
            }

            var before = new ScoreFacts
            {
                Title = stored.Facts.Title,
                Composer = stored.Facts.Composer,
                Subtitle = stored.Facts.Subtitle
            };
            FieldDiff(stored, "title", pair.Value.Facts.Title, out var titleTo, out var titleKeep);
            FieldDiff(stored, "composer", pair.Value.Facts.Composer, out var composerTo, out var composerKeep);
            FieldDiff(stored, "subtitle", pair.Value.Facts.Subtitle, out var subtitleTo, out _);
            if (titleTo is not null || composerTo is not null || subtitleTo is not null)
            {
                changes.Add(new ReanalysisChange(
                    key,
                    "refresh",
                    before,
                    new ScoreFacts
                    {
                        Title = titleTo ?? before.Title,
                        Composer = composerTo ?? before.Composer,
                        Subtitle = subtitleTo ?? before.Subtitle
                    }));
            }

            if (titleKeep || composerKeep)
            {
                preserved.Add(key);
            }
        }

        return new ReanalysisPreview(changes, preserved);
    }

    private static void FieldDiff(
        ScoreCatalogEntry stored,
        string field,
        string? incoming,
        out string? updatedTo,
        out bool keptManual)
    {
        keptManual = stored.OriginOf(field) == ScoreFieldOrigin.Manual;
        updatedTo = null;
        if (keptManual || string.IsNullOrWhiteSpace(incoming))
        {
            return;
        }

        var current = field switch
        {
            "title" => stored.Facts.Title,
            "composer" => stored.Facts.Composer,
            _ => stored.Facts.Subtitle
        };
        if (!string.Equals(current, incoming, StringComparison.Ordinal))
        {
            updatedTo = incoming;
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
        return TryFindKey(catalog, key, out _);
    }

    private static bool TryFindKey(JsonObject catalog, string key, out string? actualKey)
    {
        foreach (var pair in catalog)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                actualKey = pair.Key;
                return true;
            }
        }

        actualKey = null;
        return false;
    }

    /// <summary>
    /// Refresh an existing entry from regenerated facts. Returns true when the
    /// stored JSON changed. Rules:
    /// - Legacy entries (no provenance): never overwritten here; gazetteer-style
    ///   reviewable proposals are produced by a separate dry-run path.
    /// - Manual fields (including deliberately empty): always preserved.
    /// - Generated fields: refresh on source change or extractor upgrade, and
    ///   improve independently per field (title and composer never block each other).
    /// - Unresolved stays unresolved unless the new value is non-blank.
    /// </summary>
    private static bool ApplyRefresh(JsonObject catalog, string actualKey, CatalogMergeCandidate candidate)
    {
        var stored = CatalogProvenanceJson.ParseEntry(catalog[actualKey]);
        if (stored.IsLegacy)
        {
            return false;
        }

        var provenance = stored.Provenance!;
        var incoming = candidate.Provenance;
        var sourceChanged = candidate.SourcePath is not null
            && (!provenance.MatchesSource(candidate.Length, candidate.LastWriteUtc)
                || incoming?.MatchesSource(candidate.Length, candidate.LastWriteUtc) == true
                    && (incoming.SourceLength != provenance.SourceLength
                        || incoming.SourceLastWriteUtc != provenance.SourceLastWriteUtc));
        var extractorUpgraded = incoming is not null
            && incoming.ExtractorVersion > provenance.ExtractorVersion;
        var retryDue = provenance.Status == ExtractionStatus.FailedTransient
            && (!provenance.NextRetryUtc.HasValue || provenance.NextRetryUtc.Value <= DateTime.UtcNow);
        if (!sourceChanged && !extractorUpgraded && !retryDue)
        {
            return false;
        }

        var before = catalog[actualKey]?.ToJsonString();
        CopyField(stored, provenance, "title", candidate.Facts.Title, incoming);
        CopyField(stored, provenance, "composer", candidate.Facts.Composer, incoming);
        CopyField(stored, provenance, "subtitle", candidate.Facts.Subtitle, incoming);
        provenance.ExtractorVersion = Math.Max(
            provenance.ExtractorVersion, incoming?.ExtractorVersion ?? provenance.ExtractorVersion);
        if (candidate.SourcePath is not null)
        {
            provenance.SourceLength = candidate.Length;
            provenance.SourceLastWriteUtc = candidate.LastWriteUtc;
        }

        provenance.Status = incoming?.Status ?? provenance.Status;
        provenance.Attempts = incoming?.Attempts ?? (provenance.Attempts + 1);
        provenance.NextRetryUtc = incoming?.NextRetryUtc;
        catalog[actualKey] = CatalogProvenanceJson.ToNode(stored.Facts, provenance);
        return before != catalog[actualKey]?.ToJsonString();
    }

    private static void CopyField(
        ScoreCatalogEntry stored,
        CatalogProvenance provenance,
        string field,
        string? incoming,
        CatalogProvenance? incomingProvenance)
    {
        if (provenance.Fields.TryGetValue(field, out var current) && current.Origin == ScoreFieldOrigin.Manual)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(incoming))
        {
            return;
        }

        switch (field)
        {
            case "title": stored.Facts.Title = incoming; break;
            case "composer": stored.Facts.Composer = incoming; break;
            case "subtitle": stored.Facts.Subtitle = incoming; break;
            default: return;
        }

        var target = provenance.Field(field);
        target.Origin = ScoreFieldOrigin.Generated;
        if (incomingProvenance?.Fields.TryGetValue(field, out var source) == true)
        {
            target.Confidence = source.Confidence;
            target.Explanation = source.Explanation;
        }
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
    DateTime LastWriteUtc = default,
    CatalogProvenance? Provenance = null);

public readonly record struct CatalogMergeResult(
    CatalogMergeStatus Status,
    int InsertedCount,
    IReadOnlyList<string>? Rejected = null)
{
    public IReadOnlyList<string> RejectedKeys => Rejected ?? Array.Empty<string>();
}

public sealed record ReanalysisChange(
    string Key,
    string Kind,
    ScoreFacts? Before,
    ScoreFacts After);

public sealed record ReanalysisPreview(
    IReadOnlyList<ReanalysisChange> Changes,
    IReadOnlyList<string> Preserved);
