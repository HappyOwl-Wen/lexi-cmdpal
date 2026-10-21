namespace Lexi.AI;

public enum AiTaskKind
{
    /// <summary>Meaning and usage of a word.</summary>
    ExplainWord,
    /// <summary>German grammar of a word (gender, plural, cases, conjugation, separable prefix …).</summary>
    ExplainGrammar,
    /// <summary>Differences between easily confused words.</summary>
    CompareWords,
    /// <summary>Translate a sentence typed by the user.</summary>
    TranslateSentence,
    /// <summary>A term in a professional context (e.g. IT security).</summary>
    ExplainInContext,
}

/// <summary>What the user asked for, plus local dictionary facts the model may rely on.</summary>
public sealed record AiRequest(
    AiTaskKind Kind,
    string Input,
    string? LanguageName = null,
    string? DictionaryContext = null,
    IReadOnlyList<string>? CompareWith = null,
    string? Domain = null);

public sealed record AiResult(string Text, string? Model, TimeSpan Elapsed, bool FromCache, DateTimeOffset CreatedAt);

/// <summary>A failure the UI can show as-is (no secrets, no stack traces).</summary>
public sealed class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An LLM backend. Calls are always user-initiated; the dictionary never depends on them.</summary>
public interface IAiProvider
{
    string Name { get; }

    /// <summary>Returns null when the provider can be used, otherwise a human-readable reason.</summary>
    string? CheckAvailability();

    Task<AiResult> CompleteAsync(string prompt, IProgress<string>? partial, CancellationToken cancellationToken);
}
