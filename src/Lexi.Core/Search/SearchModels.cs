using Lexi.Core.Models;

namespace Lexi.Core.Search;

[Flags]
public enum WordFlags
{
    None = 0,
    HasZh = 1,
    HasEn = 2,
    HasDe = 4,
    MultiWord = 8,
    ProperNoun = 16,
    /// <summary>Learner core vocabulary (exam lists, Oxford 3000, Collins ★).</summary>
    Core = 32,
    HasAudio = 64,
    HasIpa = 128,
}

/// <summary>One headword (lemma) of one language: the unit of search results.</summary>
public sealed record WordRecord(
    long Id,
    Lang Lang,
    string Word,
    string Fold,
    float Zipf,
    WordFlags Flags,
    string? Badge,
    string? Brief);

/// <summary>An inflected/variant form that points to a lemma (e.g. "Häuser" → Haus, "went" → go).</summary>
public sealed record FormRecord(string Form, string Tags, WordRecord Lemma);

public enum MatchKind : byte
{
    /// <summary>Headword equals the query (case-insensitive, accent-sensitive).</summary>
    Exact,
    /// <summary>Query is an inflected form of the headword.</summary>
    Form,
    /// <summary>Query uses ae/oe/ue/ss for ä/ö/ü/ß ("Uebertragung").</summary>
    Translit,
    FormTranslit,
    /// <summary>Query matches when diacritics are ignored ("hauser" → Häuser).</summary>
    Fold,
    FormFold,
    Prefix,
    Fuzzy,
    FuzzyForm,
}

public sealed class SearchRequest
{
    public required string Text { get; init; }

    /// <summary>Restrict to one language; null = all.</summary>
    public Lang? Language { get; init; }

    public int Limit { get; init; } = 20;

    /// <summary>Allow fuzzy (spelling-correction) candidates.</summary>
    public bool Fuzzy { get; init; } = true;
}

public sealed class SearchHit
{
    public required WordRecord Word { get; init; }

    public MatchKind Kind { get; set; }

    /// <summary>The form/variant string that matched (e.g. "Häuser" for lemma Haus).</summary>
    public string? Matched { get; set; }

    /// <summary>Grammatical tags of the matched form (space separated), when <see cref="Kind"/> is a form match.</summary>
    public string? FormTags { get; set; }

    public int Distance { get; set; }

    public double Score { get; set; }

    public bool IsCorrection => Kind is MatchKind.Fuzzy or MatchKind.FuzzyForm;

    public bool IsForm => Kind is MatchKind.Form or MatchKind.FormFold or MatchKind.FormTranslit or MatchKind.FuzzyForm;

    public override string ToString() => $"{Word.Word} [{Word.Lang}] {Kind} d={Distance} score={Score:F1}{(Matched is null ? "" : " via " + Matched)}";
}

public sealed class SearchResult
{
    public static readonly SearchResult Empty = new() { Query = QueryInfo.Empty, Hits = [] };

    public required QueryInfo Query { get; init; }

    public required IReadOnlyList<SearchHit> Hits { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>True when no exact/form match exists and the top hits are spelling corrections.</summary>
    public bool IsCorrection => Hits.Count > 0 && Hits[0].IsCorrection;
}
