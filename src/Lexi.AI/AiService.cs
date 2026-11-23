using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lexi.AI;

/// <summary>
/// Runs user-triggered AI requests: one at a time, at most one start per <see cref="MinInterval"/>,
/// with an answer cache (memory + disk) so repeated questions cost nothing.
/// </summary>
public sealed class AiService
{
    private readonly Func<IAiProvider> _providerFactory;
    private readonly AiCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastStart = DateTimeOffset.MinValue;

    public AiService(Func<IAiProvider> providerFactory, string? cacheDirectory = null)
    {
        _providerFactory = providerFactory;
        _cache = new AiCache(cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lexi", "ai-cache"));
    }

    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(1.5);

    public bool IsBusy => _gate.CurrentCount == 0;

    public IAiProvider CurrentProvider => _providerFactory();

    public async Task<AiResult> RunAsync(AiRequest request, bool bypassCache, IProgress<string>? partial, CancellationToken cancellationToken)
    {
        var provider = _providerFactory();
        var availability = provider.CheckAvailability();
        if (availability is not null)
        {
            throw new AiException(availability);
        }

        var prompt = PromptTemplates.Build(request);
        var model = provider is ClaudeCodeProvider cc ? cc.Options.Model : null;
        var key = AiCache.Key(provider.Name, model, prompt);
        if (!bypassCache && _cache.TryGet(key, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A previous request may have filled the cache while this one waited.
            if (!bypassCache && _cache.TryGet(key, out cached))
            {
                return cached;
            }

            var wait = _lastStart + MinInterval - DateTimeOffset.Now;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _lastStart = DateTimeOffset.Now;
            var result = await provider.CompleteAsync(prompt, partial, cancellationToken).ConfigureAwait(false);
            _cache.Put(key, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AiCache.Record))]
internal sealed partial class AiCacheJsonContext : JsonSerializerContext;

/// <summary>Answer cache keyed by SHA-256, with a bounded memory LRU and a 30-day disk fallback.</summary>
internal sealed class AiCache(string directory)
{
    internal const int MemoryLimit = 128;
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(30);
    private readonly Lock _memoryLock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, AiResult Result)>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, AiResult Result)> _order = new();

    public static string Key(string provider, string? model, string prompt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}\n{model}\n{PromptTemplates.Version}\n{prompt}"));
        return Convert.ToHexStringLower(bytes)[..32];
    }

    public bool TryGet(string key, out AiResult result)
    {
        lock (_memoryLock)
        {
            if (_memory.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                if (DateTimeOffset.Now - node.Value.Result.CreatedAt < Ttl)
                {
                    _order.AddFirst(node);
                    result = node.Value.Result with { FromCache = true };
                    return true;
                }

                _memory.Remove(key);
            }
        }

        try
        {
            var path = Path.Combine(directory, key + ".json");
            if (File.Exists(path))
            {
                var rec = JsonSerializer.Deserialize(File.ReadAllText(path), AiCacheJsonContext.Default.Record);
                if (rec is not null && DateTimeOffset.Now - rec.CreatedAt < Ttl)
                {
                    result = new AiResult(rec.Text, rec.Model, TimeSpan.Zero, FromCache: true, rec.CreatedAt);
                    Remember(key, result);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken cache entry is simply a cache miss.
        }

        result = null!;
        return false;
    }

    public void Put(string key, AiResult result)
    {
        Remember(key, result);
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(new Record(result.Text, result.Model, result.CreatedAt), AiCacheJsonContext.Default.Record);
            File.WriteAllText(Path.Combine(directory, key + ".json"), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Caching is best-effort.
        }
    }

    private void Remember(string key, AiResult result)
    {
        lock (_memoryLock)
        {
            if (_memory.TryGetValue(key, out var node))
            {
                node.Value = (key, result);
                _order.Remove(node);
                _order.AddFirst(node);
            }
            else
            {
                _memory.Add(key, _order.AddFirst((key, result)));
                if (_memory.Count > MemoryLimit)
                {
                    _memory.Remove(_order.Last!.Value.Key);
                    _order.RemoveLast();
                }
            }
        }
    }

    internal sealed record Record(string Text, string? Model, DateTimeOffset CreatedAt);
}
