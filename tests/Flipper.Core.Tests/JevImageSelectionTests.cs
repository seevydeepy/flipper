using System.Net;
using System.Text;
using System.Text.Json;
using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class JevImageSelectionTests
{
    private static readonly ScoreFactInference.CandidateSet Candidates = new(
        [new("Moonlight Sonata", "printed heading")],
        [new("Ludwig van Beethoven", "printed credit")], []);

    [Theory]
    [InlineData("Moonlight Sonata", 0.99)]
    [InlineData(null, 0.99)]
    public async Task ConfidentTextIncludingNone_DoesNotRenderOrUpload(string? title, double probability)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("Unexpected upload")));
        var text = new JevScoreSelector.Selection(title, probability, "Ludwig van Beethoven", 0.95, "jev-1.13.0");
        var result = await new JevScoreSelector(http).RefineWithImagesAsync("key", "scan.pdf", default,
            Candidates, text, _ => { calls++; return Task.FromResult<IReadOnlyList<JevScoreSelector.PageImage>>([Page(1)]); }, default);

        Assert.Equal(0, calls);
        Assert.Equal(text, result);
    }

    [Fact]
    public async Task NoCandidates_DoesNotRenderOrUpload()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("Unexpected upload")));
        var selector = new JevScoreSelector(http);
        var candidates = new ScoreFactInference.CandidateSet([], [], []);
        var text = await selector.SelectAsync("key", "scan.pdf", default, candidates, default);
        var result = await selector.RefineWithImagesAsync("key", "scan.pdf", default, candidates, text,
            _ => { calls++; return Task.FromResult<IReadOnlyList<JevScoreSelector.PageImage>>([Page(1)]); }, default);

        Assert.Equal(0, calls);
        Assert.Equal(text, result);
    }

    [Fact]
    public async Task ImagePass_SendsBoundedMultipartAndOnlyAsksUncertainFields()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            requests++;
            Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization!.Parameter);
            if (requests == 1)
            {
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                Assert.False(request.Headers.Contains("TypeSafe-Danger-Zone-Images"));
                return Reply("""{"title":{"type":"choice","choice":"t0","probabilities":{"t0":0.99}},"composer":{"type":"choice","choice":"none","probabilities":{"none":0.55}}} """);
            }
            Assert.Equal(2, requests);
            Assert.Equal("true", Assert.Single(request.Headers.GetValues("TypeSafe-Danger-Zone-Images")));
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal(3, parts.Length);
            Assert.Equal("request", parts[0].Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("application/json", parts[0].Headers.ContentType!.MediaType);
            var json = await parts[0].ReadAsStringAsync(token);
            Assert.DoesNotContain("test-key", json);
            using var document = JsonDocument.Parse(json);
            var body = document.RootElement;
            Assert.Equal("jev-1.13.0", body.GetProperty("model").GetString());
            var questions = body.GetProperty("questions");
            Assert.Equal("composer", Assert.Single(questions.EnumerateObject()).Name);
            Assert.Equal(new[] { "none", "c0" }, questions.GetProperty("composer").GetProperty("criteria").EnumerateObject().Select(p => p.Name));
            var pages = body.GetProperty("state").GetProperty("pageImages");
            Assert.Equal(2, pages.GetArrayLength());
            var assets = body.GetProperty("assets");
            Assert.Equal(2, assets.EnumerateObject().Count());
            for (var i = 0; i < 2; i++)
            {
                Assert.Equal(i + 1, pages[i].GetProperty("pageNumber").GetInt32());
                var reference = pages[i].GetProperty("image").GetString()!;
                Assert.True(Guid.TryParse(reference, out _));
                var asset = assets.GetProperty(reference);
                Assert.Equal("image", asset.GetProperty("type").GetString());
                Assert.Equal(parts[i + 1].Headers.ContentDisposition!.Name!.Trim('"'), asset.GetProperty("part").GetString());
                Assert.Equal("image/jpeg", parts[i + 1].Headers.ContentType!.MediaType);
                Assert.Equal(Page(i + 1).Jpeg, await parts[i + 1].ReadAsByteArrayAsync(token));
            }
            // Even an extra answer must not overwrite the already accepted text title.
            return Reply("""{"title":{"type":"choice","choice":"none","probabilities":{"none":1}},"composer":{"type":"choice","choice":"c0","probabilities":{"c0":0.96}}} """);
        }));
        var selector = new JevScoreSelector(http);
        var text = await selector.SelectAsync("test-key", "scan.pdf", default, Candidates, default);
        var result = await selector.RefineWithImagesAsync("test-key", "scan.pdf", default, Candidates,
            text, _ => Task.FromResult<IReadOnlyList<JevScoreSelector.PageImage>>([Page(1), Page(2)]), default);

        Assert.Equal(2, requests);
        Assert.Equal("Moonlight Sonata", result.Title);
        Assert.Equal(0.99, result.TitleProbability);
        Assert.Equal("Ludwig van Beethoven", result.Composer);
        Assert.Equal(0.96, result.ComposerProbability);
        Assert.False(result.TitleUsedImages);
        Assert.True(result.ComposerUsedImages);
        Assert.Null(result.ImageWarning);
    }

    [Fact]
    public async Task Images_DoNotLowerAcceptanceThresholds()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply("""
            {"title":{"type":"choice","choice":"t0","probabilities":{"t0":0.97}},
             "composer":{"type":"choice","choice":"c0","probabilities":{"c0":0.89}}}
            """))));
        var text = new JevScoreSelector.Selection(null, 0.6, null, 0.5, "jev-1.13.0");
        var result = await Refine(http, text);

        Assert.Null(result.Title);
        Assert.Null(result.Composer);
        Assert.True(result.TitleUsedImages);
        Assert.True(result.ComposerUsedImages);
        Assert.Null(result.ImageWarning);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("invalid-choice")]
    [InlineData("render")]
    public async Task FailedImageCheck_KeepsTextResultsAndReportsSafeWarning(string failure)
    {
        using var http = new HttpClient(new Handler((_, _) => failure switch
        {
            "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)),
            "timeout" => throw new TaskCanceledException("private server details"),
            _ => Task.FromResult(Reply("""{"composer":{"type":"choice","choice":"c4","probabilities":{"c4":0.1}}} """))
        }));
        var text = new JevScoreSelector.Selection("Moonlight Sonata", 0.99, null, 0.5, "jev-1.13.0");
        var result = await new JevScoreSelector(http).RefineWithImagesAsync("key", "scan.pdf", default,
            Candidates, text, _ => failure == "render" ? throw new IOException("private path")
                : Task.FromResult<IReadOnlyList<JevScoreSelector.PageImage>>([Page(1)]), default);

        Assert.Equal(text with { ImageWarning = result.ImageWarning }, result);
        Assert.Equal("Could not check page images. Text results were kept.", result.ImageWarning);
    }

    [Fact]
    public async Task CancellationDuringImageUpload_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        }));
        var text = new JevScoreSelector.Selection(null, 0.6, null, 0.5, "jev-1.13.0");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new JevScoreSelector(http)
            .RefineWithImagesAsync("key", "scan.pdf", default, Candidates, text,
                _ => Task.FromResult<IReadOnlyList<JevScoreSelector.PageImage>>([Page(1)]), cancellation.Token));
    }

    [Theory]
    [InlineData("small")]
    [InlineData("large")]
    [InlineData("bytes")]
    [InlineData("pages")]
    [InlineData("later-page")]
    [InlineData("empty")]
    public async Task InvalidImages_AreNotUploaded(string invalid)
    {
        var uploads = 0;
        using var http = new HttpClient(new Handler((_, _) => { uploads++; throw new Exception("Unexpected upload"); }));
        IReadOnlyList<JevScoreSelector.PageImage> images = invalid switch
        {
            "small" => [Page(1) with { Width = 279 }],
            "large" => [Page(1) with { Height = 1569 }],
            "bytes" => [Page(1) with { Jpeg = new byte[2 * 1024 * 1024 + 1] }],
            "pages" => [Page(1), Page(2), Page(3)],
            "later-page" => [Page(3)],
            _ => []
        };
        var text = new JevScoreSelector.Selection(null, 0.6, null, 0.5, "jev-1.13.0");
        var result = await new JevScoreSelector(http).RefineWithImagesAsync("key", "scan.pdf", default,
            Candidates, text, _ => Task.FromResult(images), default);

        Assert.Equal(0, uploads);
        Assert.NotNull(result.ImageWarning);
        Assert.Equal(text with { ImageWarning = result.ImageWarning }, result);
    }

    // Opaque bytes exercise the upload contract; the Windows render smoke check decodes real JPEGs.
    private static JevScoreSelector.PageImage Page(int page) => new(page, [(byte)page, 2, 3], 280, 1568);

    private static Task<JevScoreSelector.Selection> Refine(HttpClient http, JevScoreSelector.Selection text) =>
        new JevScoreSelector(http).RefineWithImagesAsync("key", "scan.pdf", default, Candidates,
            text, _ => Task.FromResult<IReadOnlyList<JevScoreSelector.PageImage>>([Page(1)]), default);

    private static HttpResponseMessage Reply(string answers) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"model\":\"jev-1.13.0\",\"answers\":" + answers + "}", Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
