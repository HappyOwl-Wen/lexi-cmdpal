using System.Net;
using System.Security.Cryptography;
using System.Text;
using Lexi.Core.Models;
using Lexi.Dictionary.View;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Lexi.Extension.Services;

internal enum PronunciationKind
{
    /// <summary>Real recording (Wikimedia Commons, referenced by Wiktionary).</summary>
    Recording,
    /// <summary>Windows text-to-speech.</summary>
    Synthesized,
    None,
}

internal sealed record PronunciationOutcome(PronunciationKind Kind, string Message);

/// <summary>
/// Plays a pronunciation without any UI of its own, in the order of <see cref="PronunciationPlan"/> (English: British
/// first): a cached Commons recording (downloaded on first use, then offline), a Windows voice, or a clear "none
/// available". The message says which accent and which kind was played, and why when it is a fallback.
/// </summary>
internal sealed partial class Pronouncer : IDisposable
{
    // Wikimedia's robot policy counts requests per client: one download at a time in the process, and none at all
    // while a 429 Retry-After is running.
    private static readonly SemaphoreSlim Downloads = new(1, 1);
    private static long s_commonsPausedUntil;

    private readonly string _cacheDirectory;
    private readonly Action<MediaSource>? _output;
    private readonly HttpClient _http;
    private readonly Lock _lock = new();
    private MediaPlayer? _player;
    private SpeechSynthesizer? _synth;

    /// <param name="output">Receives the audio instead of the speakers (the self-test plays nothing); null plays it.</param>
    public Pronouncer(string dataDirectory, Action<MediaSource>? output = null)
    {
        _cacheDirectory = Path.Combine(dataDirectory, "audio-cache");
        _output = output;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Lexi/0.1 (PowerToys Command Palette dictionary extension; personal use)");

        // HttpClient sends no Accept header by default; Wikimedia's front end answers such requests with HTTP 429
        // ("does not comply with our robot policy") unless the file happens to be in its cache.
        _http.DefaultRequestHeaders.Accept.ParseAdd("audio/mpeg, audio/*;q=0.9, */*;q=0.5");
    }

    /// <summary>Why the last recording download failed (HTTP status, timeout, …); null after a success.</summary>
    public string? LastDownloadError { get; private set; }

    /// <summary>Time left before Wikimedia Commons may be asked again after HTTP 429; zero when not paused.</summary>
    public static TimeSpan CommonsPause
    {
        get
        {
            var left = new DateTime(Interlocked.Read(ref s_commonsPausedUntil), DateTimeKind.Utc) - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <param name="accent">Null: British first for English, other accents as a fallback; "UK" / "US": that accent only.</param>
    public async Task<PronunciationOutcome> PlayAsync(WordView view, string? accent, CancellationToken ct)
    {
        var voices = SpeechSynthesizer.AllVoices;
        var plan = PronunciationPlan.For(view, accent, voices.Select(v => v.Language).ToList());
        var english = view.Word.Lang == Lang.En;
        var wanted = accent == PronunciationPlan.American ? PronunciationPlan.American : PronunciationPlan.British;
        string? downloadError = null;
        foreach (var step in plan)
        {
            if (step.Recording is { } audio)
            {
                var file = await EnsureCachedAsync(audio, ct).ConfigureAwait(false);
                if (file is null)
                {
                    downloadError ??= LastDownloadError ?? "离线？";
                    continue;
                }

                Play(MediaSource.CreateFromUri(new Uri(file)));
                var name = PronunciationPlan.AccentName(audio.Region);
                var why = step.OtherAccent ? $"；{FallbackReason(view, wanted, voices, downloadError)}" : string.Empty;
                return new(PronunciationKind.Recording, $"🔊 {name}真人录音（Wikimedia Commons：{audio.File}{why}）");
            }

            var voice = voices.First(v => v.Language == step.VoiceLanguage);
            SpeechSynthesizer synth;
            lock (_lock)
            {
                _synth ??= new SpeechSynthesizer();
                _synth.Voice = voice;
                synth = _synth;
            }

            var stream = await synth.SynthesizeTextToStreamAsync(view.Word.Word);
            Play(MediaSource.CreateFromStream(stream, stream.ContentType));
            var voiceAccent = english ? (voice.Language.Equals("en-GB", StringComparison.OrdinalIgnoreCase) ? "英式" : voice.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? "美式" : string.Empty) : string.Empty;
            var reason = step.OtherAccent
                ? FallbackReason(view, wanted, voices, downloadError)
                : downloadError is not null ? $"录音下载失败：{downloadError}" : english ? $"词典中没有{PronunciationPlan.AccentName(wanted)}录音" : "词典中没有录音";
            return new(PronunciationKind.Synthesized, $"🔈 {voiceAccent}合成语音 · {voice.DisplayName}（{reason}）");
        }

        if (english && accent is not null)
        {
            var name = PronunciationPlan.AccentName(wanted);
            var recordings = downloadError is not null ? $"{name}录音下载失败：{downloadError}" : $"词典里没有{name}录音";
            return new(PronunciationKind.None, $"没有{name}发音：{recordings}，也未安装{name}语音（{(wanted == PronunciationPlan.British ? AddBritishVoice : AddVoice)}）");
        }

        var langName = english ? "英语" : "德语";
        var none = downloadError is not null ? $"录音下载失败：{downloadError}" : "没有录音";
        return new(PronunciationKind.None, $"无可用发音：{none}，且未安装{langName}语音（{AddVoice}）");
    }

    private const string AddVoice = "设置 → 时间和语言 → 语音 → 添加语音";
    private const string AddBritishVoice = "设置 → 时间和语言 → 语音 → 添加「英语(英国)」";

    /// <summary>Why a recording or voice of another accent is played instead of the wanted one.</summary>
    private static string FallbackReason(WordView view, string wanted, IReadOnlyList<VoiceInformation> voices, string? downloadError)
    {
        var name = PronunciationPlan.AccentName(wanted);
        var recordings = view.Audio.Any(a => PronunciationPlan.IsAccent(a.Region, wanted))
            ? $"{name}录音下载失败：{downloadError}"
            : $"没有{name}录音";
        var voiceLanguage = wanted == PronunciationPlan.American ? "en-US" : "en-GB";
        return voices.Any(v => v.Language.Equals(voiceLanguage, StringComparison.OrdinalIgnoreCase))
            ? recordings
            : $"{recordings}，也未安装{name}语音（{(wanted == PronunciationPlan.British ? AddBritishVoice : AddVoice)}）";
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _player?.Dispose();
            _player = null;
            _synth?.Dispose();
            _synth = null;
        }

        _http.Dispose();
    }

    /// <summary>Commons transcodes every audio file to MP3; the path uses the MD5 of the file name.</summary>
    internal static string CommonsMp3Url(string file)
    {
        var name = file.Replace(' ', '_');
#pragma warning disable CA5351 // MD5 is mandated by the Commons upload path scheme; not used for security.
        var md5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(name)));
#pragma warning restore CA5351
        var escaped = Uri.EscapeDataString(name);
        return name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            ? $"https://upload.wikimedia.org/wikipedia/commons/{md5[0]}/{md5[..2]}/{escaped}"
            : $"https://upload.wikimedia.org/wikipedia/commons/transcoded/{md5[0]}/{md5[..2]}/{escaped}/{escaped}.mp3";
    }

    internal static string RecordingUrl(AudioView audio) => string.IsNullOrEmpty(audio.Mp3Url) ? CommonsMp3Url(audio.File) : audio.Mp3Url;

    /// <summary>
    /// Downloads the recording into the audio cache (once); null when unavailable. After HTTP 429 nothing is requested
    /// until its Retry-After has passed, so pronunciation falls back to the next step at once.
    /// </summary>
    internal async Task<string?> EnsureCachedAsync(AudioView audio, CancellationToken ct)
    {
        var url = RecordingUrl(audio);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24];
        var path = Path.Combine(_cacheDirectory, key + ".mp3");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        await Downloads.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = CommonsPause;
            if (wait > TimeSpan.Zero)
            {
                LastDownloadError = RateLimited(wait);
                return null;
            }

            // One retry for transient failures (5xx, dropped connection); rate limiting is waited out instead.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(_cacheDirectory);
                    using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var retryAfter = response.Headers.RetryAfter is { } r ? r.Delta ?? (r.Date - DateTimeOffset.UtcNow) : null;
                        var pause = TimeSpan.FromSeconds(Math.Clamp(retryAfter?.TotalSeconds ?? 30, 1, 600));
                        Interlocked.Exchange(ref s_commonsPausedUntil, (DateTime.UtcNow + pause).Ticks);
                        LastDownloadError = RateLimited(pause);
                        return null;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        LastDownloadError = $"HTTP {(int)response.StatusCode}";
                        if (attempt == 1 && (int)response.StatusCode >= 500)
                        {
                            await Task.Delay(500, ct).ConfigureAwait(false);
                            continue;
                        }

                        return null;
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if (bytes.Length == 0)
                    {
                        LastDownloadError = "空文件";
                        return null;
                    }

                    var tmp = path + ".part";
                    await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                    File.Move(tmp, path, overwrite: true);
                    LastDownloadError = null;
                    return path;
                }
                catch (HttpRequestException ex) when (attempt == 1)
                {
                    LastDownloadError = ex.Message;
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
                {
                    LastDownloadError = ex is TaskCanceledException ? "超时" : ex.Message;
                    return null;
                }
            }
        }
        finally
        {
            Downloads.Release();
        }
    }

    private static string RateLimited(TimeSpan wait) => $"Wikimedia 限流，{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} 秒后再试";

    private void Play(MediaSource source)
    {
        if (_output is not null)
        {
            _output(source);
            return;
        }

        lock (_lock)
        {
            _player?.Dispose();
            _player = new MediaPlayer { AutoPlay = true, Source = source };
        }
    }
}
