using System.Net;
using System.Text;
using System.Text.Json;

namespace Lexi.Translation;

public sealed record TranslationResult(string Text, string? SourceLanguage, bool FromCache = false);

/// <summary>A user-facing translation failure; response bodies and request text are never included.</summary>
public sealed class TranslationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Opt-in online translation (English/German → Chinese, Chinese → English/German) via Google's public, keyless
/// endpoint — not the supported Cloud API; using it is the user's deliberate choice (see CLAUDE.md). No Claude
/// dependency, credentials, disk cache or background requests. The endpoint may change or rate-limit.
/// </summary>
public sealed class GoogleTranslator : IDisposable
{
    public const int MaxCharacters = 2000;

    /// <summary>Target language codes Lexi asks for.</summary>
    public const string Chinese = "zh-CN";
    public const string English = "en";
    public const string German = "de";

    private const int CacheLimit = 128;
    private static readonly Dictionary<string, Uri> Endpoints = new[] { Chinese, English, German }.ToDictionary(
        t => t,
        t => new Uri($"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={t}&dt=t&dj=1"),
        StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TranslationResult> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private long _lastRequest;

    public GoogleTranslator(HttpClient? http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };
    }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <param name="targetLanguage"><see cref="Chinese"/>, <see cref="English"/> or <see cref="German"/>; the source language is detected.</param>
    public async Task<TranslationResult> TranslateAsync(string input, string targetLanguage = Chinese, bool bypassCache = false, CancellationToken cancellationToken = default)
    {
        if (!Endpoints.TryGetValue(targetLanguage, out var endpoint))
        {
            throw new ArgumentOutOfRangeException(nameof(targetLanguage), targetLanguage, "Unsupported target language.");
        }

        var text = input.Trim();
        if (text.Length == 0 || text.Length > MaxCharacters)
        {
            throw new TranslationException($"请输入 1–{MaxCharacters} 个字符的句子，较长的内容请分段翻译。");
        }

        var key = targetLanguage + "\n" + text;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bypassCache && _cache.TryGetValue(key, out var cached))
            {
                return cached with { FromCache = true };
            }

            var remaining = MinInterval.TotalMilliseconds - (Environment.TickCount64 - _lastRequest);
            if (remaining > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellationToken);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            TranslationResult result;
            try
            {
                // Keep the user's sentence in the request body, out of the URL and diagnostic messages.
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]),
                };
                _lastRequest = Environment.TickCount64;
                using var response = await _http.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new TranslationException(response.StatusCode switch
                    {
                        HttpStatusCode.TooManyRequests => "Google 翻译请求过于频繁，请稍后重试。",
                        HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "Google 翻译暂时拒绝访问，请稍后重试，或在命令菜单中打开 Google 翻译网页。",
                        _ => $"Google 翻译暂时不可用（HTTP {(int)response.StatusCode}），请稍后重试。",
                    });
                }

                result = Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                timeout.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TranslationException("在线翻译超时，请检查网络后重试。");
            }
            catch (HttpRequestException)
            {
                throw new TranslationException("无法连接 Google 翻译，请检查网络后重试。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_cache.ContainsKey(key))
            {
                if (_cache.Count >= CacheLimit)
                {
                    _cache.Remove(_cacheOrder.Dequeue());
                }

                _cacheOrder.Enqueue(key);
            }

            _cache[key] = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The Google Translate web page for the same request (no request is made here).</summary>
    public static string WebUrl(string text, string targetLanguage) =>
        $"https://translate.google.com/?sl=auto&tl={targetLanguage}&text={Uri.EscapeDataString(text)}&op=translate";

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }

        // Do not dispose the semaphore while an outstanding request still needs to release it.
    }

    private static TranslationResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sentences", out var sentences) && sentences.ValueKind == JsonValueKind.Array)
            {
                var translated = new StringBuilder();
                var complete = true;
                foreach (var sentence in sentences.EnumerateArray())
                {
                    if (sentence.ValueKind == JsonValueKind.Object && sentence.TryGetProperty("trans", out var part) && part.ValueKind == JsonValueKind.String)
                    {
                        translated.Append(part.GetString());
                    }
                    else
                    {
                        complete = false;
                        break;
                    }
                }

                var value = translated.ToString();
                if (complete && !string.IsNullOrWhiteSpace(value))
                {
                    var language = root.TryGetProperty("src", out var source) && source.ValueKind == JsonValueKind.String ? source.GetString() : null;
                    return new TranslationResult(value, language);
                }
            }
        }
        catch (JsonException)
        {
            // The public endpoint can change. Never display its raw body as a translation.
        }

        throw new TranslationException("Google 翻译未返回有效译文，接口可能暂时不可用。可重试或在命令菜单中打开 Google 翻译网页。");
    }
}
