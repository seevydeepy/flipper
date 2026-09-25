using System.Text.Json.Nodes;

namespace Flipper.Core.Library;

/// <summary>
/// Where a single catalog field value came from.
/// Unresolved beats wrong: unknown is an explicit state, not a blank.
/// </summary>
public enum ScoreFieldOrigin
{
    Unresolved,
    Generated,
    Manual,
    Legacy
}

/// <summary>
/// How complete the last extraction attempt for an entry was.
/// A failure is never persisted as a successful identification.
/// </summary>
public enum ExtractionStatus
{
    Complete,
    Partial,
    FailedTransient,
    FailedPermanent
}

public sealed class FieldProvenance
{
    public ScoreFieldOrigin Origin { get; set; } = ScoreFieldOrigin.Unresolved;
    public double? Confidence { get; set; }
    public string? Explanation { get; set; }
}

public sealed class CatalogProvenance
{
    public int ExtractorVersion { get; set; }
    public long SourceLength { get; set; }
    public DateTime SourceLastWriteUtc { get; set; }
    public ExtractionStatus Status { get; set; } = ExtractionStatus.Partial;
    public int Attempts { get; set; } = 1;
    public DateTime? NextRetryUtc { get; set; }
    public Dictionary<string, FieldProvenance> Fields { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public FieldProvenance Field(string name)
    {
        if (!Fields.TryGetValue(name, out var field))
        {
            field = new FieldProvenance();
            Fields[name] = field;
        }

        return field;
    }

    public bool MatchesSource(long length, DateTime lastWriteUtc)
    {
        return SourceLength == length && SourceLastWriteUtc == lastWriteUtc;
    }

    /// <summary>
    /// Bounded backoff for transient failures:
    /// 1m, 5m, 30m, 2h, 12h, then daily. After <see cref="MaxAttempts"/>
    /// the entry parks until the source changes or a deliberate reanalysis.
    /// </summary>
    public const int MaxAttempts = 10;

    public static DateTime? BackoffAfter(int attempts, DateTime nowUtc)
    {
        if (attempts >= MaxAttempts)
        {
            return null;
        }

        var delay = attempts switch
        {
            <= 1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(30),
            4 => TimeSpan.FromHours(2),
            5 => TimeSpan.FromHours(12),
            _ => TimeSpan.FromHours(24)
        };
        return nowUtc + delay;
    }

    public static CatalogProvenance ForGenerated(
        int extractorVersion,
        long sourceLength,
        DateTime sourceLastWriteUtc,
        ScoreFacts facts,
        ExtractionStatus status,
        string? explanation = null)
    {
        var provenance = new CatalogProvenance
        {
            ExtractorVersion = extractorVersion,
            SourceLength = sourceLength,
            SourceLastWriteUtc = sourceLastWriteUtc,
            Status = status,
            NextRetryUtc = status == ExtractionStatus.FailedTransient ? BackoffAfter(1, DateTime.UtcNow) : null
        };
        provenance.Field("title").Origin =
            string.IsNullOrWhiteSpace(facts.Title) ? ScoreFieldOrigin.Unresolved : ScoreFieldOrigin.Generated;
        provenance.Field("composer").Origin =
            string.IsNullOrWhiteSpace(facts.Composer) ? ScoreFieldOrigin.Unresolved : ScoreFieldOrigin.Generated;
        provenance.Field("subtitle").Origin =
            string.IsNullOrWhiteSpace(facts.Subtitle) ? ScoreFieldOrigin.Unresolved : ScoreFieldOrigin.Generated;
        if (explanation is not null)
        {
            foreach (var field in provenance.Fields.Values)
            {
                field.Explanation = explanation;
            }
        }

        return provenance;
    }

    public static CatalogProvenance ForFailure(
        int extractorVersion,
        long sourceLength,
        DateTime sourceLastWriteUtc,
        ExtractionStatus status,
        int attempts,
        DateTime nowUtc)
    {
        return new CatalogProvenance
        {
            ExtractorVersion = extractorVersion,
            SourceLength = sourceLength,
            SourceLastWriteUtc = sourceLastWriteUtc,
            Status = status,
            Attempts = attempts,
            NextRetryUtc = status == ExtractionStatus.FailedTransient
                ? BackoffAfter(attempts, nowUtc)
                : null
        };
    }
}

/// <summary>
/// A catalog entry: resolved facts plus optional provenance.
/// Provenance absent means legacy: unknown origin, never auto-overwritten.
/// </summary>
public sealed class ScoreCatalogEntry
{
    public ScoreFacts Facts { get; set; } = new();
    public CatalogProvenance? Provenance { get; set; }

    public bool IsLegacy => Provenance is null;

    public bool AllFieldsManual()
    {
        if (Provenance is null)
        {
            return false;
        }

        return OriginOf("title") == ScoreFieldOrigin.Manual
            && OriginOf("composer") == ScoreFieldOrigin.Manual
            && OriginOf("subtitle") == ScoreFieldOrigin.Manual;
    }

    public ScoreFieldOrigin OriginOf(string field)
    {
        if (Provenance?.Fields.TryGetValue(field, out var provenance) == true)
        {
            return provenance.Origin;
        }

        return Provenance is null ? ScoreFieldOrigin.Legacy : ScoreFieldOrigin.Unresolved;
    }
}

internal static class CatalogProvenanceJson
{
    public static ScoreCatalogEntry ParseEntry(JsonNode? node)
    {
        var entry = new ScoreCatalogEntry();
        if (node is not JsonObject obj)
        {
            return entry;
        }

        entry.Facts = new ScoreFacts
        {
            Title = StringOrNull(obj["title"]),
            Subtitle = StringOrNull(obj["subtitle"]),
            Composer = StringOrNull(obj["composer"])
        };
        entry.Provenance = ParseProvenance(obj["provenance"]);
        return entry;
    }

    public static JsonObject ToNode(ScoreFacts facts, CatalogProvenance? provenance)
    {
        var node = new JsonObject
        {
            ["title"] = JsonValue.Create<string?>(facts.Title),
            ["subtitle"] = JsonValue.Create<string?>(facts.Subtitle),
            ["composer"] = JsonValue.Create<string?>(facts.Composer)
        };
        if (provenance is not null)
        {
            node["provenance"] = ToNode(provenance);
        }

        return node;
    }

    public static JsonObject ToNode(CatalogProvenance provenance)
    {
        var fields = new JsonObject();
        foreach (var pair in provenance.Fields)
        {
            var field = new JsonObject
            {
                ["origin"] = pair.Value.Origin switch
                {
                    ScoreFieldOrigin.Generated => "generated",
                    ScoreFieldOrigin.Manual => "manual",
                    ScoreFieldOrigin.Legacy => "legacy",
                    _ => "unresolved"
                },
                ["confidence"] = JsonValue.Create(pair.Value.Confidence),
                ["explanation"] = pair.Value.Explanation
            };
            fields[pair.Key.ToLowerInvariant()] = field;
        }

        var node = new JsonObject
        {
            ["extractorVersion"] = provenance.ExtractorVersion,
            ["sourceLength"] = provenance.SourceLength,
            ["sourceLastWriteUtc"] = provenance.SourceLastWriteUtc.ToUniversalTime().ToString("o"),
            ["status"] = provenance.Status switch
            {
                ExtractionStatus.Complete => "complete",
                ExtractionStatus.FailedTransient => "failedTransient",
                ExtractionStatus.FailedPermanent => "failedPermanent",
                _ => "partial"
            },
            ["attempts"] = provenance.Attempts,
            ["fields"] = fields,
            ["nextRetryUtc"] = provenance.NextRetryUtc?.ToUniversalTime().ToString("o")
        };
        return node;
    }

    public static CatalogProvenance? ParseProvenance(JsonNode? node)
    {
        try { return ParseProvenanceCore(node); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            // Malformed provenance is unknown history, never authority to overwrite facts.
            return null;
        }
    }

    private static CatalogProvenance? ParseProvenanceCore(JsonNode? node)
    {
        if (node is not JsonObject obj
            || !obj.TryGetPropertyValue("extractorVersion", out var versionNode)
            || versionNode?.GetValueKind() != System.Text.Json.JsonValueKind.Number)
        {
            return null;
        }

        var provenance = new CatalogProvenance
        {
            ExtractorVersion = versionNode.GetValue<int>()
        };
        if (obj.TryGetPropertyValue("sourceLength", out var lengthNode)
            && lengthNode?.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            provenance.SourceLength = lengthNode.GetValue<long>();
        }

        if (obj.TryGetPropertyValue("sourceLastWriteUtc", out var stampNode)
            && DateTime.TryParse(
                stampNode?.GetValue<string>(),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var stamp))
        {
            provenance.SourceLastWriteUtc = stamp.ToUniversalTime();
        }

        if (obj.TryGetPropertyValue("status", out var statusNode))
        {
            provenance.Status = statusNode?.GetValue<string>()?.ToLowerInvariant() switch
            {
                "complete" => ExtractionStatus.Complete,
                "failedtransient" => ExtractionStatus.FailedTransient,
                "failedpermanent" => ExtractionStatus.FailedPermanent,
                _ => ExtractionStatus.Partial
            };
        }

        if (obj.TryGetPropertyValue("attempts", out var attemptsNode)
            && attemptsNode?.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            provenance.Attempts = attemptsNode.GetValue<int>();
        }

        if (obj.TryGetPropertyValue("nextRetryUtc", out var retryNode)
            && DateTime.TryParse(
                retryNode?.GetValue<string>(),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var retry))
        {
            provenance.NextRetryUtc = retry.ToUniversalTime();
        }

        if (obj.TryGetPropertyValue("fields", out var fieldsNode) && fieldsNode is JsonObject fields)
        {
            foreach (var pair in fields)
            {
                var field = new FieldProvenance();
                if (pair.Value is JsonObject fieldObj)
                {
                    field.Origin = fieldObj["origin"]?.GetValue<string>()?.ToLowerInvariant() switch
                    {
                        "generated" => ScoreFieldOrigin.Generated,
                        "manual" => ScoreFieldOrigin.Manual,
                        "legacy" => ScoreFieldOrigin.Legacy,
                        _ => ScoreFieldOrigin.Unresolved
                    };
                    if (fieldObj.TryGetPropertyValue("confidence", out var confidenceNode)
                        && confidenceNode?.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                    {
                        field.Confidence = confidenceNode.GetValue<double>();
                    }

                    field.Explanation = StringOrNull(fieldObj["explanation"]);
                }

                provenance.Fields[pair.Key] = field;
            }
        }

        return provenance;
    }

    private static string? StringOrNull(JsonNode? node)
    {
        if (node is null || node.GetValueKind() == System.Text.Json.JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
