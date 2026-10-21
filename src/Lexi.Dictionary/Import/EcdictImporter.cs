using System.Globalization;
using System.Text.RegularExpressions;
using Lexi.Core.Models;
using Lexi.Core.Search;

namespace Lexi.Dictionary.Import;

/// <summary>
/// Imports ecdict.csv (word, phonetic, definition, translation, pos, collins, oxford, tag, bnc, frq, exchange, detail, audio).
/// Line breaks inside fields are stored as the two characters "\n".
/// </summary>
internal static partial class EcdictImporter
{
    private static readonly Dictionary<char, string> ExchangeTags = new()
    {
        ['p'] = "past",
        ['d'] = "participle past",
        ['i'] = "participle present",
        ['3'] = "present singular third-person",
        ['r'] = "comparative",
        ['t'] = "superlative",
        ['s'] = "plural",
    };

    private static readonly HashSet<string> CoreTags = new(StringComparer.Ordinal) { "zk", "gk", "cet4", "cet6", "ky", "toefl", "ielts", "gre" };

    public static void Import(ImportContext ctx, string csvPath, int sourceId, int? limit)
    {
        var rows = 0;
        var entries = 0;
        var formOnly = 0;
        var first = true;
        foreach (var f in CsvReader.Read(csvPath))
        {
            if (first)
            {
                first = false;
                continue;
            }

            if (f.Length < 11)
            {
                continue;
            }

            rows++;
            var word = f[0].Trim();
            if (word.Length == 0 || word.Length > 60 || !word.Any(char.IsLetter))
            {
                continue;
            }

            var phonetic = f[1].Trim();
            var zhLines = Lines(f[3]);
            var enLines = Lines(f[2]);
            var collins = ParseInt(f[5]);
            var oxford = f[6].Trim() == "1";
            var tags = f[7].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var bnc = ParseInt(f[8]);
            var frq = ParseInt(f[9]);
            var exchange = ParseExchange(f[10]);

            foreach (var (code, forms) in exchange)
            {
                if (ExchangeTags.TryGetValue(code, out var t))
                {
                    foreach (var form in forms)
                    {
                        ctx.StageForm(Lang.En, form, word, t, sourceId);
                    }
                }
            }

            if (exchange.TryGetValue('0', out var lemmas))
            {
                var types = exchange.TryGetValue('1', out var ty) ? string.Concat(ty) : string.Empty;
                var lemmaTags = string.Join(' ', types.Select(c => ExchangeTags.GetValueOrDefault(c)).Where(x => x is not null).Distinct());
                foreach (var lemma in lemmas)
                {
                    ctx.StageForm(Lang.En, word, lemma, lemmaTags, sourceId);
                }
            }

            if (zhLines.Count == 0 && enLines.Count == 0)
            {
                continue;
            }

            // Rows like "children: pl. 孩子" or "went: go的过去式" are only a pointer to the lemma.
            if (lemmas is not null && zhLines.Count > 0 && zhLines.All(IsInflectionNote))
            {
                formOnly++;
                continue;
            }

            var pos = MainPos(zhLines);
            var entry = new EntryData
            {
                Headword = word,
                Pos = pos,
                RawRef = $"ecdict:{word}",
                Senses = [],
            };

            if (phonetic.Length > 0)
            {
                entry.Ipa = [new Pron { Text = "/" + phonetic + "/", Note = "ECDICT" }];
            }

            foreach (var line in zhLines.Take(12))
            {
                entry.Senses.Add(new Sense { Gloss = line, GlossLang = "zh" });
            }

            foreach (var line in enLines.Take(10))
            {
                entry.Senses.Add(new Sense { Gloss = line, GlossLang = "en" });
            }

            var grammar = new Grammar
            {
                Past = Get(exchange, 'p'),
                PastParticiple = Get(exchange, 'd'),
                PresentParticiple = Get(exchange, 'i'),
                Present = Get(exchange, '3'),
                Comparative = Get(exchange, 'r'),
                Superlative = Get(exchange, 't'),
                Plural = Get(exchange, 's'),
            };
            if (!grammar.IsEmpty)
            {
                entry.Grammar = grammar;
            }

            var isCore = oxford || collins > 0 || tags.Any(CoreTags.Contains);
            if (tags.Count > 0 || collins > 0 || oxford || bnc > 0 || frq > 0)
            {
                entry.Learner = new LearnerInfo { Tags = tags.Count > 0 ? tags : null, Collins = collins, Oxford = oxford, Bnc = bnc, Frq = frq };
            }

            var w = ctx.Word(Lang.En, word);
            ctx.AddEntry(w, sourceId, entry);
            entries++;

            if (zhLines.Count > 0)
            {
                w.Flags |= WordFlags.HasZh;
                w.OfferBrief("zh", BriefFromZh(zhLines), 0);
            }

            if (enLines.Count > 0)
            {
                w.OfferBrief("en", TextCleanup.Truncate(enLines[0], 80), 5);
            }

            w.OfferBadge(PosBadge(pos), 10);
            if (isCore)
            {
                w.Flags |= WordFlags.Core;
            }

            // Capitalized, not an acronym, and no part-of-speech marker in the translation: a name ("Hauser: 豪泽").
            if (pos is null && char.IsUpper(word[0]) && word.Skip(1).Any(char.IsLower) && !isCore)
            {
                w.Flags |= WordFlags.ProperNoun;
            }

            w.RankZipf = Math.Max(w.RankZipf, Math.Max(RankToZipf(frq), RankToZipf(bnc)));

            if (limit is not null && entries >= limit)
            {
                break;
            }
        }

        ctx.Log($"  ECDICT: {rows:N0} rows, {entries:N0} entries, {formOnly:N0} inflection-only rows linked to lemmas");
    }

    /// <summary>Approximate Zipf value from a corpus frequency rank (rank 1 ≈ 7.7, 1 000 ≈ 5.0, 50 000 ≈ 3.5).</summary>
    internal static float RankToZipf(int rank) => rank <= 0 ? 0f : (float)Math.Max(0, 7.7 - (0.9 * Math.Log10(rank)));

    internal static string? PosBadge(string? pos) => pos switch
    {
        "noun" => "n.",
        "verb" => "v.",
        "adj" => "adj.",
        "adv" => "adv.",
        "prep" => "prep.",
        "conj" => "conj.",
        "pron" => "pron.",
        "intj" => "int.",
        "num" => "num.",
        "article" => "art.",
        "abbrev" => "abbr.",
        "phrase" => "phr.",
        _ => null,
    };

    private static string? MainPos(List<string> zhLines)
    {
        foreach (var line in zhLines)
        {
            var m = PosPrefix().Match(line);
            if (!m.Success)
            {
                continue;
            }

            return m.Groups[1].Value switch
            {
                "n" => "noun",
                "v" or "vt" or "vi" or "aux" => "verb",
                "a" or "adj" => "adj",
                "ad" or "adv" => "adv",
                "prep" => "prep",
                "conj" => "conj",
                "pron" => "pron",
                "int" or "interj" => "intj",
                "num" => "num",
                "art" => "article",
                "abbr" => "abbrev",
                _ => null,
            };
        }

        return null;
    }

    private static string BriefFromZh(List<string> zhLines)
    {
        // Prefer curated lines over "[网络]" (web-collected) ones.
        var lines = zhLines.Where(l => !l.StartsWith("[网络]", StringComparison.Ordinal)).Take(2).ToList();
        if (lines.Count == 0)
        {
            lines = zhLines.Take(1).ToList();
        }

        return TextCleanup.Truncate(string.Join("；", lines), 64);
    }

    private static bool IsInflectionNote(string line) => InflectionNote().IsMatch(line);

    private static List<string> Lines(string field)
    {
        var result = new List<string>();
        foreach (var raw in field.Replace("\\r", string.Empty, StringComparison.Ordinal).Split("\\n", StringSplitOptions.None))
        {
            var line = TextCleanup.Normalize(raw);
            if (line is not null)
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static Dictionary<char, List<string>> ParseExchange(string exchange)
    {
        var result = new Dictionary<char, List<string>>();
        foreach (var part in exchange.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length < 3 || part[1] != ':')
            {
                continue;
            }

            var value = part[2..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (!result.TryGetValue(part[0], out var list))
            {
                result[part[0]] = list = [];
            }

            if (part[0] == '1')
            {
                list.Add(value);
            }
            else
            {
                foreach (var v in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    list.Add(v);
                }
            }
        }

        return result;
    }

    private static List<string>? Get(Dictionary<char, List<string>> exchange, char code) =>
        exchange.TryGetValue(code, out var v) && v.Count > 0 ? v : null;

    private static int ParseInt(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    [GeneratedRegex(@"^(n|v|vt|vi|aux|a|adj|ad|adv|prep|conj|pron|int|interj|num|art|abbr)\.")]
    private static partial Regex PosPrefix();

    [GeneratedRegex(@"^(pl\.|.{0,40}的(过去式|过去分词|现在分词|复数|第三人称单数|比较级|最高级|复数形式))")]
    private static partial Regex InflectionNote();
}
