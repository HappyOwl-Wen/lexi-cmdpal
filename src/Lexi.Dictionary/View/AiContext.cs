using System.Text;
using Lexi.Core.Models;
using Lexi.Core.Search;

namespace Lexi.Dictionary.View;

/// <summary>
/// Local dictionary facts handed to the AI as reference material (so it does not have to invent forms or IPA).
/// Shared by the extension and `lexi ai`, so both send the same prompt.
/// </summary>
public static class AiContext
{
    public static string LanguageName(Lang lang) => lang == Lang.De ? "德语" : "英语";

    /// <summary>Words to contrast with when the user asks for easily confused words without naming any.</summary>
    public static IReadOnlyList<string> CompareCandidates(WordView v) => v.Related.Concat(v.Synonyms).Distinct().Take(3).ToList();

    /// <summary>The dictionary's English and German equivalents of a Chinese query, best first (null when none).</summary>
    public static string? ChineseCandidates(IReadOnlyList<SearchHit> hits)
    {
        var sb = new StringBuilder();
        foreach (var lang in new[] { Lang.En, Lang.De })
        {
            var words = hits.Where(h => h.Word.Lang == lang && h.Kind == MatchKind.Chinese)
                .Select(h => lang == Lang.De && h.Word.Badge is "der" or "die" or "das" ? $"{h.Word.Badge} {h.Word.Word}" : h.Word.Word)
                .Take(6)
                .ToList();
            if (words.Count > 0)
            {
                sb.Append("- 词典中的").Append(LanguageName(lang)).Append("对应词（按相关度）：").AppendLine(string.Join("、", words));
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    public static string Describe(WordView v)
    {
        var sb = new StringBuilder();
        sb.Append("词条：").Append(v.Title).Append('（').Append(LanguageName(v.Word.Lang));
        foreach (var p in v.PartsOfSpeech.Take(2))
        {
            sb.Append('，').Append(WordViewBuilder.PosLabel(p, v.Word.Lang));
        }

        sb.AppendLine("）");
        foreach (var g in v.Grammar.Take(8))
        {
            sb.Append("- ").Append(g.Label).Append('：').AppendLine(g.Value);
        }

        if (v.Ipa.Count > 0)
        {
            sb.Append("- IPA：").AppendLine(v.Ipa[0].Text);
        }

        void Senses(string label, IReadOnlyList<MeaningBlock> blocks, int max)
        {
            var glosses = blocks.SelectMany(b => b.Senses).Select(s => s.Gloss).Where(g => !string.IsNullOrEmpty(g)).Distinct().Take(max).ToList();
            if (glosses.Count > 0)
            {
                sb.Append("- ").Append(label).Append('：').AppendLine(string.Join("；", glosses));
            }
        }

        Senses("中文释义", v.Chinese, 4);
        Senses("English", v.English, 4);
        Senses("Deutsch", v.German, 2);
        if (v.Related.Count + v.Synonyms.Count > 0)
        {
            sb.Append("- 近义/相关词：").AppendLine(string.Join(", ", v.Synonyms.Concat(v.Related).Take(8)));
        }

        return sb.ToString();
    }
}
