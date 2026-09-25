using System.Net;
using System.Text;
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
