using Lexi.Core.Models;
using Lexi.Core.Morphology;
using Lexi.Core.Search;
using Lexi.Dictionary.Storage;

namespace Lexi.Dictionary.View;

/// <summary>
/// Aggregates the per-source entries of one headword. Order of preference:
/// English words — Chinese: ECDICT; English: Wiktionary, then ECDICT; IPA: Wiktionary (UK/US), then ECDICT.
/// German words — Chinese: zh.wiktionary glosses, then de.wiktionary sense translations; English: en.wiktionary,
/// then de.wiktionary translations; grammar and IPA: de.wiktionary first.
/// </summary>
public static class WordViewBuilder
{
    public static WordView Build(LexiDatabase db, WordRecord word)
    {
        var entries = db.GetEntries(word.Id);
        var forms = db.GetFormsOfLemma(word.Id);
        return Build(word, entries, forms);
    }

    public static WordView Build(WordRecord word, IReadOnlyList<StoredEntry> entries, IReadOnlyList<(string Form, string Tags)> forms)
    {
        var lang = word.Lang;
        var ordered = entries.OrderBy(e => SourceRank(e.Source.Code, lang)).ThenBy(e => e.Seq).ToList();

        var chinese = new List<MeaningBlock>();
        var english = new List<MeaningBlock>();
        var german = new List<MeaningBlock>();
        var ipa = new List<PronView>();
        var audio = new List<AudioView>();
        var synonyms = new List<string>();
        var related = new List<string>();
        var pos = new List<string>();
        var sources = new List<SourceInfo>();
        LearnerInfo? learner = null;

        foreach (var e in ordered)
        {
            var d = e.Data;
            var used = false;
            if (d.Pos is { } partOfSpeech && partOfSpeech != "other" && !pos.Contains(partOfSpeech))
            {
                pos.Add(partOfSpeech);
            }

            var senses = d.Senses ?? [];
            var zh = senses.Where(s => s.GlossLang == "zh").ToList();
            var en = senses.Where(s => s.GlossLang == "en").ToList();
            var de = senses.Where(s => s.GlossLang == "de").ToList();
            if (zh.Count > 0)
            {
                chinese.Add(new MeaningBlock(e.Source, "gloss", d.Pos, zh));
                used = true;
            }

            if (en.Count > 0)
            {
                english.Add(new MeaningBlock(e.Source, "gloss", d.Pos, en));
                used = true;
            }

            if (de.Count > 0)
            {
                german.Add(new MeaningBlock(e.Source, "gloss", d.Pos, de));
                used = true;

                // Sense-aligned translation equivalents (de.wiktionary translation tables). Several senses often
                // share one equivalent (Haus 1–3 → 房子), so each equivalent is listed once.
                var zhTr = de.Where(s => s.Zh is { Count: > 0 })
                    .Select(s => new Sense { Number = s.Number, Gloss = string.Join("，", s.Zh!), GlossLang = "zh" })
                    .DistinctBy(s => s.Gloss, StringComparer.Ordinal)
                    .ToList();
                if (zhTr.Count > 0)
                {
                    chinese.Add(new MeaningBlock(e.Source, "translation", d.Pos, zhTr));
                }

                var enTr = de.Where(s => s.En is { Count: > 0 })
                    .Select(s => new Sense { Number = s.Number, Gloss = string.Join(", ", s.En!), GlossLang = "en" })
                    .DistinctBy(s => s.Gloss, StringComparer.Ordinal)
                    .ToList();
                if (enTr.Count > 0)
                {
                    english.Add(new MeaningBlock(e.Source, "translation", d.Pos, enTr));
                }
            }

            foreach (var p in d.Ipa ?? [])
            {
                if (!ipa.Any(x => x.Text == p.Text))
                {
                    ipa.Add(new PronView(p.Text, p.Region, e.Source, p.Note));
                    used = true;
                }
            }

            foreach (var a in d.Audio ?? [])
            {
                if (!audio.Any(x => x.File == a.File))
                {
                    audio.Add(new AudioView(a.File, a.Region, a.Mp3Url, e.Source));
                }
            }

            foreach (var s in d.Synonyms ?? [])
            {
                if (!synonyms.Contains(s) && s != word.Word)
                {
                    synonyms.Add(s);
                }
            }

            foreach (var s in d.Related ?? [])
            {
                if (!related.Contains(s) && s != word.Word)
                {
                    related.Add(s);
                }
            }

            learner ??= d.Learner;
            if (used && !sources.Any(s => s.Id == e.Source.Id))
            {
                sources.Add(e.Source);
            }
        }

        // IPA: keep the preferred source's transcriptions; only fall back to others when it has none.
        if (ipa.Count > 0)
        {
            var bestSource = ipa[0].Source.Id;
            ipa = ipa.Where(p => p.Source.Id == bestSource).Take(4).ToList();
        }

        var (grammar, grammarSource, cases, summary, gender) = BuildGrammar(lang, ordered);
        var article = lang == Lang.De && gender is not null && pos.FirstOrDefault() == "noun"
            ? Import.KaikkiImporter.Article(gender)
            : null;

        return new WordView
        {
            Word = word,
            Title = article is null ? word.Word : $"{article} {word.Word}",
            Article = article,
            PartsOfSpeech = pos,
            Ipa = ipa,
            Audio = audio.OrderBy(a => RegionRank(a.Region)).ToList(),
            Grammar = grammar,
            GrammarSource = grammarSource,
            Cases = cases,
            GrammarSummary = summary,
            // Direct definitions before sense-aligned translation equivalents (OrderBy is stable).
            Chinese = chinese.OrderBy(b => b.Kind == "gloss" ? 0 : 1).ToList(),
            English = english.OrderBy(b => b.Kind == "gloss" ? 0 : 1).ToList(),
            German = german,
            Synonyms = synonyms.Take(8).ToList(),
            Related = related.Take(8).ToList(),
            Learner = learner,
            Forms = SummarizeForms(lang, forms),
            Sources = sources,
            RawRefs = ordered.Select(e => e.Data.RawRef).OfType<string>().Distinct().ToList(),
        };
    }

    private static (List<GrammarLine>, SourceInfo?, IReadOnlyList<CaseRow>?, string?, string?) BuildGrammar(Lang lang, List<StoredEntry> ordered)
    {
        var lines = new List<GrammarLine>();
        SourceInfo? source = null;
        IReadOnlyList<CaseRow>? cases = null;
        string? summary = null;
        string? gender = null;

        void Add(string label, IEnumerable<string>? values, SourceInfo s)
        {
            if (values is null || lines.Any(l => l.Label == label))
            {
                return;
            }

            var v = string.Join(", ", values.Distinct());
            if (v.Length > 0)
            {
                lines.Add(new GrammarLine(label, v));
                source ??= s;
            }
        }

        foreach (var e in ordered)
        {
            var g = e.Data.Grammar;
            if (g is null)
            {
                continue;
            }

            gender ??= g.Gender;
            cases ??= g.Cases;
            summary ??= g.Summary;
            if (lang == Lang.De)
            {
                if (g.Gender is not null)
                {
                    Add("词性 Genus", [GenderLabel(g.Gender)], e.Source);
                }

                Add("复数 Plural", g.Plural, e.Source);
                Add("单数属格 Genitiv", g.GenitiveSingular is null ? null : [g.GenitiveSingular], e.Source);
                Add("现在时 Präsens", g.Present, e.Source);
                Add("过去时 Präteritum", g.Past, e.Source);
                Add("过去分词 Partizip II", g.PastParticiple, e.Source);
                Add("助动词 Hilfsverb", g.Auxiliary, e.Source);
                Add("虚拟式II Konjunktiv II", g.SubjunctiveII, e.Source);
                Add("命令式 Imperativ", g.Imperative, e.Source);
                Add("可分前缀 trennbar", g.SeparablePrefix is null ? null : [g.SeparablePrefix + "-"], e.Source);
                Add("比较级 Komparativ", g.Comparative, e.Source);
                Add("最高级 Superlativ", g.Superlative, e.Source);
            }
            else
            {
                Add("复数", g.Plural, e.Source);
                Add("第三人称单数", g.Present, e.Source);
                Add("过去式", g.Past, e.Source);
                Add("过去分词", g.PastParticiple, e.Source);
                Add("现在分词", g.PresentParticiple, e.Source);
                Add("比较级", g.Comparative, e.Source);
                Add("最高级", g.Superlative, e.Source);
            }
        }

        return (lines, source, cases, summary, gender);
    }

    private static IReadOnlyList<FormView> SummarizeForms(Lang lang, IReadOnlyList<(string Form, string Tags)> forms)
    {
        var result = new List<FormView>();
        foreach (var (form, tags) in forms)
        {
            if (result.Count >= 40)
            {
                break;
            }

            if (!result.Any(r => r.Form == form))
            {
                result.Add(new FormView(form, FormDescriber.Describe(tags, lang)));
            }
        }

        return result;
    }

    public static string GenderLabel(string gender) => string.Join(" / ", gender.Split(',').Select(g => g switch
    {
        "m" => "阳性 maskulin",
        "f" => "阴性 feminin",
        "n" => "中性 neutrum",
        _ => g,
    }));

    public static string PosLabel(string pos, Lang lang) => pos switch
    {
        "noun" => lang == Lang.De ? "名词 Substantiv" : "名词 noun",
        "verb" => lang == Lang.De ? "动词 Verb" : "动词 verb",
        "adj" => lang == Lang.De ? "形容词 Adjektiv" : "形容词 adj.",
        "adv" => lang == Lang.De ? "副词 Adverb" : "副词 adv.",
        "pron" => "代词",
        "prep" => "介词",
        "conj" => "连词",
        "intj" => "感叹词",
        "num" => "数词",
        "det" => "限定词",
        "article" => "冠词",
        "particle" => "小品词",
        "phrase" => "短语",
        "abbrev" => "缩写",
        _ => pos,
    };

    private static int SourceRank(string code, Lang lang) => (lang, code) switch
    {
        (Lang.En, "wikt-en") => 0,
        (Lang.En, "ecdict") => 1,
        (Lang.De, "wikt-de") => 0,
        (Lang.De, "wikt-zh") => 1,
        (Lang.De, "wikt-en") => 2,
        _ => 5,
    };

    private static int RegionRank(string? region) => region switch
    {
        "UK" => 0,
        "US" => 1,
        null => 2,
        _ => 3,
    };
}
