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
    private readonly object _gate = new();
    private string? _path;
    private long _length;
    private DateTime _lastWriteUtc;
    private IReadOnlyDictionary<string, ScoreFacts> _map =
        new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, ScoreCatalogEntry> _entries =
        new Dictionary<string, ScoreCatalogEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ScoreFacts> Load(string root)
    {
        lock (_gate)
        {
            Refresh(root);
            return _map;
        }
    }

    public IReadOnlyDictionary<string, ScoreCatalogEntry> LoadEntries(string root)
    {
        lock (_gate)
        {
            Refresh(root);
            return _entries;
        }
    }

    private void Refresh(string root)
    {
        var path = Path.Combine(root, ScoreCatalog.FileName);
        if (!File.Exists(path))
        {
            Clear();
            return;
        }

        var info = new FileInfo(path);
        if (_path is not null
            && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)
            && _length == info.Length
            && _lastWriteUtc == info.LastWriteTimeUtc)
        {
            return;
        }

        _entries = ScoreCatalog.LoadEntries(root);
        _map = _entries.ToDictionary(pair => pair.Key, pair => pair.Value.Facts, StringComparer.OrdinalIgnoreCase);
        _path = path;
        _length = info.Length;
        _lastWriteUtc = info.LastWriteTimeUtc;
    }

    private void Clear()
    {
        _path = null;
        _length = 0;
        _lastWriteUtc = default;
        _map = new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);
        _entries = new Dictionary<string, ScoreCatalogEntry>(StringComparer.OrdinalIgnoreCase);
    }
}

public static class ScoreCatalog
{
    public const string FileName = ".flipper-catalog.json";

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public static IReadOnlyDictionary<string, ScoreFacts> Load(string root)
    {
        return LoadEntries(root).ToDictionary(pair => pair.Key, pair => pair.Value.Facts, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, ScoreCatalogEntry> LoadEntries(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, ScoreCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(path);
            var map = new Dictionary<string, ScoreCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            if (JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) is not JsonObject raw)
            {
                return map;
            }

            foreach (var pair in raw)
            {
                map[pair.Key.Replace('/', '\\')] = CatalogProvenanceJson.ParseEntry(pair.Value);
            }

            return map;
        }
        catch (JsonException)
        {
            return new Dictionary<string, ScoreCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new Dictionary<string, ScoreCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, ScoreCatalogEntry>(StringComparer.OrdinalIgnoreCase);
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
                var parsed = JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true });
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

    /// <summary>
    /// Resolve a catalog key back to a source PDF path for fingerprinting.
    /// Null when the file is absent (fingerprint stays zeros, refreshable).
    /// </summary>
    private static string? SourcePathForKey(string root, string key)
    {
        try
        {
            var relative = key.Replace('\\', Path.DirectorySeparatorChar);
            var full = Path.Combine(root, relative);
            return File.Exists(full) ? full : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
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

            var parsed = JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true });
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
    /// since extraction, the extractor moved on, or a transient failure's
    /// backoff expired. Successfully inspected unknown fields are stable. Manual-only and legacy entries
    /// are never scheduled here; legacy review goes through the dry-run
    /// proposal path instead.
    /// </summary>
    public static bool NeedsReanalysis(
        string root,
        ScoreEntry score,
        DateTime? nowUtc = null)
    {
        var key = Key(score.RelativeFolder, Path.GetFileName(score.DisplayFullPath)).Replace('/', '\\');
        return NeedsReanalysis(LoadEntry(root, key), score, nowUtc);
    }

    /// <summary>Pure eligibility check over a cached entry; safe for a whole-library pass.</summary>
    public static bool NeedsReanalysis(
        ScoreCatalogEntry? entry,
        ScoreEntry score,
        DateTime? nowUtc = null,
        bool includeExtractorUpgrade = true)
    {
        nowUtc ??= DateTime.UtcNow;
        if (entry is null)
        {
            return true;
        }

        if (entry.IsLegacy || new[] { "title", "composer", "subtitle" }.All(
            field => entry.OriginOf(field) is ScoreFieldOrigin.Manual or ScoreFieldOrigin.Legacy))
        {
            return false;
        }

        var provenance = entry.Provenance!;
        if (!provenance.MatchesSource(score.Length, score.LastWriteUtc))
        {
            return true;
        }

        if (includeExtractorUpgrade && provenance.ExtractorVersion < ScoreFacts.CurrentExtractorVersion)
        {
            return true;
        }

        // An unknown field after a successful inspection is a stable result.
        return provenance.Status == ExtractionStatus.FailedTransient
            && provenance.Attempts < CatalogProvenance.MaxAttempts
            && (!provenance.NextRetryUtc.HasValue || provenance.NextRetryUtc.Value <= nowUtc.Value);
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
            var parsed = JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true });
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
                var parsed = JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true });
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
            var wasLegacy = stored.Provenance is null;
            var provenance = stored.Provenance ?? new CatalogProvenance
            {
                ExtractorVersion = ScoreFacts.CurrentExtractorVersion,
                Status = ExtractionStatus.Partial
            };
            if (wasLegacy)
            {
                // First provenance attach on a legacy entry: fingerprint the
                // real source PDF when present so the entry does not look
                // "source changed", and mark every unedited field Legacy so
                // later refreshes never touch inherited values the extractor
                // did not produce.
                var pdfPath = SourcePathForKey(root, key);
                if (pdfPath is not null)
                {
                    var info = new FileInfo(pdfPath);
                    provenance.SourceLength = info.Length;
                    provenance.SourceLastWriteUtc = info.LastWriteTimeUtc;
                }

                ApplyCorrection(stored, provenance, "title", title, clearTitle, markUneditedLegacy: true);
                ApplyCorrection(stored, provenance, "composer", composer, clearComposer, markUneditedLegacy: true);
                ApplyCorrection(stored, provenance, "subtitle", subtitle, clearSubtitle, markUneditedLegacy: true);
            }
            else
            {
                ApplyCorrection(stored, provenance, "title", title, clearTitle);
                ApplyCorrection(stored, provenance, "composer", composer, clearComposer);
                ApplyCorrection(stored, provenance, "subtitle", subtitle, clearSubtitle);
            }
            StoreEntry(catalog, actualKey ?? key.Replace('/', '\\'), stored.Facts, provenance);
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
        bool clear,
        bool markUneditedLegacy = false)
    {
        if (!clear && value is null)
        {
            if (markUneditedLegacy)
            {
                provenance.Field(field).Origin = ScoreFieldOrigin.Legacy;
            }

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
        var entries = LoadEntries(root);
        foreach (var pair in generatedFacts)
        {
            var key = pair.Key.Replace('/', '\\');
            var stored = entries.GetValueOrDefault(key);
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
            FieldDiff(stored, "subtitle", pair.Value.Facts.Subtitle, out var subtitleTo, out var subtitleKeep);
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

            if (titleKeep || composerKeep || subtitleKeep)
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
        // Unresolved-only improvement: same PDF, same extractor, no failure —
        // but a deliberate extraction fills a still-blank field.
        // PreviewReanalysis proposes it; the merge must
        // persist it, or missing fields can never be reconsidered without a
        // source change. Manual/Legacy fields are still never touched
        // (CopyField), and blank incoming never clears (CopyField).
        var unresolvedFill =
            (string.IsNullOrWhiteSpace(stored.Facts.Title)
                && !string.IsNullOrWhiteSpace(candidate.Facts.Title))
            || (string.IsNullOrWhiteSpace(stored.Facts.Composer)
                && !string.IsNullOrWhiteSpace(candidate.Facts.Composer));
        if (!sourceChanged && !extractorUpgraded && !retryDue && !unresolvedFill)
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
        provenance.Attempts = sourceChanged || extractorUpgraded ? 1 : provenance.Attempts + 1;
        provenance.NextRetryUtc = provenance.Status == ExtractionStatus.FailedTransient
            ? CatalogProvenance.BackoffAfter(provenance.Attempts, DateTime.UtcNow) : null;
        if (provenance.Status == ExtractionStatus.FailedTransient && provenance.Attempts >= CatalogProvenance.MaxAttempts)
            provenance.Status = ExtractionStatus.FailedPermanent;
        StoreEntry(catalog, actualKey, stored.Facts, provenance);
        return before != catalog[actualKey]?.ToJsonString();
    }

    private static void StoreEntry(JsonObject catalog, string key, ScoreFacts facts, CatalogProvenance provenance)
    {
        var update = CatalogProvenanceJson.ToNode(facts, provenance);
        if (catalog[key] is JsonObject existing) MergeObject(existing, update);
        else catalog[key] = update;
    }

    private static void MergeObject(JsonObject target, JsonObject update)
    {
        // Update owned fields while retaining annotations from other catalogue tools.
        foreach (var pair in update)
        {
            if (pair.Value is JsonObject child && target[pair.Key] is JsonObject existing)
                MergeObject(existing, child);
            else target[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static void CopyField(
        ScoreCatalogEntry stored,
        CatalogProvenance provenance,
        string field,
        string? incoming,
        CatalogProvenance? incomingProvenance)
    {
        if (provenance.Fields.TryGetValue(field, out var current)
            && (current.Origin == ScoreFieldOrigin.Manual || current.Origin == ScoreFieldOrigin.Legacy))
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
