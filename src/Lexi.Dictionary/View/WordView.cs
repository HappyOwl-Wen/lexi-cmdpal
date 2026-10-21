using Lexi.Core.Models;
using Lexi.Core.Search;

namespace Lexi.Dictionary.View;

/// <summary>A block of meanings from exactly one source (never mixed), so attribution stays exact.</summary>
public sealed record MeaningBlock(SourceInfo Source, string Kind, string? Pos, IReadOnlyList<Sense> Senses);

public sealed record PronView(string Text, string? Region, SourceInfo Source, string? Note);

public sealed record AudioView(string File, string? Region, string? Mp3Url, SourceInfo Source);

public sealed record GrammarLine(string Label, string Value);

public sealed record FormView(string Form, string Label);

/// <summary>Display model of one headword, aggregated from all sources.</summary>
public sealed class WordView
{
    public required WordRecord Word { get; init; }

    /// <summary>Headword with article for German nouns ("die Schwachstelle").</summary>
    public required string Title { get; init; }

    public string? Article { get; init; }

    /// <summary>Distinct parts of speech, normalized (noun, verb …).</summary>
    public IReadOnlyList<string> PartsOfSpeech { get; init; } = [];

    public IReadOnlyList<PronView> Ipa { get; init; } = [];

    public IReadOnlyList<AudioView> Audio { get; init; } = [];

    /// <summary>Compact grammar facts (gender, plural, principal parts, auxiliary …).</summary>
    public IReadOnlyList<GrammarLine> Grammar { get; init; } = [];

    public SourceInfo? GrammarSource { get; init; }

    public IReadOnlyList<CaseRow>? Cases { get; init; }

    public string? GrammarSummary { get; init; }

    public IReadOnlyList<MeaningBlock> Chinese { get; init; } = [];

    public IReadOnlyList<MeaningBlock> English { get; init; } = [];

    public IReadOnlyList<MeaningBlock> German { get; init; } = [];

    public IReadOnlyList<string> Synonyms { get; init; } = [];

    /// <summary>Related/contrasting terms (useful for "compare words").</summary>
    public IReadOnlyList<string> Related { get; init; } = [];

    public LearnerInfo? Learner { get; init; }

    public IReadOnlyList<FormView> Forms { get; init; } = [];

    public IReadOnlyList<SourceInfo> Sources { get; init; } = [];

    public IReadOnlyList<string> RawRefs { get; init; } = [];

    public bool HasChinese => Chinese.Count > 0;

    /// <summary>One-line summary for list subtitles and clipboard.</summary>
    public string Brief => Word.Brief ?? string.Empty;
}
