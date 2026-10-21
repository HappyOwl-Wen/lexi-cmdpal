using System.Text.Json.Serialization;

namespace Lexi.Core.Models;

/// <summary>
/// Unified dictionary entry: one lexical entry (headword + part of speech) from one source.
/// Entries from different sources are kept separate (never merged at import) so every field stays
/// attributable; <c>WordView</c> aggregates them for display. Short JSON names keep the database compact.
/// </summary>
public sealed class EntryData
{
    /// <summary><see cref="Kind"/> of an entry whose senses are Chinese words listing this headword as a translation.</summary>
    public const string ReverseKind = "reverse";

    [JsonPropertyName("h")]
    public string Headword { get; set; } = string.Empty;

    /// <summary>
    /// Null for an ordinary entry. <see cref="ReverseKind"/>: each sense is a Chinese headword (simplified, with its
    /// sense description as label) whose own entry lists this word as a translation — a link, not a definition.
    /// </summary>
    [JsonPropertyName("k")]
    public string? Kind { get; set; }

    /// <summary>Normalized part of speech: noun, verb, adj, adv, pron, prep, conj, intj, num, det, article, particle, phrase, prefix, suffix, abbrev, name, other.</summary>
    [JsonPropertyName("p")]
    public string? Pos { get; set; }

    [JsonPropertyName("g")]
    public Grammar? Grammar { get; set; }

    [JsonPropertyName("ipa")]
    public List<Pron>? Ipa { get; set; }

    [JsonPropertyName("au")]
    public List<AudioRef>? Audio { get; set; }

    [JsonPropertyName("s")]
    public List<Sense>? Senses { get; set; }

    /// <summary>Selected inflected forms (with grammatical tags) as given by the source.</summary>
    [JsonPropertyName("fm")]
    public List<WordForm>? Forms { get; set; }

    [JsonPropertyName("syn")]
    public List<string>? Synonyms { get; set; }

    /// <summary>Related/contrasting terms (e.g. Wiktionary coordinate terms).</summary>
    [JsonPropertyName("rel")]
    public List<string>? Related { get; set; }

    [JsonPropertyName("li")]
    public LearnerInfo? Learner { get; set; }

    /// <summary>Reference back to the raw source record, e.g. "kaikki:dewiktionary@2026-10-03:Schwachstelle#noun".</summary>
    [JsonPropertyName("ref")]
    public string? RawRef { get; set; }
}

public sealed class Grammar
{
    /// <summary>German noun gender: "m", "f", "n" (comma separated when several).</summary>
    [JsonPropertyName("gen")]
    public string? Gender { get; set; }

    [JsonPropertyName("pl")]
    public List<string>? Plural { get; set; }

    [JsonPropertyName("gs")]
    public string? GenitiveSingular { get; set; }

    /// <summary>German noun declension, four rows (nominative, genitive, dative, accusative).</summary>
    [JsonPropertyName("cases")]
    public List<CaseRow>? Cases { get; set; }

    /// <summary>Present tense: German ich/du/er-sie-es forms; English third person singular.</summary>
    [JsonPropertyName("pres")]
    public List<string>? Present { get; set; }

    /// <summary>German Präteritum (1st/3rd sg.) or English simple past.</summary>
    [JsonPropertyName("past")]
    public List<string>? Past { get; set; }

    /// <summary>German Partizip II or English past participle.</summary>
    [JsonPropertyName("pp")]
    public List<string>? PastParticiple { get; set; }

    [JsonPropertyName("prp")]
    public List<string>? PresentParticiple { get; set; }

    [JsonPropertyName("k2")]
    public List<string>? SubjunctiveII { get; set; }

    [JsonPropertyName("imp")]
    public List<string>? Imperative { get; set; }

    /// <summary>Perfect auxiliary: haben / sein.</summary>
    [JsonPropertyName("aux")]
    public List<string>? Auxiliary { get; set; }

    /// <summary>Separable verb prefix ("an" for anfangen), derived from source forms.</summary>
    [JsonPropertyName("sep")]
    public string? SeparablePrefix { get; set; }

    [JsonPropertyName("cmp")]
    public List<string>? Comparative { get; set; }

    [JsonPropertyName("sup")]
    public List<string>? Superlative { get; set; }

    /// <summary>Free-text grammar summary from the source (e.g. a Wiktionary head line).</summary>
    [JsonPropertyName("sum")]
    public string? Summary { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        Gender is null && Plural is null && GenitiveSingular is null && Cases is null && Present is null && Past is null &&
        PastParticiple is null && PresentParticiple is null && SubjunctiveII is null && Imperative is null &&
        Auxiliary is null && SeparablePrefix is null && Comparative is null && Superlative is null && Summary is null;
}

public sealed class CaseRow
{
    /// <summary>nom, gen, dat, acc</summary>
    [JsonPropertyName("c")]
    public string Case { get; set; } = string.Empty;

    [JsonPropertyName("sg")]
    public string? Singular { get; set; }

    [JsonPropertyName("pl")]
    public string? Plural { get; set; }
}

public sealed class Pron
{
    [JsonPropertyName("t")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Region: UK, US, DE, AT, CH, AU … or null when unspecified.</summary>
    [JsonPropertyName("r")]
    public string? Region { get; set; }

    [JsonPropertyName("n")]
    public string? Note { get; set; }
}

public sealed class AudioRef
{
    /// <summary>Wikimedia Commons file name (e.g. "De-Schwachstelle.ogg").</summary>
    [JsonPropertyName("f")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("r")]
    public string? Region { get; set; }

    /// <summary>Direct MP3 URL when the source provides one.</summary>
    [JsonPropertyName("mp3")]
    public string? Mp3Url { get; set; }
}

public sealed class Sense
{
    /// <summary>Sense number as given by the source ("1", "1a").</summary>
    [JsonPropertyName("n")]
    public string? Number { get; set; }

    /// <summary>Definition/gloss text.</summary>
    [JsonPropertyName("d")]
    public string? Gloss { get; set; }

    /// <summary>Language of <see cref="Gloss"/>: "zh", "en" or "de".</summary>
    [JsonPropertyName("dl")]
    public string? GlossLang { get; set; }

    /// <summary>Usage/domain labels (e.g. "computing", "colloquial", "[计]").</summary>
    [JsonPropertyName("lb")]
    public List<string>? Labels { get; set; }

    [JsonPropertyName("ex")]
    public List<Example>? Examples { get; set; }

    /// <summary>Sense-aligned Chinese equivalents (e.g. de.wiktionary translation tables).</summary>
    [JsonPropertyName("zh")]
    public List<string>? Zh { get; set; }

    /// <summary>Sense-aligned English equivalents.</summary>
    [JsonPropertyName("en")]
    public List<string>? En { get; set; }
}

public sealed class Example
{
    [JsonPropertyName("t")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("tr")]
    public string? Translation { get; set; }
}

public sealed class WordForm
{
    [JsonPropertyName("f")]
    public string Form { get; set; } = string.Empty;

    [JsonPropertyName("t")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("a")]
    public string? Article { get; set; }
}

/// <summary>Learner-oriented metadata (ECDICT): exam word lists, Collins stars, Oxford 3000, corpus ranks.</summary>
public sealed class LearnerInfo
{
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("collins")]
    public int Collins { get; set; }

    [JsonPropertyName("oxford")]
    public bool Oxford { get; set; }

    [JsonPropertyName("bnc")]
    public int Bnc { get; set; }

    [JsonPropertyName("frq")]
    public int Frq { get; set; }
}
