using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Core.Text;

namespace Lexi.Tests;

/// <summary>In-memory <see cref="ILexiconStore"/>: lets the ranking logic be tested without SQLite.</summary>
internal sealed class MemoryStore : ILexiconStore
{
    private readonly List<WordRecord> _words = [];
    private readonly List<FormRecord> _forms = [];
    private FuzzyIndex? _fuzzy;

    public MemoryStore Word(string word, Lang lang, float zipf, string? brief = null, WordFlags flags = WordFlags.None)
    {
        _words.Add(new WordRecord(_words.Count + 1, lang, word, TextNormalizer.ToFold(word), zipf, flags, null, brief));
        _fuzzy = null;
        return this;
    }

    public MemoryStore Form(string form, string lemma, Lang lang, string tags)
    {
        _forms.Add(new FormRecord(form, tags, _words.Single(w => w.Word == lemma && w.Lang == lang)));
        _fuzzy = null;
        return this;
    }

    public IReadOnlyList<WordRecord> GetWordsByFold(string fold, Lang? language) =>
        _words.Where(w => w.Fold == fold && (language is null || w.Lang == language)).ToList();

    public IReadOnlyList<WordRecord> GetWordsByFolds(IReadOnlyCollection<string> folds, Lang? language) =>
        _words.Where(w => folds.Contains(w.Fold) && (language is null || w.Lang == language)).ToList();

    public IReadOnlyList<FormRecord> GetFormsByFold(string fold, Lang? language) =>
        _forms.Where(f => TextNormalizer.ToFold(f.Form) == fold && (language is null || f.Lemma.Lang == language)).ToList();

    public IReadOnlyList<FormRecord> GetFormsByFolds(IReadOnlyCollection<string> folds, Lang? language) =>
        _forms.Where(f => folds.Contains(TextNormalizer.ToFold(f.Form)) && (language is null || f.Lemma.Lang == language)).ToList();

    public IReadOnlyList<WordRecord> GetWordsByPrefix(string foldPrefix, Lang? language, int limit) =>
        _words.Where(w => w.Fold.StartsWith(foldPrefix, StringComparison.Ordinal) && (language is null || w.Lang == language))
            .OrderByDescending(w => w.Zipf).Take(limit).ToList();

    public FuzzyIndex? GetFuzzyIndex() =>
        _fuzzy ??= FuzzyIndex.Build(_words.Select(w => w.Fold).Concat(_forms.Select(f => TextNormalizer.ToFold(f.Form))));
}

public class SearchEngineTests
{
    private static MemoryStore Sample() => new MemoryStore()
        .Word("Haus", Lang.De, 6.0f, "房子", WordFlags.HasZh)
        .Word("Hau", Lang.De, 1.5f)
        .Word("Hausaufgabe", Lang.De, 4.0f)
        .Word("Maus", Lang.De, 4.5f)
        .Word("gehen", Lang.De, 6.6f, "走", WordFlags.HasZh)
        .Word("ging", Lang.En, 2.2f, "帮派")
        .Word("Übertragung", Lang.De, 4.2f, "传输")
        .Word("neu", Lang.De, 6.0f)
        .Word("schon", Lang.De, 6.3f)
        .Word("schön", Lang.De, 5.6f)
        .Word("the", Lang.En, 7.7f)
        .Word("teh", Lang.En, 1.0f)
        .Word("vulnerability", Lang.En, 3.9f, "弱点", WordFlags.HasZh)
        .Word("vulnerably", Lang.En, 1.5f)
        .Word("Gift", Lang.De, 4.5f)
        .Word("gift", Lang.En, 5.0f)
        .Form("Häuser", "Haus", Lang.De, "nominative plural")
        .Form("Haus", "Hau", Lang.De, "genitive singular")
        .Form("ging", "gehen", Lang.De, "past")
        .Form("neue", "neu", Lang.De, "positive nominative");

    private static SearchResult Search(MemoryStore store, string text, Lang? lang = null) =>
        new SearchEngine(store).Search(new SearchRequest { Text = text, Language = lang });

    [Fact]
    public void Exact_headword_ranks_first_and_completions_follow()
    {
        var r = Search(Sample(), "haus");
        Assert.Equal("Haus", r.Hits[0].Word.Word);
        Assert.Equal(MatchKind.Exact, r.Hits[0].Kind);
        Assert.Contains(r.Hits, h => h.Word.Word == "Hausaufgabe" && h.Kind == MatchKind.Prefix);
    }

    [Fact]
    public void Inflected_form_resolves_to_lemma()
    {
        var hit = Search(Sample(), "Häuser").Hits[0];
        Assert.Equal("Haus", hit.Word.Word);
        Assert.Equal(MatchKind.Form, hit.Kind);
        Assert.Equal("Häuser", hit.Matched);
    }

    [Fact]
    public void Popular_inflection_beats_rare_exact_homograph()
    {
        // German "ging" (past of gehen) vs a rare English headword "ging".
        var r = Search(Sample(), "ging");
        Assert.Equal("gehen", r.Hits[0].Word.Word);
        Assert.Contains(r.Hits, h => h.Word.Word == "ging" && h.Word.Lang == Lang.En);
    }

    [Fact]
    public void Popular_exact_headword_beats_rare_lemma_of_same_form()
    {
        var r = Search(Sample(), "Haus");
        Assert.Equal("Haus", r.Hits[0].Word.Word);
        Assert.Contains(r.Hits, h => h.Word.Word == "Hau");
    }

    [Fact]
    public void Transliteration_maps_ue_to_umlaut()
    {
        var hit = Search(Sample(), "Uebertragung").Hits[0];
        Assert.Equal("Übertragung", hit.Word.Word);
        Assert.Equal(MatchKind.Translit, hit.Kind);
    }

    [Fact]
    public void Transliteration_is_verified_and_never_maps_neue_to_neu()
    {
        // "neue" contains "ue" but is not a transliteration of "neu"; it is an inflected form instead.
        var r = Search(Sample(), "neue");
        Assert.DoesNotContain(r.Hits, h => h.Kind == MatchKind.Translit);
        Assert.Equal("neu", r.Hits[0].Word.Word);
        Assert.Equal(MatchKind.Form, r.Hits[0].Kind);
    }

    [Fact]
    public void Missing_umlauts_still_find_the_word_but_exact_spelling_wins()
    {
        var r = Search(Sample(), "schon");
        Assert.Equal("schon", r.Hits[0].Word.Word);
        Assert.Equal("schön", r.Hits[1].Word.Word);
        Assert.Equal(MatchKind.Fold, r.Hits[1].Kind);

        var hauser = Search(Sample(), "hauser");
        Assert.Equal("Haus", hauser.Hits[0].Word.Word);
        Assert.Equal(MatchKind.FormFold, hauser.Hits[0].Kind);
    }

    [Fact]
    public void Spelling_correction_when_nothing_matches()
    {
        var r = Search(Sample(), "vulnerabilty");
        Assert.Equal("vulnerability", r.Hits[0].Word.Word);
        Assert.Equal(MatchKind.Fuzzy, r.Hits[0].Kind);
        Assert.Equal(1, r.Hits[0].Distance);
        Assert.True(r.IsCorrection);
    }

    [Fact]
    public void Correct_words_are_not_cluttered_with_corrections()
    {
        // "Haus" is correct; Maus (distance 1) must not be suggested.
        Assert.DoesNotContain(Search(Sample(), "Haus").Hits, h => h.Word.Word == "Maus");
    }

    [Fact]
    public void Far_more_common_correction_is_offered_even_for_an_existing_word()
    {
        var r = Search(Sample(), "teh");
        Assert.Contains(r.Hits, h => h.Word.Word == "the" && h.Kind == MatchKind.Fuzzy);
    }

    [Fact]
    public void Language_filter_restricts_results()
    {
        var r = Search(Sample(), "gift", Lang.De);
        Assert.All(r.Hits, h => Assert.Equal(Lang.De, h.Word.Lang));
        Assert.Equal("Gift", r.Hits[0].Word.Word);
    }

    [Fact]
    public void Empty_and_cjk_queries_return_nothing()
    {
        Assert.Empty(Search(Sample(), "   ").Hits);
        Assert.Empty(Search(Sample(), "漏洞").Hits);
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new SearchEngine(Sample()).Search(new SearchRequest { Text = "vulnerabilty" }, cts.Token));
    }
}
