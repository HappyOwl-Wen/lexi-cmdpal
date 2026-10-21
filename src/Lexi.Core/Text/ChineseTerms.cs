using System.Text;

namespace Lexi.Core.Text;

/// <summary>
/// Splits Chinese definition lines into the terms of the reverse (中 → 英/德) index and normalizes Chinese
/// queries. "n. 恐吓, 恶兆, 威胁" → 恐吓, 恶兆, 威胁. Part-of-speech prefixes, bracketed notes ("[医]",
/// "（intimidate的现在分词）", "〈阴〉") and tokens with Latin letters or digits are dropped; every term keeps its
/// position in the line, because the first terms of a definition are its main meaning.
/// </summary>
public static class ChineseTerms
{
    /// <summary>Longer "terms" are descriptions rather than translations.</summary>
    public const int MaxTermLength = 8;

    // Segments are the items of a definition line; a segment may still join several terms ("计算机·网络").
    private static readonly char[] SegmentSeparators = [',', ';', '、', '/', '|', ':', '!', '?', '。', ' '];
    private static readonly char[] TermSeparators = ['.', '·', '=', '+', '&'];

    /// <param name="Text">The term (simplified Chinese when the source is).</param>
    /// <param name="Position">Index among the line's terms; 0 is usually the main meaning.</param>
    /// <param name="IsStem">Derived from an adjective/adverb by dropping 的/地 ("危险的" → 危险).</param>
    public readonly record struct Term(string Text, int Position, bool IsStem);

    private static readonly string[] NameTags = ["[人名]", "[地名]", "[男子名]", "[女子名]", "[姓氏]"];

    /// <summary>ECDICT lines taken from web corpora ("[网络] …") are less reliable.</summary>
    public static bool IsWebLine(string line) => line.AsSpan().TrimStart().StartsWith("[网络]", StringComparison.Ordinal);

    /// <summary>ECDICT transliterations of names ("[地名] …", "[人名] …"): rarely what a Chinese query is after.</summary>
    public static bool IsNameLine(string line)
    {
        var text = line.AsSpan().TrimStart();
        foreach (var tag in NameTags)
        {
            if (text.StartsWith(tag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static List<Term> Extract(string? line)
    {
        var terms = new List<Term>();
        if (string.IsNullOrWhiteSpace(line))
        {
            return terms;
        }

        var text = RemoveBracketed(TextNormalizer.Clean(line));
        var position = 0;
        foreach (var segment in text.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A pattern with a placeholder ("吃...的人", "给…房子住", "～的") is not a translation; its pieces are
            // fragments. NFKC has already turned "…" into "..." and "～" into "~".
            if (segment.Contains("..", StringComparison.Ordinal) || segment.Contains('~'))
            {
                continue;
            }

            foreach (var token in segment.Split(TermSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IsTerm(token))
                {
                    continue;
                }

                terms.Add(new Term(token, position, IsStem: false));
                if (token.Length >= 3 && token[^1] is '的' or '地')
                {
                    terms.Add(new Term(token[..^1], position, IsStem: true));
                }

                position++;
            }
        }

        return terms;
    }

    /// <summary>The Chinese characters of a query, without spaces and punctuation ("房子 苹果" → 房子苹果).</summary>
    public static string QueryKey(string query)
    {
        var sb = new StringBuilder(query.Length);
        foreach (var c in query)
        {
            if (IsHan(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static bool IsHan(char c) => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿' or '〇';

    private static bool IsTerm(string token)
    {
        if (token.Length is 0 or > MaxTermLength || token[0] == '的')
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!IsHan(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Drops "( … )", "[ … ]", "〈 … 〉", "【 … 】", "< … >", "〔 … 〕" segments and quotation marks.</summary>
    private static string RemoveBracketed(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var close = c switch
            {
                '(' => ')',
                '[' => ']',
                '〈' => '〉',
                '【' => '】',
                '<' => '>',
                '〔' => '〕',
                '{' => '}',
                _ => '\0',
            };

            if (close != '\0')
            {
                var end = FindClose(text, i, c, close);
                if (end > i)
                {
                    // Keep the bracket's position as a separator: "裂缝(chink的过去分词)弱点" → "裂缝 弱点".
                    sb.Append(' ');
                    i = end;
                    continue;
                }
            }

            if (c is '"' or '\'' or '“' or '”' or '「' or '」' or '『' or '』' or '《' or '》')
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static int FindClose(string text, int start, char open, char close)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
