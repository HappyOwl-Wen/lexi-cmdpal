using System.Buffers;
using Lexi.AI;
using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Core.Text;
using Lexi.Dictionary.View;
using Lexi.Extension.Commands;
using Lexi.Translation;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Lexi.Extension.Pages;

/// <summary>
/// Chinese input (中 → 英/德): the dictionary's English and German equivalents in two sections. The optional
/// translations — Google (online) and Claude Code (AI) — are separate items, never mixed into the dictionary results.
/// </summary>
internal sealed partial class DictionaryPage
{
    internal const string EnglishSection = "英语 English";
    internal const string GermanSection = "德语 Deutsch";
    internal const string TranslationSection = "翻译";

    // Command Palette shows about eight result rows. Six per language leaves room for the German heading and its first
    // rows on the first screen; translations placed first take the room of their heading and one row each.
    private const int ChinesePerLanguage = 6;

    // Longer input, or input with sentence punctuation, is a sentence rather than a word to look up.
    private const int MaxChineseWordLength = 8;
    private static readonly SearchValues<char> SentencePunctuation = SearchValues.Create("，。？！；：,?!;");

    private IListItem[] BuildChineseResults(QueryInfo q, CancellationToken ct)
    {
        var items = new List<IListItem>();
        var sentence = IsChineseSentence(q.Clean);
        if (!_hub.Dictionary.EnsureLoaded())
        {
            AddChineseTranslations(items, q.Clean, [], sentence);
            items.Add(MissingDatabaseItem());
            return WithSectionHeaders(items);
        }

        if (_hub.Dictionary.Info is { ChineseTerms: 0 })
        {
            AddChineseTranslations(items, q.Clean, [], sentence);
            items.Add(MissingChineseIndexItem());
            return WithSectionHeaders(items);
        }

        // The engine splits the limit between the languages (or gives it all to the filtered one).
        var result = _hub.Dictionary.Search(q.Clean, _filters.Selected, 2 * ChinesePerLanguage, ct);
        ct.ThrowIfCancellationRequested();

        // A word with whole-term matches: dictionary first, so Enter opens the best equivalent. A sentence, or input
        // only partly found: the translations first.
        var translationsFirst = sentence || !result.Hits.Any(h => h.Kind == MatchKind.Chinese);
        var leading = translationsFirst ? AddChineseTranslations(items, q.Clean, result.Hits, sentence) : 0;
        var perLanguage = _filters.Selected is not null ? int.MaxValue
            : leading > 0 ? ChinesePerLanguage - leading - 1
            : ChinesePerLanguage;
        var shown = new Dictionary<Lang, int>();
        foreach (var hit in result.Hits)
        {
            var count = shown.GetValueOrDefault(hit.Word.Lang);
            if (count == perLanguage)
            {
                continue;
            }

            shown[hit.Word.Lang] = count + 1;
            var item = WordItems.Create(_hub, hit, result.Query);
            item.Section = hit.Word.Lang == Lang.De ? GermanSection : EnglishSection;
            items.Add(item);
        }

        if (!translationsFirst)
        {
            AddChineseTranslations(items, q.Clean, result.Hits, sentence);
        }

        if (items.Count == 0)
        {
            EmptyContent = new CommandItem(new NoOpCommand())
            {
                Title = $"没有找到「{q.Clean}」的英语或德语对应词",
                Subtitle = "换一个更常用的说法试试，或切换语言筛选",
                Icon = Glyphs.Info,
            };
        }

        return WithSectionHeaders(items);
    }

    /// <summary>
    /// Command Palette draws a heading only for a list item without a command (the toolkit's <see cref="Separator"/>);
    /// the Section of an ordinary item is not shown. Inserts such a heading wherever the section changes.
    /// </summary>
    internal static IListItem[] WithSectionHeaders(IEnumerable<IListItem> items)
    {
        var result = new List<IListItem>();
        string? current = null;
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Section) && item.Section != current)
            {
                result.Add(new Separator(item.Section));
            }

            current = item.Section;
            result.Add(item);
        }

        return [.. result];
    }

    internal static bool IsChineseSentence(string text) =>
        ChineseTerms.QueryKey(text).Length > MaxChineseWordLength || text.AsSpan().IndexOfAny(SentencePunctuation) >= 0;

    /// <summary>Adds the Google item (online translation on) and the Claude Code item (AI on); returns how many.</summary>
    private int AddChineseTranslations(List<IListItem> items, string text, IReadOnlyList<SearchHit> hits, bool sentence)
    {
        var count = items.Count;
        if (_hub.SentenceTranslationEnabled && text.Length <= GoogleTranslator.MaxCharacters)
        {
            items.Add(new ListItem(new TranslationResultPage(_hub, text, [GoogleTranslator.English, GoogleTranslator.German]))
            {
                Title = "Google 翻译成英语和德语",
                Subtitle = "在线翻译 · 按 Enter 才联网（Google 非官方免 Key 接口）",
                Icon = Glyphs.Translate,
                Tags = [new Tag("在线翻译")],
                Section = TranslationSection,
                Details = new Details
                {
                    Title = "在线翻译 · 中文 → 英语 / 德语",
                    Body = $"**原文**\n\n{text}\n\n---\n按 **Enter** 把这段中文发送到 **Google 翻译**，依次取得英语和德语译文。\n\n- 使用 Google 的非官方免 Key 接口（不是官方 Cloud API），可能限流或失效；\n- 不调用 Claude Code，不需要 API Key；\n- 支持取消、复制和重新翻译；译文只缓存在本次运行的内存中。",
                },
            });
        }

        if (_hub.AiEnabled)
        {
            items.Add(ChineseAiItem(text, hits, sentence));
        }

        return items.Count - count;
    }

    private ListItem ChineseAiItem(string text, IReadOnlyList<SearchHit> hits, bool sentence)
    {
        var title = sentence ? "AI 翻译成英语和德语" : $"AI 讲解「{text}」的英语和德语说法";
        var context = AiContext.ChineseCandidates(hits);
        return new ListItem(new AiPage(_hub, title, () => new AiRequest(AiTaskKind.TranslateChinese, text, null, context)))
        {
            Title = title,
            Subtitle = sentence ? "按 Enter 调用 Claude Code 翻译这句话" : "按 Enter 让 Claude Code 给出常用译法、区别和例句",
            Icon = Glyphs.Ai,
            Tags = [new Tag("AI")],
            Section = TranslationSection,
            Details = new Details
            {
                Title = title,
                Body = "按 **Enter** 调用本机 **Claude Code**（`claude -p`），把中文翻译成英语和德语。\n\n- 只在你按下回车时请求，会消耗你的 Claude Code 用量；\n- 回答会明确标注为 **AI 生成**，不会写入词典数据；\n"
                    + (context is null ? string.Empty : "- 词典给出的对应词会一并发给 AI 作参考；\n")
                    + "- 支持取消、重新生成，结果会缓存 30 天。",
            },
        };
    }

    private static ListItem MissingChineseIndexItem()
    {
        const string command = "powershell -ExecutionPolicy Bypass -File scripts\\import-data.ps1";
        return new ListItem(new CopyCommand("复制导入命令", "导入命令", () => command))
        {
            Title = "当前词典数据库还不支持中文反查",
            Subtitle = "重新导入一次词典数据即可（会建立中文 → 英语/德语索引）",
            Icon = Glyphs.Warning,
            Details = new Details
            {
                Title = "更新词典数据库",
                Body = $"这个词典数据库是在中文反查功能之前构建的，里面没有中文索引。\n\n在 Lexi 源码目录的**普通终端**（开始菜单里的 PowerShell 或 Windows Terminal）中运行：\n\n`{command}`\n\n已下载的原始数据会直接复用，导入约需 10 分钟。完成后重新打开 Lexi。",
            },
        };
    }
}
