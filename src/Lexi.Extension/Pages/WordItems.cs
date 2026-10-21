using Lexi.AI;
using Lexi.Core.Models;
using Lexi.Core.Morphology;
using Lexi.Core.Search;
using Lexi.Dictionary.View;
using Lexi.Extension.Commands;
using Lexi.Extension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.System;

namespace Lexi.Extension.Pages;

/// <summary>List item whose details are built only when the host asks for them (i.e. when the item is selected).</summary>
internal sealed partial class WordListItem : ListItem
{
    private readonly Lazy<IDetails?> _details;

    public WordListItem(ICommand command, Func<IDetails?> details)
        : base(command)
    {
        _details = new Lazy<IDetails?>(() =>
        {
            try
            {
                return details();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new Details { Title = "无法加载详情", Body = ex.Message };
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public override IDetails? Details
    {
        get => _details.Value;
        set => base.Details = value;
    }
}

/// <summary>Builds list items and their context commands for dictionary hits.</summary>
internal static class WordItems
{
    public static string DisplayTitle(WordRecord w) =>
        w.Lang == Lang.De && w.Badge is "der" or "die" or "das" or "der/die" or "der/das" or "die/das" or "der/die/das"
            ? $"{w.Badge} {w.Word}"
            : w.Word;

    public static ListItem Create(ServiceHub hub, SearchHit hit, QueryInfo query)
    {
        var w = hit.Word;
        var item = new WordListItem(new EntryPage(hub, w, hit), () => BuildDetails(hub, hit))
        {
            Title = DisplayTitle(w),
            Subtitle = Subtitle(hit),
            Tags = BuildTags(hit),
            MoreCommands = ContextCommands(hub, w, query.Clean),
        };

        // Right arrow in the search box accepts a spelling correction.
        if (hit.IsCorrection || hit.Kind is MatchKind.Fold or MatchKind.Translit)
        {
            item.TextToSuggest = w.Word;
        }

        return item;
    }

    public static IContextItem[] ContextCommands(ServiceHub hub, WordRecord w, string? query)
    {
        var items = new List<IContextItem>
        {
            // First item = secondary command (Ctrl+Enter).
            new CommandContextItem(new CopyCommand("复制释义", "释义", () => hub.Dictionary.GetView(w) is { } v ? MarkdownRenderer.PlainDefinition(v) : null)),
            new CommandContextItem(new PronounceCommand(hub, w))
            {
                RequestedShortcut = KeyChordHelpers.FromModifiers(ctrl: true, vkey: VirtualKey.P),
            },
            new CommandContextItem(new CopyCommand("复制单词", "单词", () => w.Word))
            {
                RequestedShortcut = KeyChordHelpers.FromModifiers(ctrl: true, shift: true, vkey: VirtualKey.C),
            },
        };

        if (w.Lang == Lang.En)
        {
            items.Add(new CommandContextItem(new PronounceCommand(hub, w, "UK")));
            items.Add(new CommandContextItem(new PronounceCommand(hub, w, "US")));
        }

        if (hub.AiEnabled)
        {
            items.Add(new CommandContextItem(AiCommand(hub, w, AiTaskKind.ExplainWord))
            {
                RequestedShortcut = KeyChordHelpers.FromModifiers(ctrl: true, vkey: VirtualKey.I),
            });
            if (w.Lang == Lang.De)
            {
                items.Add(new CommandContextItem(AiCommand(hub, w, AiTaskKind.ExplainGrammar))
                {
                    RequestedShortcut = KeyChordHelpers.FromModifiers(ctrl: true, vkey: VirtualKey.G),
                });
            }

            items.Add(new CommandContextItem(AiCommand(hub, w, AiTaskKind.CompareWords)));
            if (hub.Settings.Domain is { } domain)
            {
                items.Add(new CommandContextItem(AiCommand(hub, w, AiTaskKind.ExplainInContext, domain)));
            }
        }

        var wiktionary = w.Lang == Lang.De ? "https://de.wiktionary.org/wiki/" : "https://en.wiktionary.org/wiki/";
        items.Add(new CommandContextItem(new OpenUrlCommand(wiktionary + Uri.EscapeDataString(w.Word.Replace(' ', '_'))) { Name = "在 Wiktionary 中打开（浏览器）", Icon = Glyphs.Globe }));
        return [.. items];
    }

    /// <summary>A page command for an AI task about a dictionary word (the request starts when the page opens).</summary>
    public static ICommand AiCommand(ServiceHub hub, WordRecord w, AiTaskKind kind, string? domain = null, IReadOnlyList<string>? compareWith = null)
    {
        var name = kind switch
        {
            AiTaskKind.ExplainWord => "AI 解释词义与用法",
            AiTaskKind.ExplainGrammar => "AI 讲解德语语法",
            AiTaskKind.CompareWords => compareWith is { Count: > 0 } ? $"AI 辨析：{string.Join(" · ", compareWith)}" : "AI 易混词辨析",
            AiTaskKind.ExplainInContext => $"AI 专业语境解释（{domain}）",
            _ => "AI",
        };
        return new AiPage(hub, name, () =>
        {
            var view = hub.Dictionary.GetView(w);
            var others = compareWith ?? (view is null ? [] : AiContext.CompareCandidates(view));
            return new AiRequest(
                kind,
                w.Word,
                AiContext.LanguageName(w.Lang),
                view is null ? null : AiContext.Describe(view),
                kind == AiTaskKind.CompareWords ? others : null,
                kind is AiTaskKind.CompareWords or AiTaskKind.ExplainInContext ? domain ?? hub.Settings.Domain : null);
        });
    }

    private static Details BuildDetails(ServiceHub hub, SearchHit hit)
    {
        var view = hub.Dictionary.GetView(hit.Word);
        if (view is null)
        {
            return new Details { Title = hit.Word.Word, Body = hub.Dictionary.ErrorMessage ?? "词典不可用" };
        }

        return new Details
        {
            Title = view.Title,
            Body = MarkdownRenderer.Details(view, hit, includeTitle: false, showGerman: hub.Settings.ShowGermanDefinitions),
            Size = ContentSize.Medium,
        };
    }

    private static string Subtitle(SearchHit hit)
    {
        var w = hit.Word;
        var brief = w.Brief ?? string.Empty;
        // ECDICT one-liners already start with their own part of speech ("n. 易受伤…"); don't add a second one.
        var badge = w.Badge is null or "der" or "die" or "das" || w.Badge.Contains('/', StringComparison.Ordinal) || StartsWithPosAbbreviation(brief)
            ? string.Empty
            : w.Badge + " ";
        return hit.Kind switch
        {
            MatchKind.Form or MatchKind.FormFold or MatchKind.FormTranslit =>
                $"{hit.Matched} → {FormDescriber.Describe(hit.FormTags, w.Lang)} · {badge}{brief}",
            MatchKind.Fuzzy or MatchKind.FuzzyForm => $"你要找的是不是 {w.Word}？ · {badge}{brief}",
            MatchKind.ChinesePart => $"部分匹配「{hit.Matched}」 · {badge}{brief}",
            // The one-line summary may come from a source that does not use the matched term (loophole: 枪眼 …).
            MatchKind.Chinese when hit.Matched is { } term && !brief.Contains(term, StringComparison.Ordinal) => $"「{term}」 · {badge}{brief}",
            _ => badge + brief,
        };
    }

    /// <summary>"n. …", "adj. …", "vt. …": a short lower-case abbreviation followed by ". ".</summary>
    internal static bool StartsWithPosAbbreviation(string text)
    {
        var dot = text.IndexOf(". ", StringComparison.Ordinal);
        return dot is > 0 and <= 5 && text.AsSpan(0, dot).IndexOfAnyExceptInRange('a', 'z') < 0;
    }

    private static ITag[] BuildTags(SearchHit hit)
    {
        var tags = new List<ITag> { new Tag(hit.Word.Lang.Tag()) { ToolTip = hit.Word.Lang.DisplayName() } };
        var label = hit.Kind switch
        {
            MatchKind.Fuzzy or MatchKind.FuzzyForm => "纠错",
            MatchKind.Form or MatchKind.FormFold or MatchKind.FormTranslit => "词形",
            MatchKind.Translit => "ae→ä",
            MatchKind.Fold => "无变音",
            MatchKind.ChinesePart => "部分匹配",
            _ => null,
        };
        if (label is not null)
        {
            tags.Add(new Tag(label));
        }

        return [.. tags];
    }
}
