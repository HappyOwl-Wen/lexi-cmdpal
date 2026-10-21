using System.Diagnostics;
using Lexi.Core.Models;
using Lexi.Core.Text;

namespace Lexi.Core.Search;

/// <summary>
/// Local dictionary search: exact → inflected form → ae/oe/ue transliteration → accent-insensitive →
/// prefix completion → fuzzy correction, then one scoring function ranks everything.
/// Pure logic over <see cref="ILexiconStore"/>; no UI or database dependencies.
/// </summary>
public sealed class SearchEngine
{
    private const int MinPrefixLength = 2;

    // Three letters allow one edit ("teh" → "the"); popularity ranking and the strong-match rule keep it quiet.
    private const int MinFuzzyLength = 3;
    private const int PrefixLimitWhenStrong = 8;
    private const int PrefixLimit = 24;

    // Chinese reverse lookup: candidates fetched per language and term, longest input considered, and how much
    // popularity (Zipf 0–8) counts against the term weight (1–100).
    private const int ChineseCandidates = 40;
    private const int MaxChineseQuery = 16;
    public const double ChineseZipfWeight = 5;

    // An inflected form is hidden when its lemma scores at least this close ("chinks" next to chink).
    private const double InflectedMargin = 5;

    // Partial matches: a longer matched part is more specific ("网络安全" before "漏洞" in "网络安全漏洞").
    private const double PartLengthWeight = 12;

    private readonly ILexiconStore _store;

    public SearchEngine(ILexiconStore store)
    {
        _store = store;
    }

    public SearchResult Search(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var q = QueryInfo.Parse(request.Text);
        if (q.HasCjk)
        {
            return SearchChinese(q, request, sw, cancellationToken);
        }

        if (q.IsEmpty || q.LetterCount == 0)
        {
            return new SearchResult { Query = q, Hits = [], Elapsed = sw.Elapsed };
        }

        var lang = request.Language;
        var hits = new Dictionary<long, SearchHit>();

        // 1. Headwords equal to the query, with or without diacritics.
        foreach (var w in _store.GetWordsByFold(q.Fold, lang))
        {
            Consider(hits, w, ClassifyHeadword(w.Word, q), w.Word, null, 0);
        }

        // 2. Inflected forms ("Häuser" → Haus, "ging" → gehen, "went" → go).
        foreach (var f in _store.GetFormsByFold(q.Fold, lang))
        {
            Consider(hits, f.Lemma, ClassifyForm(f.Form, q), f.Form, f.Tags, 0);
        }

        // 3. ae/oe/ue spelled out ("Uebertragung" → Übertragung), verified against the real transliteration.
        foreach (var variant in GermanTransliteration.ReverseVariants(q.Fold))
        {
            foreach (var w in _store.GetWordsByFold(variant, lang))
            {
                if (TextNormalizer.ToTranslit(w.Word) == q.Fold)
                {
                    Consider(hits, w, MatchKind.Translit, w.Word, null, 0);
                }
            }

            foreach (var f in _store.GetFormsByFold(variant, lang))
            {
                if (TextNormalizer.ToTranslit(f.Form) == q.Fold)
                {
                    Consider(hits, f.Lemma, MatchKind.FormTranslit, f.Form, f.Tags, 0);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var strong = BestPopularity(hits.Values);

        // 4. Prefix completion ("authen" → authentication …).
        if (q.Fold.Length >= MinPrefixLength && !q.IsSentence)
        {
            var limit = strong is null ? PrefixLimit : PrefixLimitWhenStrong;
            foreach (var w in _store.GetWordsByPrefix(q.Fold, lang, limit))
            {
                if (w.Fold != q.Fold)
                {
                    Consider(hits, w, MatchKind.Prefix, w.Word, null, 0);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 5. Spelling correction.
        if (request.Fuzzy && q.Fold.Length >= MinFuzzyLength && !q.IsSentence && _store.GetFuzzyIndex() is { } fuzzy)
        {
            AddFuzzy(hits, fuzzy, q, lang, strong, cancellationToken);
        }

        foreach (var h in hits.Values)
        {
            h.Score = Score(h, q);
        }

        var ranked = hits.Values
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Word.Word.Length)
            .ThenBy(h => h.Word.Word, StringComparer.Ordinal)
            .Take(Math.Max(1, request.Limit))
            .ToList();

        return new SearchResult { Query = q, Hits = ranked, Elapsed = sw.Elapsed };
    }

    /// <summary>
    /// Chinese → English/German: headwords whose Chinese definitions contain the input as a whole term. When no
    /// definition uses the whole input, its longest parts that do are looked up instead ("网络安全漏洞" → 网络安全,
    /// 安全漏洞). Results are ranked per language so both languages reach the first screen.
    /// </summary>
    private SearchResult SearchChinese(QueryInfo q, SearchRequest request, Stopwatch sw, CancellationToken ct)
    {
        var text = ChineseTerms.QueryKey(q.Clean);
        var hits = new Dictionary<long, SearchHit>();
        if (text.Length is > 0 and <= MaxChineseQuery)
        {
            AddChinese(hits, text, MatchKind.Chinese, request.Language);

            // Part lookups need several queries, so they are left out of the cheap mode (Fuzzy = false).
            if (hits.Count == 0 && request.Fuzzy && text.Length >= 3)
            {
                var covered = new bool[text.Length];
                for (var length = text.Length - 1; length >= 2 && Array.IndexOf(covered, false) >= 0; length--)
                {
                    for (var start = 0; start + length <= text.Length; start++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (Array.IndexOf(covered, false, start, length) < 0)
                        {
                            continue;
                        }

                        if (AddChinese(hits, text.Substring(start, length), MatchKind.ChinesePart, request.Language) > 0)
                        {
                            Array.Fill(covered, true, start, length);
                        }
                    }
                }
            }
        }

        var perLanguage = request.Language is null ? Math.Max(1, request.Limit / 2) : Math.Max(1, request.Limit);
        var ranked = new List<SearchHit>();
        foreach (var group in hits.Values.GroupBy(h => h.Word.Lang).OrderBy(g => g.Key == Lang.En ? 0 : 1))
        {
            var taken = 0;
            foreach (var h in group.OrderByDescending(h => h.Score).ThenBy(h => h.Word.Word.Length).ThenBy(h => h.Word.Word, StringComparer.Ordinal))
            {
                if (taken == perLanguage)
                {
                    break;
                }

                ct.ThrowIfCancellationRequested();
                if (!IsInflectedDuplicate(h, hits))
                {
                    ranked.Add(h);
                    taken++;
                }
            }
        }

        return new SearchResult { Query = q, Hits = ranked, Elapsed = sw.Elapsed };
    }

    private int AddChinese(Dictionary<long, SearchHit> hits, string term, MatchKind kind, Lang? language)
    {
        // Ask per language so the richer English data cannot crowd German candidates out of the limit.
        var matches = language is { } only
            ? _store.GetWordsByChinese(term, only, ChineseCandidates)
            : [.. _store.GetWordsByChinese(term, Lang.En, ChineseCandidates), .. _store.GetWordsByChinese(term, Lang.De, ChineseCandidates)];
        foreach (var m in matches)
        {
            var hit = new SearchHit { Word = m.Word, Kind = kind, Matched = m.Term, Score = ScoreChinese(m, kind) };
            if (!hits.TryGetValue(m.Word.Id, out var existing) || hit.Score > existing.Score)
            {
                hits[m.Word.Id] = hit;
            }
        }

        return matches.Count;
    }

    /// <summary>
    /// True for an inflected form of a better hit of the same language ("threats", "apples" next to threat, apple):
    /// ECDICT gives many plurals and participles an entry that repeats the lemma's translation. A form that scores
    /// higher than its lemma keeps its place ("data" next to datum, "glasses" next to glass).
    /// </summary>
    private bool IsInflectedDuplicate(SearchHit h, Dictionary<long, SearchHit> hits)
    {
        var key = TextNormalizer.ToKey(h.Word.Word);
        foreach (var f in _store.GetFormsByFold(h.Word.Fold, h.Word.Lang))
        {
            if (f.Lemma.Id != h.Word.Id && hits.TryGetValue(f.Lemma.Id, out var lemma) && lemma.Score >= h.Score - InflectedMargin && TextNormalizer.ToKey(f.Form) == key)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How central the term is in the definition (first term of the first sense = 100) plus popularity, so that
    /// equally good translations are ordered by how common they are ("威胁" → threat, threaten, menace …).
    /// </summary>
    internal static double ScoreChinese(ZhMatch m, MatchKind kind)
    {
        var w = m.Word;
        var s = kind == MatchKind.ChinesePart ? (m.Weight * 0.6) + (m.Term.Length * PartLengthWeight) : m.Weight;
        s += Math.Clamp(w.Zipf, 0f, 8f) * ChineseZipfWeight;

        // A letter's Chinese text lists what it abbreviates ("B": 字节, 数据库, 总线 …); abbreviations rank after words.
        if (w.Word.Length == 1)
        {
            s -= 30;
        }
        else if (IsAbbreviation(w.Word))
        {
            s -= 6;
        }

        if ((w.Flags & WordFlags.Core) != 0)
        {
            s += 2;
        }

        if ((w.Flags & WordFlags.MultiWord) != 0)
        {
            s -= 4;
        }

        if ((w.Flags & WordFlags.ProperNoun) != 0)
        {
            s -= 8;
        }

        return s;
    }

    /// <summary>"DB", "XFER", "CPU" (upper-case letters or digits only) and "Dr.", "app." (ending in a period).</summary>
    internal static bool IsAbbreviation(string word) =>
        word.Length >= 2 && (word[^1] == '.' || (word.Any(char.IsLetter) && word.All(c => char.IsUpper(c) || char.IsDigit(c))));

    private void AddFuzzy(Dictionary<long, SearchHit> hits, FuzzyIndex fuzzy, QueryInfo q, Lang? lang, float? strongZipf, CancellationToken ct)
    {
        // Short words tolerate one edit; longer words two. Distance 2 is only searched when distance 1 found little.
        var maxDistance = q.Fold.Length <= 5 ? 1 : 2;
        var matches = fuzzy.Search(q.Fold, 1, 400, ct);
        if (maxDistance == 2 && matches.Count(m => m.Distance == 1) < 3)
        {
            matches = fuzzy.Search(q.Fold, 2, 800, ct);
        }

        var distances = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in matches)
        {
            if (m.Distance > 0)
            {
                distances[m.Key] = m.Distance;
            }
        }

        if (distances.Count == 0)
        {
            return;
        }

        // With a confident exact/form match, only keep corrections that are far more common ("teh" → "the"),
        // so correct input like "Haus" is not cluttered with Maus/Laus/Haut.
        bool Allowed(WordRecord w, int distance) =>
            strongZipf is null || (distance == 1 && w.Zipf >= strongZipf.Value + 1.5f);

        foreach (var w in _store.GetWordsByFolds(distances.Keys, lang))
        {
            var d = distances[w.Fold];
            if (Allowed(w, d))
            {
                Consider(hits, w, MatchKind.Fuzzy, w.Word, null, d);
            }
        }

        foreach (var f in _store.GetFormsByFolds(distances.Keys, lang))
        {
            if (distances.TryGetValue(TextNormalizer.ToFold(f.Form), out var d) && Allowed(f.Lemma, d))
            {
                Consider(hits, f.Lemma, MatchKind.FuzzyForm, f.Form, f.Tags, d);
            }
        }
    }

    private static float? BestPopularity(IEnumerable<SearchHit> hits)
    {
        float? best = null;
        foreach (var h in hits)
        {
            if (h.Kind <= MatchKind.FormFold && (best is null || h.Word.Zipf > best))
            {
                best = h.Word.Zipf;
            }
        }

        return best;
    }

    private static MatchKind ClassifyHeadword(string headword, QueryInfo q)
    {
        var key = TextNormalizer.ToKey(headword);
        if (key == q.Key)
        {
            return MatchKind.Exact;
        }

        return TextNormalizer.TranslitKey(key) == q.Fold && TextNormalizer.HasGermanLetters(headword) ? MatchKind.Translit : MatchKind.Fold;
    }

    private static MatchKind ClassifyForm(string form, QueryInfo q)
    {
        var key = TextNormalizer.ToKey(form);
        if (key == q.Key)
        {
            return MatchKind.Form;
        }

        return TextNormalizer.TranslitKey(key) == q.Fold && TextNormalizer.HasGermanLetters(form) ? MatchKind.FormTranslit : MatchKind.FormFold;
    }

    private static void Consider(Dictionary<long, SearchHit> hits, WordRecord word, MatchKind kind, string matched, string? tags, int distance)
    {
        var candidate = new SearchHit { Word = word, Kind = kind, Matched = matched, FormTags = tags, Distance = distance };
        if (!hits.TryGetValue(word.Id, out var existing) || BaseScore(candidate) > BaseScore(existing))
        {
            hits[word.Id] = candidate;
        }
    }

    // Accent-insensitive matches score close to exact ones: typing German without umlauts ("hauser" for
    // Häuser) is far more common among learners than looking up a rare homograph spelled exactly that way.
    internal static double BaseScore(SearchHit h) => h.Kind switch
    {
        MatchKind.Exact => 100,
        MatchKind.Form => 96,
        MatchKind.Translit => 95,
        MatchKind.FormTranslit => 93,
        MatchKind.Fold => 93,
        MatchKind.FormFold => 91,
        MatchKind.Prefix => 62,
        MatchKind.Fuzzy => h.Distance <= 1 ? 58 : 44,
        MatchKind.FuzzyForm => h.Distance <= 1 ? 56 : 42,
        _ => 0,
    };

    /// <summary>
    /// Base score by match quality, plus popularity (Zipf scale, 0–8) so that the more common reading of an
    /// exact string wins (German "ging" → gehen over a rare English homograph), plus small heuristics.
    /// </summary>
    internal static double Score(SearchHit h, QueryInfo q)
    {
        var w = h.Word;
        var s = BaseScore(h);
        s += Math.Clamp(w.Zipf, 0f, 8f) * 3.2;

        switch (h.Kind)
        {
            case MatchKind.Prefix:
                // Prefer completions close in length to what was typed; popularity still dominates.
                s -= Math.Min(8, 0.4 * Math.Max(0, w.Fold.Length - q.Fold.Length));
                break;
            case MatchKind.Fuzzy or MatchKind.FuzzyForm:
                var target = TextNormalizer.ToFold(h.Matched ?? w.Word);
                s += target.Length > 0 && target[0] == q.Fold[0] ? 3 : -4;
                if (Math.Abs(target.Length - q.Fold.Length) <= 1)
                {
                    s += 1;
                }

                break;
        }

        if (q.HasGermanLetters)
        {
            s += w.Lang == Lang.De ? 6 : -6;
        }

        if (h.Kind == MatchKind.Exact && string.Equals(w.Word, q.Clean, StringComparison.Ordinal))
        {
            s += 1.5;
        }

        if ((w.Flags & WordFlags.HasZh) != 0)
        {
            s += 1.5;
        }

        if ((w.Flags & WordFlags.Core) != 0)
        {
            s += 2;
        }

        if ((w.Flags & WordFlags.MultiWord) != 0 && q.WordCount == 1)
        {
            s -= 3;
        }

        if ((w.Flags & WordFlags.ProperNoun) != 0)
        {
            s -= 8;
        }

        return s;
    }
}
