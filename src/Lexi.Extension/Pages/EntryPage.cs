using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Dictionary.View;
using Lexi.Extension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Lexi.Extension.Pages;

/// <summary>Full dictionary entry (all senses, examples, grammar tables, forms, sources) as a native content page.</summary>
internal sealed partial class EntryPage : ContentPage
{
    private readonly ServiceHub _hub;
    private readonly SearchHit? _hit;
    private readonly string _lookupText;
    private readonly Lang? _lookupLang;
    private WordRecord? _word;

    public EntryPage(ServiceHub hub, WordRecord word, SearchHit? hit)
        : this(hub, word.Word, word.Lang)
    {
        _word = word;
        _hit = hit;
        Title = WordItems.DisplayTitle(word);
        Commands = WordItems.ContextCommands(hub, word, null);
    }

    /// <summary>Entry resolved by exact lookup when opened (used for history items).</summary>
    public EntryPage(ServiceHub hub, string word, Lang? lang)
    {
        _hub = hub;
        _lookupText = word;
        _lookupLang = lang;
        Name = "查看完整词条";
        Title = word;
        Icon = Glyphs.Dictionary;
    }

    public override IContent[] GetContent()
    {
        var word = _word ??= Resolve();
        if (word is null)
        {
            return [new MarkdownContent($"# {MarkdownRenderer.Escape(_lookupText)}\n\n词典中没有找到这个词。{_hub.Dictionary.ErrorMessage}")];
        }

        if (Commands.Length == 0)
        {
            Commands = WordItems.ContextCommands(_hub, word, null);
        }

        var view = _hub.Dictionary.GetView(word);
        if (view is null)
        {
            return [new MarkdownContent($"# {MarkdownRenderer.Escape(word.Word)}\n\n⚠️ 词典不可用：{_hub.Dictionary.ErrorMessage}")];
        }

        _hub.History.Add(word.Word, word.Lang, word.Brief);
        var body = MarkdownRenderer.Full(view);
        if (_hit is { IsForm: true } || _hit is { IsCorrection: true } || _hit?.Kind is MatchKind.Translit or MatchKind.Fold or MatchKind.Chinese or MatchKind.ChinesePart)
        {
            // Repeat why this entry was shown (e.g. "Häuser is the plural of Haus", "房子 → house").
            var details = MarkdownRenderer.Details(view, _hit, includeTitle: false);
            var note = details.Split('\n').FirstOrDefault(l => l.StartsWith("> ", StringComparison.Ordinal));
            if (note is not null)
            {
                var firstBreak = body.IndexOf('\n', StringComparison.Ordinal);
                body = body[..(firstBreak + 1)] + "\n" + note + "\n" + body[(firstBreak + 1)..];
            }
        }

        return [new MarkdownContent(body)];
    }

    private WordRecord? Resolve()
    {
        var result = _hub.Dictionary.Lookup(_lookupText);
        return result.Hits.FirstOrDefault(h => h.Word.Word == _lookupText && (_lookupLang is null || h.Word.Lang == _lookupLang))?.Word
            ?? (result.Hits.Count > 0 ? result.Hits[0].Word : null);
    }
}
