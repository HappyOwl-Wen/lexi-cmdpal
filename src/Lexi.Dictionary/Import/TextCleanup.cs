using System.Text;
using System.Text.RegularExpressions;

namespace Lexi.Dictionary.Import;

internal static partial class TextCleanup
{
    /// <summary>Collapses whitespace and trims; returns null for empty input.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    public static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = text.LastIndexOfAny([' ', '，', '；', ',', ';'], max - 1);
        if (cut < max / 2)
        {
            cut = max - 1;
        }

        return string.Concat(text.AsSpan(0, cut).TrimEnd(" ,;，；"), "…");
    }

    /// <summary>
    /// zh.wiktionary German glosses often start with grammar notes: "〈阴〉 pl.Schwachstellen 缺点，弱点".
    /// Returns the cleaned gloss and the gender hint (m/f/n) found in it.
    /// </summary>
    public static (string? Gloss, string? Gender) CleanZhGloss(string gloss)
    {
        string? gender = null;
        var m = ZhGenderPrefix().Match(gloss);
        if (m.Success)
        {
            gender = m.Groups[1].Value switch
            {
                "阳" or "陽" => "m",
                "阴" or "陰" => "f",
                "中" => "n",
                _ => null,
            };
        }

        var s = ZhGrammarNoise().Replace(gloss, " ");
        return (Normalize(s), gender);
    }

    /// <summary>Splits "German sentence　中文翻译" (full-width space) into an example pair.</summary>
    public static bool TrySplitExample(string gloss, out string text, out string translation)
    {
        var idx = gloss.IndexOf('　');
        if (idx > 0 && idx < gloss.Length - 1)
        {
            var left = gloss[..idx].Trim();
            var right = gloss[(idx + 1)..].Trim();
            if (left.Length > 0 && right.Length > 0 && IsMostlyLatin(left))
            {
                text = left;
                translation = right;
                return true;
            }
        }

        text = translation = string.Empty;
        return false;
    }

    public static bool IsMostlyLatin(string s)
    {
        int latin = 0, other = 0;
        foreach (var c in s)
        {
            if (char.IsLetter(c))
            {
                if (c < 0x250)
                {
                    latin++;
                }
                else
                {
                    other++;
                }
            }
        }

        return latin > 0 && latin >= other * 3;
    }

    public static bool ContainsCjk(string s)
    {
        foreach (var c in s)
        {
            if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿')
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^\s*[〈<（(]\s*(阳|陽|阴|陰|中)\s*[〉>）)]")]
    private static partial Regex ZhGenderPrefix();

    // 〈阴〉, 〈复〉, pl.Schwachstellen, (pl.) markers
    [GeneratedRegex(@"[〈<]\s*[^〉>]{1,4}\s*[〉>]|\bpl\.\s*\S+|\(pl\.\)")]
    private static partial Regex ZhGrammarNoise();
}
