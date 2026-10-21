using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Core.Text;

namespace Lexi.Dictionary.Import;

public enum KaikkiEdition
{
    /// <summary>English Wiktionary (glosses in English).</summary>
    English,
    /// <summary>German Wiktionary (glosses in German, rich grammar tables, translations).</summary>
    German,
    /// <summary>Chinese Wiktionary (glosses in Chinese).</summary>
    Chinese,
}

/// <summary>
/// Imports Wiktionary data extracted by wiktextract (kaikki.org JSONL, optionally gzipped).
/// Works with both the per-language "postprocessed" files and the raw multi-language dumps (filtered by lang_code).
/// </summary>
internal static class KaikkiImporter
{
    private static readonly HashSet<string> SkipPos = new(StringComparer.Ordinal)
    {
        "name", "character", "symbol", "punct", "romanization", "suffix", "prefix", "infix", "interfix",
        "circumfix", "affix", "combining_form", "syllable",
    };

    private static readonly HashSet<string> FormNoiseTags = new(StringComparer.Ordinal)
    {
        "table-tags", "inflection-template", "class", "romanization", "error-unknown-tag",
    };

    private static readonly HashSet<string> LabelTags = new(StringComparer.Ordinal)
    {
        "transitive", "intransitive", "reflexive", "impersonal", "colloquial", "informal", "formal", "slang",
        "vulgar", "derogatory", "archaic", "obsolete", "dated", "rare", "literary", "poetic", "figuratively",
        "humorous", "technical", "jargon", "regional", "Austrian", "Switzerland", "Swiss", "uncountable",
        "outdated", "euphemistic", "pejorative", "neologism", "nonstandard",
    };

    private static readonly string[] GermanPronouns = ["er/sie/es ", "ich ", "du ", "er ", "sie ", "es ", "wir ", "ihr ", "man "];

    public sealed record Options(
        string Path,
        KaikkiEdition Edition,
        Lang Lang,
        int SourceId,
        string RefPrefix,
        int? Limit = null,
        Func<string, bool>? AcceptHeadword = null);

    public static void Import(ImportContext ctx, Options o)
    {
        var code = o.Lang.Code();
        var needleA = Encoding.UTF8.GetBytes($"\"lang_code\": \"{code}\"");
        var needleB = Encoding.UTF8.GetBytes($"\"lang_code\":\"{code}\"");
        using var reader = new Utf8LineReader(o.Path);
        long lines = 0, records = 0, entries = 0;
        var sw = Stopwatch.StartNew();
        var lastReport = sw.Elapsed;
        while (reader.TryReadLine(out var line))
        {
            lines++;
            var span = line.Span;
            if (span.IndexOf(needleA) < 0 && span.IndexOf(needleB) < 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (Str(root, "lang_code") != code)
            {
                continue;
            }

            var word = Str(root, "word")?.Trim();
            if (string.IsNullOrEmpty(word) || word.Length > 60 || !word.Any(char.IsLetter))
            {
                continue;
            }

            records++;
            var added = o.Edition switch
            {
                KaikkiEdition.English => MapEnglishEdition(ctx, o, root, word),
                KaikkiEdition.German => MapGermanEdition(ctx, o, root, word),
                _ => MapChineseEdition(ctx, o, root, word),
            };
            if (added)
            {
                entries++;
            }

            if (sw.Elapsed - lastReport > TimeSpan.FromSeconds(15))
            {
                lastReport = sw.Elapsed;
                ctx.Log($"    … {lines:N0} lines, {records:N0} {code} records, {entries:N0} entries ({100.0 * reader.Position / reader.Length:F0}% of file)");
            }

            if (o.Limit is not null && entries >= o.Limit)
            {
                break;
            }
        }

        ctx.Log($"  {o.RefPrefix} ({code}): {lines:N0} lines, {records:N0} records, {entries:N0} entries in {sw.Elapsed.TotalSeconds:F0}s");
    }

    // ---------------------------------------------------------------- English Wiktionary

    private static bool MapEnglishEdition(ImportContext ctx, Options o, JsonElement root, string word)
    {
        var pos = Str(root, "pos") ?? "other";
        if (SkipPos.Contains(pos) || word.StartsWith('-') || word.EndsWith('-'))
        {
            return false;
        }

        var senses = new List<Sense>();
        string? gender = null;
        foreach (var sense in Arr(root, "senses"))
        {
            var tags = StrArr(sense, "tags");
            gender ??= GenderFromTags(tags);
            if (tags.Contains("form-of") && sense.TryGetProperty("form_of", out var formOf))
            {
                var formTags = FormTags(tags);
                foreach (var fo in formOf.EnumerateArray())
                {
                    var lemma = Str(fo, "word");
                    if (lemma is not null)
                    {
                        ctx.StageForm(o.Lang, word, lemma, formTags, o.SourceId);
                    }
                }

                continue;
            }

            if (tags.Contains("alt-of") || tags.Contains("misspelling"))
            {
                continue;
            }

            var glosses = StrArr(sense, "glosses");
            if (glosses.Count == 0 || senses.Count >= 10)
            {
                continue;
            }

            var s = new Sense
            {
                Gloss = TextCleanup.Truncate(TextCleanup.Normalize(glosses[^1]) ?? string.Empty, 400),
                GlossLang = "en",
                // wiktextract expands topics hierarchically (computing → engineering, mathematics …): keep the first only.
                Labels = Labels(tags, StrArr(sense, "topics").Take(1)),
                Examples = Examples(sense, o.Lang == Lang.De ? "english" : null, 2),
            };
            if (s.Gloss.Length > 0)
            {
                if (o.Lang == Lang.En)
                {
                    // Translation tables exist only on English entries of the English Wiktionary.
                    s.Zh = AddMandarin(s.Zh, Arr(sense, "translations"));
                }

                senses.Add(s);
            }
        }

        if (o.Lang == Lang.En && senses.Count > 0)
        {
            // Tables wiktextract could not attach to a sense: match their heading ("human abode") to a gloss.
            foreach (var t in Arr(root, "translations"))
            {
                if (IsMandarin(t))
                {
                    var target = senses[BestSense(senses, Str(t, "sense"))];
                    target.Zh = AddMandarin(target.Zh, [t]);
                }
            }
        }

        var forms = ParseForms(root, source: null);
        foreach (var f in forms)
        {
            var tagText = string.Join(' ', f.Tags ?? []);
            if (o.Lang == Lang.De)
            {
                var cleaned = CleanGermanForm(f.Form, null);
                if (cleaned is not null && f.Tags?.Contains("auxiliary") != true)
                {
                    ctx.StageForm(o.Lang, cleaned, word, tagText, o.SourceId);
                }
            }
            else if (!f.Form.Contains(' ', StringComparison.Ordinal))
            {
                ctx.StageForm(o.Lang, f.Form, word, tagText, o.SourceId);
            }
        }

        if (senses.Count == 0 || (o.AcceptHeadword is not null && !o.AcceptHeadword(word)))
        {
            return false;
        }

        var entry = new EntryData
        {
            Headword = word,
            Pos = NormalizePos(pos),
            Senses = senses,
            RawRef = $"{o.RefPrefix}:{word}#{pos}{EtymologySuffix(root)}",
        };
        ReadSounds(root, entry, englishRegions: o.Lang == Lang.En);

        var grammar = new Grammar();
        if (o.Lang == Lang.En)
        {
            grammar.Plural = Pick(forms, t => t.Contains("plural"));
            grammar.Present = Pick(forms, t => t.Contains("third-person") && t.Contains("present"));
            grammar.Past = Pick(forms, t => t.Contains("past") && !t.Contains("participle"));
            grammar.PastParticiple = Pick(forms, t => t.Contains("past") && t.Contains("participle"));
            grammar.PresentParticiple = Pick(forms, t => t.Contains("present") && t.Contains("participle"));
            grammar.Comparative = Pick(forms, t => t.Contains("comparative"));
            grammar.Superlative = Pick(forms, t => t.Contains("superlative"));
        }
        else
        {
            grammar.Gender = gender ?? GenderFromTags(StrArr(root, "tags"));
            grammar.Plural = Pick(forms, t => t.Count == 1 && t.Contains("plural"));
            grammar.GenitiveSingular = Pick(forms, t => t.Count == 1 && t.Contains("genitive"))?.FirstOrDefault();
            grammar.Present = Pick(forms, t => t.Contains("third-person") && t.Contains("present") && t.Contains("singular"));
            grammar.Past = Pick(forms, t => t.Count == 1 && t.Contains("past"));
            grammar.PastParticiple = Pick(forms, t => t.Contains("participle") && t.Contains("past"));
            grammar.Auxiliary = Pick(forms, t => t.Contains("auxiliary"))?.Where(a => a is "haben" or "sein").Distinct().ToList() is { Count: > 0 } aux ? aux : null;
            grammar.Comparative = Pick(forms, t => t.Count == 1 && t.Contains("comparative"));
            grammar.Superlative = Pick(forms, t => t.Count == 1 && t.Contains("superlative"));
            var head = Arr(root, "head_templates").Select(h => Str(h, "expansion")).FirstOrDefault(e => !string.IsNullOrEmpty(e));
            if (head is not null && head.Length > word.Length + 2)
            {
                grammar.Summary = TextCleanup.Truncate(head, 220);
            }
        }

        if (!grammar.IsEmpty)
        {
            entry.Grammar = grammar;
        }

        var shownForms = forms.Where(f => f.Tags is { Count: > 0 } && !f.Tags.Contains("alternative") && !f.Tags.Contains("obsolete")).Take(14).ToList();
        if (shownForms.Count > 0)
        {
            entry.Forms = shownForms;
        }

        entry.Synonyms = Words(root, "synonyms", 6);

        var w = ctx.Word(o.Lang, word);
        ctx.AddEntry(w, o.SourceId, entry);
        w.Flags |= WordFlags.HasEn;
        var brief = string.Join("; ", senses.Select(s => s.Gloss).Where(g => !string.IsNullOrEmpty(g)).Take(2));
        w.OfferBrief("en", TextCleanup.Truncate(brief, 90), o.Lang == Lang.De ? 5 : 10);
        var zh = senses.SelectMany(s => s.Zh ?? []).Distinct().Take(4).ToList();
        if (zh.Count > 0)
        {
            w.Flags |= WordFlags.HasZh;
            w.OfferBrief("zh", TextCleanup.Truncate(string.Join("，", zh), 64), 12);
        }
        if (o.Lang == Lang.De && grammar.Gender is not null && entry.Pos == "noun")
        {
            w.OfferBadge(Article(grammar.Gender), 5);
        }
        else
        {
            w.OfferBadge(EcdictImporter.PosBadge(entry.Pos), 20);
        }

        return true;
    }

    // ---------------------------------------------------------------- German Wiktionary

    private static readonly HashSet<string> GermanFormTitles = new(StringComparer.Ordinal)
    {
        "Konjugierte Form", "Deklinierte Form", "Komparativ", "Superlativ", "Partizip I", "Partizip II", "Erweiterter Infinitiv",
    };

    private static bool MapGermanEdition(ImportContext ctx, Options o, JsonElement root, string word)
    {
        var pos = Str(root, "pos") ?? "other";
        var posTitle = Str(root, "pos_title");
        var rootTags = StrArr(root, "tags");
        if (rootTags.Contains("form-of") || (posTitle is not null && GermanFormTitles.Contains(posTitle)))
        {
            foreach (var sense in Arr(root, "senses"))
            {
                var tags = StrArr(sense, "tags");
                var formTags = FormTags(tags.Count > 1 ? tags : [.. tags, .. TitleTags(posTitle)]);
                foreach (var fo in Arr(sense, "form_of"))
                {
                    var lemma = Str(fo, "word");
                    if (lemma is not null)
                    {
                        ctx.StageForm(Lang.De, word, lemma, formTags, o.SourceId);
                    }
                }
            }

            return false;
        }

        if (SkipPos.Contains(pos) || word.StartsWith('-') || word.EndsWith('-'))
        {
            return false;
        }

        // Sense-aligned translations (zh, en).
        var zhBySense = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var enBySense = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in Arr(root, "translations"))
        {
            var lc = Str(t, "lang_code");
            if (lc is not ("zh" or "en"))
            {
                continue;
            }

            var tw = TextCleanup.Normalize(Str(t, "word"));
            if (tw is null)
            {
                continue;
            }

            if (lc == "zh")
            {
                if (!TextCleanup.ContainsCjk(tw))
                {
                    continue;
                }

                tw = ChineseScript.ToSimplified(tw);
            }

            var idx = Str(t, "sense_index") ?? "1";
            var map = lc == "zh" ? zhBySense : enBySense;
            if (!map.TryGetValue(idx, out var list))
            {
                map[idx] = list = [];
            }

            if (!list.Contains(tw) && list.Count < 8)
            {
                list.Add(tw);
            }
        }

        var senses = new List<Sense>();
        foreach (var sense in Arr(root, "senses"))
        {
            var glosses = StrArr(sense, "glosses");
            var tags = StrArr(sense, "tags");
            if (glosses.Count == 0 || tags.Contains("form-of"))
            {
                continue;
            }

            var idx = Str(sense, "sense_index");
            var s = new Sense
            {
                Number = idx,
                Gloss = TextCleanup.Truncate(TextCleanup.Normalize(glosses[0]) ?? string.Empty, 400),
                GlossLang = "de",
                Labels = Labels(tags, StrArr(sense, "raw_tags").Concat(StrArr(sense, "topics").Take(1))),
                Examples = Examples(sense, null, senses.Count < 4 ? 2 : 1),
            };
            if (idx is not null)
            {
                s.Zh = zhBySense.GetValueOrDefault(idx);
                s.En = enBySense.GetValueOrDefault(idx);
            }

            if (s.Gloss.Length > 0 && senses.Count < 15)
            {
                senses.Add(s);
            }
        }

        // Translations whose sense number has no matching sense (or no senses at all) go to the first sense.
        var usedIdx = new HashSet<string>(senses.Select(s => s.Number ?? string.Empty), StringComparer.Ordinal);
        foreach (var (idx, list) in zhBySense)
        {
            if (!usedIdx.Contains(idx) && senses.Count > 0)
            {
                senses[0].Zh = [.. (senses[0].Zh ?? []).Concat(list).Distinct().Take(8)];
            }
        }

        foreach (var (idx, list) in enBySense)
        {
            if (!usedIdx.Contains(idx) && senses.Count > 0)
            {
                senses[0].En = [.. (senses[0].En ?? []).Concat(list).Distinct().Take(8)];
            }
        }

        if (senses.Count == 0)
        {
            return false;
        }

        var entry = new EntryData
        {
            Headword = word,
            Pos = NormalizePos(pos),
            Senses = senses,
            RawRef = $"{o.RefPrefix}:{word}#{pos}",
        };
        ReadSounds(root, entry, englishRegions: false);

        var overview = ParseForms(root, source: string.Empty);
        var grammar = new Grammar { Gender = GenderFromTags(rootTags) };
        if (entry.Pos == "noun")
        {
            grammar.Cases = Cases(overview);
            grammar.Plural = Pick(overview, t => t.Contains("nominative") && t.Contains("plural"));
            grammar.GenitiveSingular = Pick(overview, t => t.Contains("genitive") && t.Contains("singular"))?.FirstOrDefault();
        }
        else if (entry.Pos == "verb")
        {
            grammar.Present = Pick(overview, t => t.Count == 1 && t.Contains("present"), 3);
            grammar.Past = Pick(overview, t => t.Count == 1 && t.Contains("past"));
            grammar.SubjunctiveII = Pick(overview, t => t.Contains("subjunctive-ii"));
            grammar.Imperative = Pick(overview, t => t.Contains("imperative"), 4);
            grammar.PastParticiple = Pick(overview, t => t.Contains("participle-2"));
            grammar.Auxiliary = Pick(overview, t => t.Contains("auxiliary"))?.Where(a => a is "haben" or "sein").Distinct().ToList() is { Count: > 0 } aux ? aux : null;
            grammar.SeparablePrefix = SeparablePrefix(word, grammar.Present);
        }
        else if (entry.Pos == "adj")
        {
            grammar.Comparative = Pick(overview, t => t.Contains("comparative"));
            grammar.Superlative = Pick(overview, t => t.Contains("superlative"));
        }

        if (!grammar.IsEmpty)
        {
            entry.Grammar = grammar;
        }

        // Every usable form (overview + full Flexion tables) feeds the form → lemma index.
        foreach (var f in ParseForms(root, source: null))
        {
            var cleaned = CleanGermanForm(f.Form, grammar.SeparablePrefix);
            if (cleaned is not null && f.Tags is not null && !f.Tags.Contains("auxiliary"))
            {
                ctx.StageForm(Lang.De, cleaned, word, string.Join(' ', f.Tags), o.SourceId);
            }
        }

        entry.Synonyms = Words(root, "synonyms", 8);
        entry.Related = Words(root, "coordinate_terms", 8);

        var w = ctx.Word(Lang.De, word);
        ctx.AddEntry(w, o.SourceId, entry);
        w.Flags |= WordFlags.HasDe;
        w.OfferBrief("de", TextCleanup.Truncate(senses[0].Gloss ?? string.Empty, 80), 20);
        var zh = senses.SelectMany(s => s.Zh ?? []).Distinct().Take(4).ToList();
        if (zh.Count > 0)
        {
            w.Flags |= WordFlags.HasZh;
            w.OfferBrief("zh", TextCleanup.Truncate(string.Join("，", zh), 64), 10);
        }

        var en = senses.SelectMany(s => s.En ?? []).Distinct().Take(4).ToList();
        if (en.Count > 0)
        {
            w.Flags |= WordFlags.HasEn;
            w.OfferBrief("en", TextCleanup.Truncate(string.Join(", ", en), 80), 15);
        }

        if (entry.Pos == "noun" && grammar.Gender is not null)
        {
            w.OfferBadge(Article(grammar.Gender), 0);
        }
        else
        {
            w.OfferBadge(EcdictImporter.PosBadge(entry.Pos), 10);
        }

        return true;
    }

    // ---------------------------------------------------------------- Chinese Wiktionary

    private static bool MapChineseEdition(ImportContext ctx, Options o, JsonElement root, string word)
    {
        var pos = Str(root, "pos") ?? "other";
        if (SkipPos.Contains(pos) || pos == "unknown" || word.StartsWith('-') || word.EndsWith('-'))
        {
            return false;
        }

        string? gender = GenderFromTags(StrArr(root, "tags"));
        var senses = new List<Sense>();
        var examples = new List<Example>();
        foreach (var sense in Arr(root, "senses"))
        {
            var tags = StrArr(sense, "tags");
            if (tags.Contains("form-of") || tags.Contains("alt-of"))
            {
                continue;
            }

            foreach (var gloss in StrArr(sense, "glosses"))
            {
                if (TextCleanup.TrySplitExample(gloss, out var text, out var translation))
                {
                    examples.Add(new Example { Text = text, Translation = ChineseScript.ToSimplified(translation) });
                    continue;
                }

                var (clean, g) = TextCleanup.CleanZhGloss(gloss);
                gender ??= g;
                if (clean is null || !TextCleanup.ContainsCjk(clean) || senses.Count >= 10)
                {
                    continue;
                }

                senses.Add(new Sense
                {
                    Gloss = TextCleanup.Truncate(ChineseScript.ToSimplified(clean), 200),
                    GlossLang = "zh",
                    Labels = Labels(tags, StrArr(sense, "raw_tags").Select(ChineseScript.ToSimplified)),
                });
            }

            foreach (var ex in Arr(sense, "examples").Take(1))
            {
                var t = TextCleanup.Normalize(Str(ex, "text"));
                var tr = TextCleanup.Normalize(Str(ex, "translation"));
                if (t is not null && t.Length <= 200 && examples.Count < 3)
                {
                    examples.Add(new Example { Text = t, Translation = tr is null ? null : ChineseScript.ToSimplified(tr) });
                }
            }
        }

        if (senses.Count == 0)
        {
            return false;
        }

        if (examples.Count > 0)
        {
            senses[0].Examples = examples.Take(3).ToList();
        }

        var entry = new EntryData
        {
            Headword = word,
            Pos = NormalizePos(pos),
            Senses = senses,
            RawRef = $"{o.RefPrefix}:{word}#{pos}",
        };
        if (gender is not null && entry.Pos == "noun")
        {
            entry.Grammar = new Grammar { Gender = gender };
        }

        ReadSounds(root, entry, englishRegions: false);

        var w = ctx.Word(o.Lang, word);
        ctx.AddEntry(w, o.SourceId, entry);
        w.Flags |= WordFlags.HasZh;
        w.OfferBrief("zh", TextCleanup.Truncate(string.Join("；", senses.Take(2).Select(s => s.Gloss)), 64), 1);
        if (gender is not null && entry.Pos == "noun")
        {
            w.OfferBadge(Article(gender), 3);
        }

        return true;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// zh.wiktionary entries of Chinese words list English and German translations ("防火牆" → firewall, Firewall).
    /// Every translation that is a headword of the dictionary gets one <see cref="EntryData.ReverseKind"/> entry
    /// naming those Chinese words, so the 中 → 英/德 index finds it and its page shows where the link comes from.
    /// Runs after the other sources: only headwords that already have entries are linked.
    /// </summary>
    public static void ImportReverseLinks(ImportContext ctx, string path, int sourceId, string refPrefix, int? limit)
    {
        var needleA = "\"lang_code\": \"zh\""u8.ToArray();
        var needleB = "\"lang_code\":\"zh\""u8.ToArray();
        var links = new Dictionary<WordAcc, List<ReverseLink>>();
        using var reader = new Utf8LineReader(path);
        long records = 0, count = 0;
        var sw = Stopwatch.StartNew();
        while (reader.TryReadLine(out var line))
        {
            var span = line.Span;
            if (span.IndexOf("\"translations\""u8) < 0 || (span.IndexOf(needleA) < 0 && span.IndexOf(needleB) < 0))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var word = Str(root, "word")?.Trim();
            if (Str(root, "lang_code") != "zh" || string.IsNullOrEmpty(word) || word.Length > ChineseTerms.MaxTermLength || !word.All(ChineseTerms.IsHan))
            {
                continue;
            }

            records++;
            var chinese = ChineseScript.ToSimplified(word);
            var rawRef = $"{refPrefix}:{word}#{Str(root, "pos") ?? "other"}";
            foreach (var t in Arr(root, "translations").Concat(Arr(root, "senses").SelectMany(s => Arr(s, "translations"))))
            {
                Lang? lang = Str(t, "lang_code") switch { "en" => Lang.En, "de" => Lang.De, _ => null };
                if (lang is not { } l || TextCleanup.Normalize(Str(t, "word")) is not { } target || ResolveHeadword(ctx, l, target) is not { } acc)
                {
                    continue;
                }

                if (!links.TryGetValue(acc, out var list))
                {
                    links[acc] = list = [];
                }

                if (list.Count < 12 && !list.Any(x => x.Chinese == chinese))
                {
                    var sense = TextCleanup.Normalize(Str(t, "sense"));
                    list.Add(new ReverseLink(chinese, sense is null ? null : TextCleanup.Truncate(ChineseScript.ToSimplified(sense), 40), rawRef));
                    count++;
                }
            }

            if (limit is not null && records >= limit)
            {
                break;
            }
        }

        foreach (var (acc, list) in links)
        {
            ctx.AddEntry(acc, sourceId, new EntryData
            {
                Headword = acc.Word,
                Kind = EntryData.ReverseKind,
                Senses = list.Select(x => new Sense { Gloss = x.Chinese, GlossLang = "zh", Labels = x.Sense is null ? null : [x.Sense] }).ToList(),
                RawRef = string.Join(", ", list.Select(x => x.RawRef).Distinct().Take(3)),
            });
            acc.Flags |= WordFlags.HasZh;
            acc.OfferBrief("zh", TextCleanup.Truncate(string.Join("，", list.Select(x => x.Chinese).Take(4)), 64), 20);
        }

        ctx.Log($"  {refPrefix} (zh): {records:N0} Chinese entries with translations, {count:N0} links to {links.Count:N0} headwords in {sw.Elapsed.TotalSeconds:F0}s");
    }

    private sealed record ReverseLink(string Chinese, string? Sense, string RawRef);

    /// <summary>The headword a translation names, as written or without "to " / "sich " ("to walk" → walk).</summary>
    private static WordAcc? ResolveHeadword(ImportContext ctx, Lang lang, string target)
    {
        if (ctx.TryGetWord(lang, target, out var acc) && acc.Entries > 0)
        {
            return acc;
        }

        var bare = lang == Lang.En && target.StartsWith("to ", StringComparison.Ordinal) ? target[3..]
            : lang == Lang.De && target.StartsWith("sich ", StringComparison.Ordinal) ? target[5..]
            : null;
        return bare is not null && ctx.TryGetWord(lang, bare, out acc) && acc.Entries > 0 ? acc : null;
    }

    // ---------------------------------------------------------------- Translation tables

    /// <summary>Tags of Chinese varieties other than standard Mandarin (their words differ: Hokkien 厝 "house").</summary>
    private static readonly HashSet<string> ChineseVarietyTags = new(StringComparer.Ordinal)
    {
        "Hokkien", "Dungan", "Cantonese", "Teochew", "Hakka", "Wu", "Min", "Min-Nan", "Min-Dong", "Jin", "Xiang", "Gan",
        "Shanghainese", "Sichuanese", "Taishanese", "Classical-Chinese", "Literary-Chinese", "dialectal",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "with", "that", "from", "into", "which", "used", "being", "something", "someone", "other", "such", "their",
        "this", "these", "those", "very", "have", "been", "especially", "usually", "often", "also", "more", "most",
        "some", "than", "they", "them", "what", "when", "where", "whose",
    };

    /// <summary>Mandarin rows of an en.wiktionary translation table, and generic "Chinese" rows of no other variety.</summary>
    private static bool IsMandarin(JsonElement t)
    {
        if ((Str(t, "lang_code") ?? Str(t, "code")) is not ("cmn" or "zh"))
        {
            return false;
        }

        var lang = Str(t, "lang") ?? string.Empty;
        return lang.Contains("Mandarin", StringComparison.Ordinal)
            || (lang == "Chinese" && !StrArr(t, "tags").Any(ChineseVarietyTags.Contains));
    }

    /// <summary>
    /// Adds the simplified words of Mandarin translation rows (at most 8): "防火牆 /防火墙" → 防火墙. A row without a
    /// word may give it in its note ("電腦 /电脑 (diànnǎo, literally …)"); rows with Latin letters are skipped.
    /// </summary>
    private static List<string>? AddMandarin(List<string>? list, IEnumerable<JsonElement> rows)
    {
        foreach (var t in rows)
        {
            if (!IsMandarin(t) || (Str(t, "word") ?? Str(t, "note")) is not { } text)
            {
                continue;
            }

            var paren = text.IndexOfAny(['(', '（']);
            foreach (var part in (paren >= 0 ? text[..paren] : text).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length > ChineseTerms.MaxTermLength || !part.All(ChineseTerms.IsHan))
                {
                    continue;
                }

                var zh = ChineseScript.ToSimplified(part);
                list ??= [];
                if (list.Count < 8 && !list.Contains(zh))
                {
                    list.Add(zh);
                }
            }
        }

        return list;
    }

    /// <summary>The sense whose gloss shares the most words with a translation table's heading; the first if none does.</summary>
    private static int BestSense(List<Sense> senses, string? heading)
    {
        var words = ContentWords(heading);
        var best = 0;
        var bestShared = 0;
        for (var i = 0; i < senses.Count && words.Count > 0; i++)
        {
            var shared = ContentWords(senses[i].Gloss).Count(words.Contains);
            if (shared > bestShared)
            {
                best = i;
                bestShared = shared;
            }
        }

        return best;
    }

    private static HashSet<string> ContentWords(string? text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
        {
            return words;
        }

        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && char.IsLetter(text[i]))
            {
                start = start < 0 ? i : start;
                continue;
            }

            if (start >= 0 && i - start >= 4 && !StopWords.Contains(text[start..i]))
            {
                words.Add(text[start..i]);
            }

            start = -1;
        }

        return words;
    }

    internal static string? CleanGermanForm(string form, string? separablePrefix)
    {
        var f = form.Trim();
        if (f.EndsWith('!'))
        {
            f = f[..^1].TrimEnd();
        }

        if (f.Length == 0 || f.Contains('…', StringComparison.Ordinal) || f.Contains('/', StringComparison.Ordinal) && !f.StartsWith("er/sie/es ", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var p in GermanPronouns)
        {
            if (f.StartsWith(p, StringComparison.Ordinal))
            {
                f = f[p.Length..];
                break;
            }
        }

        if (!f.Contains(' ', StringComparison.Ordinal))
        {
            return f;
        }

        var parts = f.Split(' ');
        if (parts.Length == 2 && ((separablePrefix is not null && parts[1] == separablePrefix) || parts[0] == "am"))
        {
            return f;
        }

        return null; // compound tenses ("bin gegangen"), zu-infinitives, "gehen Sie"
    }

    private static string? SeparablePrefix(string lemma, List<string>? present)
    {
        if (present is null)
        {
            return null;
        }

        foreach (var p in present)
        {
            var parts = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && lemma.StartsWith(parts[1], StringComparison.Ordinal) && lemma.Length > parts[1].Length + 2)
            {
                return parts[1];
            }
        }

        return null;
    }

    private static List<CaseRow>? Cases(List<WordForm> overview)
    {
        var rows = new List<CaseRow>();
        foreach (var c in new[] { "nominative", "genitive", "dative", "accusative" })
        {
            string? Cell(string number)
            {
                var f = overview.FirstOrDefault(x => x.Tags is not null && x.Tags.Contains(c) && x.Tags.Contains(number));
                return f is null ? null : (f.Article is null ? f.Form : $"{f.Article} {f.Form}");
            }

            var sg = Cell("singular");
            var pl = Cell("plural");
            if (sg is not null || pl is not null)
            {
                rows.Add(new CaseRow { Case = c[..3], Singular = sg, Plural = pl });
            }
        }

        return rows.Count > 0 ? rows : null;
    }

    private static List<WordForm> ParseForms(JsonElement root, string? source)
    {
        var result = new List<WordForm>();
        foreach (var f in Arr(root, "forms"))
        {
            var form = TextCleanup.Normalize(Str(f, "form"));
            if (form is null || form.Length > 60)
            {
                continue;
            }

            var fs = Str(f, "source") ?? string.Empty;
            if (source is not null && fs != source)
            {
                continue;
            }

            var tags = StrArr(f, "tags");
            if (tags.Any(FormNoiseTags.Contains) || form == "-" || form == "—")
            {
                continue;
            }

            result.Add(new WordForm { Form = form, Tags = tags.Count > 0 ? tags : null, Article = Str(f, "article") });
        }

        return result;
    }

    private static List<string>? Pick(List<WordForm> forms, Func<List<string>, bool> predicate, int max = 2)
    {
        var result = forms.Where(f => f.Tags is not null && predicate(f.Tags)).Select(f => f.Form).Distinct().Take(max).ToList();
        return result.Count > 0 ? result : null;
    }

    private static void ReadSounds(JsonElement root, EntryData entry, bool englishRegions)
    {
        var ipa = new List<Pron>();
        var audio = new List<AudioRef>();
        foreach (var s in Arr(root, "sounds"))
        {
            var tags = StrArr(s, "tags");
            var region = englishRegions ? EnglishRegion(tags) : GermanRegion(tags, StrArr(s, "raw_tags"));
            var text = Str(s, "ipa");
            if (!string.IsNullOrWhiteSpace(text) && ipa.Count < 6 && !ipa.Any(p => p.Text == text && p.Region == region))
            {
                var note = string.Join(", ", StrArr(s, "raw_tags").Take(1));
                ipa.Add(new Pron { Text = text.Trim(), Region = region, Note = note.Length > 0 ? TextCleanup.Truncate(note, 60) : null });
            }

            var file = Str(s, "audio");
            if (!string.IsNullOrWhiteSpace(file) && audio.Count < 4)
            {
                audio.Add(new AudioRef { File = file.Trim(), Region = region, Mp3Url = Str(s, "mp3_url") });
            }
        }

        if (ipa.Count > 0)
        {
            // Phonemic /…/ before phonetic […], keeping region grouping stable.
            entry.Ipa = ipa.OrderBy(p => p.Text.StartsWith('/') ? 0 : 1).ToList();
        }

        if (audio.Count > 0)
        {
            entry.Audio = audio;
        }
    }

    private static string? EnglishRegion(List<string> tags)
    {
        foreach (var t in tags)
        {
            switch (t)
            {
                case "UK" or "Received-Pronunciation" or "British" or "RP" or "England" or "Southern-England":
                    return "UK";
                case "US" or "General-American" or "American":
                    return "US";
            }
        }

        foreach (var t in tags)
        {
            switch (t)
            {
                case "Australia": return "AU";
                case "Canada": return "CA";
                case "New-Zealand": return "NZ";
                case "Ireland": return "IE";
                case "Scotland": return "SC";
                case "India": return "IN";
            }
        }

        return null;
    }

    private static string? GermanRegion(List<string> tags, List<string> rawTags)
    {
        if (tags.Contains("Austrian German") || tags.Contains("Austrian") || rawTags.Any(r => r.Contains("Österreich", StringComparison.Ordinal)))
        {
            return "AT";
        }

        if (tags.Contains("Swiss German") || tags.Contains("Switzerland") || rawTags.Any(r => r.Contains("Schweiz", StringComparison.Ordinal)))
        {
            return "CH";
        }

        return null;
    }

    private static List<Example>? Examples(JsonElement sense, string? translationField, int max)
    {
        var list = new List<Example>();
        foreach (var ex in Arr(sense, "examples"))
        {
            var type = Str(ex, "type");
            if (type == "quotation")
            {
                continue;
            }

            var text = TextCleanup.Normalize(Str(ex, "text"));
            if (text is null || text.Length > 220)
            {
                continue;
            }

            var tr = translationField is null ? null : TextCleanup.Normalize(Str(ex, translationField) ?? Str(ex, "translation"));
            list.Add(new Example { Text = text, Translation = tr });
        }

        if (list.Count == 0)
        {
            return null;
        }

        return list.OrderBy(e => e.Text.Length).Take(max).ToList();
    }

    private static List<string>? Labels(List<string> tags, IEnumerable<string> extra)
    {
        var labels = tags.Where(LabelTags.Contains).ToList();
        foreach (var e in extra)
        {
            var n = TextCleanup.Normalize(e);
            if (n is not null && n.Length <= 40 && !labels.Contains(n))
            {
                labels.Add(n);
            }
        }

        return labels.Count > 0 ? labels.Take(4).ToList() : null;
    }

    private static List<string>? Words(JsonElement root, string property, int max)
    {
        var list = Arr(root, property).Select(e => TextCleanup.Normalize(Str(e, "word"))).OfType<string>().Distinct().Take(max).ToList();
        return list.Count > 0 ? list : null;
    }

    private static string FormTags(IEnumerable<string> tags) =>
        string.Join(' ', tags.Where(t => t is not "form-of" and not "alt-of" and not "active" and not "indicative").Distinct());

    private static IEnumerable<string> TitleTags(string? posTitle) => posTitle switch
    {
        "Komparativ" => ["comparative"],
        "Superlativ" => ["superlative"],
        "Partizip I" => ["participle-1"],
        "Partizip II" => ["participle-2"],
        _ => [],
    };

    private static string? GenderFromTags(List<string> tags)
    {
        var g = new List<string>(3);
        if (tags.Contains("masculine"))
        {
            g.Add("m");
        }

        if (tags.Contains("feminine"))
        {
            g.Add("f");
        }

        if (tags.Contains("neuter"))
        {
            g.Add("n");
        }

        return g.Count > 0 ? string.Join(',', g) : null;
    }

    internal static string Article(string gender) => string.Join('/', gender.Split(',').Select(g => g switch
    {
        "m" => "der",
        "f" => "die",
        "n" => "das",
        _ => g,
    }));

    private static string NormalizePos(string pos) => pos switch
    {
        "noun" or "verb" or "adj" or "adv" or "pron" or "prep" or "conj" or "intj" or "num" or "det" or "article" or "particle" or "phrase" => pos,
        "prep_phrase" or "proverb" => "phrase",
        "abbrev" or "contraction" => "abbrev",
        "postp" => "prep",
        _ => "other",
    };

    private static string EtymologySuffix(JsonElement root) =>
        root.TryGetProperty("etymology_number", out var n) && n.ValueKind == JsonValueKind.Number ? $"/{n.GetInt32()}" : string.Empty;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static List<string> StrArr(JsonElement e, string name)
    {
        var list = new List<string>();
        foreach (var x in Arr(e, name))
        {
            if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s)
            {
                list.Add(s);
            }
        }

        return list;
    }
}
