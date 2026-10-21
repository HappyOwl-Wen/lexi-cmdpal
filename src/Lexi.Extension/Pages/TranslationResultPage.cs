using Lexi.Extension.Commands;
using Lexi.Extension.Services;
using Lexi.Translation;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Lexi.Extension.Pages;

/// <summary>
/// Requests online translations on open — one target language, or several in turn (中文 → 英语和德语) — with
/// cancellation, copy and retry. No AI service is involved.
/// </summary>
internal sealed partial class TranslationResultPage : ContentPage, IDisposable
{
    private readonly ServiceHub _hub;
    private readonly string _sentence;
    private readonly IReadOnlyList<string> _targets;
    private readonly MarkdownContent _content = new();
    private readonly Lock _gate = new();
    private readonly CommandContextItem _cancel;
    private readonly CommandContextItem _retry;
    private readonly CommandContextItem[] _browser;
    private CancellationTokenSource? _cts;
    private bool _started;

    /// <param name="targets">Target languages, translated in this order; Chinese when omitted.</param>
    public TranslationResultPage(ServiceHub hub, string sentence, IReadOnlyList<string>? targets = null)
    {
        _hub = hub;
        _sentence = sentence;
        _targets = targets is { Count: > 0 } ? targets : [GoogleTranslator.Chinese];
        var single = _targets.Count == 1;
        Name = single && _targets[0] == GoogleTranslator.Chinese ? "翻译这句话" : $"Google 翻译成{string.Join("和", _targets.Select(LanguageName))}";
        Title = single ? "句子翻译 · Google" : "在线翻译 · Google";
        Icon = Glyphs.Translate;
        _cancel = new CommandContextItem(new AnonymousCommand(Cancel) { Name = "取消翻译", Icon = Glyphs.Cancel, Result = CommandResult.KeepOpen() });
        _retry = new CommandContextItem(new AnonymousCommand(() => Start(bypassCache: true)) { Name = "重新翻译", Icon = Glyphs.Refresh, Result = CommandResult.KeepOpen() });
        _browser =
        [
            .. _targets.Select(t => new CommandContextItem(new OpenUrlCommand(GoogleTranslator.WebUrl(sentence, t))
            {
                Name = single ? "在 Google 翻译网页中打开" : $"在 Google 翻译网页中打开（{LanguageName(t)}）",
                Icon = Glyphs.Globe,
            })),
        ];
        Commands = [_cancel, .. _browser];
    }

    public override IContent[] GetContent()
    {
        lock (_gate)
        {
            if (!_started)
            {
                _started = true;
                Start(bypassCache: false);
            }
        }

        return [_content];
    }

    /// <summary>Chinese name of a target or detected source language ("zh-CN", "en", "de").</summary>
    internal static string LanguageName(string code) => code switch
    {
        GoogleTranslator.English => "英语",
        GoogleTranslator.German => "德语",
        _ when code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) => "中文",
        _ => "自动检测",
    };

    private void Start(bool bypassCache)
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            if (!_hub.SentenceTranslationEnabled)
            {
                _content.Body = "在线翻译已关闭，请在 Lexi 设置中重新启用。";
                Commands = [_retry];
                IsLoading = false;
                return;
            }

            _cts = new CancellationTokenSource();
            IsLoading = true;
            Commands = [_cancel, .. _browser];
            var into = _targets.Count > 1 ? $"（{string.Join("、", _targets.Select(LanguageName))}）" : string.Empty;
            _content.Body = $"正在请求 **Google 翻译**{into}…\n\n按 Enter 可取消。";
            _ = CompleteAsync(_cts, bypassCache);
        }
    }

    private async Task CompleteAsync(CancellationTokenSource cts, bool bypassCache)
    {
        var single = _targets.Count == 1;
        var sections = new List<string>();
        var copies = new List<CommandContextItem>();
        var fromCache = true;
        string body;
        try
        {
            foreach (var target in _targets)
            {
                try
                {
                    var result = await _hub.Translation.TranslateAsync(_sentence, target, bypassCache, cts.Token);
                    fromCache &= result.FromCache;
                    sections.Add($"**Google 翻译 · {LanguageName(result.SourceLanguage ?? string.Empty)} → {LanguageName(target)}**\n\n{result.Text}");
                    var text = result.Text;
                    copies.Add(new CommandContextItem(new CopyCommand(single ? "复制译文" : $"复制{LanguageName(target)}译文", "译文", () => text)));
                }
                catch (TranslationException ex)
                {
                    // With several languages, a failed one is reported in its place and the others are kept.
                    fromCache = false;
                    sections.Add(single ? "⚠️ " + ex.Message : $"**Google 翻译 · → {LanguageName(target)}**\n\n⚠️ {ex.Message}");
                }
            }

            body = single && copies.Count == 0
                ? sections[0]
                : $"{string.Join("\n\n", sections)}\n\n---\n**原文**\n\n{_sentence}\n\n*在线机器翻译{(fromCache ? " · 来自本次运行的缓存" : string.Empty)}*";
        }
        catch (OperationCanceledException)
        {
            copies.Clear();
            body = "已取消翻译。按 Enter 可重新翻译。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            copies.Clear();
            body = "⚠️ 在线翻译暂时失败，请重试，或在命令菜单中打开 Google 翻译网页。";
        }

        lock (_gate)
        {
            // A canceled or replaced response must never replace a newer translation.
            if (!ReferenceEquals(_cts, cts))
            {
                return;
            }

            if (!_hub.SentenceTranslationEnabled)
            {
                copies.Clear();
                body = "在线翻译已关闭，请在 Lexi 设置中重新启用。";
            }
            else if (cts.IsCancellationRequested)
            {
                copies.Clear();
                body = "已取消翻译。按 Enter 可重新翻译。";
            }

            _content.Body = body;
            Commands = [.. copies, _retry, .. _browser];
            IsLoading = false;
        }
    }

    private void Cancel()
    {
        lock (_gate)
        {
            _cts?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
