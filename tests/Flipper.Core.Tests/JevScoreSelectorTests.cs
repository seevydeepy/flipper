using System.Net;
using System.Text;
using System.Text.Json;
using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class JevScoreSelectorTests
{
    [Fact]
    public void Candidates_KeepPrintedWorkAndComposerButExcludeArranger()
    {
        var lines = new[]
        {
            Line("Moonlight Sonata"),
            Line("Composed by Ludwig van Beethoven"),
            Line("Arranged by John Smith")
        };
        var candidates = ScoreFactInference.ProposeRichCandidates(
            "scan0042.pdf", new ScoreMetadata("Sheet Music", "Uploader Name", null), lines);

        Assert.Contains(candidates.Titles, item => item.Text == "Moonlight Sonata");
        Assert.Contains(candidates.Composers, item => item.Text == "Ludwig van Beethoven");
        Assert.DoesNotContain(candidates.Composers, item => item.Text == "John Smith");
        Assert.DoesNotContain(candidates.Composers, item => item.Text == "Uploader Name");
    }

    [Fact]
    public async Task Choice_AcceptsSupportedFieldsIndependently()
    {
        using var http = new HttpClient(new ReplyHandler("""
            {"model":"jev-1.13.0","answers":{
              "title":{"type":"choice","choice":"t0","confidence":0.9,"probabilities":{"t0":0.99,"none":0.01}},
              "composer":{"type":"choice","choice":"none","confidence":0.8,"probabilities":{"c0":0.04,"none":0.96}}
            },"usage":{"input_tokens":10,"output_tokens":2}}
            """));
        var selector = new JevScoreSelector(http);
        var candidates = new ScoreFactInference.CandidateSet(
            [new("Moonlight Sonata", "printed heading")],
            [new("Uploader Name", "PDF Author")], []);

        var result = await selector.SelectAsync("test-key", "scan.pdf", default, candidates, default);

        Assert.Equal("Moonlight Sonata", result.Title);
        Assert.Null(result.Composer);
    }

    [Fact]
    public async Task Choice_BelowThresholdAbstains()
    {
        using var http = new HttpClient(new ReplyHandler("""
            {"model":"jev-1.13.0","answers":{
              "title":{"type":"choice","choice":"t0","confidence":0.7,"probabilities":{"t0":0.97,"none":0.03}}
            },"usage":{"input_tokens":10,"output_tokens":1}}
            """));
        var selector = new JevScoreSelector(http);
        var candidates = new ScoreFactInference.CandidateSet(
            [new("A possible title", "printed heading")], [], []);

        var result = await selector.SelectAsync("test-key", "scan.pdf", default, candidates, default);

        Assert.Null(result.Title);
        Assert.Equal(0.97, result.TitleProbability);
    }

    [Theory]
    [InlineData("t9", 0.2)]
    [InlineData("c0", 0.99)]
    [InlineData("t00", 0.2)]
    [InlineData("t0", -0.1)]
    [InlineData("t0", 1.1)]
    public async Task InvalidChoice_FailsRatherThanClearingAnExistingIdentification(string choice, double probability)
    {
        var json = JsonSerializer.Serialize(new
        {
            model = "jev-1.13.0",
            answers = new
            {
                title = new { type = "choice", choice, probabilities = new Dictionary<string, double> { [choice] = probability } }
            }
        });
        using var http = new HttpClient(new ReplyHandler(json));
        var selector = new JevScoreSelector(http);
        var candidates = new ScoreFactInference.CandidateSet([new("Moonlight Sonata", "printed heading")], [], []);

        await Assert.ThrowsAsync<JsonException>(() => selector.SelectAsync("test-key", "scan.pdf", default, candidates, default));
    }

    [Fact]
    public async Task Request_SendsOnlyExtractedEvidenceAndAskedFields()
    {
        using var http = new HttpClient(new InspectRequestHandler());
        var selector = new JevScoreSelector(http);
        var candidates = new ScoreFactInference.CandidateSet([new("Moonlight Sonata", "printed heading")], [],
            [Line("Moonlight Sonata")]);

        var result = await selector.SelectAsync("test-key", "scan.pdf", new("PDF title", "PDF author", "PDF subject"), candidates, default);

        Assert.Equal("Moonlight Sonata", result.Title);
        Assert.Null(result.Composer);
    }

    private sealed class InspectRequestHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain("test-key", body);
            using var document = JsonDocument.Parse(body);
            var state = document.RootElement.GetProperty("state");
            Assert.Equal(new[] { "fileName", "metadata", "pageLines" }, state.EnumerateObject().Select(p => p.Name));
            Assert.Equal("scan.pdf", state.GetProperty("fileName").GetString());
            Assert.Equal("Moonlight Sonata", state.GetProperty("pageLines")[0].GetProperty("Text").GetString());
            var questions = document.RootElement.GetProperty("questions");
            Assert.Single(questions.EnumerateObject());
            Assert.True(questions.GetProperty("title").GetProperty("criteria").TryGetProperty("none", out _));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"jev-1.13.0","answers":{"title":{"type":"choice","choice":"t0","probabilities":{"t0":0.99,"none":0.01}}}}
                    """, Encoding.UTF8, "application/json")
            };
        }
    }

    private static ScoreTextLine Line(string text) =>
        new(1, text, 0.1, 0.1, 0.8, 0.05, 16, null, false, ScoreTextSource.Embedded);

    private sealed class ReplyHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
