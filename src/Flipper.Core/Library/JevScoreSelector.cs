using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Flipper.Core.Library;

/// <summary>Optional, closed-set selection over text already extracted locally.</summary>
public sealed class JevScoreSelector(HttpClient http)
{
    public const string Model = "jev-1.13.0";
    // ponytail: conservative fixed thresholds; tune against labelled scores when available.
    public const double TitleProbabilityThreshold = 0.98;
    public const double ComposerProbabilityThreshold = 0.90;
    private const string None = "none";
    private static readonly Uri Endpoint = new("https://api.typesafe.ai/v1/systemone");

    public sealed record Selection(
        string? Title, double? TitleProbability,
        string? Composer, double? ComposerProbability,
        string Model);

    public async Task<Selection> SelectAsync(
        string apiKey, string fileName, ScoreMetadata metadata,
        ScoreFactInference.CandidateSet candidates, CancellationToken cancellationToken)
    {
        var questions = new Dictionary<string, object>();
        if (candidates.Titles.Count > 0)
            questions["title"] = new
            {
                type = "choice",
                instructions = "Which candidate is the printed title of this musical work? Ignore composer credits, movement directions, collection headings, and publisher text. Choose none when no printed title is supported.",
                criteria = Criteria(candidates.Titles, "t", "No candidate is a supported printed work title")
            };
        if (candidates.Composers.Count > 0)
            questions["composer"] = new
            {
                type = "choice",
                instructions = "Who composed this musical work? Exclude arrangers, transcribers, performers, lyricists, and PDF uploaders. Choose none when no composer is supported.",
                criteria = Criteria(candidates.Composers, "c", "No candidate is a supported composer")
            };
        if (questions.Count == 0) return new Selection(null, null, null, null, Model);

        var state = new
        {
            fileName,
            metadata = new { metadata.Title, metadata.Author, metadata.Subject },
            pageLines = candidates.Lines.Select(line => new
            {
                line.PageNumber, line.Text, line.Y, line.FontSize, line.Bold,
                source = line.Source.ToString()
            })
        };
        var body = JsonSerializer.Serialize(new { model = Model, state, questions });
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = result.RootElement;
        var answers = root.GetProperty("answers");
        var model = root.GetProperty("model").GetString() ?? Model;
        var title = ReadChoice(answers, "title", candidates.Titles, "t", TitleProbabilityThreshold);
        var composer = ReadChoice(answers, "composer", candidates.Composers, "c", ComposerProbabilityThreshold);
        return new Selection(title.Text, title.Probability, composer.Text, composer.Probability, model);
    }

    private static Dictionary<string, string> Criteria(
        IReadOnlyList<ScoreFactInference.ScoreCandidate> candidates, string prefix, string noneDescription)
    {
        var choices = new Dictionary<string, string> { [None] = noneDescription };
        for (var i = 0; i < candidates.Count; i++)
            choices[$"{prefix}{i}"] = $"{candidates[i].Text} ({candidates[i].Evidence})";
        return choices;
    }

    private static (string? Text, double? Probability) ReadChoice(
        JsonElement answers, string question,
        IReadOnlyList<ScoreFactInference.ScoreCandidate> candidates, string prefix, double threshold)
    {
        if (candidates.Count == 0) return (null, null);
        var answer = answers.GetProperty(question);
        if (answer.GetProperty("type").GetString() != "choice") throw new JsonException("Unexpected Jev answer type");
        var choice = answer.GetProperty("choice").GetString();
        if (choice is null) throw new JsonException("Missing Jev choice");
        var probabilities = answer.GetProperty("probabilities");
        var probability = probabilities.GetProperty(choice).GetDouble();
        if (!double.IsFinite(probability) || probability is < 0 or > 1)
            throw new JsonException("Invalid Jev probability");
        if (choice == None) return (null, probability);
        if (!choice.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(choice.AsSpan(prefix.Length), out var index)
            || index < 0 || index >= candidates.Count || choice != $"{prefix}{index}")
            throw new JsonException("Unknown Jev candidate");
        if (probability < threshold) return (null, probability);
        return (candidates[index].Text, probability);
    }
}
