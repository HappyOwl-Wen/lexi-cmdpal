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
/// Plays a pronunciation without any UI of its own: a cached Commons recording when the dictionary has one
/// (downloaded on first use, then offline), otherwise Windows TTS, otherwise a clear "none available".
/// </summary>
internal sealed partial class Pronouncer : IDisposable
{
    private readonly string _cacheDirectory;
    private readonly HttpClient _http;
    private readonly Lock _lock = new();
    private MediaPlayer? _player;
    private SpeechSynthesizer? _synth;

    public Pronouncer(string dataDirectory)
    {
        _cacheDirectory = Path.Combine(dataDirectory, "audio-cache");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Lexi/0.1 (PowerToys Command Palette dictionary extension; personal use)");
    }

    /// <summary>Why the last recording download failed (HTTP status, timeout, …); null after a success.</summary>
    public string? LastDownloadError { get; private set; }

    public async Task<PronunciationOutcome> PlayAsync(WordView view, string? preferredRegion, CancellationToken ct)
    {
        var lang = view.Word.Lang;
        foreach (var audio in view.Audio.OrderBy(a => a.Region == preferredRegion ? 0 : 1).ThenBy(a => RegionRank(lang, a.Region)))
        {
            var file = await EnsureCachedAsync(audio, ct).ConfigureAwait(false);
            if (file is not null)
            {
                Play(MediaSource.CreateFromUri(new Uri(file)));
                var region = audio.Region is null ? string.Empty : $" · {audio.Region}";
                return new(PronunciationKind.Recording, $"🔊 真人录音{region}（Wikimedia Commons：{audio.File}）");
            }
        }

        var voice = PickVoice(lang, preferredRegion);
        if (voice is not null)
        {
            SpeechSynthesizer synth;
            lock (_lock)
            {
                _synth ??= new SpeechSynthesizer();
                _synth.Voice = voice;
                synth = _synth;
            }

            var stream = await synth.SynthesizeTextToStreamAsync(view.Word.Word);
            Play(MediaSource.CreateFromStream(stream, stream.ContentType));
            var reason = view.Audio.Count == 0 ? "词典中没有录音" : $"录音下载失败：{LastDownloadError ?? "离线？"}";
            return new(PronunciationKind.Synthesized, $"🔈 系统合成语音 · {voice.DisplayName}（{reason}）");
        }

        var langName = lang == Lang.De ? "德语" : "英语";
        return new(PronunciationKind.None, $"无可用发音：没有录音，且未安装{langName}语音（设置 → 时间和语言 → 语音 → 添加语音）");
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

    /// <summary>Downloads the recording into the audio cache (once); null when unavailable.</summary>
    internal async Task<string?> EnsureCachedAsync(AudioView audio, CancellationToken ct)
    {
        var url = RecordingUrl(audio);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24];
        var path = Path.Combine(_cacheDirectory, key + ".mp3");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        // One retry for transient failures (rate limiting, 5xx, dropped connection).
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    LastDownloadError = $"HTTP {(int)response.StatusCode}";
                    if (attempt == 1 && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500))
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

    private void Play(MediaSource source)
    {
        lock (_lock)
        {
            _player?.Dispose();
            _player = new MediaPlayer { AutoPlay = true, Source = source };
        }
    }

    private static VoiceInformation? PickVoice(Lang lang, string? region)
    {
        var voices = SpeechSynthesizer.AllVoices;
        var prefix = lang == Lang.De ? "de" : "en";
        var preferred = (lang, region) switch
        {
            (Lang.En, "US") => "en-US",
            (Lang.En, _) => "en-GB",
            (Lang.De, "AT") => "de-AT",
            (Lang.De, "CH") => "de-CH",
            _ => "de-DE",
        };
        return voices.FirstOrDefault(v => v.Language.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => v.Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static int RegionRank(Lang lang, string? region) => lang == Lang.En
        ? region switch { "UK" => 0, "US" => 1, null => 2, _ => 3 }
        : region switch { null => 0, "DE" => 0, "AT" => 1, "CH" => 2, _ => 3 };
}
