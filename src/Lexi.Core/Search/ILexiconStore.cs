using Lexi.Core.Models;

namespace Lexi.Core.Search;

/// <summary>
/// Candidate retrieval used by <see cref="SearchEngine"/>. Implemented over SQLite in Lexi.Dictionary and
/// in memory in the tests, so ranking logic can be tested without PowerToys or a database.
/// </summary>
public interface ILexiconStore
{
    /// <summary>Headwords whose fold key equals <paramref name="fold"/>.</summary>
    IReadOnlyList<WordRecord> GetWordsByFold(string fold, Lang? language);

    /// <summary>Headwords for several fold keys (fuzzy candidates).</summary>
    IReadOnlyList<WordRecord> GetWordsByFolds(IReadOnlyCollection<string> folds, Lang? language);

    /// <summary>Inflected forms whose fold key equals <paramref name="fold"/>, with their lemma.</summary>
    IReadOnlyList<FormRecord> GetFormsByFold(string fold, Lang? language);

    IReadOnlyList<FormRecord> GetFormsByFolds(IReadOnlyCollection<string> folds, Lang? language);

    /// <summary>Headwords whose fold key starts with <paramref name="foldPrefix"/>, most frequent first.</summary>
    IReadOnlyList<WordRecord> GetWordsByPrefix(string foldPrefix, Lang? language, int limit);

    /// <summary>Fuzzy automaton over fold keys, or null when unavailable (search then skips spelling correction).</summary>
    FuzzyIndex? GetFuzzyIndex();

    /// <summary>
    /// Reverse lookup: headwords whose Chinese definition contains <paramref name="term"/> as a whole term,
    /// best candidates first (term weight and popularity). Empty when the store has no Chinese index.
    /// </summary>
    IReadOnlyList<ZhMatch> GetWordsByChinese(string term, Lang? language, int limit);
}
