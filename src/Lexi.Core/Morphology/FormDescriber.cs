using Lexi.Core.Models;

namespace Lexi.Core.Morphology;

/// <summary>
/// Turns grammatical tags of an inflected form (Wiktionary/ECDICT vocabulary, space separated)
/// into a short bilingual label, e.g. "nominative plural" → "复数 Plural", "past" (de) → "过去时 Präteritum".
/// </summary>
public static class FormDescriber
{
    public static string Describe(string? tags, Lang lang)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return lang == Lang.De ? "词形 Form" : "词形";
        }

        var t = new HashSet<string>(tags.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        bool Has(string x) => t.Contains(x);

        string? person = null;
        if (Has("first-person"))
        {
            person = Has("plural") ? "1Pl" : "1Sg";
        }
        else if (Has("second-person"))
        {
            person = Has("plural") ? "2Pl" : "2Sg";
        }
        else if (Has("third-person"))
        {
            person = Has("plural") ? "3Pl" : "3Sg";
        }

        if (lang == Lang.De)
        {
            if (Has("participle-2") || (Has("participle") && Has("past")))
            {
                return "过去分词 Partizip II";
            }

            if (Has("participle-1") || (Has("participle") && Has("present")))
            {
                return "现在分词 Partizip I";
            }

            if (Has("subjunctive-ii"))
            {
                return With("虚拟式II Konjunktiv II", person);
            }

            if (Has("subjunctive-i"))
            {
                return With("虚拟式I Konjunktiv I", person);
            }

            if (Has("imperative"))
            {
                return "命令式 Imperativ";
            }

            if (Has("past") || Has("preterite"))
            {
                return With("过去时 Präteritum", person);
            }

            if (Has("present"))
            {
                return With("现在时 Präsens", person);
            }

            if (Has("comparative"))
            {
                return "比较级 Komparativ";
            }

            if (Has("superlative"))
            {
                return "最高级 Superlativ";
            }

            if (Has("diminutive"))
            {
                return "指小形式 Diminutiv";
            }

            var kase = CaseLabel(t);
            if (Has("plural"))
            {
                return kase is null ? "复数 Plural" : $"复数{kase.Value.Zh} Plural {kase.Value.De}";
            }

            if (kase is not null && Has("singular"))
            {
                return $"单数{kase.Value.Zh} {kase.Value.De} Sg.";
            }

            if (Has("positive") || Has("strong") || Has("weak") || Has("mixed"))
            {
                return "变格形式 Deklination";
            }

            if (Has("alternative") || Has("variant"))
            {
                return "异体 Variante";
            }

            return "词形 Form";
        }

        // English
        if (Has("participle") && Has("past"))
        {
            return "过去分词";
        }

        if (Has("participle") && Has("present"))
        {
            return "现在分词";
        }

        if (Has("past"))
        {
            return "过去式";
        }

        if (Has("third-person") && Has("present"))
        {
            return "第三人称单数";
        }

        if (Has("plural"))
        {
            return "复数";
        }

        if (Has("comparative"))
        {
            return "比较级";
        }

        if (Has("superlative"))
        {
            return "最高级";
        }

        if (Has("alternative") || Has("variant"))
        {
            return "异体拼写";
        }

        return "词形";
    }

    private static string With(string label, string? person) => person is null ? label : $"{label} ({person})";

    private static (string Zh, string De)? CaseLabel(HashSet<string> t)
    {
        // A form shared by several cases (Häuser: nom/gen/acc pl.) is best described without a case.
        var cases = (t.Contains("nominative") ? 1 : 0) + (t.Contains("genitive") ? 1 : 0) + (t.Contains("dative") ? 1 : 0) + (t.Contains("accusative") ? 1 : 0);
        if (cases != 1)
        {
            return null;
        }

        if (t.Contains("genitive"))
        {
            return ("属格", "Genitiv");
        }

        if (t.Contains("dative"))
        {
            return ("与格", "Dativ");
        }

        if (t.Contains("accusative"))
        {
            return ("宾格", "Akkusativ");
        }

        // Nominative is the citation case: "nominative plural" is simply "the plural".
        return null;
    }
}
