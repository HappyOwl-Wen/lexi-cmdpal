using Lexi.AI;
using Lexi.Core.Search;
using Lexi.Extension.Services;
using Lexi.Translation;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Lexi.Extension.Pages;

/// <summary>
/// A separate, opt-in online translator; when AI is on, the Claude Code translation is offered next to it.
/// Typing never opens the dictionary or sends a request.
/// </summary>
internal sealed partial class SentenceTranslationPage : DynamicListPage, IDisposable
{
    private readonly ServiceHub _hub;
    private readonly Lock _gate = new();
    private IListItem[] _items;

    public SentenceTranslationPage(ServiceHub hub)
    {
        _hub = hub;
        Id = "com.lexi.sentenceTranslation";
        Name = "打开句子翻译";
        Title = "Lexi 句子翻译 · 英语/德语 → 中文";
        Icon = Glyphs.Translate;
        PlaceholderText = "输入英语或德语句子，按 Enter 翻译成中文…";
        ShowDetails = true;
        _items = BuildItems(string.Empty);
        _hub.Settings.Changed += OnSettingsChanged;
    }

    public override IListItem[] GetItems()
    {
        lock (_gate)
        {
            return _items;
        }
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => Refresh();

    public void Dispose() => _hub.Settings.Changed -= OnSettingsChanged;

    internal static ListItem TranslationItem(ServiceHub hub, string sentence) => new(
        new TranslationResultPage(hub, sentence))
    {
        Title = "翻译这句话",
        Subtitle = "英语/德语 → 中文 · 按 Enter 使用 Google 在线翻译",
        Icon = Glyphs.Translate,
        Tags = [new Tag("在线翻译")],
        Details = new Details
        {
            Title = "句子翻译 · 英语/德语 → 中文",
            Body = $"**原文**\n\n{sentence}\n\n---\n按 **Enter** 将这段文字发送至 **Google 翻译**并获取中文译文。\n\n使用 Google 的非官方免 Key 接口（不是官方 Cloud API，可能限流或失效），不调用 Claude Code。支持取消、复制译文和重新翻译；最近的译文仅缓存在本次运行的内存中。",
        },
    };

    /// <summary>The same sentence through Claude Code: a translation plus key words or grammar, labelled as AI.</summary>
    internal static ListItem AiTranslationItem(ServiceHub hub, string sentence) => new(
        new AiPage(hub, "AI 翻译这句话", () => new AiRequest(AiTaskKind.TranslateSentence, sentence)))
    {
        Title = "AI 翻译这句话",
        Subtitle = "英语/德语 → 中文 · 按 Enter 调用 Claude Code 翻译并讲解要点",
        Icon = Glyphs.Ai,
        Tags = [new Tag("AI")],
        Details = new Details
        {
            Title = "AI 翻译 · 英语/德语 → 中文",
            Body = $"**原文**\n\n{sentence}\n\n---\n按 **Enter** 调用本机 **Claude Code**（`claude -p`），给出中文译文，并讲解 1–3 个关键词或语法点。\n\n- 只在你按下回车时请求，会消耗你的 Claude Code 用量；\n- 回答会明确标注为 **AI 生成**；\n- 支持取消、重新生成，结果会缓存 30 天。",
        },
    };

    private void OnSettingsChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        int count;
        lock (_gate)
        {
            _items = BuildItems(base.SearchText);
            count = _items.Length;
        }

        RaiseItemsChanged(count);
    }

    private IListItem[] BuildItems(string text)
    {
        if (!_hub.Settings.SentenceTranslationEnabled)
        {
            return [Hint("句子翻译已关闭", "在 Lexi 设置中启用「独立句子翻译（英语/德语 → 中文）」")];
        }

        var query = QueryInfo.Parse(text);
        if (query.Clean.Length > GoogleTranslator.MaxCharacters)
        {
            return [Hint("句子过长，请分段翻译", $"每次最多 {GoogleTranslator.MaxCharacters} 个字符")];
        }
        if (query.IsEmpty)
        {
            return [Hint("输入英语或德语句子", "例如 The weather is nice today. 或 Guten Morgen! · 按 Enter 翻译")];
        }

        if (query.HasCjk)
        {
            return [Hint("本页用于英语/德语 → 中文", "中文查询请使用 Lexi 词典")];
        }

        // Count words containing letters, so punctuation and numbers cannot turn a single word into a sentence.
        // Two-word phrases are accepted here because the user explicitly chose the sentence translator.
        if (!CanTranslate(query))
        {
            return [Hint("请输入句子或短语", "单个单词请使用 Lexi 词典；本页接受至少两个词，例如 Guten Morgen")];
        }

        return _hub.AiEnabled
            ? [TranslationItem(_hub, query.Clean), AiTranslationItem(_hub, query.Clean)]
            : [TranslationItem(_hub, query.Clean)];
    }

    internal static bool CanTranslate(QueryInfo query) => !query.HasCjk
        && query.Clean.Length <= GoogleTranslator.MaxCharacters
        && query.Clean.Split(' ').Count(part => part.Any(char.IsLetter)) >= 2;

    private static ListItem Hint(string title, string subtitle) => new(new NoOpCommand())
    {
        Title = title,
        Subtitle = subtitle,
        Icon = Glyphs.Info,
    };
}
