using Lexi.Core.Text;

namespace Lexi.Core.Search;

/// <summary>Normalized views of the user's query.</summary>
public sealed class QueryInfo
{
    public static readonly QueryInfo Empty = new(string.Empty);

    private QueryInfo(string raw)
    {
        Raw = raw;
        Clean = TextNormalizer.Clean(raw);
        Key = Clean.ToLowerInvariant();
        Fold = TextNormalizer.FoldKey(Key);
        HasGermanLetters = TextNormalizer.HasGermanLetters(Clean);
        HasCjk = TextNormalizer.HasCjk(Clean);
        WordCount = Clean.Length == 0 ? 0 : Clean.Count(c => c == ' ') + 1;
        var letters = 0;
        foreach (var c in Clean)
        {
            if (char.IsLetter(c))
            {
                letters++;
            }
        }

        LetterCount = letters;
    }

    public string Raw { get; }

    /// <summary>NFKC-cleaned text with original casing.</summary>
    public string Clean { get; }

    /// <summary>Lower-cased, diacritics kept.</summary>
    public string Key { get; }

    /// <summary>Lower-cased, diacritics removed, ß → ss.</summary>
    public string Fold { get; }

    public bool HasGermanLetters { get; }

    public bool HasCjk { get; }

    public int WordCount { get; }

    public int LetterCount { get; }

    public bool IsEmpty => Clean.Length == 0;

    /// <summary>Looks like a sentence rather than a word/phrase lookup.</summary>
    public bool IsSentence => WordCount >= 5 || (WordCount >= 3 && Clean.IndexOfAny(['.', '?', '!', ',']) >= 0);

    public static QueryInfo Parse(string? raw) => string.IsNullOrWhiteSpace(raw) ? Empty : new QueryInfo(raw);
}
