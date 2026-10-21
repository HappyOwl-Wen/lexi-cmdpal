using System.Globalization;
using System.Text;

namespace Lexi.Core.Text;

/// <summary>
/// Normalization used identically at import time and at query time, so keys always agree.
/// <list type="bullet">
/// <item><b>Clean</b>: NFKC (full-width IME input → ASCII, ligatures), unified quotes/dashes, no control/zero-width chars, single spaces.</item>
/// <item><b>Key</b>: Clean + lower-case. Keeps diacritics: "Häuser" → "häuser".</item>
/// <item><b>Fold</b>: Key without diacritics, ß → ss: "Häuser" → "hauser". Used for diacritic-insensitive lookup and fuzzy search.</item>
/// <item><b>Translit</b>: German transliteration ä→ae, ö→oe, ü→ue, ß→ss, then folded: "Übertragung" → "uebertragung".</item>
/// </list>
/// </summary>
public static class TextNormalizer
{
    // Precomputed fold table for U+0000..U+024F (Basic Latin .. Latin Extended-B).
    private static readonly string?[] FoldTable = BuildFoldTable();

    public static string Clean(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var s = NormalizeKc(input);
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var ch in s)
        {
            var c = ch switch
            {
                '‘' or '’' or '‛' or 'ʼ' or '′' or '`' or '´' => '\'',
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                _ => ch,
            };

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (char.IsControl(c) || c is '​' or '‌' or '‍' or '⁠' or '﻿' or '­')
            {
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    public static string ToKey(string? text) => Clean(text).ToLowerInvariant();

    /// <summary>Diacritic-insensitive key. Input may be raw text; it is cleaned and lower-cased first.</summary>
    public static string ToFold(string? text) => FoldKey(ToKey(text));

    /// <summary>Folds an already computed <see cref="ToKey"/> value.</summary>
    public static string FoldKey(string key)
    {
        if (key.Length == 0)
        {
            return key;
        }

        var allAscii = true;
        foreach (var c in key)
        {
            if (c >= 0x80)
            {
                allAscii = false;
                break;
            }
        }

        if (allAscii)
        {
            return key;
        }

        var sb = new StringBuilder(key.Length + 4);
        foreach (var c in key)
        {
            if (c < 0x80)
            {
                sb.Append(c);
            }
            else if (c < FoldTable.Length)
            {
                sb.Append(FoldTable[c]);
            }
            else if (char.IsSurrogate(c))
            {
                // Characters outside the BMP (emoji, rare CJK) have no diacritics to fold.
                sb.Append(c);
            }
            else
            {
                sb.Append(FoldSlow(c));
            }
        }

        return sb.ToString();
    }

    private static string NormalizeKc(string input)
    {
        try
        {
            return input.IsNormalized(NormalizationForm.FormKC) ? input : input.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Invalid UTF-16 (lone surrogates): drop the broken code units and retry.
            var sb = new StringBuilder(input.Length);
            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                {
                    sb.Append(c).Append(input[++i]);
                }
                else if (!char.IsSurrogate(c))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Normalize(NormalizationForm.FormKC);
        }
    }

    /// <summary>German transliteration key (ä→ae, ö→oe, ü→ue, ß→ss), then folded.</summary>
    public static string ToTranslit(string? text) => TranslitKey(ToKey(text));

    public static string TranslitKey(string key)
    {
        if (key.AsSpan().IndexOfAny("äöüß") < 0)
        {
            return FoldKey(key);
        }

        var sb = new StringBuilder(key.Length + 4);
        foreach (var c in key)
        {
            switch (c)
            {
                case 'ä': sb.Append("ae"); break;
                case 'ö': sb.Append("oe"); break;
                case 'ü': sb.Append("ue"); break;
                case 'ß': sb.Append("ss"); break;
                default: sb.Append(c); break;
            }
        }

        return FoldKey(sb.ToString());
    }

    /// <summary>True when the text contains letters that only occur in German among our languages.</summary>
    public static bool HasGermanLetters(string text) => text.AsSpan().IndexOfAny("äöüßÄÖÜẞ") >= 0;

    /// <summary>True for text containing CJK ideographs (used to route Chinese input).</summary>
    public static bool HasCjk(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿')
            {
                return true;
            }
        }

        return false;
    }

    private static string FoldSlow(char c)
    {
        var special = SpecialFold(c);
        if (special is not null)
        {
            return special;
        }

        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var d in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(d);
            if (cat is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                sb.Append(d);
            }
        }

        return sb.ToString();
    }

    private static string? SpecialFold(char c) => c switch
    {
        'ß' or 'ẞ' => "ss",
        'æ' => "ae",
        'œ' => "oe",
        'ø' => "o",
        'ł' => "l",
        'đ' or 'ð' => "d",
        'þ' => "th",
        'ı' => "i",
        'ĳ' => "ij",
        'ŋ' => "n",
        _ => null,
    };

    private static string?[] BuildFoldTable()
    {
        var table = new string?[0x250];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = i < 0x80 ? ((char)i).ToString() : FoldSlow((char)i);
        }

        return table;
    }
}
