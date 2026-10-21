using System.Collections.Concurrent;
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

/// <summary>Answer cache keyed by SHA-256 of provider, model, template version and prompt.</summary>
internal sealed class AiCache(string directory)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(30);
    private readonly ConcurrentDictionary<string, AiResult> _memory = new(StringComparer.Ordinal);

    public static string Key(string provider, string? model, string prompt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}\n{model}\n{PromptTemplates.Version}\n{prompt}"));
        return Convert.ToHexStringLower(bytes)[..32];
    }

    public bool TryGet(string key, out AiResult result)
    {
        if (_memory.TryGetValue(key, out result!) && DateTimeOffset.Now - result.CreatedAt < Ttl)
        {
            result = result with { FromCache = true };
            return true;
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
                    _memory[key] = result;
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
        _memory[key] = result;
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

    internal sealed record Record(string Text, string? Model, DateTimeOffset CreatedAt);
}
