using System.Net;
using Lexi.Translation;

namespace Lexi.Tests;

public class TranslationTests
{
    private const string Response = """{"sentences":[{"trans":"早上好！"},{"trans":"今天好吗？"}],"src":"de"}""";

    [Fact]
    public async Task Posts_encoded_text_without_credentials_and_combines_segments()
    {
        const string input = "Guten Morgen! A&B + C? #ä";
        using var handler = new StubHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("translate.googleapis.com", request.RequestUri.Host);
            Assert.Contains("sl=auto&tl=zh-CN", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("q=", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.Null(request.Headers.Authorization);
            var body = await request.Content!.ReadAsStringAsync(ct);
            Assert.StartsWith("q=", body, StringComparison.Ordinal);
            Assert.Equal(input, WebUtility.UrlDecode(body[2..]));
            return Json(Response);
        });
        using var http = new HttpClient(handler);
        using var service = new GoogleTranslator(http);

        var result = await service.TranslateAsync(input, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("早上好！今天好吗？", result.Text);
        Assert.Equal("de", result.SourceLanguage);
        Assert.False(result.FromCache);
    }

    [Fact]
    public async Task Reuses_memory_cache_but_explicit_retry_makes_a_new_request()
    {
        var requests = 0;
        using var handler = new StubHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(Response));
        });
        using var http = new HttpClient(handler);
        using var service = new GoogleTranslator(http) { MinInterval = TimeSpan.Zero };

        await service.TranslateAsync("Guten Morgen!", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True((await service.TranslateAsync("  Guten Morgen!  ", cancellationToken: TestContext.Current.CancellationToken)).FromCache);
        Assert.Equal(1, requests);
        Assert.False((await service.TranslateAsync("Guten Morgen!", bypassCache: true, cancellationToken: TestContext.Current.CancellationToken)).FromCache);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData(429, "过于频繁")]
    [InlineData(403, "拒绝访问")]
    [InlineData(503, "503")]
    public async Task Http_errors_are_actionable_and_do_not_expose_response_bodies(int status, string expected)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private request text") }));
        using var http = new HttpClient(handler);
        using var service = new GoogleTranslator(http);

        var error = await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync("A test sentence.", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private request text", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<html>unavailable</html>")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"sentences":[{"orig":"Hello"}]}""")]
    [InlineData("""{"sentences":[{"trans":"你好。"},{"orig":"How are you?"}]}""")]
    [InlineData("""{"sentences":[{"trans":" "}]}""")]
    public async Task Invalid_responses_are_not_displayed_or_cached(string json)
    {
        var requests = 0;
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(++requests == 1 ? json : Response)));
        using var http = new HttpClient(handler);
        using var service = new GoogleTranslator(http) { MinInterval = TimeSpan.Zero };

        await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync("Guten Morgen!", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("早上好！今天好吗？", (await service.TranslateAsync("Guten Morgen!", cancellationToken: TestContext.Current.CancellationToken)).Text);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Timeout_is_explained_and_does_not_leave_requests_blocked()
    {
        var requests = 0;
        using var handler = new StubHandler(async (_, ct) =>
        {
            if (++requests == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return Json(Response);
        });
        using var http = new HttpClient(handler);
        using var service = new GoogleTranslator(http) { RequestTimeout = TimeSpan.FromMilliseconds(50), MinInterval = TimeSpan.Zero };

        var error = await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync("Guten Morgen!", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("超时", error.Message, StringComparison.Ordinal);
        Assert.False((await service.TranslateAsync("Guten Morgen!", cancellationToken: TestContext.Current.CancellationToken)).FromCache);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_timeout_and_is_not_cached()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var handler = new StubHandler(async (_, ct) =>
        {
            if (++requests == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return Json(Response);
        });
        using var http = new HttpClient(handler);
        using var service = new GoogleTranslator(http) { MinInterval = TimeSpan.Zero };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var pending = service.TranslateAsync("Guten Morgen!", cancellationToken: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False((await service.TranslateAsync("Guten Morgen!", cancellationToken: TestContext.Current.CancellationToken)).FromCache);
    }

    [Fact]
    public async Task Target_language_selects_the_endpoint_and_is_part_of_the_cache_key()
    {
        var targets = new List<string>();
        using var handler = new StubHandler((request, _) =>
        {
            targets.Add(request.RequestUri!.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("tl=", StringComparison.Ordinal))[3..]);
            return Task.FromResult(Json("""{"sentences":[{"trans":"I want to learn German."}],"src":"zh-CN"}"""));
        });
        using var http = new HttpClient(handler);
        using var service = new GoogleTranslator(http) { MinInterval = TimeSpan.Zero };
        var ct = TestContext.Current.CancellationToken;

        var english = await service.TranslateAsync("我想学习德语。", GoogleTranslator.English, cancellationToken: ct);
        Assert.False((await service.TranslateAsync("我想学习德语。", GoogleTranslator.German, cancellationToken: ct)).FromCache);
        Assert.True((await service.TranslateAsync("我想学习德语。", GoogleTranslator.English, cancellationToken: ct)).FromCache);

        Assert.Equal(["en", "de"], targets);
        Assert.Equal("zh-CN", english.SourceLanguage);
        Assert.Equal("https://translate.google.com/?sl=auto&tl=de&text=%E5%A5%BD&op=translate", GoogleTranslator.WebUrl("好", GoogleTranslator.German));
    }

    [Fact]
    public async Task Unsupported_target_language_is_rejected_before_any_request()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var service = new GoogleTranslator(http);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TranslateAsync("Hallo Welt", "fr&dt=x", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Empty_or_oversized_input_never_reaches_the_network()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var service = new GoogleTranslator(http);

        await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync("   ", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync(new string('a', GoogleTranslator.MaxCharacters + 1), cancellationToken: TestContext.Current.CancellationToken));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
