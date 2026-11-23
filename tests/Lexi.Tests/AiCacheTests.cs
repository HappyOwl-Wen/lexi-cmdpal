using System.Globalization;
using System.Text.Json;
using Lexi.AI;

namespace Lexi.Tests;

public sealed class AiCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lexi-cache-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Memory_evicts_least_recently_used_answers_and_falls_back_to_disk()
    {
        var cache = new AiCache(_directory);
        for (var i = 0; i < AiCache.MemoryLimit; i++)
        {
            var key = i.ToString(CultureInfo.InvariantCulture);
            cache.Put(key, Result(key));
        }

        // A hit keeps the oldest answer resident; the second-oldest must be evicted.
        Assert.True(cache.TryGet("0", out _));
        cache.Put("overflow", Result("overflow"));
        WriteDisk("0", "disk zero");
        WriteDisk("1", "disk one");

        Assert.True(cache.TryGet("1", out var evicted));
        Assert.Equal("disk one", evicted.Text);
        Assert.True(evicted.FromCache);
        Assert.True(cache.TryGet("0", out var retained));
        Assert.Equal("0", retained.Text);
    }

    [Fact]
    public void Expired_answers_are_misses_and_can_be_reloaded_from_disk()
    {
        var cache = new AiCache(_directory);
        cache.Put("old", Result("expired") with { CreatedAt = DateTimeOffset.Now.AddDays(-31) });
        Assert.False(cache.TryGet("old", out _));
        WriteDisk("old", "fresh");
        Assert.True(cache.TryGet("old", out var result));
        Assert.Equal("fresh", result.Text);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task Queued_requests_recheck_cache_unless_explicitly_bypassed(bool bypass, int expectedCalls)
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new BlockingProvider();
        var service = new AiService(() => provider, _directory) { MinInterval = TimeSpan.Zero };
        var request = new AiRequest(AiTaskKind.ExplainWord, "example");
        var first = service.RunAsync(request, false, null, ct);
        await provider.Started.Task.WaitAsync(ct);
        var second = service.RunAsync(request, bypass, null, ct);
        Assert.False(second.IsCompleted);
        provider.Answer.SetResult(Result("answer"));

        var results = await Task.WhenAll(first, second);
        Assert.Equal(expectedCalls, provider.Calls);
        Assert.Equal(!bypass, results[1].FromCache);
        Assert.All(results, r => Assert.Equal("answer", r.Text));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void WriteDisk(string key, string text)
    {
        var json = JsonSerializer.Serialize(new AiCache.Record(text, "test", DateTimeOffset.Now), AiCacheJsonContext.Default.Record);
        File.WriteAllText(Path.Combine(_directory, key + ".json"), json);
    }

    private static AiResult Result(string text) => new(text, "test", TimeSpan.Zero, false, DateTimeOffset.Now);

    private sealed class BlockingProvider : IAiProvider
    {
        public string Name => "fake";
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AiResult> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? CheckAvailability() => null;

        public Task<AiResult> CompleteAsync(string prompt, IProgress<string>? partial, CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            return Answer.Task.WaitAsync(cancellationToken);
        }
    }
}
