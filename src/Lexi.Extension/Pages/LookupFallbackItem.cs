using Lexi.Core.Search;
using Lexi.Core.Text;
using Lexi.Extension.Services;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Lexi.Extension.Pages;

/// <summary>
/// Main-page fallback: shows a one-line definition for what the user types on the Command Palette home page,
/// e.g. "dict vulnerability" or (if enabled in settings) just "vulnerability"; for Chinese ("dict 房子") the best
/// English and German equivalents. Enter opens the Lexi page with the query pre-filled. The host calls
/// UpdateQuery on background threads for every keystroke, so only the cheap exact/inflection lookup runs here
/// (no fuzzy search, no partial Chinese matches), and stale updates are discarded.
/// </summary>
internal sealed partial class LookupFallbackItem : FallbackCommandItem
{
    private static readonly string[] Prefixes = ["dict ", "lexi ", "查 "];
    private readonly ServiceHub _hub;
    private readonly NoOpCommand _idle;
    private long _generation;

    public LookupFallbackItem(ServiceHub hub)
        : this(hub, new NoOpCommand())
    {
    }

    private LookupFallbackItem(ServiceHub hub, NoOpCommand idle)
        : base(idle, "Lexi 查词", "com.lexi.fallback")
    {
        _hub = hub;
        _idle = idle;
        Title = string.Empty;
        Subtitle = string.Empty;
        Icon = IconHelpers.FromRelativePath("Assets\\LexiIcon.png");
    }

    public override void UpdateQuery(string query)
    {
        var gen = Interlocked.Increment(ref _generation);
        var (explicitPrefix, text) = Strip(query);
        if (text.Length == 0 || text.Length > 60 || (!explicitPrefix && !_hub.Settings.FallbackEnabled))
        {
            Clear(gen);
            return;
        }

        var q = QueryInfo.Parse(text);
        if (q.HasCjk)
        {
            UpdateChinese(gen, explicitPrefix, text);
            return;
        }

        if (q.IsEmpty || q.LetterCount < 2 || (!explicitPrefix && q.WordCount > 2))
        {
            Clear(gen);
            return;
        }

        SearchResult result;
        try
        {
            result = explicitPrefix ? _hub.Dictionary.Search(text, null, 3, CancellationToken.None) : _hub.Dictionary.Lookup(text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Clear(gen);
            return;
        }

        if (Interlocked.Read(ref _generation) != gen)
        {
            return;
        }

        // Without an explicit "dict" prefix, only speak up for real words (exact or inflected), never guesses.
        var hit = result.Hits.FirstOrDefault(h => explicitPrefix || h.Kind is MatchKind.Exact or MatchKind.Form or MatchKind.Translit or MatchKind.FormTranslit);
        if (hit is null)
        {
            if (explicitPrefix)
            {
                Command = new DictionaryPage(_hub, text);
                Title = $"在 Lexi 中查找「{text}」";
                Subtitle = "Lexi · 没有完全匹配，回车查看建议";
            }
            else
            {
                Clear(gen);
            }

            return;
        }

        var w = hit.Word;
        var via = hit.IsForm && hit.Matched is not null ? $"{hit.Matched} → " : string.Empty;
        Command = new DictionaryPage(_hub, explicitPrefix ? text : w.Word);
        Title = $"{via}{WordItems.DisplayTitle(w)} — {w.Brief}";
        Subtitle = $"Lexi · {(w.Lang == Core.Models.Lang.De ? "Deutsch" : "English")} · 回车查看完整释义";
    }

    /// <summary>Chinese input: the best English and the best German equivalent ("房子 → house · das Haus").</summary>
    private void UpdateChinese(long gen, bool explicitPrefix, string text)
    {
        // Without a prefix, only words: a single character is too ambiguous and sentences are not lookups.
        var length = ChineseTerms.QueryKey(text).Length;
        if (!explicitPrefix && (length < 2 || DictionaryPage.IsChineseSentence(text)))
        {
            Clear(gen);
            return;
        }

        SearchResult result;
        try
        {
            result = _hub.Dictionary.Lookup(text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Clear(gen);
            return;
        }

        if (Interlocked.Read(ref _generation) != gen)
        {
            return;
        }

        var best = result.Hits.Where(h => h.Kind == MatchKind.Chinese).DistinctBy(h => h.Word.Lang).ToList();
        if (best.Count == 0)
        {
            if (explicitPrefix)
            {
                Command = new DictionaryPage(_hub, text);
                Title = $"在 Lexi 中查找「{text}」";
                Subtitle = "Lexi · 没有完全对应的词，回车查看部分匹配";
            }
            else
            {
                Clear(gen);
            }

            return;
        }

        Command = new DictionaryPage(_hub, text);
        Title = $"{text} → {string.Join(" · ", best.Select(h => WordItems.DisplayTitle(h.Word)))}";
        Subtitle = "Lexi · 中文反查 · 回车查看全部英语和德语对应词";
    }

    private static (bool Explicit, string Text) Strip(string query)
    {
        var q = query.TrimStart();
        foreach (var p in Prefixes)
        {
            if (q.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return (true, q[p.Length..].Trim());
            }
        }

        return (false, q.Trim());
    }

    private void Clear(long gen)
    {
        if (Interlocked.Read(ref _generation) == gen && Title.Length > 0)
        {
            // An empty CommandItem.Title falls back to the command's Name ("打开词典"), which would keep the
            // item visible on the main page; switch back to the nameless no-op command as well.
            Command = _idle;
            Title = string.Empty;
            Subtitle = string.Empty;
        }
    }
}
