// flipper-score: batch CLI over the authoritative Flipper.Core score pipeline.
// Reads one PDF (or a fixture JSON), prints inferred facts. No catalog writes.
using System.Text.Json;
using Flipper.Core.Library;

if (args.Length == 0 || args is ["-h" or "--help"])
{
    Console.WriteLine("usage: flipper-score infer <pdf-path> [--json] [--explain]");
    Console.WriteLine("       flipper-score infer-text <fixture.json> [--json] [--explain]");
    Console.WriteLine("       flipper-score preview <catalog-root> <pdf-path...> [--json]");
    Console.WriteLine("       flipper-score correct <catalog-root> <key> [--title t] [--composer c] [--subtitle s] [--clear-title] ...");
    Console.WriteLine("       flipper-score reanalyse <catalog-root> [--apply] [--json]");
    Console.WriteLine("fixture: {\"fileName\": \"x.pdf\", \"metadata\": {\"title\":..,\"author\":..,\"subject\":..}, \"lines\": [\"line\",..]}");
    return 0;
}

var jsonOut = args.Contains("--json");
var explain = args.Contains("--explain");
try
{
    switch (args[0])
    {
        case "infer":
        {
            var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Console.Error.WriteLine("pdf not found");
                return 2;
            }

            var fileName = Path.GetFileName(path);
            var embedded = PdfEmbeddedTextReader.ReadRich(path);
            var decision = ScoreFactInference.InferWithEvidence(
                fileName, embedded.Metadata, embedded.PageLines);
            // Rich path merges wrapped headings; prefer it when layout exists.
            if (embedded.Lines.Count > 0)
            {
                var rich = ScoreFactInference.InferRich(fileName, embedded.Metadata, embedded.Lines);
                decision = decision with { Facts = rich };
            }

            Print(fileName, decision, jsonOut, explain);
            return 0;
        }

        case "infer-text":
        {
            var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Console.Error.WriteLine("fixture not found");
                return 2;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var fileName = root.GetProperty("fileName").GetString() ?? "score.pdf";
            var meta = root.TryGetProperty("metadata", out var m)
                ? new ScoreMetadata(
                    m.TryGetProperty("title", out var t) ? t.GetString() : null,
                    m.TryGetProperty("author", out var a) ? a.GetString() : null,
                    m.TryGetProperty("subject", out var s) ? s.GetString() : null)
                : default;
            var lines = root.TryGetProperty("lines", out var l)
                ? l.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
                : Array.Empty<string>();
            var decision = ScoreFactInference.InferWithEvidence(fileName, meta, lines);
            Print(fileName, decision, jsonOut, explain);
            return 0;
        }

        case "preview":
        case "reanalyse":
        {
            // preview: show what a reanalysis merge would change (dry-run first).
            // reanalyse: preview, or with --apply, merge after re-checking.
            var positional = args.Skip(1).Where(a => !a.StartsWith('-')).ToArray();
            if (positional.Length < 1)
            {
                Console.Error.WriteLine("catalog root required");
                return 2;
            }

            var catalogRoot = positional[0];
            var targets = positional.Skip(1).ToArray();
            if (targets.Length == 0 && positional.Length >= 1)
            {
                targets = Directory.Exists(catalogRoot)
                    ? Directory.GetFiles(catalogRoot, "*.pdf", SearchOption.AllDirectories)
                    : [];
            }

            var candidates = new Dictionary<string, CatalogMergeCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var pdf in targets)
            {
                string key;
                try
                {
                    key = Path.GetRelativePath(catalogRoot, pdf).Replace('/', '\\');
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var embedded = PdfEmbeddedTextReader.ReadRich(pdf);
                var info = new FileInfo(pdf);
                var facts = embedded.Lines.Count > 0
                    ? ScoreFactInference.InferRich(Path.GetFileName(pdf), embedded.Metadata, embedded.Lines)
                    : ScoreFactInference.Infer(Path.GetFileName(pdf), embedded.Metadata, embedded.PageLines);
                var status = embedded.Outcome switch
                {
                    ScoreExtractionOutcome.Success => facts.Composer is null || facts.Title is null
                        ? ExtractionStatus.Partial : ExtractionStatus.Complete,
                    ScoreExtractionOutcome.ExtractionFailure => ExtractionStatus.FailedTransient,
                    _ => ExtractionStatus.Partial
                };
                candidates[key] = new CatalogMergeCandidate(
                    facts,
                    pdf,
                    info.Exists ? info.Length : 0,
                    info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
                    CatalogProvenance.ForGenerated(
                        ScoreFacts.CurrentExtractorVersion,
                        info.Exists ? info.Length : 0,
                        info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
                        facts, status));
            }

            var preview = ScoreCatalog.PreviewReanalysis(catalogRoot, candidates);
            if (jsonOut)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    changes = preview.Changes.Select(c => new
                    {
                        key = c.Key,
                        kind = c.Kind,
                        before = c.Before,
                        after = c.After
                    }),
                    preserved = preview.Preserved
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                foreach (var change in preview.Changes)
                {
                    Console.WriteLine($"{change.Kind}: {change.Key}");
                    Console.WriteLine($"  title:    {change.Before?.Title} -> {change.After.Title}");
                    Console.WriteLine($"  composer: {change.Before?.Composer} -> {change.After.Composer}");
                }

                if (preview.Preserved.Count > 0)
                {
                    Console.WriteLine($"preserved manual/legacy: {preview.Preserved.Count}");
                }

                Console.WriteLine("No catalog writes were made.");
            }

            if (args[0] == "reanalyse" && args.Contains("--apply"))
            {
                // Re-check: only apply while the reviewed state still matches.
                var reread = ScoreCatalog.PreviewReanalysis(catalogRoot, candidates);
                var keys = preview.Changes.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var nowKeys = reread.Changes.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!keys.SetEquals(nowKeys))
                {
                    Console.Error.WriteLine("catalog changed since preview; refusing to apply");
                    return 1;
                }

                var result = ScoreCatalog.TryMergeMissing(catalogRoot, candidates);
                Console.WriteLine($"merge: {result.Status} ({result.InsertedCount})");
                return result.Status is CatalogMergeStatus.Inserted ? 0 : 1;
            }

            return 0;
        }

        case "correct":
        {
            var positional = args.Skip(1).Where(a => !a.StartsWith('-')).ToArray();
            if (positional.Length < 2)
            {
                Console.Error.WriteLine("usage: correct <catalog-root> <key> [--title t] ...");
                return 2;
            }

            string? Opt(string name) =>
                args.SkipWhile(a => a != name).Skip(1).FirstOrDefault(v => !v.StartsWith('-'));
            var ok = ScoreCatalog.TryCorrect(
                positional[0], positional[1],
                Opt("--title"), Opt("--composer"), Opt("--subtitle"),
                args.Contains("--clear-title"), args.Contains("--clear-composer"), args.Contains("--clear-subtitle"));
            Console.WriteLine(ok ? "corrected" : "failed");
            return ok ? 0 : 1;
        }

        default:
        {
            Console.Error.WriteLine($"unknown command '{args[0]}'");
            return 2;
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void Print(string fileName, ScoreFactInference.InferenceDecision decision, bool jsonOut, bool explain)
{
    var facts = decision.Facts;
    if (jsonOut)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            file = fileName,
            title = facts.Title,
            subtitle = facts.Subtitle,
            composer = facts.Composer,
            titleVerified = decision.TitleVerified,
            titleEvidence = decision.TitleEvidence,
            composerEvidence = decision.ComposerEvidence,
            titleRunnerUp = decision.TitleRunnerUp
        }));
    }
    else
    {
        Console.WriteLine($"file:     {fileName}");
        Console.WriteLine($"title:    {facts.Title}" + (decision.TitleVerified ? string.Empty : "  [unverified fallback]"));
        Console.WriteLine($"subtitle: {facts.Subtitle}");
        Console.WriteLine($"composer: {facts.Composer}");
        if (explain)
        {
            Console.WriteLine($"why title:    {decision.TitleEvidence}");
            Console.WriteLine($"why composer: {decision.ComposerEvidence}");
            if (decision.TitleRunnerUp is not null)
            {
                Console.WriteLine($"runner-up:    {decision.TitleRunnerUp}");
            }
        }
    }
}
