// flipper-score: batch CLI over the authoritative Flipper.Core score pipeline.
// Reads one PDF (or a fixture JSON), prints inferred facts. No catalog writes.
using System.Text.Json;
using Flipper.Core.Library;

if (args.Length == 0 || args is ["-h" or "--help"])
{
    Console.WriteLine("usage: flipper-score infer <pdf-path> [--json]");
    Console.WriteLine("       flipper-score infer-text <fixture.json> [--json]");
    Console.WriteLine("fixture: {\"fileName\": \"x.pdf\", \"metadata\": {\"title\":..,\"author\":..,\"subject\":..}, \"pages\": [[\"line\",..],..]}");
    return 0;
}

if (args[0] is not ("infer" or "infer-text"))
{
    Console.Error.WriteLine($"unknown command '{args[0]}'");
    return 2;
}

var jsonOut = args.Contains("--json");
try
{
    if (args[0] == "infer")
    {
        var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.Error.WriteLine("pdf not found");
            return 2;
        }

        var embedded = PdfEmbeddedTextReader.Read(path);
        var facts = ScoreFactInference.Infer(Path.GetFileName(path), embedded.Metadata, embedded.PageLines);
        Print(Path.GetFileName(path), facts, jsonOut);
        return 0;
    }
    else
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
        var facts = ScoreFactInference.Infer(fileName, meta, lines);
        Print(fileName, facts, jsonOut);
        return 0;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void Print(string fileName, ScoreFacts facts, bool jsonOut)
{
    if (jsonOut)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            file = fileName,
            title = facts.Title,
            subtitle = facts.Subtitle,
            composer = facts.Composer,
        }));
    }
    else
    {
        Console.WriteLine($"file:     {fileName}");
        Console.WriteLine($"title:    {facts.Title}");
        Console.WriteLine($"subtitle: {facts.Subtitle}");
        Console.WriteLine($"composer: {facts.Composer}");
    }
}
